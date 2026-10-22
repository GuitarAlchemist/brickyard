package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/pion/rtp"
	"github.com/pion/webrtc/v4"
)

type testGateway struct {
	s   *server
	ts  *httptest.Server
	now time.Time
}

func newTestGateway(t *testing.T, home string) *testGateway {
	t.Helper()
	g := &testGateway{now: time.Unix(1_800_000_000, 0)}
	s, err := newServer(home, true)
	if err != nil {
		t.Fatal(err)
	}
	s.now = func() time.Time { return g.now }
	g.ts = httptest.NewServer(nil)
	s.host = g.ts.Listener.Addr().String()
	g.ts.Config.Handler = s.handler()
	g.s = s
	t.Cleanup(func() { g.ts.Close(); s.close() })
	return g
}

func (g *testGateway) do(t *testing.T, path string, body []byte, cookie, origin string) *http.Response {
	t.Helper()
	req, _ := http.NewRequest(http.MethodPost, g.ts.URL+path, bytes.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	if cookie != "" {
		req.Header.Set("Cookie", cookie)
	}
	if origin != "" {
		req.Header.Set("Origin", origin)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	return resp
}

// pair trades code for the device cookie the page would then hold.
func (g *testGateway) pair(t *testing.T, code string) (*http.Response, string) {
	t.Helper()
	body, _ := json.Marshal(map[string]string{"code": code})
	resp := g.do(t, "/pair", body, "", g.ts.URL)
	for _, c := range resp.Cookies() {
		if c.Name == deviceCookie {
			return resp, c.Name + "=" + c.Value
		}
	}
	return resp, ""
}

// offer makes a receiving peer like the page's and returns it with the offer the page would post.
func offer(t *testing.T) (*webrtc.PeerConnection, []byte) {
	t.Helper()
	se := webrtc.SettingEngine{}
	se.SetIncludeLoopbackCandidate(true)
	pc, err := webrtc.NewAPI(webrtc.WithSettingEngine(se)).NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { pc.Close() })
	if _, err := pc.AddTransceiverFromKind(webrtc.RTPCodecTypeVideo,
		webrtc.RTPTransceiverInit{Direction: webrtc.RTPTransceiverDirectionRecvonly}); err != nil {
		t.Fatal(err)
	}
	o, err := pc.CreateOffer(nil)
	if err != nil {
		t.Fatal(err)
	}
	gathered := webrtc.GatheringCompletePromise(pc)
	if err := pc.SetLocalDescription(o); err != nil {
		t.Fatal(err)
	}
	<-gathered
	body, _ := json.Marshal(pc.LocalDescription())
	return pc, body
}

func TestAPairingCodeIsTradedOnceForADeviceCookie(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	code := g.s.issueCode()
	resp, cookie := g.pair(t, code)
	if resp.StatusCode != http.StatusOK || cookie == "" {
		t.Fatalf("pairing: status %d, cookie %q", resp.StatusCode, cookie)
	}
	set := resp.Header.Get("Set-Cookie")
	for _, attr := range []string{"HttpOnly", "Secure", "SameSite=Strict"} {
		if !strings.Contains(set, attr) {
			t.Errorf("the device cookie lacks %s: %s", attr, set)
		}
	}
	if resp, _ := g.pair(t, code); resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("the same code twice: status %d, want 401", resp.StatusCode)
	}
}

func TestACodeOlderThanTwoMinutesIsRefused(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	code := g.s.issueCode()
	g.now = g.now.Add(2*time.Minute + time.Second)
	if resp, _ := g.pair(t, code); resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("status %d, want 401", resp.StatusCode)
	}
}

func TestFiveWrongCodesBurnTheCode(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	code := g.s.issueCode()
	for range 5 {
		g.pair(t, "WRONG-CODE")
	}
	if resp, _ := g.pair(t, code); resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("the right code after five wrong ones: status %d, want 401", resp.StatusCode)
	}
}

