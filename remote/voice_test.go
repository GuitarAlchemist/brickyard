package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

const secret = "aa11bb22cc33dd44ee55ff66aa11bb22cc33dd44ee55ff66aa11bb22cc33dd44"

// fakeBrain answers like Delta's brain on 8807 (brain/remote.go): /hear, /said and /link, behind X-Delta-Hook and
// X-Delta-Remote, refusing any Origin.
type fakeBrain struct {
	*httptest.Server
	mu    sync.Mutex
	heard [][]byte
	links []string
	bad   []string // why a request was refused
	said  []string // SSE data lines /said sends
}

func newFakeBrain(t *testing.T) *fakeBrain {
	b := &fakeBrain{}
	b.Server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		b.mu.Lock()
		defer b.mu.Unlock()
		if r.Header.Get("X-Delta-Hook") != "1" || r.Header.Get("X-Delta-Remote") != secret || r.Header.Get("Origin") != "" {
			b.bad = append(b.bad, r.URL.Path)
			w.WriteHeader(http.StatusForbidden)
			return
		}
		switch r.URL.Path {
		case "/hear":
			body, _ := io.ReadAll(r.Body)
			b.heard = append(b.heard, body)
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusAccepted)
			io.WriteString(w, `{"utterance_id":"7","text":"bonjour Delta","route":"channel"}`)
		case "/link":
			var req struct{ URL string }
			json.NewDecoder(r.Body).Decode(&req)
			b.links = append(b.links, req.URL)
			w.WriteHeader(http.StatusNoContent)
		case "/said":
			w.Header().Set("Content-Type", "text/event-stream")
			for _, d := range b.said {
				io.WriteString(w, "data: "+d+"\n\n")
			}
			w.(http.Flusher).Flush()
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	t.Cleanup(b.Close)
	return b
}

func (b *fakeBrain) snapshot() (heard [][]byte, links, bad []string) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return append([][]byte(nil), b.heard...), append([]string(nil), b.links...), append([]string(nil), b.bad...)
}

