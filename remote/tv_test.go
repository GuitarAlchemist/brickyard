package main

import (
	"bufio"
	"context"
	"crypto/tls"
	"encoding/binary"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"testing"
	"time"

	"golang.org/x/net/dns/dnsmessage"
)

func TestACastMessageReadsAsItWasWritten(t *testing.T) {
	m := castMessage{"sender-0", "receiver-0", nsReceiver, `{"type":"LAUNCH","appId":"CC1AD845","requestId":1}`}
	b := m.marshal()
	// protocol_version 0, then source_id "sender-0": the protocol buffer's own encoding.
	if want := append([]byte{0x08, 0x00, 0x12, 0x08}, "sender-0"...); string(b[:len(want)]) != string(want) {
		t.Errorf("the message begins % x, want % x", b[:len(want)], want)
	}
	got, err := unmarshalCast(b)
	if err != nil || got != m {
		t.Errorf("read back %+v, %v; want %+v", got, err, m)
	}
	for _, bad := range [][]byte{{0x12, 0x09, 'a'}, {0x0b}, {0x80}} {
		if _, err := unmarshalCast(bad); err == nil {
			t.Errorf("% x read without an error", bad)
		}
	}
}

// fakeChromecast is a Chromecast's Cast protocol on 127.0.0.1, over TLS with a certificate of its own.
type fakeChromecast struct {
	t    *testing.T
	addr string
	got  chan castMessage // what the gateway sends
	conn chan net.Conn
}

func newFakeChromecast(t *testing.T) *fakeChromecast {
	t.Helper()
	cert, err := ownCertificate(t.TempDir(), net.IPv4(127, 0, 0, 1))
	if err != nil {
		t.Fatal(err)
	}
	l, err := tls.Listen("tcp", "127.0.0.1:0", &tls.Config{Certificates: []tls.Certificate{cert}})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { l.Close() })
	f := &fakeChromecast{t: t, addr: l.Addr().String(), got: make(chan castMessage, 32), conn: make(chan net.Conn, 1)}
	go func() {
		conn, err := l.Accept()
		if err != nil {
			return
		}
		t.Cleanup(func() { conn.Close() })
		f.conn <- conn
		r := bufio.NewReader(conn)
		for {
			var size [4]byte
			if _, err := io.ReadFull(r, size[:]); err != nil {
				close(f.got)
				return
			}
			body := make([]byte, binary.BigEndian.Uint32(size[:]))
			io.ReadFull(r, body)
			m, err := unmarshalCast(body)
			if err != nil {
				t.Errorf("the gateway sent a message that does not parse: %v", err)
			}
			f.got <- m
		}
	}()
	return f
}

// expect waits for the gateway's next message, and fails unless it is of kind on namespace to destination.
func (f *fakeChromecast) expect(destination, namespace, kind string) map[string]any {
	f.t.Helper()
	select {
	case m, ok := <-f.got:
		if !ok {
			f.t.Fatalf("the gateway closed the connection; want %s on %s", kind, namespace)
		}
		var p map[string]any
		json.Unmarshal([]byte(m.payload), &p)
		if m.destination != destination || m.namespace != namespace || p["type"] != kind {
			f.t.Fatalf("the gateway sent %s to %s on %s; want %s to %s on %s", p["type"], m.destination, m.namespace, kind, destination, namespace)
		}
		return p
	case <-time.After(5 * time.Second):
		f.t.Fatalf("the gateway sent nothing; want %s on %s", kind, namespace)
	}
	return nil
}

func (f *fakeChromecast) say(conn net.Conn, source, namespace, payload string) {
	body := castMessage{source, castSender, namespace, payload}.marshal()
	conn.Write(append(binary.BigEndian.AppendUint32(nil, uint32(len(body))), body...))
}

func (f *fakeChromecast) accepted() net.Conn {
	select {
	case c := <-f.conn:
		return c
	case <-time.After(5 * time.Second):
		f.t.Fatal("the gateway did not connect")
	}
	return nil
}

const receiverStatus = `{"type":"RECEIVER_STATUS","requestId":1,"status":{"applications":[` +
	`{"appId":"CC1AD845","sessionId":"s-1","transportId":"t-1"}]}}`

