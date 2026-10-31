package main

import (
	"crypto"
	"crypto/rsa"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math/big"
	"net/http"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/pion/webrtc/v4"
)

// access checks the token Cloudflare Access puts on each request it lets through to the owner's entry: signed by the
// team's keys, for this application, still valid, and for the one email allowed. Access itself asks for that email
// and mails it a PIN; the gateway never sees the PIN, and checks the token so that a request that did not come through
// Access, or an application set too wide, gets nothing.
type access struct {
	team, aud, email string
	certs            string // the team's public keys, https://<team>.cloudflareaccess.com/cdn-cgi/access/certs
	client           *http.Client
	now              func() time.Time

	mu      sync.Mutex
	keys    map[string]*rsa.PublicKey // by kid
	fetched time.Time
}

func newAccess(team, aud, email string) *access {
	return &access{
		team: team, aud: aud, email: strings.ToLower(email),
		certs:  "https://" + team + ".cloudflareaccess.com/cdn-cgi/access/certs",
		client: &http.Client{Timeout: 5 * time.Second},
		now:    time.Now,
	}
}

// verify says why token does not let its bearer in, or nil.
func (a *access) verify(token string) error {
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return errors.New("no Access token")
	}
	var head struct{ Alg, Kid string }
	if err := decodePart(parts[0], &head); err != nil || head.Alg != "RS256" {
		return errors.New("an Access token that is not RS256")
	}
	key, err := a.key(head.Kid)
	if err != nil {
		return err
	}
	sig, err := base64.RawURLEncoding.DecodeString(parts[2])
	if err != nil {
		return errors.New("an Access token with a broken signature")
	}
	sum := sha256.Sum256([]byte(parts[0] + "." + parts[1]))
	if rsa.VerifyPKCS1v15(key, crypto.SHA256, sum[:], sig) != nil {
		return errors.New("an Access token not signed by the team")
	}
	var claims struct {
		Aud      audience
		Iss      string
		Email    string
		Exp, Nbf int64
	}
	if err := decodePart(parts[1], &claims); err != nil {
		return errors.New("an Access token whose claims do not read")
	}
	now := a.now().Unix()
	switch {
	case !slices.Contains(claims.Aud, a.aud):
		return errors.New("an Access token for another application")
	case claims.Iss != "https://"+a.team+".cloudflareaccess.com":
		return errors.New("an Access token from another team")
	case claims.Exp <= now || claims.Nbf > now+60:
		return errors.New("an Access token out of its time")
	case strings.ToLower(claims.Email) != a.email:
		return errors.New("an Access token for another email")
	}
	return nil
}

// key is the team's key kid, fetched again when it is not known yet, at most once a minute.
func (a *access) key(kid string) (*rsa.PublicKey, error) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if k, ok := a.keys[kid]; ok {
		return k, nil
	}
	if a.now().Sub(a.fetched) < time.Minute {
		return nil, errors.New("an Access token signed by an unknown key")
	}
	a.fetched = a.now()
	resp, err := a.client.Get(a.certs)
	if err != nil {
		return nil, errors.New("the team's Access keys are out of reach")
	}
	defer resp.Body.Close()
	var set struct {
		Keys []struct{ Kid, Kty, N, E string }
	}
	if resp.StatusCode != http.StatusOK || json.NewDecoder(io.LimitReader(resp.Body, 64<<10)).Decode(&set) != nil {
		return nil, fmt.Errorf("the team's Access keys answered %s", resp.Status)
	}
	a.keys = map[string]*rsa.PublicKey{}
	for _, k := range set.Keys {
		n, errN := base64.RawURLEncoding.DecodeString(k.N)
		e, errE := base64.RawURLEncoding.DecodeString(k.E)
		if k.Kty != "RSA" || errN != nil || errE != nil || len(e) > 4 {
			continue
		}
		a.keys[k.Kid] = &rsa.PublicKey{N: new(big.Int).SetBytes(n), E: int(new(big.Int).SetBytes(e).Int64())}
	}
	if k, ok := a.keys[kid]; ok {
		return k, nil
	}
	return nil, errors.New("an Access token signed by an unknown key")
}

func decodePart(part string, v any) error {
	raw, err := base64.RawURLEncoding.DecodeString(part)
	if err != nil {
		return err
	}
	return json.Unmarshal(raw, v)
}

// audience reads a token's aud, which Access writes as a list and the standard allows as a single string.
type audience []string

func (a *audience) UnmarshalJSON(b []byte) error {
	var one string
	if json.Unmarshal(b, &one) == nil {
		*a = audience{one}
		return nil
	}
	return json.Unmarshal(b, (*[]string)(a))
}

// ownerHandler is the paired device's page for its owner away from home, at the host Access guards: the same page,
// speech and Delta's sentences as on the Wi-Fi, invitations included, but no pairing, since Access's identity stands
// for the device, and the video through the relay, as for guests. Every request needs Access's token.
func (s *server) ownerHandler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /{$}", static("text/html; charset=utf-8", pageHTML))
	mux.HandleFunc("GET /page.js", static("text/javascript; charset=utf-8", pageJS))
	mux.HandleFunc("GET /talk.js", static("text/javascript; charset=utf-8", talkJS))
	mux.HandleFunc("GET /page.css", static("text/css; charset=utf-8", pageCSS))
	mux.HandleFunc("GET /recorder.js", static("text/javascript; charset=utf-8", recorderJS))
	mux.HandleFunc("GET /ice", s.relay)
	mux.HandleFunc("POST /offer", s.ownerOffer)
	mux.HandleFunc("POST /hear", s.hear)
	mux.HandleFunc("GET /said", s.said)
	mux.HandleFunc("POST /invite", s.invite)
	mux.HandleFunc("POST /guests/end", s.endGuests)
	return checked(s.ownerHost, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if err := s.access.verify(r.Header.Get("Cf-Access-Jwt-Assertion")); err != nil {
			logf("the owner's entry refused %s %s: %v", r.Method, r.URL.Path, err)
			http.Error(w, "only the owner, through Cloudflare Access, gets in here", http.StatusForbidden)
			return
		}
		mux.ServeHTTP(w, r)
	}))
}

// ownerOffer is offer for the owner away from home: the same one device at a time, but through the relay.
func (s *server) ownerOffer(w http.ResponseWriter, r *http.Request) {
	var offer webrtc.SessionDescription
	if err := json.NewDecoder(io.LimitReader(r.Body, 64<<10)).Decode(&offer); err != nil || offer.Type != webrtc.SDPTypeOffer {
		http.Error(w, "an SDP offer is needed", http.StatusBadRequest)
		return
	}
	servers, err := s.ice()
	if err != nil {
		logf("the relay: %v", err)
		http.Error(w, "the relay is unavailable", http.StatusBadGateway)
		return
	}
	offer.SDP = withoutRelay(offer.SDP)
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.peer != nil {
		http.Error(w, "another device is connected", http.StatusConflict)
		return
	}
	pc, answer, err := s.answer(s.guestAPI, guestConfig(servers), offer)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	logf("the owner, away: the gateway answers %s", candidates(answer.SDP))
	s.peer = pc
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(answer)
}
