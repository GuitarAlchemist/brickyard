package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
	"time"
)

// A web page reaches 127.0.0.1 too: with an Origin, which browsers add, or through DNS rebinding, under its own name.
func TestTheBrainAnswersThisComputerOnly(t *testing.T) {
	var s seen
	h := testHooks(&s, "")
	srv := httptest.NewServer(h.handler())
	defer srv.Close()
	body := `{"session_id":"s1","hook_event_name":"Notification"}`
	for _, c := range []struct {
		host, origin string
		want         int
	}{
		{"", "", http.StatusNoContent},
		{"localhost:8807", "", http.StatusNoContent},
		{"rebound.example:8807", "", http.StatusForbidden},
		{"", "http://rebound.example", http.StatusForbidden},
		{"", "null", http.StatusForbidden},
	} {
		req, _ := http.NewRequest(http.MethodPost, srv.URL+"/hook", strings.NewReader(body))
		req.Header.Set(hookHeader, "1")
		if c.host != "" {
			req.Host = c.host
		}
		if c.origin != "" {
			req.Header.Set("Origin", c.origin)
		}
		r, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		r.Body.Close()
		if r.StatusCode != c.want {
			t.Errorf("Host %q, Origin %q: %d, want %d", c.host, c.origin, r.StatusCode, c.want)
		}
	}
}

func remoteServer(t *testing.T, r *remote) *httptest.Server {
	t.Helper()
	h := &hooks{logf: t.Logf, remote: r}
	srv := httptest.NewServer(h.handler())
	t.Cleanup(srv.Close)
	return srv
}

func wavBytes(t *testing.T, rate int, seconds float64) []byte {
	t.Helper()
	path := filepath.Join(t.TempDir(), "a.wav")
	if err := writeWav(path, rate, make([]float32, int(seconds*float64(rate)))); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return data
}

func TestARemoteDeviceSpeaksToDeltaWithTheSecret(t *testing.T) {
	var routed []utterance
	chimes, where := 0, "channel"
	r := &remote{secret: "s3cret",
		route: func(u utterance) (int, string) { routed = append(routed, u); return 7, where },
		chime: func() { chimes++ }}
	srv := remoteServer(t, r)
	post := func(body []byte, secret string) (int, map[string]any) {
		req, _ := http.NewRequest(http.MethodPost, srv.URL+"/hear", bytes.NewReader(body))
		req.Header.Set(hookHeader, "1")
		req.Header.Set("X-Delta-Source", "remote")
		if secret != "" {
			req.Header.Set(remoteHeader, secret)
		}
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		defer resp.Body.Close()
		var out map[string]any
		json.NewDecoder(resp.Body).Decode(&out)
		return resp.StatusCode, out
	}
	speech := wavBytes(t, earsRate, 1)
	if code, _ := post(speech, ""); code != http.StatusForbidden {
		t.Fatalf("without the secret: %d", code)
	}
	if code, _ := post(speech, "guess"); code != http.StatusForbidden {
		t.Fatalf("with a wrong secret: %d", code)
	}
	if code, _ := post(speech, "s3cret"); code != http.StatusServiceUnavailable {
		t.Fatalf("before Delta hears: %d", code)
	}
	r.hearWith(func([]float32) (string, error) { return " Delta, quelle heure est-il ? ", nil })
	if code, _ := post(wavBytes(t, 22050, 1), "s3cret"); code != http.StatusBadRequest {
		t.Fatalf("a WAV at 22 kHz: %d", code)
	}
	if code, _ := post(wavBytes(t, earsRate, 31), "s3cret"); code != http.StatusRequestEntityTooLarge {
		t.Fatalf("31 s: %d", code)
	}
	code, out := post(speech, "s3cret")
	if code != http.StatusAccepted || out["utterance_id"] != "7" || out["text"] != "Delta, quelle heure est-il ?" || out["route"] != "channel" {
		t.Fatalf("%d %v", code, out)
	}
	if len(routed) != 1 || routed[0].source != "remote" || routed[0].text != "Delta, quelle heure est-il ?" || chimes != 0 {
		t.Fatalf("routed %+v, %d chimes: Delta's acknowledgement tells the room", routed, chimes)
	}
	where = "dropped"
	if post(speech, "s3cret"); chimes != 1 {
		t.Fatalf("a sentence Delta does not answer at once: %d chimes", chimes)
	}

	// a brain without a secret refuses everyone, an empty header included
	empty := remoteServer(t, &remote{})
	req, _ := http.NewRequest(http.MethodPost, empty.URL+"/hear", bytes.NewReader(speech))
	req.Header.Set(hookHeader, "1")
	req.Header.Set(remoteHeader, "")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != http.StatusForbidden {
		t.Fatalf("an empty secret: %d", resp.StatusCode)
	}
}

