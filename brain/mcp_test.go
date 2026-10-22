package main

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// lineSink collects what the server writes: one message a Write.
type lineSink chan []byte

func (s lineSink) Write(p []byte) (int, error) {
	s <- append([]byte(nil), p...)
	return len(p), nil
}

func startServer(t *testing.T, configure func(*mcpServer)) (*mcpServer, io.Writer, lineSink) {
	t.Helper()
	out := make(lineSink, 16)
	m := &mcpServer{version: "test", out: out, listenMin: 10 * time.Millisecond, listenMax: time.Second}
	m.gate.window = time.Minute
	if configure != nil {
		configure(m)
	}
	r, w := io.Pipe()
	go m.serve(r)
	t.Cleanup(func() { w.Close() })
	return m, w, out
}

func next(t *testing.T, out lineSink) map[string]any {
	t.Helper()
	select {
	case line := <-out:
		var msg map[string]any
		if err := json.Unmarshal(line, &msg); err != nil {
			t.Fatalf("%s: %v", line, err)
		}
		return msg
	case <-time.After(2 * time.Second):
		t.Fatal("no message from the server")
		return nil
	}
}

func silent(t *testing.T, out lineSink) {
	t.Helper()
	select {
	case line := <-out:
		t.Fatalf("unexpected message %s", line)
	case <-time.After(50 * time.Millisecond):
	}
}

func call(in io.Writer, id int, tool, args string) {
	fmt.Fprintf(in, `{"jsonrpc":"2.0","id":%d,"method":"tools/call","params":{"name":%q,"arguments":%s}}`+"\n", id, tool, args)
}

func toolText(t *testing.T, msg map[string]any) string {
	t.Helper()
	result, ok := msg["result"].(map[string]any)
	if !ok {
		t.Fatalf("no result: %v", msg)
	}
	return result["content"].([]any)[0].(map[string]any)["text"].(string)
}

func isError(msg map[string]any) bool {
	result, _ := msg["result"].(map[string]any)
	b, _ := result["isError"].(bool)
	return b
}

func waitFor(t *testing.T, what string, done func() bool) {
	t.Helper()
	for deadline := time.Now().Add(2 * time.Second); time.Now().Before(deadline); time.Sleep(5 * time.Millisecond) {
		if done() {
			return
		}
	}
	t.Fatal("timed out waiting for", what)
}

func pending(m *mcpServer) int {
	m.mu.Lock()
	defer m.mu.Unlock()
	return len(m.waiting)
}

func TestMCPHandshakeDeclaresAChannelAndTwoTools(t *testing.T) {
	_, in, out := startServer(t, nil)
	io.WriteString(in, `{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}`+"\n")
	result := next(t, out)["result"].(map[string]any)
	capabilities := result["capabilities"].(map[string]any)
	if _, ok := capabilities["experimental"].(map[string]any)["claude/channel"]; !ok {
		t.Fatalf("no channel capability: %v", capabilities)
	}
	if _, ok := capabilities["tools"]; !ok {
		t.Fatalf("no tools capability: %v", capabilities)
	}
	if result["protocolVersion"] != "2025-06-18" || !strings.Contains(result["instructions"].(string), "say tool") ||
		!strings.Contains(result["instructions"].(string), "github.com/GuitarAlchemist/brickyard") {
		t.Fatalf("%v", result)
	}

	io.WriteString(in, `{"jsonrpc":"2.0","method":"notifications/initialized"}`+"\n")
	io.WriteString(in, `{"jsonrpc":"2.0","id":"two","method":"tools/list"}`+"\n")
	list := next(t, out) // the notification got no answer: this is the list's
	if list["id"] != "two" {
		t.Fatalf("%v", list)
	}
	var names []string
	for _, tool := range list["result"].(map[string]any)["tools"].([]any) {
		names = append(names, tool.(map[string]any)["name"].(string))
	}
	if strings.Join(names, ",") != "say,listen" {
		t.Fatalf("tools %v", names)
	}

	io.WriteString(in, `{"jsonrpc":"2.0","id":3,"method":"ping"}`+"\n")
	if ping := next(t, out); ping["id"] != 3.0 || ping["result"] == nil {
		t.Fatalf("%v", ping)
	}
	io.WriteString(in, `{"jsonrpc":"2.0","id":4,"method":"resources/list"}`+"\n")
	if e := next(t, out)["error"].(map[string]any); e["code"] != -32601.0 {
		t.Fatalf("%v", e)
	}
	call(in, 5, "jump", `{}`)
	if e := next(t, out)["error"].(map[string]any); e["code"] != -32602.0 {
		t.Fatalf("%v", e)
	}
}

