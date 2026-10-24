package main

import (
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/pion/rtp"
	"github.com/pion/webrtc/v4"
)

// withGuests opens g's guests' entry at its own address, as the tunnel reaches it.
func withGuests(t *testing.T, g *testGateway) *httptest.Server {
	t.Helper()
	ts := httptest.NewServer(nil)
	g.s.guestHost = ts.Listener.Addr().String()
	ts.Config.Handler = g.s.guestHandler()
	t.Cleanup(ts.Close)
	return ts
}

func request(t *testing.T, method, url string, body []byte, cookie string) *http.Response {
	t.Helper()
	req, _ := http.NewRequest(method, url, strings.NewReader(string(body)))
	req.Header.Set("Content-Type", "application/json")
	if cookie != "" {
		req.Header.Set("Cookie", cookie)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	return resp
}

// invite has the paired device holding cookie ask for an invitation, and returns its code.
func invite(t *testing.T, g *testGateway, cookie string) string {
	t.Helper()
	resp := g.do(t, "/invite", nil, cookie, g.ts.URL)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("invite: status %d, want 200", resp.StatusCode)
	}
	var got struct{ Link string }
	json.NewDecoder(resp.Body).Decode(&got)
	m := regexp.MustCompile(`^https://` + regexp.QuoteMeta(g.s.guestHost) + `/#invite=([0-9a-f]{32})$`).FindStringSubmatch(got.Link)
	if m == nil {
		t.Fatalf("the invitation's link is %q, want https://%s/#invite=<32 hex digits>", got.Link, g.s.guestHost)
	}
	return m[1]
}

// join trades code at the guests' entry for the cookie a guest's page would then hold.
func join(t *testing.T, guests *httptest.Server, code string) (*http.Response, string) {
	t.Helper()
	body, _ := json.Marshal(map[string]string{"invite": code})
	resp := request(t, http.MethodPost, guests.URL+"/join", body, "")
	for _, c := range resp.Cookies() {
		if c.Name == guestCookie {
			return resp, c.Name + "=" + c.Value
		}
	}
	return resp, ""
}

// The guests' entry is what the internet reaches through the tunnel: a guest watches and hears Delta's voice (/said,
// see TestAGuestHearsDeltasVoiceOnly), and nothing else is there.
func TestTheGuestsEntryHasNothingButWatching(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	guests := withGuests(t, g)
	for _, path := range []string{"/hear", "/pair", "/link", "/invite", "/guests/end", "/guests/mic", "/page.js", "/talk.js", "/recorder.js"} {
		for _, method := range []string{http.MethodGet, http.MethodPost} {
			if resp := request(t, method, guests.URL+path, nil, ""); resp.StatusCode != http.StatusNotFound && resp.StatusCode != http.StatusMethodNotAllowed {
				t.Errorf("%s %s at the guests' entry: status %d, want none there", method, path, resp.StatusCode)
			}
		}
	}
	names := regexp.MustCompile(`(?:src|href)="([^"]+)"`).FindAllSubmatch(guestHTML, -1)
	if len(names) != 2 {
		t.Fatalf("the guest's page names %d files, want its script and its style", len(names))
	}
	for _, m := range names {
		if resp := request(t, http.MethodGet, guests.URL+"/"+string(m[1]), nil, ""); resp.StatusCode != http.StatusOK {
			t.Errorf("%s: status %d, want 200", m[1], resp.StatusCode)
		}
	}
	if resp := request(t, http.MethodGet, guests.URL+"/", nil, ""); resp.Header.Get("Content-Security-Policy") != pagePolicy {
		t.Errorf("the guest's page has the policy %q, want the page's", resp.Header.Get("Content-Security-Policy"))
	}
}

