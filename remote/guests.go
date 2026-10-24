package main

import (
	"crypto/rand"
	_ "embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/pion/webrtc/v4"
)

var (
	//go:embed guest.html
	guestHTML []byte
	//go:embed guest.js
	guestJS []byte
)

const (
	guestCookie = "delta_guest"
	inviteLife  = time.Hour        // an invitation not used by then is void
	guestLife   = 2 * time.Hour    // how long a guest may watch
	maxGuests   = 2                // beside the paired device
	guestRenew  = 30 * time.Second // how often the brain hears that guests still watch; its badge goes after 60 s
)

// guestBook holds the open invitations and the guests who traded one. An invitation is single use and lives an hour;
// a guest's cookie lives two hours. Only their SHA-256 is kept, in memory, so a restart of the gateway ends them all.
type guestBook struct {
	mu      sync.Mutex
	invites map[string]time.Time // when each open invitation expires
	guests  map[string]time.Time // when each guest's cookie expires
}

func newGuestBook() *guestBook {
	return &guestBook{invites: map[string]time.Time{}, guests: map[string]time.Time{}}
}

// token is 128 random bits, as 32 hex digits.
func token() string {
	b := make([]byte, 16)
	rand.Read(b)
	return hex.EncodeToString(b)
}

func (g *guestBook) invite(now time.Time) string {
	code := token()
	g.mu.Lock()
	defer g.mu.Unlock()
	g.invites[digest(code)] = now.Add(inviteLife)
	return code
}

// join spends an invitation for a guest's cookie, or says why not.
func (g *guestBook) join(code string, now time.Time) (string, error) {
	g.mu.Lock()
	defer g.mu.Unlock()
	h := digest(code)
	expires, ok := g.invites[h]
	delete(g.invites, h)
	if !ok || now.After(expires) {
		return "", errors.New("this invitation was used already or has expired: ask for a new link")
	}
	cookie := token()
	g.guests[digest(cookie)] = now.Add(guestLife)
	return cookie, nil
}

// left is how long the guest holding cookie may still watch: 0 if not at all.
func (g *guestBook) left(cookie string, now time.Time) time.Duration {
	g.mu.Lock()
	defer g.mu.Unlock()
	if expires, ok := g.guests[digest(cookie)]; ok && now.Before(expires) {
		return expires.Sub(now)
	}
	return 0
}

// end voids every invitation and every guest's cookie.
func (g *guestBook) end() {
	g.mu.Lock()
	defer g.mu.Unlock()
	clear(g.invites)
	clear(g.guests)
}

// guestHandler is what the tunnel reaches, on 127.0.0.1 only: the guest's page, the trade of an invitation for a
// cookie, the relay's addresses and the guest's WebRTC offer. Nothing else is there: no speech, none of Delta's
// sentences and no pairing, so that a guest only ever watches.
func (s *server) guestHandler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /{$}", static("text/html; charset=utf-8", guestHTML))
	mux.HandleFunc("GET /guest.js", static("text/javascript; charset=utf-8", guestJS))
	mux.HandleFunc("GET /page.css", static("text/css; charset=utf-8", pageCSS))
	mux.HandleFunc("POST /join", s.join)
	mux.HandleFunc("GET /ice", s.guest(s.relay))
	mux.HandleFunc("POST /offer", s.guest(s.guestOffer))
	return checked(s.guestHost, mux)
}

// invite opens an invitation for one guest and gives the paired device its link, to send on. The code is in the
// link's #fragment, which the browser sends to no server, the tunnel's included.
func (s *server) invite(w http.ResponseWriter, r *http.Request) {
	if s.guestHost == "" {
		http.Error(w, "guests are off: the gateway needs -guest-host", http.StatusNotFound)
		return
	}
	link := "https://" + s.guestHost + "/#invite=" + s.book.invite(s.now())
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	json.NewEncoder(w).Encode(map[string]string{"link": link})
}

// endGuests voids every invitation and cookie and closes every guest's connection.
func (s *server) endGuests(w http.ResponseWriter, r *http.Request) {
	s.book.end()
	s.mu.Lock()
	for pc := range s.guests {
		go pc.Close()
	}
	s.mu.Unlock()
	w.WriteHeader(http.StatusNoContent)
}

func (s *server) join(w http.ResponseWriter, r *http.Request) {
	var req struct{ Invite string }
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<10)).Decode(&req); err != nil {
		http.Error(w, "an invitation is needed", http.StatusBadRequest)
		return
	}
	cookie, err := s.book.join(req.Invite, s.now())
	if err != nil {
		http.Error(w, err.Error(), http.StatusUnauthorized)
		return
	}
	http.SetCookie(w, &http.Cookie{
		Name: guestCookie, Value: cookie, Path: "/", MaxAge: int(guestLife / time.Second),
		HttpOnly: true, Secure: true, SameSite: http.SameSiteStrictMode,
	})
	w.WriteHeader(http.StatusOK)
}

// guest lets only a guest whose time is not up through to h.
func (s *server) guest(h http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if c, err := r.Cookie(guestCookie); err != nil || s.book.left(c.Value, s.now()) == 0 {
			http.Error(w, "this invitation has ended", http.StatusUnauthorized)
			return
		}
		h(w, r)
	}
}