// saidEvent is one sentence from /said; the utterance is a pointer, so that a test tells "" from absent.
type saidEvent struct {
	SayID     int     `json:"say_id"`
	Utterance *string `json:"utterance_id"`
	By        string  `json:"by"`
	Text      string  `json:"text"`
	Seconds   float64 `json:"seconds"`
	TMs       int64   `json:"t_ms"`
	Wav       string  `json:"wav_b64"`
}

// subscribeSaid opens /said as a device does, and waits until the brain counts it.
func subscribeSaid(t *testing.T, r *remote) *bufio.Scanner {
	t.Helper()
	srv := remoteServer(t, r)
	req, _ := http.NewRequest(http.MethodGet, srv.URL+"/said", nil)
	req.Header.Set(hookHeader, "1")
	req.Header.Set(remoteHeader, r.secret)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { resp.Body.Close() })
	if resp.StatusCode != http.StatusOK || resp.Header.Get("Content-Type") != "text/event-stream" {
		t.Fatalf("%d %s", resp.StatusCode, resp.Header.Get("Content-Type"))
	}
	deadline := time.Now().Add(2 * time.Second)
	for !r.listened() {
		if time.Now().After(deadline) {
			t.Fatal("the device never subscribed")
		}
		time.Sleep(5 * time.Millisecond)
	}
	lines := bufio.NewScanner(resp.Body)
	lines.Buffer(make([]byte, 1<<20), 1<<20)
	return lines
}

func nextSaid(t *testing.T, lines *bufio.Scanner) saidEvent {
	t.Helper()
	for lines.Scan() {
		line := lines.Text()
		if !strings.HasPrefix(line, "data: ") {
			continue
		}
		var event saidEvent
		if err := json.Unmarshal([]byte(strings.TrimPrefix(line, "data: ")), &event); err != nil {
			t.Fatal(err)
		}
		return event
	}
	t.Fatalf("no event: %v", lines.Err())
	return saidEvent{}
}

// Each sentence reaches the device as it starts, with its sound and the utterance it answers, as its say note gives
// them, so that a device matches an answer to its question; "" when it answers none, or when no note came.
func TestARemoteDeviceHearsEachSentenceAsItStarts(t *testing.T) {
	r := &remote{secret: "s3cret"}
	lines := subscribeSaid(t, r)
	path := filepath.Join(t.TempDir(), "s.wav")
	if err := writeWav(path, 22050, make([]float32, 2205)); err != nil {
		t.Fatal(err)
	}
	w, _ := readWav(path)
	r.attribute(map[string]any{"say_id": 3, "utterance_id": "4", "text": "Il est midi.", "by": "claude"})
	r.publish(3, "Il est midi.", w, path)
	event := nextSaid(t, lines)
	sound, _ := base64.StdEncoding.DecodeString(event.Wav)
	file, _ := os.ReadFile(path)
	if event.SayID != 3 || event.Text != "Il est midi." || event.Seconds != 0.1 || event.TMs == 0 || !bytes.Equal(sound, file) ||
		event.Utterance == nil || *event.Utterance != "4" || event.By != "claude" {
		t.Fatalf("%+v", event)
	}
	r.publish(3, "Bonne journée.", w, path) // the say call's next sentence answers the same question
	if event := nextSaid(t, lines); event.SayID != 3 || event.Utterance == nil || *event.Utterance != "4" || event.By != "claude" {
		t.Fatalf("the second sentence: say %d, by %q, utterance %v", event.SayID, event.By, event.Utterance)
	}
	r.attribute(map[string]any{"say_id": 5, "utterance_id": "", "text": "Bonsoir.", "by": "local"})
	r.publish(5, "Bonsoir.", w, path)
	r.publish(6, "Hum.", w, path)
	for _, want := range []struct {
		say int
		by  string
	}{{5, "local"}, {6, ""}} {
		if event := nextSaid(t, lines); event.SayID != want.say || event.Utterance == nil || *event.Utterance != "" || event.By != want.by {
			t.Fatalf("%+v", event)
		}
	}
	for id := 7; id < 300; id++ { // sentences never said, such as the acknowledgements the mouth drops
		r.attribute(map[string]any{"say_id": id, "utterance_id": "9", "by": "ack"})
	}
	if len(r.answers) > 64 {
		t.Fatalf("the brain keeps %d sentences that were never said", len(r.answers))
	}
}