func TestMCPSayQueuesTheText(t *testing.T) {
	said := make(chan string, 1)
	_, in, out := startServer(t, func(m *mcpServer) {
		m.speak = func(text string, _ int, _ string) (string, error) { said <- text; return "3 words, about 2 s", nil }
	})
	call(in, 1, "say", `{"text":"Bonjour, je t'écoute."}`)
	if r := next(t, out); toolText(t, r) != "Delta is saying it (say 1): 3 words, about 2 s." || isError(r) {
		t.Fatalf("%v", r)
	}
	if got := <-said; got != "Bonjour, je t'écoute." {
		t.Fatalf("said %q", got)
	}
	call(in, 2, "say", `{"text":"  "}`)
	if r := next(t, out); !isError(r) {
		t.Fatalf("empty text accepted: %v", r)
	}
}

// An answer names the utterance it answers, so that the journal ties what was heard to what Delta said back.
func TestMCPSayTiesTheAnswerToTheUtterance(t *testing.T) {
	ids := make(chan int, 2)
	records := make(chan map[string]any, 2)
	_, in, out := startServer(t, func(m *mcpServer) {
		m.speak = func(_ string, id int, _ string) (string, error) { ids <- id; return "5 words, about 3 s", nil }
		m.record = func(event string, fields map[string]any) { fields["event"] = event; records <- fields }
	})
	call(in, 1, "say", `{"text":"Le dépôt IX est GuitarAlchemist ix.","utterance_id":"4"}`)
	if r := next(t, out); toolText(t, r) != "Delta is saying it (say 1, answering utterance 4): 5 words, about 3 s." {
		t.Fatalf("%v", r)
	}
	if id := <-ids; id != 1 {
		t.Fatalf("say id %d", id)
	}
	if r := <-records; r["event"] != "say" || r["say_id"] != 1 || r["utterance_id"] != "4" || r["request"] != "1" ||
		r["text"] != "Le dépôt IX est GuitarAlchemist ix." {
		t.Fatalf("%v", r)
	}
	call(in, 2, "say", `{"text":"Autre chose ?","utterance_id":5}`) // a number is taken too
	if r := next(t, out); toolText(t, r) != "Delta is saying it (say 2, answering utterance 5): 5 words, about 3 s." {
		t.Fatalf("%v", r)
	}
}

func TestMCPRecordsWhereEachUtteranceWent(t *testing.T) {
	records := make(chan map[string]any, 2)
	m, _, out := startServer(t, func(m *mcpServer) {
		m.record = func(event string, fields map[string]any) { fields["event"] = event; records <- fields }
	})
	now := time.Now()
	m.hear(utterance{text: "Delta, quel est le dépôt IX ?", asr: 900 * time.Millisecond}, now)
	if meta := next(t, out)["params"].(map[string]any)["meta"].(map[string]any); meta["utterance_id"] != "1" {
		t.Fatalf("%v", meta)
	}
	if r := <-records; r["event"] != "heard" || r["utterance_id"] != "1" || r["route"] != "channel" ||
		r["asr_ms"] != int64(900) || r["text"] != "Delta, quel est le dépôt IX ?" {
		t.Fatalf("%v", r)
	}
	m.hear(utterance{text: "la télé parle"}, now.Add(time.Hour))
	if r := <-records; r["utterance_id"] != "2" || r["route"] != "ignored" {
		t.Fatalf("%v", r)
	}
}

func TestMCPListenGetsTheNextTranscriptWithoutTheWakeWord(t *testing.T) {
	m, in, out := startServer(t, nil)
	call(in, 7, "listen", `{"timeout_seconds":1}`)
	waitFor(t, "the listen call", func() bool { return pending(m) == 1 })
	if where := m.hear(utterance{text: "on reprend le build"}, time.Now()); where != "listen" {
		t.Fatalf("went to %s", where)
	}
	r := next(t, out)
	if r["id"] != 7.0 || !strings.Contains(toolText(t, r), "on reprend le build") {
		t.Fatalf("%v", r)
	}
	silent(t, out) // and no channel event besides
}