// relay gives the guest's browser the relay's addresses, with credentials that last as long as the guest may watch.
func (s *server) relay(w http.ResponseWriter, r *http.Request) {
	servers, err := s.ice()
	if err != nil {
		logf("the relay: %v", err)
		http.Error(w, "the relay is unavailable", http.StatusBadGateway)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	json.NewEncoder(w).Encode(map[string]any{"iceServers": forBrowser(servers)})
}

// guestOffer answers a guest's offer, through the relay alone when there is one, so that the guest never learns this
// network's address. The connection closes when the guest's time is up.
func (s *server) guestOffer(w http.ResponseWriter, r *http.Request) {
	var offer webrtc.SessionDescription
	if err := json.NewDecoder(io.LimitReader(r.Body, 64<<10)).Decode(&offer); err != nil || offer.Type != webrtc.SDPTypeOffer {
		http.Error(w, "an SDP offer is needed", http.StatusBadRequest)
		return
	}
	c, _ := r.Cookie(guestCookie)
	s.mu.Lock()
	if len(s.guests)+s.pending >= maxGuests {
		s.mu.Unlock()
		http.Error(w, "two guests watch already", http.StatusConflict)
		return
	}
	s.pending++ // answering waits for the relay, which s.mu must not
	s.mu.Unlock()
	defer func() {
		s.mu.Lock()
		s.pending--
		s.mu.Unlock()
	}()

	servers, err := s.ice()
	if err != nil {
		logf("the relay: %v", err)
		http.Error(w, "the relay is unavailable", http.StatusBadGateway)
		return
	}
	pc, answer, err := s.answer(s.guestAPI, guestConfig(servers), offer)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	s.mu.Lock()
	s.guests[pc] = false
	s.mu.Unlock()
	time.AfterFunc(s.book.left(c.Value, s.now()), func() { pc.Close() })
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(answer)
}

// announce tells the brain how many guests watch, at each change and every guestRenew while any does, for the badge
// in Delta's window: the brain takes the badge away 60 s after the last word, should the gateway stop.
func (s *server) announce() {
	tick := time.NewTicker(guestRenew)
	defer tick.Stop()
	n := 0
	for {
		select {
		case n = <-s.watching:
		case <-tick.C:
			if n == 0 {
				continue
			}
		}
		if err := s.brain.guests(n); err != nil {
			logf("the guests' badge: %v", err)
		}
	}
}

// guestConfig has a guest's connection go through the relay alone when there is one.
func guestConfig(servers []iceServer) webrtc.Configuration {
	config := webrtc.Configuration{ICEServers: forGateway(servers)}
	if len(config.ICEServers) > 0 {
		config.ICETransportPolicy = webrtc.ICETransportPolicyRelay
	}
	return config
}

// iceServer is one entry of RTCPeerConnection's iceServers, as Cloudflare gives it and the browser takes it.
type iceServer struct {
	URLs       []string `json:"urls"`
	Username   string   `json:"username,omitempty"`
	Credential string   `json:"credential,omitempty"`
}

// forBrowser leaves out port 53, which browsers block: the page gathers its candidates before it sends its offer, and
// would wait for that one to time out.
func forBrowser(servers []iceServer) []iceServer {
	out := []iceServer{}
	for _, sv := range servers {
		kept := sv
		kept.URLs = nil
		for _, u := range sv.URLs {
			if !strings.Contains(u, ":53?") && !strings.HasSuffix(u, ":53") {
				kept.URLs = append(kept.URLs, u)
			}
		}
		if len(kept.URLs) > 0 {
			out = append(out, kept)
		}
	}
	return out
}

// forGateway keeps the relay over UDP alone: this machine reaches it, and every other way only slows the gathering.
func forGateway(servers []iceServer) []webrtc.ICEServer {
	var out []webrtc.ICEServer
	for _, sv := range servers {
		for _, u := range sv.URLs {
			if strings.HasPrefix(u, "turn:") && strings.Contains(u, "transport=udp") && !strings.Contains(u, ":53?") {
				out = append(out, webrtc.ICEServer{URLs: []string{u}, Username: sv.Username, Credential: sv.Credential})
			}
		}
	}
	return out
}

// cloudflareRelay asks Cloudflare's TURN service for the relay's addresses and credentials that last guestLife. The
// key file holds the TURN key made in Cloudflare's dashboard (Realtime, TURN) as {"id": "…", "token": "…"}; it is
// read at each call, so a new key needs no restart, and never written anywhere.
func cloudflareRelay(keyFile string) func() ([]iceServer, error) {
	client := &http.Client{Timeout: 5 * time.Second}
	return func() ([]iceServer, error) {
		raw, err := os.ReadFile(keyFile)
		if err != nil {
			return nil, fmt.Errorf("no TURN key in %s", keyFile)
		}
		var key struct{ ID, Token string }
		if json.Unmarshal(raw, &key) != nil || key.ID == "" || key.Token == "" {
			return nil, fmt.Errorf(`%s should hold {"id": "…", "token": "…"}`, keyFile)
		}
		body := fmt.Sprintf(`{"ttl": %d}`, int(guestLife/time.Second))
		req, err := http.NewRequest(http.MethodPost, "https://rtc.live.cloudflare.com/v1/turn/keys/"+url.PathEscape(key.ID)+
			"/credentials/generate-ice-servers", strings.NewReader(body))
		if err != nil {
			return nil, err
		}
		req.Header.Set("Authorization", "Bearer "+key.Token)
		req.Header.Set("Content-Type", "application/json")
		resp, err := client.Do(req)
		if err != nil {
			return nil, errors.New("Cloudflare's TURN service is out of reach")
		}
		defer resp.Body.Close()
		if resp.StatusCode/100 != 2 {
			return nil, errors.New("Cloudflare's TURN service answered " + resp.Status)
		}
		var out struct {
			ICEServers []iceServer `json:"iceServers"`
		}
		if err := json.NewDecoder(io.LimitReader(resp.Body, 64<<10)).Decode(&out); err != nil {
			return nil, err
		}
		return out.ICEServers, nil
	}
}
