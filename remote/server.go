package main

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"sync"
	"sync/atomic"
	"time"

	"github.com/pion/ice/v4"
	"github.com/pion/webrtc/v4"
)

var (
	//go:embed page.html
	pageHTML []byte
	//go:embed page.js
	pageJS []byte
	//go:embed talk.js
	talkJS []byte
	//go:embed page.css
	pageCSS []byte
	//go:embed recorder.js
	recorderJS []byte
)

// The page runs its own script only, talks to its own origin only, and can be framed by nobody.
const pagePolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; " +
	"media-src 'self' mediastream: blob:; base-uri 'none'; frame-ancestors 'none'; form-action 'none'"

// server answers one device at a time: it serves the page, pairs devices and takes a paired device's WebRTC offer,
// to send it Delta's window as H.264. Every request must name the gateway's own host (no DNS rebinding), and a POST
// must come from the page's own origin when it says where it comes from. The pairing code reaches the page in the
// link's #fragment, which the browser sends to no server. Beside the device, up to two guests watch through the
// guests' entry (guests.go).
type server struct {
	host  string // host:port the device uses, as it appears in Host
	pair  *pairing
	api   *webrtc.API
	video *webrtc.TrackLocalStaticRTP
	now   func() time.Time

	guestHost string                          // the guests' host behind the tunnel, delta.<domain>; "": no guests
	localHost string                          // host:port of Delta's window's own entry, on 127.0.0.1
	guestAPI  *webrtc.API                     // WebRTC for guests, who are not on this network
	book      *guestBook                      // their invitations and cookies
	ice       func() ([]iceServer, error)     // the relay's addresses and credentials for one guest
	watching  chan int                        // the number of guests who watch, for announce; nil: nobody is told
	pending   int                             // under mu: guests' offers being answered
	guests    map[*webrtc.PeerConnection]bool // under mu: each guest's connection, true once connected
	told      int                             // under mu: how many guests announce was last given
	guestN    atomic.Int64                    // guests' offers so far, to tell them apart in the log
	voice     *webrtc.TrackLocalStaticRTP     // the microphone guests hear, as Opus; nil: guests hear nothing
	onGuests  func(hear bool)                 // starts and stops the microphone's capture
	heard     bool                            // under mu: the microphone's capture runs
	micOn     bool                            // under mu: the user lets guests hear the microphone; off at each start
	toldMic   bool                            // under mu: the microphone's state announce was last given

	brain *brain // nil: no voice and no QR

	linkMu   sync.Mutex
	linkCode string        // the code Delta's window holds for its QR card
	linkFile string        // where the link that pairs now is kept, for the script that started the gateway; "": none
	life     time.Duration // how often that code is renewed

	mu      sync.Mutex
	peer    *webrtc.PeerConnection
	live    bool                 // peer is connected
	watched bool                 // the capture runs, for peer or a guest
	onPeer  func(connected bool) // starts and stops the capture
}

// newServer makes a server whose WebRTC stays on this machine, for the tests: see loopbackOnly.
func newServer(home string) (*server, error) {
	return newServerWith(home, loopbackOnly())
}

// loopbackOnly gathers candidates on the loopback addresses only and sends no mDNS. A binary that opens only those
// sockets makes Windows ask for no firewall rule: each test build has a new path, so each one used to ask.
func loopbackOnly() webrtc.SettingEngine {
	se := webrtc.SettingEngine{}
	se.SetIncludeLoopbackCandidate(true)
	se.SetIPFilter(func(ip net.IP) bool { return ip.IsLoopback() })
	se.SetICEMulticastDNSMode(ice.MulticastDNSModeDisabled)
	return se
}

func newServerWith(home string, se webrtc.SettingEngine) (*server, error) {
	p, err := loadPairing(home)
	if err != nil {
		return nil, err
	}
	v, err := webrtc.NewTrackLocalStaticRTP(webrtc.RTPCodecCapability{
		MimeType:    webrtc.MimeTypeH264,
		ClockRate:   90000,
		SDPFmtpLine: "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f",
	}, "video", "delta")
	if err != nil {
		return nil, err
	}
	api := webrtc.NewAPI(webrtc.WithSettingEngine(se))
	return &server{
		pair:     p,
		api:      api,
		video:    v,
		now:      time.Now,
		guestAPI: api,
		book:     newGuestBook(),
		ice:      func() ([]iceServer, error) { return nil, nil },
		guests:   map[*webrtc.PeerConnection]bool{},
		life:     renewEvery,
		onPeer:   func(bool) {},
		onGuests: func(bool) {},
	}, nil
}

// issueCode opens pairing for one device and has Delta's window keep the link for its QR card. The code is replaced
// as soon as a device uses it, and every s.life (100 s, inside the brain's 120 s), so the card always holds a QR that
// works while the gateway runs. The card opens only when the user asks for it.
func (s *server) issueCode() string {
	code := s.pair.issue(s.now())
	s.showLink(code)
	time.AfterFunc(s.life, func() { s.renew(code) })
	return code
}