// castUntilLoaded runs castPlay against f until the stream is loaded, and returns its connection and how castPlay ends.
func castUntilLoaded(t *testing.T, f *fakeChromecast, ctx context.Context, url string, playing func()) (net.Conn, chan error) {
	t.Helper()
	ended := make(chan error, 1)
	go func() { ended <- castPlay(ctx, f.addr, url, playing) }()
	conn := f.accepted()
	f.expect(castPlatform, nsConnection, "CONNECT")
	if p := f.expect(castPlatform, nsReceiver, "LAUNCH"); p["appId"] != defaultReceiver {
		t.Fatalf("LAUNCH %v, want Google's default media receiver %s", p["appId"], defaultReceiver)
	}
	f.say(conn, castPlatform, nsHeartbeat, `{"type":"PING"}`)
	f.expect(castPlatform, nsHeartbeat, "PONG")
	f.say(conn, castPlatform, nsReceiver, receiverStatus)
	f.expect("t-1", nsConnection, "CONNECT")
	load := f.expect("t-1", nsMedia, "LOAD")
	media, _ := load["media"].(map[string]any)
	if media["contentId"] != url || media["streamType"] != "LIVE" || load["sessionId"] != "s-1" {
		t.Fatalf("LOAD %v, want the live stream %s in session s-1", load, url)
	}
	return conn, ended
}

// The gateway starts the default media receiver, answers its heartbeat, loads the stream, and closes the receiver
// when the cast is stopped.
func TestTheChromecastPlaysTheStreamUntilTheCastStops(t *testing.T) {
	f := newFakeChromecast(t)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	playing := make(chan bool, 1)
	conn, ended := castUntilLoaded(t, f, ctx, "http://192.0.2.1:8815/tv/abc/index.m3u8", func() { playing <- true })
	f.say(conn, "t-1", nsMedia, `{"type":"MEDIA_STATUS","status":[{"playerState":"BUFFERING"}]}`)
	select {
	case <-playing:
	case <-time.After(5 * time.Second):
		t.Fatal("the Chromecast buffers the stream, and the gateway does not know it plays")
	}
	f.say(conn, castPlatform, nsReceiver, receiverStatus) // a volume change, say: the receiver still runs
	cancel()
	if p := f.expect(castPlatform, nsReceiver, "STOP"); p["sessionId"] != "s-1" {
		t.Errorf("STOP %v, want session s-1", p)
	}
	if err := <-ended; !errors.Is(err, context.Canceled) {
		t.Errorf("the cast ended with %v, want its stop", err)
	}
}

func TestTheCastEndsWhenTheTelevisionStopsIt(t *testing.T) {
	for _, c := range []struct {
		name, namespace, source, payload, want string
	}{
		{"the receiver closed", nsReceiver, castPlatform, `{"type":"RECEIVER_STATUS","status":{"applications":[{"appId":"E8C28D3C","sessionId":"b"}]}}`, errStoppedOnTV.Error()},
		{"the stream failed", nsMedia, "t-1", `{"type":"MEDIA_STATUS","status":[{"playerState":"IDLE","idleReason":"ERROR"}]}`, "ERROR"},
		{"the load failed", nsMedia, "t-1", `{"type":"LOAD_FAILED","requestId":2}`, "LOAD_FAILED"},
	} {
		t.Run(c.name, func(t *testing.T) {
			f := newFakeChromecast(t)
			conn, ended := castUntilLoaded(t, f, context.Background(), "http://192.0.2.1:8815/tv/abc/index.m3u8", nil)
			f.say(conn, c.source, c.namespace, c.payload)
			select {
			case err := <-ended:
				if err == nil || !strings.Contains(err.Error(), c.want) {
					t.Errorf("the cast ended with %v, want %q", err, c.want)
				}
			case <-time.After(5 * time.Second):
				t.Fatal("the cast goes on")
			}
		})
	}
}