// An invitation serves for its whole hour: the guest opens the link again in another browser, or passes it on, and
// each trade gives a cookie that lasts two hours from its own trade.
func TestAnInvitationServesForItsHourAndEachCookieForTwoHours(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	guests := withGuests(t, g)
	_, device := g.pair(t, g.s.issueCode())
	code := invite(t, g, device)
	resp, first := join(t, guests, code)
	if resp.StatusCode != http.StatusOK || first == "" {
		t.Fatalf("join: status %d and cookie %q, want 200 and a guest's cookie", resp.StatusCode, first)
	}
	g.now = g.now.Add(inviteLife - time.Minute)
	resp, second := join(t, guests, code)
	if resp.StatusCode != http.StatusOK || second == "" || second == first {
		t.Fatalf("the same invitation 59 minutes on: status %d, want 200 and a cookie of its own", resp.StatusCode)
	}
	g.now = g.now.Add(2 * time.Minute)
	if resp, late := join(t, guests, code); resp.StatusCode != http.StatusUnauthorized || late != "" {
		t.Errorf("the invitation after its hour: status %d, want 401 and no cookie", resp.StatusCode)
	}
	if resp := request(t, http.MethodGet, guests.URL+"/ice", nil, first); resp.StatusCode != http.StatusOK {
		t.Errorf("the first cookie an hour on: status %d, want 200", resp.StatusCode)
	}
	g.now = g.now.Add(guestLife - inviteLife)
	if resp := request(t, http.MethodGet, guests.URL+"/ice", nil, first); resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("the first cookie two hours on: status %d, want 401", resp.StatusCode)
	}
	if resp := request(t, http.MethodGet, guests.URL+"/ice", nil, second); resp.StatusCode != http.StatusOK {
		t.Errorf("the second cookie, traded later: status %d, want 200", resp.StatusCode)
	}
}

func TestAnInvitationUnusedForAnHourIsVoid(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	guests := withGuests(t, g)
	_, device := g.pair(t, g.s.issueCode())
	code := invite(t, g, device)
	g.now = g.now.Add(inviteLife + time.Second)
	if resp, _ := join(t, guests, code); resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("join after an hour: status %d, want 401", resp.StatusCode)
	}
}

func TestOnlyAPairedDeviceInvitesAndOnlyWithGuestsOn(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	_, device := g.pair(t, g.s.issueCode())
	if resp := g.do(t, "/invite", nil, device, g.ts.URL); resp.StatusCode != http.StatusNotFound {
		t.Errorf("guests off: status %d, want 404", resp.StatusCode)
	}
	withGuests(t, g)
	if resp := g.do(t, "/invite", nil, "", g.ts.URL); resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("no device: status %d, want 401", resp.StatusCode)
	}
	if resp := g.do(t, "/guests/end", nil, "", g.ts.URL); resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("ending guests with no device: status %d, want 401", resp.StatusCode)
	}
}

// A guest's cookie opens nothing of the device's, and a device's cookie is no guest's: each door has its own key.
func TestEachCookieOpensItsOwnDoorOnly(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	guests := withGuests(t, g)
	_, device := g.pair(t, g.s.issueCode())
	_, guest := join(t, guests, invite(t, g, device))
	guestValue := strings.TrimPrefix(guest, guestCookie+"=")
	for _, c := range []string{guest, deviceCookie + "=" + guestValue} {
		for _, path := range []string{"/offer", "/hear", "/invite", "/guests/end"} {
			if resp := g.do(t, path, nil, c, g.ts.URL); resp.StatusCode != http.StatusUnauthorized {
				t.Errorf("%s with the guest's cookie as %q: status %d, want 401", path, c[:strings.Index(c, "=")], resp.StatusCode)
			}
		}
	}
	deviceValue := strings.TrimPrefix(device, deviceCookie+"=")
	_, body := offer(t)
	for _, c := range []string{device, guestCookie + "=" + deviceValue} {
		if resp := request(t, http.MethodGet, guests.URL+"/ice", nil, c); resp.StatusCode != http.StatusUnauthorized {
			t.Errorf("/ice with the device's cookie as %q: status %d, want 401", c[:strings.Index(c, "=")], resp.StatusCode)
		}
		if resp := request(t, http.MethodPost, guests.URL+"/offer", body, c); resp.StatusCode != http.StatusUnauthorized {
			t.Errorf("/offer with the device's cookie as %q: status %d, want 401", c[:strings.Index(c, "=")], resp.StatusCode)
		}
	}
}