func TestARemoteSentenceNeedsNoWakeWordAndSaysWhereItCameFrom(t *testing.T) {
	var records []map[string]any
	m, _, out := startServer(t, func(m *mcpServer) {
		m.record = func(event string, fields map[string]any) { fields["event"] = event; records = append(records, fields) }
	})
	id, where := m.heardAs(utterance{text: "Quelle heure est-il ?", source: "remote"}, time.Now().Add(time.Hour))
	if where != "channel" || id != 1 {
		t.Fatalf("%d %s", id, where)
	}
	meta := next(t, out)["params"].(map[string]any)["meta"].(map[string]any)
	if meta["source"] != "remote" || records[0]["source"] != "remote" {
		t.Fatalf("%v %v", meta, records[0])
	}
	if where := m.hear(utterance{text: "Quelle heure est-il ?"}, time.Now().Add(3*time.Hour)); where != "ignored" {
		t.Fatalf("the microphone still needs the wake word: %s", where)
	}
}

// Holding the device's button addressed Delta, so the small model cannot drop what it heard; it still answers small
// talk at no cost. Live, it dropped four of five sentences from the phone.
func TestTheTriageKeepsWhatAnotherDeviceSays(t *testing.T) {
	var s spoken
	var records []map[string]any
	route := verdict{Route: "ignore"}
	m, _, out := startServer(t, func(m *mcpServer) {
		m.speak = s.speak
		m.triage = func(context.Context, []string, string) (verdict, error) { return route, nil }
		m.record = func(event string, fields map[string]any) { fields["event"] = event; records = append(records, fields) }
	})
	now := time.Now().Add(time.Hour)
	if where := m.hear(utterance{text: "Je ne sais pas, c'est pas, c'est pas.", source: "remote"}, now); where != "channel" {
		t.Fatalf("went to %s", where)
	}
	next(t, out)
	if heard := records[len(records)-1]; heard["triage"] != "ignore overruled: another device's button addressed Delta" {
		t.Fatalf("%v", heard)
	}
	if where := m.hear(utterance{text: "Delta, je ne sais pas, c'est pas."}, now.Add(time.Hour)); where != "dropped" {
		t.Fatalf("at the computer, the triage still drops: %s", where)
	}
	route = verdict{Route: "local", Answer: "Avec plaisir !"}
	if where := m.hear(utterance{text: "Merci, c'est cool.", source: "remote"}, now.Add(2*time.Hour)); where != "local" {
		t.Fatalf("small talk from the device went to %s", where)
	}
	if where := m.hear(utterance{text: "Euh...", source: "remote"}, now.Add(3*time.Hour)); where != "dropped" {
		t.Fatalf("a filler went to %s", where)
	}
	silent(t, out)
}