// mdnsAnswer is a Chromecast's answer to the gateway's query: its PTR, SRV and TXT, and its address when ip is not nil.
func mdnsAnswer(t *testing.T, instance, name string, ip net.IP) []byte {
	t.Helper()
	b := dnsmessage.NewBuilder(nil, dnsmessage.Header{Response: true, Authoritative: true})
	b.StartAnswers()
	full := dnsmessage.MustNewName(instance + "." + googlecast)
	host := dnsmessage.MustNewName(instance + ".local.")
	b.PTRResource(dnsmessage.ResourceHeader{Name: dnsmessage.MustNewName(googlecast), Class: dnsmessage.ClassINET}, dnsmessage.PTRResource{PTR: full})
	b.StartAdditionals()
	b.TXTResource(dnsmessage.ResourceHeader{Name: full, Class: dnsmessage.ClassINET}, dnsmessage.TXTResource{TXT: []string{"id=1", "fn=" + name, "md=Chromecast"}})
	b.SRVResource(dnsmessage.ResourceHeader{Name: full, Class: dnsmessage.ClassINET}, dnsmessage.SRVResource{Port: 8009, Target: host})
	if ip != nil {
		var a [4]byte
		copy(a[:], ip.To4())
		b.AResource(dnsmessage.ResourceHeader{Name: host, Class: dnsmessage.ClassINET}, dnsmessage.AResource{A: a})
	}
	raw, err := b.Finish()
	if err != nil {
		t.Fatal(err)
	}
	return raw
}

func TestTheChromecastsAreReadFromTheirAnswers(t *testing.T) {
	query, err := googlecastQuery()
	if err != nil {
		t.Fatal(err)
	}
	var q dnsmessage.Message
	if q.Unpack(query) != nil || len(q.Questions) != 1 || q.Questions[0].Type != dnsmessage.TypePTR || q.Questions[0].Name.String() != googlecast {
		t.Errorf("the query asks %v, want the PTR of %s", q.Questions, googlecast)
	}
	got := chromecastsIn([][]byte{
		mdnsAnswer(t, "Chromecast-b2", "Salon", net.IPv4(192, 168, 1, 50)),
		[]byte("not DNS"),
		mdnsAnswer(t, "Chromecast-a1", "Chambre", net.IPv4(192, 168, 1, 51)),
		mdnsAnswer(t, "Chromecast-c3", "Sans adresse", nil),
	})
	want := []chromecast{{"Chambre", "192.168.1.51:8009"}, {"Salon", "192.168.1.50:8009"}}
	if len(got) != len(want) || got[0] != want[0] || got[1] != want[1] {
		t.Errorf("found %+v, want %+v", got, want)
	}
}

// The stream is served under its token only, its playlist and segments only, to any origin since the Chromecast's
// player is one.
func TestTheStreamIsServedUnderItsTokenOnly(t *testing.T) {
	dir := t.TempDir()
	for _, name := range []string{"index.m3u8", "s00001.ts", "other.txt"} {
		os.WriteFile(filepath.Join(dir, name), []byte(name), 0o600)
	}
	tv := &tv{token: "0123456789abcdef0123456789abcdef", dir: dir}
	ts := httptest.NewServer(tv.handler())
	defer ts.Close()
	get := func(path string) *http.Response {
		resp, err := http.Get(ts.URL + path)
		if err != nil {
			t.Fatal(err)
		}
		return resp
	}
	for path, kind := range map[string]string{"/tv/" + tv.token + "/index.m3u8": "application/vnd.apple.mpegurl", "/tv/" + tv.token + "/s00001.ts": "video/mp2t"} {
		resp := get(path)
		if resp.StatusCode != http.StatusOK || resp.Header.Get("Content-Type") != kind || resp.Header.Get("Access-Control-Allow-Origin") != "*" {
			t.Errorf("%s: status %d, %q, CORS %q; want 200, %s, *", path, resp.StatusCode, resp.Header.Get("Content-Type"), resp.Header.Get("Access-Control-Allow-Origin"), kind)
		}
	}
	for _, path := range []string{"/tv/wrong/index.m3u8", "/tv/" + tv.token + "/other.txt", "/tv/" + tv.token + "/..%2findex.m3u8", "/tv//index.m3u8", "/"} {
		if resp := get(path); resp.StatusCode != http.StatusNotFound {
			t.Errorf("%s: status %d, want 404", path, resp.StatusCode)
		}
	}
	tv.token = ""
	if resp := get("/tv//index.m3u8"); resp.StatusCode != http.StatusNotFound {
		t.Errorf("no cast, an empty token: status %d, want 404", resp.StatusCode)
	}
}

// fakeTV is a tv whose Chromecasts, ffmpeg and Cast protocol are the test's.
type fakeTV struct {
	*tv
	mu      sync.Mutex
	urls    []string
	addrs   []string
	halted  int
	exit    func(error) // ends the stream's ffmpeg as if by itself
	stopped chan error  // how each play ended
}