// renew replaces code with a new one if Delta's window still holds it.
func (s *server) renew(code string) {
	s.linkMu.Lock()
	current := s.linkCode
	s.linkMu.Unlock()
	if current == code {
		s.issueCode()
	}
}

func (s *server) showLink(code string) {
	s.linkMu.Lock()
	defer s.linkMu.Unlock()
	link := "https://" + s.host + "/#code=" + code
	if s.linkFile != "" {
		if err := os.WriteFile(s.linkFile, []byte(link+"\n"), 0o600); err != nil {
			logf("the link file: %v", err)
		}
	}
	if s.brain == nil {
		return
	}
	s.linkCode = code
	if err := s.brain.link(link); err != nil {
		logf("the QR: %v", err)
	}
}

// static serves one of the embedded files under the page's policy.
func static(kind string, body []byte) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", kind)
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Referrer-Policy", "no-referrer")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("Content-Security-Policy", pagePolicy)
		w.Write(body)
	}
}

// checked lets through to h only the requests that name host, and the POSTs that come from host's pages or say
// nothing of where they come from.
func checked(host string, h http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Host != host {
			http.Error(w, "unknown host", http.StatusForbidden)
			return
		}
		if o := r.Header.Get("Origin"); r.Method != http.MethodGet && o != "" && o != "https://"+host && o != "http://"+host {
			http.Error(w, "foreign origin", http.StatusForbidden)
			return
		}
		h.ServeHTTP(w, r)
	})
}

func (s *server) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /{$}", static("text/html; charset=utf-8", pageHTML))
	mux.HandleFunc("GET /page.js", static("text/javascript; charset=utf-8", pageJS))
	mux.HandleFunc("GET /talk.js", static("text/javascript; charset=utf-8", talkJS))
	mux.HandleFunc("GET /page.css", static("text/css; charset=utf-8", pageCSS))
	mux.HandleFunc("GET /recorder.js", static("text/javascript; charset=utf-8", recorderJS))
	mux.HandleFunc("POST /pair", s.pairDevice)
	mux.HandleFunc("POST /offer", s.paired(s.offer))
	mux.HandleFunc("POST /hear", s.paired(s.hear))
	mux.HandleFunc("GET /said", s.paired(s.said))
	mux.HandleFunc("POST /invite", s.paired(s.invite))
	mux.HandleFunc("POST /guests/end", s.paired(s.endGuests))
	return checked(s.host, mux)
}

func (s *server) pairDevice(w http.ResponseWriter, r *http.Request) {
	var req struct{ Code string }
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<10)).Decode(&req); err != nil {
		http.Error(w, "a pairing code is needed", http.StatusBadRequest)
		return
	}
	cookie, err := s.pair.trade(req.Code, s.now())
	if err != nil {
		http.Error(w, err.Error(), http.StatusUnauthorized)
		return
	}
	s.issueCode() // the next device scans the window's QR again
	http.SetCookie(w, &http.Cookie{
		Name: deviceCookie, Value: cookie, Path: "/", MaxAge: int(deviceLife / time.Second),
		HttpOnly: true, Secure: true, SameSite: http.SameSiteStrictMode,
	})
	w.WriteHeader(http.StatusOK)
}

// paired lets only a paired device through to h.
func (s *server) paired(h http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if c, err := r.Cookie(deviceCookie); err != nil || !s.pair.known(c.Value) {
			http.Error(w, "this device is not paired", http.StatusUnauthorized)
			return
		}
		h(w, r)
	}
}

// hear passes what the device said, a 16 kHz WAV, to Delta's brain, and its answer back.
func (s *server) hear(w http.ResponseWriter, r *http.Request) {
	wav, err := io.ReadAll(io.LimitReader(r.Body, maxHear+1))
	if err != nil {
		http.Error(w, "the recording did not arrive", http.StatusBadRequest)
		return
	}
	if len(wav) > maxHear {
		http.Error(w, "30 seconds at most", http.StatusRequestEntityTooLarge)
		return
	}
	if s.brain == nil {
		http.Error(w, errNoBrain.Error(), http.StatusServiceUnavailable)
		return
	}
	resp, err := s.brain.hear(r.Context(), wav)
	if err != nil {
		http.Error(w, err.Error(), http.StatusServiceUnavailable)
		return
	}
	defer resp.Body.Close()
	w.Header().Set("Content-Type", resp.Header.Get("Content-Type"))
	w.WriteHeader(resp.StatusCode)
	io.Copy(w, io.LimitReader(resp.Body, 64<<10))
}

// said relays what Delta says, one SSE event per sentence with its WAV, for as long as the device listens.
func (s *server) said(w http.ResponseWriter, r *http.Request) {
	if s.brain == nil {
		http.Error(w, errNoBrain.Error(), http.StatusServiceUnavailable)
		return
	}
	resp, err := s.brain.said(r.Context(), r.URL.Query().Get("plays") == "own")
	if err != nil {
		comeBack(w)
		return
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		comeBack(w)
		return
	}
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(http.StatusOK)
	flusher, _ := w.(http.Flusher)
	buf := make([]byte, 32<<10)
	for {
		n, err := resp.Body.Read(buf)
		if n > 0 {
			if _, werr := w.Write(buf[:n]); werr != nil {
				return
			}
			if flusher != nil {
				flusher.Flush()
			}
		}
		if err != nil {
			return
		}
	}
}