func TestTheSecretIsWrittenForTheUserAlone(t *testing.T) {
	t.Setenv("LOCALAPPDATA", t.TempDir())
	secret, path, err := newRemoteSecret()
	if err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(path)
	if err != nil || string(data) != secret || len(secret) != 64 || !strings.HasSuffix(path, filepath.Join("delta", "remote.secret")) {
		t.Fatalf("%q in %s: %q %v", secret, path, data, err)
	}
	again, _, _ := newRemoteSecret()
	if again == secret {
		t.Fatal("the same secret twice")
	}
}

// A gateway posts the link a phone opens to pair with it, and Delta's window gets the link's QR code; no answer of the
// brain repeats the link, a credential while its code lives.
func TestAGatewaysPairingLinkReachesTheWindowAsAQRCode(t *testing.T) {
	var shown [][]string
	r := &remote{secret: "s3cret", pair: func(rows []string) { shown = append(shown, rows) }}
	srv := remoteServer(t, r)
	post := func(body, secret string) (int, string) {
		req, _ := http.NewRequest(http.MethodPost, srv.URL+"/link", strings.NewReader(body))
		req.Header.Set(hookHeader, "1")
		req.Header.Set(remoteHeader, secret)
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		defer resp.Body.Close()
		answer, _ := io.ReadAll(resp.Body)
		return resp.StatusCode, string(answer)
	}
	link := "https://192.168.1.20:8810/#code=ABCD-EFGH"
	if code, _ := post(`{"url":"`+link+`"}`, "guess"); code != http.StatusForbidden {
		t.Fatalf("with a wrong secret: %d", code)
	}
	for _, bad := range []string{`{"url":"http://192.168.1.20:8810/#code=ABCD-EFGH"}`, `{"url":"https:///#code=ABCD-EFGH"}`,
		`{"url":"https://192.168.1.20:8810/#code=` + strings.Repeat("A", 80) + `"}`, `{"url":"https://a b/#code=ABCD-EFGH"}`,
		`code=ABCD-EFGH`} {
		if code, answer := post(bad, "s3cret"); code != http.StatusBadRequest || strings.Contains(answer, "ABCD") {
			t.Fatalf("%s: %d %q", bad, code, answer)
		}
	}
	if len(shown) != 0 {
		t.Fatalf("a bad link reached the window: %q", shown)
	}
	if code, answer := post(`{"url":"`+link+`"}`, "s3cret"); code != http.StatusNoContent || answer != "" {
		t.Fatalf("%d %q", code, answer)
	}
	want, _ := qrEncode(link)
	if len(shown) != 1 || !slices.Equal(shown[0], want) {
		t.Fatalf("the window got %q", shown)
	}
}

// An answer to a sentence from another device plays only there while the device listens, since the computer's
// speakers would echo it a moment apart; an answer to the computer, or to nothing, plays at the computer.
func TestAnAnswerToAnotherDevicePlaysThereOnly(t *testing.T) {
	r := &remote{secret: "s3cret", route: func(u utterance) (int, string) { return 12, "channel" }}
	srv := remoteServer(t, r)
	r.hearWith(func([]float32) (string, error) { return "Delta, quelle heure est-il ?", nil })
	req, _ := http.NewRequest(http.MethodPost, srv.URL+"/hear", bytes.NewReader(wavBytes(t, earsRate, 1)))
	req.Header.Set(hookHeader, "1")
	req.Header.Set(remoteHeader, "s3cret")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	r.attribute(map[string]any{"say_id": 20, "utterance_id": "12", "by": "claude"})
	r.attribute(map[string]any{"say_id": 21, "utterance_id": "11", "by": "claude"}) // said at the computer
	r.attribute(map[string]any{"say_id": 22, "utterance_id": "", "by": "local"})
	if !r.aloud(20) {
		t.Fatal("with no device listening, the computer says the answer")
	}
	subscribeSaid(t, r)
	if r.aloud(20) || !r.aloud(21) || !r.aloud(22) || !r.aloud(99) {
		t.Fatalf("aloud: %v %v %v %v", r.aloud(20), r.aloud(21), r.aloud(22), r.aloud(99))
	}
}
