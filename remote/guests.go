package main

import (
	"bufio"
	"crypto/rand"
	_ "embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"maps"
	"net/http"
	"net/url"
	"os"
	"slices"
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

// guestBook holds the open invitations and the guests who traded one. An invitation serves for its whole hour, as
// often as it is opened: a guest who opens the link again, in another browser or after a reload, still gets in, and so
// does anyone the link is passed on to, within the two guests at a time. A guest's cookie lives two hours from its
// own trade. Only their SHA-256 is kept, in memory, so a restart of the gateway ends them all.
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
	for _, m := range []map[string]time.Time{g.invites, g.guests} {
		maps.DeleteFunc(m, func(_ string, expires time.Time) bool { return now.After(expires) })
	}
	g.invites[digest(code)] = now.Add(inviteLife)
	return code
}

// join trades an invitation for a guest's cookie, or says why not.
func (g *guestBook) join(code string, now time.Time) (string, error) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if expires, ok := g.invites[digest(code)]; !ok || now.After(expires) {
		return "", errors.New("this invitation has expired: ask for a new link")
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
	mux.HandleFunc("GET /said", s.guest(s.guestSaid))
	return checked(s.guestHost, mux)
}

// localHandler is Delta's window's own entry, on 127.0.0.1 only and never behind the tunnel: the brain asks it for an
// invitation to show as a QR card, and to end the guests. Only the brain gets through, with its secret, and no page.
func (s *server) localHandler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("POST /invite", s.windowInvite)
	mux.HandleFunc("POST /guests/end", s.endGuests)
	mux.HandleFunc("POST /guests/mic", s.guestsMic)
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Host != s.localHost || r.Header.Get("Origin") != "" || r.Header.Get("X-Delta-Hook") != "1" ||
			s.brain == nil || !s.brain.knows(r.Header.Get("X-Delta-Remote")) {
			http.Error(w, "only Delta's brain asks here", http.StatusForbidden)
			return
		}
		mux.ServeHTTP(w, r)
	})
}

// windowInvite is invite for Delta's window. Guests watch that window, so a QR card shown while one watches would
// hand them the invitation: the window invites only while nobody watches, and a paired device invites the second.
func (s *server) windowInvite(w http.ResponseWriter, r *http.Request) {
	if s.guestsWatching() > 0 {
		http.Error(w, "a guest watches Delta's window: invite from a paired device", http.StatusConflict)
		return
	}
	s.invite(w, r)
}

// guestsMic lets guests hear the microphone, or stops it, from the switch in Delta's window: {"on": true|false}.
// Guests hear Delta's voice in any case; the microphone is off at each start of the gateway.
func (s *server) guestsMic(w http.ResponseWriter, r *http.Request) {
	if s.voice == nil {
		http.Error(w, "the gateway has no microphone for guests: it needs -mic", http.StatusNotFound)
		return
	}
	var req struct{ On *bool }
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<10)).Decode(&req); err != nil || req.On == nil {
		http.Error(w, `{"on": true} or {"on": false} is needed`, http.StatusBadRequest)
		return
	}
	s.mu.Lock()
	s.micOn = *req.On
	s.watch()
	s.mu.Unlock()
	logf("guests: the microphone %s", map[bool]string{true: "on", false: "off"}[*req.On])
	w.WriteHeader(http.StatusNoContent)
}

func (s *server) mic() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.micOn
}

func (s *server) guestsWatching() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	n := 0
	for _, connected := range s.guests {
		if connected {
			n++
		}
	}
	return n
}

// invite opens an invitation and gives its link, to send on. The code is in the link's #fragment, which the browser
// sends to no server, the tunnel's included.
func (s *server) invite(w http.ResponseWriter, r *http.Request) {
	if s.guestHost == "" {
		http.Error(w, "guests are off: the gateway needs -guest-host", http.StatusNotFound)
		return
	}
	now := s.now()
	link := "https://" + s.guestHost + "/#invite=" + s.book.invite(now)
	logf("guests: an invitation, until %s", now.Add(inviteLife).Format("15:04"))
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	json.NewEncoder(w).Encode(map[string]string{"link": link, "expires": now.Add(inviteLife).Format(time.RFC3339)})
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
		logf("guests: an invitation refused: %v", err)
		http.Error(w, err.Error(), http.StatusUnauthorized)
		return
	}
	logf("guests: an invitation traded for two hours")
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
	logf("guests: the relay's addresses for a browser, %d servers", len(forBrowser(servers)))
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
	n := s.guestN.Add(1)
	config := guestConfig(servers)
	offered := candidates(offer.SDP)
	offer.SDP = withoutRelay(offer.SDP)
	var extra []webrtc.TrackLocal
	if s.voice != nil {
		extra = append(extra, s.voice)
	}
	pc, answer, err := s.answer(s.guestAPI, config, offer, extra...)
	if err != nil {
		logf("guest %d: the offer: %v", n, err)
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	// What each side offers to connect by, as candidate types only: a guest's address is not written down.
	logf("guest %d: the browser offers %s, of which it keeps %s; the gateway answers %s through %d relay URLs",
		n, offered, candidates(offer.SDP), candidates(answer.SDP), len(config.ICEServers))
	pc.OnICEConnectionStateChange(func(st webrtc.ICEConnectionState) {
		if st != webrtc.ICEConnectionStateConnected {
			logf("guest %d: ICE %s", n, st)
			return
		}
		logf("guest %d: ICE connected%s", n, selected(pc))
	})
	s.mu.Lock()
	s.guests[pc] = false
	s.mu.Unlock()
	time.AfterFunc(s.book.left(c.Value, s.now()), func() { pc.Close() })
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(answer)
}