func TestAnOfferNeedsAPairedDevice(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	_, body := offer(t)
	for _, cookie := range []string{"", deviceCookie + "=forged"} {
		if resp := g.do(t, "/offer", body, cookie, g.ts.URL); resp.StatusCode != http.StatusUnauthorized {
			t.Errorf("cookie %q: status %d, want 401", cookie, resp.StatusCode)
		}
	}
	_, cookie := g.pair(t, g.s.issueCode())
	if resp := g.do(t, "/offer", body, cookie, g.ts.URL); resp.StatusCode != http.StatusOK {
		t.Errorf("paired device: status %d, want 200", resp.StatusCode)
	}
}

func TestAPairedDeviceOutlivesARestartUntilDevicesAreForgotten(t *testing.T) {
	home := t.TempDir()
	g := newTestGateway(t, home)
	_, cookie := g.pair(t, g.s.issueCode())
	_, body := offer(t)

	again := newTestGateway(t, home)
	if resp := again.do(t, "/offer", body, cookie, again.ts.URL); resp.StatusCode != http.StatusOK {
		t.Fatalf("after a restart: status %d, want 200", resp.StatusCode)
	}
	if err := forgetDevices(home); err != nil {
		t.Fatal(err)
	}
	_, body = offer(t)
	forgot := newTestGateway(t, home)
	if resp := forgot.do(t, "/offer", body, cookie, forgot.ts.URL); resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("after forgetting devices: status %d, want 401", resp.StatusCode)
	}
}

func TestRequestsFromAnotherOriginOrHostAreRefused(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	body, _ := json.Marshal(map[string]string{"code": g.s.issueCode()})
	if resp := g.do(t, "/pair", body, "", "https://evil.example"); resp.StatusCode != http.StatusForbidden {
		t.Errorf("foreign Origin: status %d, want 403", resp.StatusCode)
	}
	// DNS rebinding: a name the gateway does not serve, pointed at its address.
	for _, r := range []struct{ method, path string }{
		{http.MethodGet, "/"}, {http.MethodGet, "/page.js"}, {http.MethodPost, "/pair"}, {http.MethodPost, "/offer"},
	} {
		method, path := r.method, r.path
		req, _ := http.NewRequest(method, g.ts.URL+path, bytes.NewReader(body))
		req.Host = "rebound.example"
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		if resp.StatusCode != http.StatusForbidden {
			t.Errorf("%s %s with a foreign Host: status %d, want 403", method, path, resp.StatusCode)
		}
	}
}

func TestThePageRunsOnlyItsOwnScriptAndCarriesNoSecret(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	code := g.s.issueCode()
	resp, err := http.Get(g.ts.URL + "/")
	if err != nil {
		t.Fatal(err)
	}
	var page bytes.Buffer
	page.ReadFrom(resp.Body)
	if resp.StatusCode != http.StatusOK || !strings.Contains(page.String(), "<video") {
		t.Fatalf("status %d, page without a video element", resp.StatusCode)
	}
	if strings.Contains(page.String(), code) {
		t.Error("the page carries the pairing code; it comes from the link's #fragment, which no server sees")
	}
	csp := resp.Header.Get("Content-Security-Policy")
	if !strings.Contains(csp, "script-src 'self'") || strings.Contains(csp, "unsafe") {
		t.Errorf("Content-Security-Policy %q: want script-src 'self' and nothing unsafe", csp)
	}
	if strings.Contains(page.String(), "<script>") {
		t.Error("an inline script: the policy would block it, keep the script in page.js")
	}
}

func TestEveryFileThePageNamesIsServed(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	names := regexp.MustCompile(`(?:src|href)="([^"]+)"`).FindAllSubmatch(pageHTML, -1)
	if len(names) < 2 {
		t.Fatalf("the page names %d files, want its script and its style", len(names))
	}
	for _, m := range names {
		resp, err := http.Get(g.ts.URL + "/" + string(m[1]))
		if err != nil {
			t.Fatal(err)
		}
		if resp.StatusCode != http.StatusOK {
			t.Errorf("%s: status %d, want 200", m[1], resp.StatusCode)
		}
	}
}

