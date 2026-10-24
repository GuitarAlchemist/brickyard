package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
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

// The guests' entry is what the internet reaches through the tunnel: a guest watches, and nothing else is there.
func TestTheGuestsEntryHasNothingButWatching(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	guests := withGuests(t, g)
	for _, path := range []string{"/hear", "/said", "/pair", "/link", "/invite", "/guests/end", "/page.js", "/talk.js", "/recorder.js"} {
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

func TestAnInvitationIsTradedOnceForTwoHoursOfWatching(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	guests := withGuests(t, g)
	_, device := g.pair(t, g.s.issueCode())
	code := invite(t, g, device)
	resp, guest := join(t, guests, code)
	if resp.StatusCode != http.StatusOK || guest == "" {
		t.Fatalf("join: status %d and cookie %q, want 200 and a guest's cookie", resp.StatusCode, guest)
	}
	if resp, again := join(t, guests, code); resp.StatusCode != http.StatusUnauthorized || again != "" {
		t.Errorf("the same invitation again: status %d, want 401 and no cookie", resp.StatusCode)
	}
	if resp := request(t, http.MethodGet, guests.URL+"/ice", nil, guest); resp.StatusCode != http.StatusOK {
		t.Errorf("the relay's addresses: status %d, want 200", resp.StatusCode)
	}
	g.now = g.now.Add(guestLife + time.Second)
	if resp := request(t, http.MethodGet, guests.URL+"/ice", nil, guest); resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("two hours on: status %d, want 401", resp.StatusCode)
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
	for _, n := range []int{1, 2, 0} {
		if err := g.s.brain.guests(n); err != nil {
			t.Fatal(err)
		}
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	if !slices.Equal(b.seen, []int{1, 2, 0}) || len(b.bad) > 0 {
		t.Errorf("the brain was told %v and refused %v, want 1, 2 and 0", b.seen, b.bad)
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