// withoutRelay leaves out of a guest's offer the candidates on the browser's own relay. Those are Cloudflare's relay
// addresses too, and Cloudflare refuses to relay to them: its ChannelBind answers 400, and pion then closes the whole
// allocation, every other way to the guest with it (seen live on 2026-10-24). The gateway's relay reaches the
// browser at its public address instead, its srflx, or the one its checks come from.
func withoutRelay(sdp string) string {
	lines := strings.SplitAfter(sdp, "\n")
	kept := lines[:0]
	for _, line := range lines {
		if f := strings.Fields(line); strings.HasPrefix(line, "a=candidate:") {
			if i := slices.Index(f, "typ"); i >= 0 && i+1 < len(f) && f[i+1] == "relay" {
				continue
			}
		}
		kept = append(kept, line)
	}
	return strings.Join(kept, "")
}

// guestSaid streams Delta's voice to a guest: the sound of each sentence Delta says, and nothing else, neither its
// text nor what anyone said to it. The brain is asked as by a page whose sound does not run, so the computer still
// says everything aloud.
func (s *server) guestSaid(w http.ResponseWriter, r *http.Request) {
	if s.brain == nil {
		http.Error(w, errNoBrain.Error(), http.StatusServiceUnavailable)
		return
	}
	resp, err := s.brain.said(r.Context(), false)
	if err != nil || resp.StatusCode != http.StatusOK {
		if err == nil {
			resp.Body.Close()
		}
		comeBack(w)
		return
	}
	defer resp.Body.Close()
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(http.StatusOK)
	flusher, _ := w.(http.Flusher)
	lines := bufio.NewScanner(resp.Body)
	lines.Buffer(make([]byte, 64<<10), maxHear*2)
	for lines.Scan() {
		data, ok := strings.CutPrefix(lines.Text(), "data:")
		if !ok {
			continue
		}
		var said struct {
			WAV string `json:"wav_b64"`
		}
		if json.Unmarshal([]byte(data), &said) != nil || said.WAV == "" {
			continue
		}
		out, _ := json.Marshal(map[string]string{"wav_b64": said.WAV})
		if _, err := fmt.Fprintf(w, "data: %s\n\n", out); err != nil {
			return
		}
		if flusher != nil {
			flusher.Flush()
		}
	}
}

// candidates counts an SDP's ICE candidates by type, such as "1 host, 2 srflx, 1 relay".
func candidates(sdp string) string {
	count := map[string]int{}
	for _, line := range strings.Split(sdp, "\n") {
		if !strings.HasPrefix(line, "a=candidate:") {
			continue
		}
		f := strings.Fields(line)
		if i := slices.Index(f, "typ"); i >= 0 && i+1 < len(f) {
			count[f[i+1]]++
		}
	}
	var out []string
	for _, typ := range []string{"host", "srflx", "prflx", "relay"} {
		if count[typ] > 0 {
			out = append(out, fmt.Sprintf("%d %s", count[typ], typ))
		}
	}
	if len(out) == 0 {
		return "no candidate"
	}
	return strings.Join(out, ", ")
}

// selected names the candidate types of the pair pc's video goes by, as ", relay here to srflx there".
func selected(pc *webrtc.PeerConnection) string {
	for _, sender := range pc.GetSenders() {
		if t := sender.Transport(); t != nil {
			if pair, err := t.ICETransport().GetSelectedCandidatePair(); err == nil && pair != nil {
				return fmt.Sprintf(", %s here to %s there", pair.Local.Typ, pair.Remote.Typ)
			}
		}
	}
	return ""
}

// announce tells the brain how many guests watch, at each change and every guestRenew while any does, for the badge
// in Delta's window: the brain takes the badge away 60 s after the last word, should the gateway stop. It also says
// whether guests hear the microphone, at the start too, for the window's switch: the gateway starts with it off.
func (s *server) announce() {
	tick := time.NewTicker(guestRenew)
	defer tick.Stop()
	n := 0
	for first := true; ; first = false {
		if !first {
			select {
			case n = <-s.watching:
			case <-tick.C:
				if n == 0 {
					continue
				}
			}
		}
		if err := s.brain.guests(n, s.mic()); err != nil {
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
