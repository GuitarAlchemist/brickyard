package main

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"sync"
	"time"

	"github.com/pion/webrtc/v4"
)

var (
	//go:embed page.html
	pageHTML []byte
	//go:embed page.js
	pageJS []byte
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
// link's #fragment, which the browser sends to no server.
type server struct {
	host  string // host:port the device uses, as it appears in Host
	pair  *pairing
	api   *webrtc.API
	video *webrtc.TrackLocalStaticRTP
	now   func() time.Time

	brain *brain // nil: no voice and no QR

	linkMu   sync.Mutex
	linkCode string // the code Delta's window shows, until it is used or expires

	mu     sync.Mutex
	peer   *webrtc.PeerConnection
	live   bool                 // the capture runs for peer
	onPeer func(connected bool) // starts and stops the capture
}

func newServer(home string, loopback bool) (*server, error) {
	se := webrtc.SettingEngine{}
	se.SetIncludeLoopbackCandidate(loopback)
	return newServerWith(home, se)
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
	return &server{
		pair:   p,
		api:    webrtc.NewAPI(webrtc.WithSettingEngine(se)),
		video:  v,
		now:    time.Now,
		onPeer: func(bool) {},
	}, nil
}

// issueCode opens pairing for one device, for two minutes, and has Delta's window show the link as a QR until the
// code is used or expires.
func (s *server) issueCode() string {
	code := s.pair.issue(s.now())
	s.showLink(code)
	time.AfterFunc(codeLife, func() { s.hideLink(code) })
	return code
}

func (s *server) showLink(code string) {
	if s.brain == nil {
		return
	}
	s.linkMu.Lock()
	defer s.linkMu.Unlock()
	s.linkCode = code
	if err := s.brain.link("https://" + s.host + "/#code=" + code); err != nil {
		logf("the QR: %v", err)
	}
}

// hideLink takes the QR away if it still shows code; "" hides whatever it shows.
func (s *server) hideLink(code string) {
	if s.brain == nil {
		return
	}
	s.linkMu.Lock()
	defer s.linkMu.Unlock()
	if s.linkCode == "" || (code != "" && code != s.linkCode) {
		return
	}
	s.linkCode = ""
	if err := s.brain.link(""); err != nil {
		logf("the QR: %v", err)
	}
}

func (s *server) handler() http.Handler {
	mux := http.NewServeMux()
	static := func(kind string, body []byte) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", kind)
			w.Header().Set("Cache-Control", "no-store")
			w.Header().Set("Referrer-Policy", "no-referrer")
			w.Header().Set("X-Content-Type-Options", "nosniff")
			w.Header().Set("Content-Security-Policy", pagePolicy)
			w.Write(body)
		}
	}
	mux.HandleFunc("GET /{$}", static("text/html; charset=utf-8", pageHTML))
	mux.HandleFunc("GET /page.js", static("text/javascript; charset=utf-8", pageJS))
	mux.HandleFunc("GET /page.css", static("text/css; charset=utf-8", pageCSS))
	mux.HandleFunc("GET /recorder.js", static("text/javascript; charset=utf-8", recorderJS))
	mux.HandleFunc("POST /pair", s.pairDevice)
	mux.HandleFunc("POST /offer", s.paired(s.offer))
	mux.HandleFunc("POST /hear", s.paired(s.hear))
	mux.HandleFunc("GET /said", s.paired(s.said))
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Host != s.host {
			http.Error(w, "unknown host", http.StatusForbidden)
			return
		}
		if o := r.Header.Get("Origin"); r.Method != http.MethodGet && o != "" && o != "https://"+s.host && o != "http://"+s.host {
			http.Error(w, "foreign origin", http.StatusForbidden)
			return
		}
		mux.ServeHTTP(w, r)
	})
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
	s.hideLink("")
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
	resp, err := s.brain.said(r.Context())
	if err != nil {
		http.Error(w, err.Error(), http.StatusServiceUnavailable)
		return
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		http.Error(w, "Delta's brain answered "+resp.Status, http.StatusBadGateway)
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
	pc, answer, err := s.answer(offer)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	s.peer = pc
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(answer)
}

func (s *server) answer(offer webrtc.SessionDescription) (*webrtc.PeerConnection, *webrtc.SessionDescription, error) {
	pc, err := s.api.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		return nil, nil, err
	}
	sender, err := pc.AddTrack(s.video)
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
	if s.peer == pc && !s.live {
		s.live = true
		s.onPeer(true)
	}
}

// drop forgets pc once it is gone, so the next device can connect.
func (s *server) drop(pc *webrtc.PeerConnection) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.peer == pc {
		s.peer = nil
		if s.live {
			s.live = false
			s.onPeer(false)
		}
	}
	go pc.Close()
}

func logf(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "delta-remote: "+format+"\n", args...)
}

func (s *server) close() {
	s.hideLink("")
	s.mu.Lock()
	pc := s.peer
	s.mu.Unlock()
	if pc != nil {
		pc.Close()
	}
}