func TestEachEntryAnswersItsOwnHostOnly(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	guests := withGuests(t, g)
	for _, c := range []struct{ url, host string }{{g.ts.URL, g.s.guestHost}, {guests.URL, g.s.host}} {
		req, _ := http.NewRequest(http.MethodGet, c.url+"/", nil)
		req.Host = c.host
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		if resp.StatusCode != http.StatusForbidden {
			t.Errorf("%s asked for %s: status %d, want 403", c.url, c.host, resp.StatusCode)
		}
	}
}

// connect has pc take the answer resp holds.
func connect(t *testing.T, pc *webrtc.PeerConnection, resp *http.Response) {
	t.Helper()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("offer: status %d, want 200", resp.StatusCode)
	}
	var answer webrtc.SessionDescription
	if err := json.NewDecoder(resp.Body).Decode(&answer); err != nil {
		t.Fatal(err)
	}
	if err := pc.SetRemoteDescription(answer); err != nil {
		t.Fatal(err)
	}
}

// receives is closed once pc gets a video packet.
func receives(pc *webrtc.PeerConnection) chan struct{} {
	got := make(chan struct{})
	pc.OnTrack(func(tr *webrtc.TrackRemote, _ *webrtc.RTPReceiver) {
		if _, _, err := tr.ReadRTP(); err == nil {
			close(got)
		}
	})
	return got
}

// told waits for announce to be given want; the guests a single change lets in or out go one by one.
func told(t *testing.T, watching chan int, want int) {
	t.Helper()
	deadline := time.After(10 * time.Second)
	for {
		select {
		case n := <-watching:
			if n == want {
				return
			}
		case <-deadline:
			t.Fatalf("the brain never hears that %d guests watch", want)
		}
	}
}

// Two guests watch beside the paired device, a third waits, and ending the guests leaves the device watching.
func TestTwoGuestsWatchBesideTheDevice(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	guests := withGuests(t, g)
	g.s.watching = make(chan int, 1)
	calls := make(chan bool, 4)
	g.s.onPeer = func(on bool) { calls <- on }
	_, device := g.pair(t, g.s.issueCode())

	pc, body := offer(t)
	waiting := []chan struct{}{receives(pc)}
	connect(t, pc, g.do(t, "/offer", body, device, g.ts.URL))
	select {
	case on := <-calls:
		if !on {
			t.Fatal("the capture stopped instead of starting")
		}
	case <-time.After(10 * time.Second):
		t.Fatal("the capture never started for the device")
	}

	var cookies []string
	for i := 1; i <= maxGuests; i++ {
		_, guest := join(t, guests, invite(t, g, device))
		cookies = append(cookies, guest)
		gpc, gbody := offer(t)
		waiting = append(waiting, receives(gpc))
		connect(t, gpc, request(t, http.MethodPost, guests.URL+"/offer", gbody, guest))
		told(t, g.s.watching, i)
	}
	_, third := join(t, guests, invite(t, g, device))
	cookies = append(cookies, third)
	_, tbody := offer(t)
	if resp := request(t, http.MethodPost, guests.URL+"/offer", tbody, third); resp.StatusCode != http.StatusConflict {
		t.Errorf("a third guest: status %d, want 409", resp.StatusCode)
	}

	deadline := time.After(10 * time.Second)
	for seq := uint16(0); len(waiting) > 0; seq++ {
		select {
		case <-waiting[0]:
			waiting = waiting[1:]
		case <-deadline:
			t.Fatalf("%d of the device and its guests got no video", len(waiting))
		case <-time.After(20 * time.Millisecond):
			g.s.video.WriteRTP(&rtp.Packet{
				Header:  rtp.Header{Version: 2, PayloadType: 96, SequenceNumber: seq, Timestamp: uint32(seq) * 3000, Marker: true},
				Payload: []byte{0x65, 0x88, 0x84, 0x00},
			})
		}
	}

	if resp := g.do(t, "/guests/end", nil, device, g.ts.URL); resp.StatusCode != http.StatusNoContent {
		t.Fatalf("ending the guests: status %d, want 204", resp.StatusCode)
	}
	told(t, g.s.watching, 0)
	select {
	case on := <-calls:
		t.Errorf("the capture switched %v while the device still watches", on)
	case <-time.After(300 * time.Millisecond):
	}
	for _, c := range cookies {
		if resp := request(t, http.MethodGet, guests.URL+"/ice", nil, c); resp.StatusCode != http.StatusUnauthorized {
			t.Errorf("a guest's cookie after the end: status %d, want 401", resp.StatusCode)
		}
	}
}