func newFakeTV(t *testing.T, found ...chromecast) *fakeTV {
	f := &fakeTV{stopped: make(chan error, 4)}
	f.tv = &tv{
		host:  "192.0.2.1:8815",
		ready: 5 * time.Second,
		find:  func() ([]chromecast, error) { return found, nil },
		stream: func(dir string, exited func(error)) (func(), error) {
			os.WriteFile(filepath.Join(dir, "index.m3u8"), []byte("#EXTM3U\n"), 0o600)
			os.WriteFile(filepath.Join(dir, "s00002.ts"), nil, 0o600)
			f.mu.Lock()
			f.exit = exited
			f.mu.Unlock()
			return func() { f.mu.Lock(); f.halted++; f.mu.Unlock() }, nil
		},
		play: func(ctx context.Context, addr, url string, playing func()) error {
			f.mu.Lock()
			f.urls, f.addrs = append(f.urls, url), append(f.addrs, addr)
			f.mu.Unlock()
			playing()
			<-ctx.Done()
			f.stopped <- ctx.Err()
			return ctx.Err()
		},
	}
	return f
}

func eventually(t *testing.T, what string, ok func() bool) {
	t.Helper()
	for deadline := time.Now().Add(5 * time.Second); !ok(); time.Sleep(10 * time.Millisecond) {
		if time.Now().After(deadline) {
			t.Fatal(what)
		}
	}
}

func TestACastGoesToTheOnlyChromecastUntilItStops(t *testing.T) {
	f := newFakeTV(t, chromecast{"Salon", "192.168.1.50:8009"})
	name, err := f.start("")
	if err != nil || name != "Salon" {
		t.Fatalf("start: %q, %v; want Salon", name, err)
	}
	eventually(t, "the Chromecast was not told to play, or does not play", func() bool { return f.state().Playing })
	f.mu.Lock()
	url, addr := f.urls[0], f.addrs[0]
	f.mu.Unlock()
	m := regexp.MustCompile(`^http://192\.0\.2\.1:8815/tv/([0-9a-f]{32})/index\.m3u8$`).FindStringSubmatch(url)
	if m == nil || addr != "192.168.1.50:8009" {
		t.Fatalf("the Chromecast at %s was told %s, want Salon's address and http://192.0.2.1:8815/tv/<32 hex>/index.m3u8", addr, url)
	}
	ts := httptest.NewServer(f.handler())
	defer ts.Close()
	if resp, err := http.Get(ts.URL + "/tv/" + m[1] + "/index.m3u8"); err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("the playlist the Chromecast was told: %v", err)
	}
	f.mu.Lock()
	dir := f.tv.dir
	f.mu.Unlock()
	f.stop()
	if err := <-f.stopped; !errors.Is(err, context.Canceled) {
		t.Errorf("the Chromecast's play ended with %v, want the stop", err)
	}
	eventually(t, "ffmpeg was not stopped", func() bool { f.mu.Lock(); defer f.mu.Unlock(); return f.halted == 1 })
	eventually(t, "the stream's folder stays", func() bool { _, err := os.Stat(dir); return os.IsNotExist(err) })
	if st := f.state(); st.On != "" {
		t.Errorf("after the stop, the state is %+v, want no cast", st)
	}
	if resp, _ := http.Get(ts.URL + "/tv/" + m[1] + "/index.m3u8"); resp.StatusCode != http.StatusNotFound {
		t.Errorf("the playlist after the stop: status %d, want 404", resp.StatusCode)
	}
}

func TestACastEndsWithItsFFmpeg(t *testing.T) {
	f := newFakeTV(t, chromecast{"Salon", "192.168.1.50:8009"})
	if _, err := f.start(""); err != nil {
		t.Fatal(err)
	}
	eventually(t, "the Chromecast does not play", func() bool { return f.state().Playing })
	f.mu.Lock()
	exit := f.exit
	f.mu.Unlock()
	exit(errors.New("exit status 1"))
	if err := <-f.stopped; !errors.Is(err, context.Canceled) {
		t.Errorf("the Chromecast's play ended with %v, want the cast's end", err)
	}
	if st := f.state(); st.On != "" || !strings.Contains(st.Ended, "ffmpeg") {
		t.Errorf("the state is %+v, want no cast, ended by ffmpeg", st)
	}
}