func TestMCPListenTimesOut(t *testing.T) {
	m, in, out := startServer(t, nil)
	call(in, 8, "listen", `{"timeout_seconds":0.02}`)
	if text := toolText(t, next(t, out)); !strings.HasPrefix(text, "Nothing heard") {
		t.Fatalf("%q", text)
	}
	if pending(m) != 0 {
		t.Fatal("the timed-out call still waits")
	}
}

func TestMCPCancelledListenGetsNoAnswer(t *testing.T) {
	m, in, out := startServer(t, nil)
	call(in, 9, "listen", `{"timeout_seconds":1}`)
	waitFor(t, "the listen call", func() bool { return pending(m) == 1 })
	io.WriteString(in, `{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":9,"reason":"user"}}`+"\n")
	waitFor(t, "the cancellation", func() bool { return pending(m) == 0 })
	silent(t, out)
}

func TestMCPListenSaysWhyDeltaCannotHearYet(t *testing.T) {
	_, in, out := startServer(t, func(m *mcpServer) {
		m.ready = func() error { return fmt.Errorf("Delta is still starting") }
	})
	call(in, 1, "listen", `{}`)
	if r := next(t, out); !isError(r) || toolText(t, r) != "Delta is still starting" {
		t.Fatalf("%v", r)
	}
}

func TestMCPChannelNeedsTheWakeWordToStartAConversation(t *testing.T) {
	thoughtful := 0
	m, _, out := startServer(t, func(m *mcpServer) { m.pushed = func() { thoughtful++ } })
	now := time.Now()
	if where := m.hear(utterance{text: "et la météo demain"}, now); where != "ignored" {
		t.Fatalf("a stray voice went to %s", where)
	}
	silent(t, out)

	if where := m.hear(utterance{text: "Delta, lance les tests.", asr: 1500 * time.Millisecond}, now); where != "channel" {
		t.Fatalf("went to %s", where)
	}
	event := next(t, out)
	params := event["params"].(map[string]any)
	meta := params["meta"].(map[string]any)
	if event["method"] != "notifications/claude/channel" || params["content"] != "Delta, lance les tests." ||
		meta["utterance_id"] != "2" || meta["asr_ms"] != "1500" || event["id"] != nil {
		t.Fatalf("%v", event)
	}

	// Within the window, the conversation goes on without the wake word, and each transcript extends it.
	if where := m.hear(utterance{text: "et aussi le lint"}, now.Add(50*time.Second)); where != "channel" {
		t.Fatalf("went to %s", where)
	}
	next(t, out)
	if where := m.hear(utterance{text: "la télé parle"}, now.Add(3*time.Minute)); where != "ignored" {
		t.Fatalf("went to %s after the window", where)
	}
	if thoughtful != 2 {
		t.Fatalf("Delta looked thoughtful %d times, not 2", thoughtful)
	}
}

func TestNamesDeltaAmongTheFirstWords(t *testing.T) {
	for text, want := range map[string]bool{
		"Delta, lance les tests.":                  true,
		"Bon, Delta ?":                             true,
		"DELTA! Tu m'entends":                      true,
		"Il faut que tu dises à Delta de chercher": false, // seventh word
		"Les deltas du Rhône":                      false,
		"":                                         false,
	} {
		if got := namesDelta(text); got != want {
			t.Errorf("namesDelta(%q) = %v", text, got)
		}
	}
}

func TestHomePathsPreferDeltaJSONThenTheHomeLayout(t *testing.T) {
	home := t.TempDir()
	os.WriteFile(filepath.Join(home, "delta.json"), []byte(`{"piper":"D:\\voices\\piper.exe","wake_word":false}`), 0o644)
	c := mcpConfig{home: home, whisper: "E:\\w\\whisper-server.exe"}
	homePaths(&c)
	if c.piper != `D:\voices\piper.exe` || c.whisper != `E:\w\whisper-server.exe` || !c.wakeOff ||
		c.voice != filepath.Join(home, "voices", "fr_FR-siwis-medium.onnx") ||
		c.whisperModel != filepath.Join(home, "whisper", "ggml-large-v3-turbo-q5_0.bin") {
		t.Fatalf("%+v", c)
	}
}