// voiceGateway is a paired test gateway wired to b, its secret written where the brain writes it.
func voiceGateway(t *testing.T, b *fakeBrain) (*testGateway, string) {
	t.Helper()
	dir := t.TempDir()
	secretPath := filepath.Join(dir, "remote.secret")
	if err := os.WriteFile(secretPath, []byte(secret+"\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	g := newTestGateway(t, t.TempDir())
	g.s.brain = newBrain(b.URL, secretPath)
	_, cookie := g.pair(t, g.s.issueCode())
	return g, cookie
}

func wav(seconds float64) []byte {
	n := int(seconds * 16000)
	var buf bytes.Buffer
	buf.WriteString("RIFF")
	buf.Write(le32(uint32(36 + 2*n)))
	buf.WriteString("WAVEfmt ")
	buf.Write(le32(16))
	buf.Write([]byte{1, 0, 1, 0})
	buf.Write(le32(16000))
	buf.Write(le32(32000))
	buf.Write([]byte{2, 0, 16, 0})
	buf.WriteString("data")
	buf.Write(le32(uint32(2 * n)))
	buf.Write(make([]byte, 2*n))
	return buf.Bytes()
}

func le32(v uint32) []byte { return []byte{byte(v), byte(v >> 8), byte(v >> 16), byte(v >> 24)} }

func TestTheDevicesSpeechReachesTheBrainWithTheSecret(t *testing.T) {
	b := newFakeBrain(t)
	g, cookie := voiceGateway(t, b)
	body := wav(1.5)
	resp := g.do(t, "/hear", body, cookie, g.ts.URL)
	got, _ := io.ReadAll(resp.Body)
	if resp.StatusCode != http.StatusAccepted || !strings.Contains(string(got), "bonjour Delta") {
		t.Fatalf("status %d, body %s: want the brain's 202 and transcript", resp.StatusCode, got)
	}
	heard, _, bad := b.snapshot()
	if len(bad) > 0 {
		t.Fatalf("the brain refused %v: the gateway must send X-Delta-Hook, the secret and no Origin", bad)
	}
	if len(heard) != 1 || !bytes.Equal(heard[0], body) {
		t.Fatalf("the brain heard %d bodies, want the device's WAV once", len(heard))
	}
}

func TestAnUnpairedDeviceCannotSpeak(t *testing.T) {
	b := newFakeBrain(t)
	g, _ := voiceGateway(t, b)
	if resp := g.do(t, "/hear", wav(1), "", g.ts.URL); resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("status %d, want 401", resp.StatusCode)
	}
	if resp := g.do(t, "/hear", wav(1), deviceCookie+"=x", "https://evil.example"); resp.StatusCode != http.StatusForbidden {
		t.Errorf("foreign origin: status %d, want 403", resp.StatusCode)
	}
	if heard, _, _ := b.snapshot(); len(heard) != 0 {
		t.Error("the brain heard an unpaired device")
	}
}

func TestSpeechLongerThanTheBrainTakesIsRefusedHere(t *testing.T) {
	b := newFakeBrain(t)
	g, cookie := voiceGateway(t, b)
	if resp := g.do(t, "/hear", wav(31), cookie, g.ts.URL); resp.StatusCode != http.StatusRequestEntityTooLarge {
		t.Errorf("31 s: status %d, want 413", resp.StatusCode)
	}
	if heard, _, _ := b.snapshot(); len(heard) != 0 {
		t.Error("an overlong WAV reached the brain")
	}
}

func TestWithoutTheBrainsSecretDeltaIsNotListening(t *testing.T) {
	b := newFakeBrain(t)
	g, cookie := voiceGateway(t, b)
	g.s.brain = newBrain(b.URL, filepath.Join(t.TempDir(), "absent.secret"))
	if resp := g.do(t, "/hear", wav(1), cookie, g.ts.URL); resp.StatusCode != http.StatusServiceUnavailable {
		t.Errorf("status %d, want 503", resp.StatusCode)
	}
}

func TestDeltasSentencesReachThePairedDevice(t *testing.T) {
	b := newFakeBrain(t)
	b.said = []string{
		`{"say_id":1,"text":"Bonjour.","seconds":0.8,"t_ms":1,"wav_b64":"UklGRg=="}`,
		`{"say_id":2,"text":"Autre appareil.","seconds":1.1,"t_ms":2,"wav_b64":"UklGRg=="}`,
	}
	g, cookie := voiceGateway(t, b)

	req, _ := http.NewRequest(http.MethodGet, g.ts.URL+"/said", nil)
	if resp, err := http.DefaultClient.Do(req); err != nil || resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("unpaired: %v %v, want 401", resp.StatusCode, err)
	}
	req, _ = http.NewRequest(http.MethodGet, g.ts.URL+"/said", nil)
	req.Header.Set("Cookie", cookie)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if ct := resp.Header.Get("Content-Type"); !strings.HasPrefix(ct, "text/event-stream") {
		t.Fatalf("Content-Type %q, want an event stream", ct)
	}
	var got []string
	done := make(chan struct{})
	go func() {
		defer close(done)
		sc := bufio.NewScanner(resp.Body)
		for sc.Scan() {
			if d, ok := strings.CutPrefix(sc.Text(), "data: "); ok {
				if got = append(got, d); len(got) == 2 {
					return
				}
			}
		}
	}()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("the sentences did not reach the device")
	}
	if got[0] != b.said[0] || got[1] != b.said[1] {
		t.Errorf("relayed %q, want the brain's events unchanged", got)
	}
}

func TestDeltaShowsThePairingLinkUntilItIsUsed(t *testing.T) {
	b := newFakeBrain(t)
	g, _ := voiceGateway(t, b) // pairs one device with the code it issues
	g.s.issueCode()
	_, links, _ := b.snapshot()
	if len(links) < 3 {
		t.Fatalf("links %q: want a link, its clearing once used, and the next link", links)
	}
	if !strings.Contains(links[0], "#code=") || links[1] != "" || !strings.Contains(links[2], "#code=") {
		t.Errorf("links %q: want a #code= link, then \"\" once it is used, then a new link", links)
	}
	if strings.Contains(strings.Join(links, " "), secret) {
		t.Error("the brain's secret went into a link")
	}
}