func TestTheBrainHearsHowManyGuestsWatch(t *testing.T) {
	b := newFakeBrain(t)
	g, _ := voiceGateway(t, b)
	for _, c := range []struct {
		n   int
		mic bool
	}{{1, false}, {2, true}, {0, false}} {
		if err := g.s.brain.guests(c.n, c.mic); err != nil {
			t.Fatal(err)
		}
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	if !slices.Equal(b.seen, []int{1, 2, 0}) || !slices.Equal(b.mics, []bool{false, true, false}) || len(b.bad) > 0 {
		t.Errorf("the brain was told %v and %v and refused %v, want 1, 2 and 0, the microphone on with 2", b.seen, b.mics, b.bad)
	}
}

// The guest's browser gets every way to the relay but port 53, which browsers block; the gateway takes the relay over
// UDP alone, and only through it, so that a guest never learns this network's address.
func TestTheRelaysAddresses(t *testing.T) {
	servers := []iceServer{
		{URLs: []string{"stun:stun.cloudflare.com:3478", "stun:stun.cloudflare.com:53"}},
		{URLs: []string{"turn:turn.cloudflare.com:3478?transport=udp", "turn:turn.cloudflare.com:53?transport=udp",
			"turn:turn.cloudflare.com:3478?transport=tcp", "turns:turn.cloudflare.com:443?transport=tcp"}, Username: "u", Credential: "c"},
	}
	browser := forBrowser(servers)
	if len(browser) != 2 || !slices.Equal(browser[0].URLs, []string{"stun:stun.cloudflare.com:3478"}) ||
		!slices.Equal(browser[1].URLs, []string{"turn:turn.cloudflare.com:3478?transport=udp",
			"turn:turn.cloudflare.com:3478?transport=tcp", "turns:turn.cloudflare.com:443?transport=tcp"}) || browser[1].Credential != "c" {
		t.Errorf("the browser gets %+v", browser)
	}
	config := guestConfig(servers)
	if len(config.ICEServers) != 1 || !slices.Equal(config.ICEServers[0].URLs, []string{"turn:turn.cloudflare.com:3478?transport=udp"}) ||
		config.ICEServers[0].Username != "u" || config.ICETransportPolicy != webrtc.ICETransportPolicyRelay {
		t.Errorf("the gateway takes %+v", config)
	}
	if config := guestConfig(nil); config.ICETransportPolicy == webrtc.ICETransportPolicyRelay {
		t.Error("with no relay, the gateway would gather nothing")
	}
}

// windowEntry opens g's entry for Delta's window, wired to the brain b, at its own address.
func windowEntry(t *testing.T, g *testGateway, b *fakeBrain, dir string) *httptest.Server {
	t.Helper()
	secretPath := filepath.Join(dir, "remote.secret")
	if err := os.WriteFile(secretPath, []byte(secret+"\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	g.s.brain = newBrain(b.URL, secretPath)
	ts := httptest.NewServer(nil)
	g.s.localHost = ts.Listener.Addr().String()
	ts.Config.Handler = g.s.localHandler()
	t.Cleanup(ts.Close)
	return ts
}

func fromWindow(t *testing.T, url string, headers map[string]string) *http.Response {
	t.Helper()
	req, _ := http.NewRequest(http.MethodPost, url, nil)
	for k, v := range headers {
		req.Header.Set(k, v)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	return resp
}

// Delta's window invites through its own entry, which only the brain's secret opens, and no page.
func TestDeltasWindowInvitesWithTheBrainsSecretOnly(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	guests := withGuests(t, g)
	local := windowEntry(t, g, newFakeBrain(t), t.TempDir())
	brain := map[string]string{"X-Delta-Hook": "1", "X-Delta-Remote": secret}
	for name, headers := range map[string]map[string]string{
		"no header":       {},
		"no secret":       {"X-Delta-Hook": "1"},
		"a wrong secret":  {"X-Delta-Hook": "1", "X-Delta-Remote": "nope"},
		"no hook header":  {"X-Delta-Remote": secret},
		"a page's Origin": {"X-Delta-Hook": "1", "X-Delta-Remote": secret, "Origin": local.URL},
	} {
		if resp := fromWindow(t, local.URL+"/invite", headers); resp.StatusCode != http.StatusForbidden {
			t.Errorf("an invitation with %s: status %d, want 403", name, resp.StatusCode)
		}
	}
	resp := fromWindow(t, local.URL+"/invite", brain)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("the brain's invitation: status %d, want 200", resp.StatusCode)
	}
	var got struct{ Link, Expires string }
	json.NewDecoder(resp.Body).Decode(&got)
	code := got.Link[strings.LastIndex(got.Link, "=")+1:]
	if expires, err := time.Parse(time.RFC3339, got.Expires); err != nil || !expires.Equal(g.now.Add(inviteLife)) {
		t.Errorf("the invitation expires %q, want an hour on", got.Expires)
	}
	if resp, cookie := join(t, guests, code); resp.StatusCode != http.StatusOK || cookie == "" {
		t.Errorf("the window's invitation at the guests' entry: status %d, want 200 and a cookie", resp.StatusCode)
	}
	if resp := fromWindow(t, local.URL+"/guests/end", brain); resp.StatusCode != http.StatusNoContent {
		t.Errorf("ending the guests from the window: status %d, want 204", resp.StatusCode)
	}
	if resp, _ := join(t, guests, code); resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("the invitation after the end: status %d, want 401", resp.StatusCode)
	}
}

// The window's QR card would show the invitation to the guests watching that window.
func TestDeltasWindowInvitesOnlyWhileNoGuestWatches(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	withGuests(t, g)
	local := windowEntry(t, g, newFakeBrain(t), t.TempDir())
	brain := map[string]string{"X-Delta-Hook": "1", "X-Delta-Remote": secret}
	watcher := &webrtc.PeerConnection{}
	g.s.mu.Lock()
	g.s.guests[watcher] = true
	g.s.mu.Unlock()
	resp := fromWindow(t, local.URL+"/invite", brain)
	g.s.mu.Lock()
	delete(g.s.guests, watcher)
	g.s.mu.Unlock()
	if resp.StatusCode != http.StatusConflict {
		t.Errorf("an invitation while a guest watches: status %d, want 409", resp.StatusCode)
	}
	if resp := fromWindow(t, local.URL+"/invite", brain); resp.StatusCode != http.StatusOK {
		t.Errorf("an invitation once nobody watches: status %d, want 200", resp.StatusCode)
	}
}

func TestTheLogNamesCandidateTypesOnly(t *testing.T) {
	sdp := "v=0\r\na=candidate:1 1 udp 2122260223 192.168.1.5 50000 typ host generation 0\r\n" +
		"a=candidate:2 1 udp 1686052607 203.0.113.7 50001 typ srflx raddr 192.168.1.5 rport 50000\r\n" +
		"a=candidate:3 1 udp 41885439 104.30.1.1 3478 typ relay raddr 203.0.113.7 rport 50001\r\n" +
		"a=candidate:4 1 udp 41885439 104.30.1.2 3479 typ relay raddr 203.0.113.7 rport 50002\r\n"
	if got := candidates(sdp); got != "1 host, 1 srflx, 2 relay" {
		t.Errorf("candidates: %q", got)
	}
	if got := candidates("v=0\r\n"); got != "no candidate" {
		t.Errorf("candidates of none: %q", got)
	}
}

// Cloudflare refuses to relay to its own relay's addresses, and pion then drops the relay altogether: the gateway
// takes a guest's offer without the browser's relay candidates.
func TestTheGatewayLeavesOutTheBrowsersRelay(t *testing.T) {
	sdp := "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\n" +
		"a=candidate:1 1 udp 2122260223 4c1f.local 50000 typ host generation 0\r\n" +
		"a=candidate:2 1 udp 1686052607 203.0.113.7 50001 typ srflx raddr 0.0.0.0 rport 0\r\n" +
		"a=candidate:3 1 udp 41885439 104.30.1.1 3478 typ relay raddr 203.0.113.7 rport 50001\r\n" +
		"a=candidate:4 1 tcp 8331263 104.30.1.2 443 typ relay raddr 203.0.113.7 rport 50002 tcptype passive\r\n" +
		"a=end-of-candidates\r\n"
	got := withoutRelay(sdp)
	if candidates(got) != "1 host, 1 srflx" || !strings.HasPrefix(got, "v=0\r\nm=video 9") ||
		!strings.HasSuffix(got, "typ srflx raddr 0.0.0.0 rport 0\r\na=end-of-candidates\r\n") {
		t.Errorf("without the relay:\n%s", got)
	}
}

// A guest hears Delta's voice, and nothing else of what the brain streams: neither the sentences' text nor anyone's
// words. The brain is asked as by a page whose sound does not run, so the computer still says everything.
func TestAGuestHearsDeltasVoiceOnly(t *testing.T) {
	b := newFakeBrain(t)
	b.said = []string{
		`{"text":"Bonjour Eric","wav_b64":"UklGRg==","by":"answer","utterance_id":"7"}`,
		`{"text":"what the user said","by":"heard"}`,
		`not json`,
	}
	g, device := voiceGateway(t, b)
	guests := withGuests(t, g)
	_, guest := join(t, guests, invite(t, g, device))
	if resp := request(t, http.MethodGet, guests.URL+"/said", nil, ""); resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("Delta's voice with no guest's cookie: status %d, want 401", resp.StatusCode)
	}
	resp := request(t, http.MethodGet, guests.URL+"/said", nil, guest)
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusOK || string(body) != "data: {\"wav_b64\":\"UklGRg==\"}\n\n" {
		t.Errorf("the guest's stream: status %d, %q", resp.StatusCode, body)
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	if !slices.Equal(b.plays, []string{""}) {
		t.Errorf("the brain was asked with X-Delta-Plays %q, want none", b.plays)
	}
}

// The microphone reaches guests only once the window's switch lets it, and only while a guest watches; the brain
// hears the switch's state.
func TestGuestsHearTheMicrophoneOnlyWithTheSwitchOn(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	withGuests(t, g)
	local := windowEntry(t, g, newFakeBrain(t), t.TempDir())
	brain := map[string]string{"X-Delta-Hook": "1", "X-Delta-Remote": secret}
	switchTo := func(body string) *http.Response {
		req, _ := http.NewRequest(http.MethodPost, local.URL+"/guests/mic", strings.NewReader(body))
		for k, v := range brain {
			req.Header.Set(k, v)
		}
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		return resp
	}
	if resp := switchTo(`{"on": true}`); resp.StatusCode != http.StatusNotFound {
		t.Errorf("the switch with no -mic: status %d, want 404", resp.StatusCode)
	}
	voice, err := webrtc.NewTrackLocalStaticRTP(webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeOpus, ClockRate: 48000, Channels: 2}, "voice", "delta")
	if err != nil {
		t.Fatal(err)
	}
	g.s.voice = voice
	g.s.watching = make(chan int, 1)
	var heard []bool
	g.s.onGuests = func(on bool) { heard = append(heard, on) }
	if resp := switchTo(`{}`); resp.StatusCode != http.StatusBadRequest {
		t.Errorf("the switch with no state: status %d, want 400", resp.StatusCode)
	}
	if resp := switchTo(`{"on": true}`); resp.StatusCode != http.StatusNoContent || len(heard) != 0 || !g.s.mic() {
		t.Errorf("the switch on with nobody watching: status %d, the microphone ran %v", resp.StatusCode, heard)
	}
	told(t, g.s.watching, 0)
	watcher := &webrtc.PeerConnection{}
	g.s.mu.Lock()
	g.s.guests[watcher] = true
	g.s.watch()
	g.s.mu.Unlock()
	if resp := switchTo(`{"on": false}`); resp.StatusCode != http.StatusNoContent || g.s.mic() {
		t.Errorf("the switch off: status %d", resp.StatusCode)
	}
	g.s.mu.Lock()
	delete(g.s.guests, watcher)
	g.s.watch()
	g.s.mu.Unlock()
	if !slices.Equal(heard, []bool{true, false}) {
		t.Errorf("the microphone ran %v, want on with the guest and the switch, then off", heard)
	}
}

// offerWithSound is offer with the guest page's audio too.
func offerWithSound(t *testing.T) []byte {
	t.Helper()
	pc, err := webrtc.NewAPI(webrtc.WithSettingEngine(loopbackOnly())).NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { pc.Close() })
	for _, kind := range []webrtc.RTPCodecType{webrtc.RTPCodecTypeVideo, webrtc.RTPCodecTypeAudio} {
		if _, err := pc.AddTransceiverFromKind(kind, webrtc.RTPTransceiverInit{Direction: webrtc.RTPTransceiverDirectionRecvonly}); err != nil {
			t.Fatal(err)
		}
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
	return body
}

// A guest's connection carries the microphone's track beside the picture; a device's does not.
func TestAGuestsConnectionCarriesTheMicrophone(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	guests := withGuests(t, g)
	voice, err := webrtc.NewTrackLocalStaticRTP(webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeOpus, ClockRate: 48000, Channels: 2}, "voice", "delta")
	if err != nil {
		t.Fatal(err)
	}
	g.s.voice = voice
	_, device := g.pair(t, g.s.issueCode())
	_, guest := join(t, guests, invite(t, g, device))
	for _, c := range []struct {
		who   string
		resp  *http.Response
		audio bool
	}{
		{"the guest", request(t, http.MethodPost, guests.URL+"/offer", offerWithSound(t), guest), true},
		{"the device", g.do(t, "/offer", offerWithSound(t), device, g.ts.URL), false},
	} {
		var answer webrtc.SessionDescription
		if c.resp.StatusCode != http.StatusOK || json.NewDecoder(c.resp.Body).Decode(&answer) != nil {
			t.Fatalf("%s's offer: status %d", c.who, c.resp.StatusCode)
		}
		// pion answers an audio section with sendonly whether or not a track is there: the track's msid tells.
		audio := regexp.MustCompile(`(?s)m=audio .*a=msid:delta voice`).MatchString(answer.SDP)
		if audio != c.audio {
			t.Errorf("%s's answer sends the microphone: %v, want %v", c.who, audio, c.audio)
		}
	}
}

func TestTheMicrophonesCaptureIsOpusOverRTP(t *testing.T) {
	args := strings.Join(micArgs("Headset Microphone (2- Jabra EVOLVE 20 MS)", 5004), " ")
	for _, want := range []string{"-f dshow", "-i audio=Headset Microphone (2- Jabra EVOLVE 20 MS)", "-c:a libopus",
		"-f rtp rtp://127.0.0.1:5004"} {
		if !strings.Contains(args, want) {
			t.Errorf("micArgs has no %q: %s", want, args)
		}
	}
	r := renumber{gap: opusTicks}
	p := &rtp.Packet{Header: rtp.Header{SSRC: 1, SequenceNumber: 10, Timestamp: 1000}}
	r.apply(p)
	next := &rtp.Packet{Header: rtp.Header{SSRC: 2, SequenceNumber: 500, Timestamp: 77}}
	r.apply(next)
	if next.SequenceNumber != 11 || next.Timestamp != 1000+opusTicks {
		t.Errorf("a new capture's first packet: %d at %d, want 11 at %d", next.SequenceNumber, next.Timestamp, 1000+opusTicks)
	}
}