// saidRetry is how soon the page asks /said again while the brain is away, as while it restarts.
const saidRetry = 2 * time.Second

// comeBack answers /said while the brain is away. An EventSource gives up for good on an error status, but asks again
// after an event stream that ends, in the retry it names: so the page hears Delta again once the brain is back.
func comeBack(w http.ResponseWriter) {
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(http.StatusOK)
	fmt.Fprintf(w, "retry: %d\n\n", saidRetry.Milliseconds())
}

func (s *server) offer(w http.ResponseWriter, r *http.Request) {
	var offer webrtc.SessionDescription
	if err := json.NewDecoder(io.LimitReader(r.Body, 64<<10)).Decode(&offer); err != nil || offer.Type != webrtc.SDPTypeOffer {
		http.Error(w, "an SDP offer is needed", http.StatusBadRequest)
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	if s.peer != nil {
		http.Error(w, "another device is connected", http.StatusConflict)
		return
	}
	pc, answer, err := s.answer(s.api, webrtc.Configuration{}, offer)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	s.peer = pc
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(answer)
}

// answer makes a connection that sends s.video, and the extra tracks, to offer's maker.
func (s *server) answer(api *webrtc.API, config webrtc.Configuration, offer webrtc.SessionDescription,
	extra ...webrtc.TrackLocal) (*webrtc.PeerConnection, *webrtc.SessionDescription, error) {
	pc, err := api.NewPeerConnection(config)
	if err != nil {
		return nil, nil, err
	}
	for _, track := range append([]webrtc.TrackLocal{s.video}, extra...) {
		sender, err := pc.AddTrack(track)
		if err != nil {
			pc.Close()
			return nil, nil, err
		}
		go func() { // RTCP has to be read for the interceptors to run
			buf := make([]byte, 1500)
			for {
				if _, _, err := sender.Read(buf); err != nil {
					return
				}
			}
		}()
	}
	// The capture starts once the device is connected, so its first frame, a keyframe, is not lost before DTLS is
	// up. Disconnected can still recover; Failed and Closed cannot.
	pc.OnConnectionStateChange(func(st webrtc.PeerConnectionState) {
		switch st {
		case webrtc.PeerConnectionStateConnected:
			s.connected(pc)
		case webrtc.PeerConnectionStateFailed, webrtc.PeerConnectionStateClosed:
			s.drop(pc)
		}
	})
	if err := pc.SetRemoteDescription(offer); err != nil {
		pc.Close()
		return nil, nil, err
	}
	a, err := pc.CreateAnswer(nil)
	if err != nil {
		pc.Close()
		return nil, nil, err
	}
	gathered := webrtc.GatheringCompletePromise(pc)
	if err := pc.SetLocalDescription(a); err != nil {
		pc.Close()
		return nil, nil, err
	}
	<-gathered
	return pc, pc.LocalDescription(), nil
}

func (s *server) connected(pc *webrtc.PeerConnection) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.peer == pc {
		s.live = true
	} else if _, ok := s.guests[pc]; ok {
		s.guests[pc] = true
	}
	s.watch()
}

// drop forgets pc once it is gone, so the next device or guest can connect.
func (s *server) drop(pc *webrtc.PeerConnection) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.peer == pc {
		s.peer = nil
		s.live = false
	}
	delete(s.guests, pc)
	s.watch()
	go pc.Close()
}

// watch runs the capture while the device or a guest is connected, and gives announce the number of guests at each
// change; with s.mu held.
func (s *server) watch() {
	n := 0
	for _, connected := range s.guests {
		if connected {
			n++
		}
	}
	if on := s.live || n > 0; on != s.watched {
		s.watched = on
		s.onPeer(on)
	}
	if hear := n > 0 && s.voice != nil && s.micOn; hear != s.heard {
		s.heard = hear
		s.onGuests(hear)
	}
	if (n != s.told || s.micOn != s.toldMic) && s.watching != nil {
		s.told, s.toldMic = n, s.micOn
		select { // only the latest number counts
		case <-s.watching:
		default:
		}
		s.watching <- n
	}
}

func logf(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "delta-remote: "+format+"\n", args...)
}

// close leaves the window's last link: the brain shows it as expired within 120 s, which is true once the gateway
// stops, and it takes no empty link.
func (s *server) close() {
	s.mu.Lock()
	pcs := []*webrtc.PeerConnection{s.peer}
	for pc := range s.guests {
		pcs = append(pcs, pc)
	}
	s.mu.Unlock()
	for _, pc := range pcs {
		if pc != nil {
			pc.Close()
		}
	}
}