func TestACastNeedsOneChromecastOrItsName(t *testing.T) {
	if _, err := newFakeTV(t).start(""); !errors.Is(err, errNoChromecast) {
		t.Errorf("no Chromecast: %v, want %v", err, errNoChromecast)
	}
	two := []chromecast{{"Chambre", "192.168.1.51:8009"}, {"Salon", "192.168.1.50:8009"}}
	if _, err := newFakeTV(t, two...).start(""); err == nil {
		t.Error("two Chromecasts and no name: cast to one of them, want an error")
	}
	if _, err := newFakeTV(t, two...).start("Cuisine"); err == nil {
		t.Error("a name none has: cast, want an error")
	}
	f := newFakeTV(t, two...)
	if name, err := f.start("Salon"); err != nil || name != "Salon" {
		t.Errorf("start Salon: %q, %v", name, err)
	}
	f.stop()
}

// Only the brain casts, through the window's entry: no page, no secret, no cast.
func TestOnlyDeltasWindowCastsToTheTelevision(t *testing.T) {
	g := newTestGateway(t, t.TempDir())
	local := windowEntry(t, g, newFakeBrain(t), t.TempDir())
	brain := map[string]string{"X-Delta-Hook": "1", "X-Delta-Remote": secret}
	if resp := fromWindow(t, local.URL+"/tv/find", brain); resp.StatusCode != http.StatusNotFound {
		t.Errorf("/tv/find with -tv-port 0: status %d, want 404", resp.StatusCode)
	}
	g.s.tv = newFakeTV(t, chromecast{"Salon", "192.168.1.50:8009"}).tv
	for _, path := range []string{"/tv/find", "/tv/start", "/tv/stop", "/tv/state"} {
		for name, headers := range map[string]map[string]string{
			"no secret":       {"X-Delta-Hook": "1"},
			"a page's Origin": {"X-Delta-Hook": "1", "X-Delta-Remote": secret, "Origin": local.URL},
		} {
			if resp := fromWindow(t, local.URL+path, headers); resp.StatusCode != http.StatusForbidden {
				t.Errorf("%s with %s: status %d, want 403", path, name, resp.StatusCode)
			}
		}
	}
	resp := fromWindow(t, local.URL+"/tv/find", brain)
	var found struct{ Chromecasts []struct{ Name string } }
	json.NewDecoder(resp.Body).Decode(&found)
	if resp.StatusCode != http.StatusOK || len(found.Chromecasts) != 1 || found.Chromecasts[0].Name != "Salon" {
		t.Errorf("/tv/find: status %d, %+v; want Salon", resp.StatusCode, found)
	}
	resp = fromWindow(t, local.URL+"/tv/start", brain)
	var st tvState
	json.NewDecoder(resp.Body).Decode(&st)
	if resp.StatusCode != http.StatusOK || st.On != "Salon" {
		t.Errorf("/tv/start: status %d, %+v; want a cast to Salon", resp.StatusCode, st)
	}
	if resp := fromWindow(t, local.URL+"/tv/stop", brain); resp.StatusCode != http.StatusNoContent {
		t.Errorf("/tv/stop: status %d, want 204", resp.StatusCode)
	}
}

// The real ffmpeg takes hlsArgs and makes a stream a player can start on: a playlist and its first segments.
func TestFFmpegMakesTheStreamFromItsArguments(t *testing.T) {
	if testing.Short() {
		t.Skip("runs ffmpeg for a few seconds")
	}
	if _, err := exec.LookPath("ffmpeg"); err != nil {
		t.Skip("no ffmpeg on PATH")
	}
	dir := t.TempDir()
	cmd := exec.Command("ffmpeg", hlsArgs("testsrc2=size=1280x720:rate=30", dir)...)
	var stderr strings.Builder
	cmd.Stderr = &stderr
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	defer func() { cmd.Process.Kill(); cmd.Wait() }()
	if !waitFor(context.Background(), filepath.Join(dir, "s00002.ts"), 20*time.Second) {
		t.Fatalf("no third segment within 20 s; ffmpeg said: %s", stderr.String())
	}
	playlist, _ := os.ReadFile(filepath.Join(dir, "index.m3u8"))
	if !strings.Contains(string(playlist), "s00000.ts") || !strings.Contains(string(playlist), "#EXT-X-TARGETDURATION:1") {
		t.Errorf("the playlist:\n%s\nwant one-second segments named s<5 digits>.ts", playlist)
	}
}