func TestTheGatewayServesOneDeviceAtATime(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	_, cookie := g.pair(t, g.s.issueCode())
	_, first := offer(t)
	if resp := g.do(t, "/offer", first, cookie, g.ts.URL); resp.StatusCode != http.StatusOK {
		t.Fatalf("first device: status %d, want 200", resp.StatusCode)
	}
	_, second := offer(t)
	if resp := g.do(t, "/offer", second, cookie, g.ts.URL); resp.StatusCode != http.StatusConflict {
		t.Errorf("second device: status %d, want 409", resp.StatusCode)
	}
}

// ffmpeg cannot be asked for a keyframe, and packets sent before the device's DTLS is up are lost: the capture starts
// once the device is connected, so its first frame is the keyframe the device sees.
func TestTheCaptureStartsOnlyOnceTheDeviceIsConnected(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	calls := make(chan bool, 4)
	g.s.onPeer = func(on bool) { calls <- on }
	_, cookie := g.pair(t, g.s.issueCode())
	pc, body := offer(t)
	resp := g.do(t, "/offer", body, cookie, g.ts.URL)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status %d, want 200", resp.StatusCode)
	}
	select {
	case on := <-calls:
		t.Fatalf("capture switched %v before the device connected", on)
	case <-time.After(500 * time.Millisecond):
	}
	var answer webrtc.SessionDescription
	json.NewDecoder(resp.Body).Decode(&answer)
	if err := pc.SetRemoteDescription(answer); err != nil {
		t.Fatal(err)
	}
	select {
	case on := <-calls:
		if !on {
			t.Fatal("capture stopped instead of starting")
		}
	case <-time.After(10 * time.Second):
		t.Fatal("the capture never started")
	}
	pc.Close()
	select {
	case on := <-calls:
		if on {
			t.Fatal("capture started again instead of stopping")
		}
	case <-time.After(40 * time.Second):
		t.Fatal("the capture never stopped after the device left")
	}
}

func TestADeviceReceivesTheVideoTheGatewayIsFed(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	_, cookie := g.pair(t, g.s.issueCode())
	pc, body := offer(t)
	got := make(chan *rtp.Packet, 1)
	pc.OnTrack(func(tr *webrtc.TrackRemote, _ *webrtc.RTPReceiver) {
		if p, _, err := tr.ReadRTP(); err == nil {
			got <- p
		}
	})
	resp := g.do(t, "/offer", body, cookie, g.ts.URL)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status %d, want 200", resp.StatusCode)
	}
	var answer webrtc.SessionDescription
	if err := json.NewDecoder(resp.Body).Decode(&answer); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(answer.SDP, "H264") {
		t.Fatalf("the answer offers no H.264:\n%s", answer.SDP)
	}
	if err := pc.SetRemoteDescription(answer); err != nil {
		t.Fatal(err)
	}
	// What ffmpeg's RTP output carries: one IDR slice NAL per packet is enough for the track to open.
	deadline := time.After(10 * time.Second)
	for seq := uint16(0); ; seq++ {
		select {
		case p := <-got:
			if len(p.Payload) == 0 {
				t.Fatal("empty payload")
			}
			return
		case <-deadline:
			t.Fatal("no video reached the device")
		case <-time.After(20 * time.Millisecond):
			g.s.video.WriteRTP(&rtp.Packet{
				Header:  rtp.Header{Version: 2, PayloadType: 96, SequenceNumber: seq, Timestamp: uint32(seq) * 3000, Marker: true},
				Payload: []byte{0x65, 0x88, 0x84, 0x00},
			})
		}
	}
}
