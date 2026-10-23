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
	m.gate.window = 20 * time.Second
	m.gate.follow = 15 * time.Second
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

func TestMCPHandshakeDeclaresAChannelAndItsTools(t *testing.T) {
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
	if strings.Join(names, ",") != "say,listen,window,shelf" {
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
		m.speak = func(text string, _ int, _, _ string) (string, error) { said <- text; return "3 words, about 2 s", nil }
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
		m.speak = func(_ string, id int, _, _ string) (string, error) { ids <- id; return "5 words, about 3 s", nil }
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
		r["asr_ms"] != int64(900) || r["text"] != "Delta, quel est le dépôt IX ?" || r["reason"] != "called Delta" {
		t.Fatalf("%v", r)
	}
	m.hear(utterance{text: "Gaïa, la télé parle"}, now.Add(time.Hour))
	if r := <-records; r["utterance_id"] != "2" || r["route"] != "ignored" || r["reason"] != "spoken to Gaïa" {
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

	// Right after naming Delta, the user may go on in another sentence without naming it again.
	if where := m.hear(utterance{text: "et aussi le lint"}, now.Add(10*time.Second)); where != "channel" {
		t.Fatalf("went to %s", where)
	}
	next(t, out)
	// Each sentence for Delta keeps the conversation open for as long again, as the window shows.
	if where := m.hear(utterance{text: "et la doc"}, now.Add(20*time.Second)); where != "channel" {
		t.Fatalf("went to %s in a conversation that went on", where)
	}
	next(t, out)
	// Silence ends it.
	if where := m.hear(utterance{text: "et la météo"}, now.Add(40*time.Second)); where != "ignored" {
		t.Fatalf("went to %s after the follow-up window", where)
	}
	// After Delta speaks, the user may answer it without naming it.
	m.gate.open(now.Add(60 * time.Second))
	if where := m.hear(utterance{text: "oui, vas-y"}, now.Add(70*time.Second)); where != "channel" {
		t.Fatalf("went to %s", where)
	}
	next(t, out)
	// A sentence for someone else is not for Delta, and ends the conversation.
	if where := m.hear(utterance{text: "Gaïa, note ça."}, now.Add(72*time.Second)); where != "ignored" {
		t.Fatalf("went to %s", where)
	}
	if where := m.hear(utterance{text: "et ensuite"}, now.Add(74*time.Second)); where != "ignored" {
		t.Fatalf("went to %s after the user turned to someone else", where)
	}
	silent(t, out)
	if thoughtful != 4 {
		t.Fatalf("Delta looked thoughtful %d times, not 4", thoughtful)
	}
}

// The sentences are those of the author's first live sessions (2026-10-22), where Delta's microphone also heard what
// was said to the Claude session in a terminal and to Gaïa. The author then named that session Brutal, and kept
// Claude for Delta's brain.
func TestAddressee(t *testing.T) {
	for text, want := range map[string]string{
		"Delta, quel est le dépôt IX ?":                       "Delta",
		"Bon, Delta ?":                                        "Delta",
		"DELTA! Tu m'entends":                                 "Delta",
		"Delta, déploie ça dans le cloud.":                    "Delta",
		"Delta, envoie ça à Gaïa.":                            "Delta",
		"Ok, Claude, améliore la rétro.":                      "Claude",
		"Allô, Claude, est-ce que tu peux améliorer tout ça?": "Claude",
		"Cloud Code, regarde ça.":                             "Claude",
		"Le cloud est lent.":                                  "",
		"Brutal, fusionne la branche.":                        "Brutal",
		"Oui, Delta, je parle à Brutal directement.":          "Brutal",
		"Quand je parle à Brutal, c'est à Brutal que je parle, c'est pas à Delta.": "Brutal",
		"X, relance le build.":                     "IX",
		"Ok Gaia, tais-toi parce que Delta parle.": "Gaïa",
		"Non, Gaïa, je vais toujours à d'altar.":   "Gaïa",
		"Delta, je ne te parle pas.":               "someone else",
		"Delta, c'est pas pour toi.":               "someone else",
		"Ok, c'est fait, Delta est lancé.":         "", // fifth word: not a call
		"Il faut que tu dises à Delta de chercher": "",
		"Les deltas du Rhône":                      "",
		"Delta, what is IX's repository?":          "Delta",
		"Hey Claude, run the tests.":               "Claude",
		"Hey Delter, can you hear me?":             "Delta", // as Whisper heard it in English, 2026-10-22
		"Gaia, start the watcher.":                 "Gaïa",
		"I'm not talking to you, Delta.":           "someone else",
		"That's not for you.":                      "someone else",
		"No, I was talking to Codex.":              "Codex",
		"The weather is nice today.":               "",
		"":                                         "",
	} {
		if got := addressee(text); got != want {
			t.Errorf("addressee(%q) = %q, not %q", text, got, want)
		}
	}
}

func TestMCPTellingDeltaItIsNotSpokenToEndsTheConversation(t *testing.T) {
	m, _, out := startServer(t, nil)
	now := time.Now()
	m.gate.open(now) // Delta just spoke
	if where := m.hear(utterance{text: "Delta, je ne te parle pas."}, now.Add(time.Second)); where != "ignored" {
		t.Fatalf("went to %s", where)
	}
	if where := m.hear(utterance{text: "je disais donc"}, now.Add(2*time.Second)); where != "ignored" {
		t.Fatalf("went to %s", where)
	}
	silent(t, out)
}

func TestHomePathsPreferDeltaJSONThenTheHomeLayout(t *testing.T) {
	home := t.TempDir()
	os.WriteFile(filepath.Join(home, "delta.json"), []byte(`{"piper":"D:\\voices\\piper.exe","wake_word":false}`), 0o644)
	c := mcpConfig{home: home, whisper: "E:\\w\\whisper-server.exe"}
	homePaths(&c)
	if c.piper != `D:\voices\piper.exe` || c.whisper != `E:\w\whisper-server.exe` || !c.wakeOff ||
		c.voice != filepath.Join(home, "voices", "en_US-ljspeech-medium.onnx") ||
		c.whisperModel != filepath.Join(home, "whisper", "ggml-large-v3-turbo-q5_0.bin") {
		t.Fatalf("%+v", c)
	}
}

func TestMCPSayShowsAnExpression(t *testing.T) {
	var s spoken
	_, in, out := startServer(t, func(m *mcpServer) { m.speak = s.speak })
	call(in, 1, "say", `{"text":"Les tests passent.","mood":"proud"}`)
	if r := next(t, out); isError(r) {
		t.Fatalf("%v", r)
	}
	call(in, 2, "say", `{"text":"Hmm.","mood":"rage"}`)
	if r := next(t, out); !isError(r) || !strings.Contains(toolText(t, r), "joy") {
		t.Fatalf("an unknown expression: %v", r)
	}
	if got := s.faces(); got != "proud" {
		t.Fatalf("faces %q", got)
	}
}

func TestClaudeMovesDeltasWindowAndWalksItsShelf(t *testing.T) {
	var shown scenery
	m, in, out := startServer(t, func(m *mcpServer) { m.show = shown.show })
	for i, c := range []struct {
		tool, args string
		fails      bool
	}{
		{"window", `{"action":"maximize"}`, false},
		{"window", `{"action":"dance"}`, true},
		{"shelf", `{"move":"plugins"}`, false},
		{"shelf", `{"move":"delete"}`, true},
	} {
		call(in, i+1, c.tool, c.args)
		if r := next(t, out); isError(r) != c.fails {
			t.Fatalf("%s %s: %v", c.tool, c.args, r)
		}
	}
	if got := shown.all(); got != `{"window":"maximize"} {"shelf":"plugins"}` {
		t.Fatalf("showed %s", got)
	}
	if where := m.hear(utterance{text: "Delta, descends."}, time.Now()); where != "shelf" {
		t.Fatalf("the shelf Claude opened does not take the next move: %s", where)
	}

	_, in, out = startServer(t, nil)
	call(in, 1, "window", `{"action":"maximize"}`)
	if r := next(t, out); !isError(r) {
		t.Fatalf("no window, yet %v", r)
	}
}

// A sentence for Claude gets a word at once, which the journal ties to it, a different word each time; not one heard
// while Delta speaks.
func TestDeltaAcknowledgesASentenceForClaudeAtOnce(t *testing.T) {
	var acks []string
	var records []map[string]any
	quiet := true
	m, _, out := startServer(t, func(m *mcpServer) {
		m.lang = languages["fr"]
		m.ack = func(text string, id int) { acks = append(acks, fmt.Sprint(id, " ", text)) }
		m.quiet = func() bool { return quiet }
		m.record = func(event string, fields map[string]any) { fields["event"] = event; records = append(records, fields) }
	})
	now := time.Now()
	m.hear(utterance{text: "Delta, quelle heure est-il ?"}, now)
	next(t, out)
	if len(acks) != 1 || acks[0] != "1 "+languages["fr"].acks[0] {
		t.Fatalf("%q", acks)
	}
	say, heard := records[0], records[1]
	if say["event"] != "say" || say["by"] != "ack" || say["say_id"] != 1 || say["utterance_id"] != "1" ||
		heard["event"] != "heard" || heard["ack"] != 1 {
		t.Fatalf("%v", records)
	}
	m.hear(utterance{text: "Delta, et demain ?"}, now.Add(time.Second))
	next(t, out)
	if len(acks) != 2 || acks[1] != "2 "+languages["fr"].acks[1] {
		t.Fatalf("the next word: %q", acks)
	}
	quiet = false
	m.hear(utterance{text: "Delta, encore une chose."}, now.Add(2*time.Second))
	next(t, out)
	if len(acks) != 2 {
		t.Fatalf("while Delta speaks: %q", acks)
	}
}

// The mouth drops an acknowledgement that something waits behind, such as Claude's answer, or that comes too late.
func TestAnAcknowledgementGivesWayToTheAnswer(t *testing.T) {
	now := time.Now()
	ack := said{text: "Une seconde.", ack: true, at: now}
	for _, c := range []struct {
		s       said
		waiting int
		after   time.Duration
		dropped bool
	}{
		{ack, 0, 200 * time.Millisecond, false},
		{ack, 1, 200 * time.Millisecond, true},
		{ack, 0, 2 * time.Second, true},
		{said{text: "La réponse."}, 3, time.Hour, false},
	} {
		if why := unsaid(c.s, c.waiting, now.Add(c.after)); (why != "") != c.dropped {
			t.Errorf("%+v, %d waiting, after %v: %q", c.s, c.waiting, c.after, why)
		}
	}
}

// A sentence the wake gate let pass for want of « Delta » shows in the window, so that the user knows it was heard;
// without bringing a closed window back, and not a filler, nor a sentence for another session.
// The window shows each sentence Delta heard, whole, and where it went: to Claude, to Delta itself, to no one for want
// of « Delta », or to whom it named. Fillers, and copies of a sentence the other device heard, are not shown.
func TestTheWindowShowsWhatDeltaHeardAndWhereItWent(t *testing.T) {
	var told, shown []map[string]any
	m, _, out := startServer(t, func(m *mcpServer) {
		m.tell = func(line map[string]any) {
			if heard, ok := line["heard"].(map[string]any); ok {
				told = append(told, heard)
			}
		}
		m.show = func(line map[string]any) { shown = append(shown, line) }
	})
	now := time.Now().Add(time.Hour)
	for text, want := range map[string]string{
		"Est-ce que tu peux danser ?": "wake",
		"Euh...":                      "",
		"Gaïa, lance le watcher.":     "Gaïa",
	} {
		told = nil
		if where := m.hear(utterance{text: text}, now); where != "ignored" {
			t.Fatalf("%q went to %s", text, where)
		}
		if want == "" && len(told) != 0 || want != "" && (len(told) != 1 || told[0]["text"] != text || told[0]["to"] != want) {
			t.Fatalf("%q: told %v", text, told)
		}
	}
	silent(t, out)
	told = nil
	m.hear(utterance{text: "Delta, lance les tests."}, now)
	next(t, out)
	if len(told) != 1 || told[0]["to"] != "claude" || len(shown) != 0 {
		t.Fatalf("told %v, shown %v", told, shown)
	}
	long := strings.Repeat("blabla ", 120)
	m.hear(utterance{text: long}, now)
	next(t, out)
	if got := told[1]["text"].(string); len([]rune(got)) > 400 || !strings.HasSuffix(got, "…") {
		t.Fatalf("a long sentence is cut: %d runes", len([]rune(got)))
	}
}

// The remote learns whom each sentence answers from its say note, journal or not (remote.go).
func TestTheSayNoteTellsTheRemoteWhomASentenceAnswers(t *testing.T) {
	var s spoken
	var told []map[string]any
	_, in, out := startServer(t, func(m *mcpServer) {
		m.speak = s.speak
		m.attribute = func(say map[string]any) { told = append(told, say) }
	})
	call(in, 1, "say", `{"text":"Il est midi.","utterance_id":"4"}`)
	next(t, out)
	if len(told) != 1 || told[0]["say_id"] != 1 || told[0]["utterance_id"] != "4" || told[0]["by"] != "claude" {
		t.Fatalf("%v", told)
	}
}

// The computer's microphone and a phone in the same room both hear a sentence, the phone's copy a few seconds later:
// it goes to Claude once. A copy the wake gate ignored here does not hold back the phone's, and a short answer said
// twice is two answers.
func TestASentenceHeardByTwoDevicesGoesOnce(t *testing.T) {
	records := make(chan map[string]any, 16)
	m, _, _ := startServer(t, func(m *mcpServer) {
		m.record = func(event string, fields map[string]any) {
			if event == "heard" {
				records <- fields
			}
		}
	})
	route := func(text, source string, at time.Time) string {
		m.hear(utterance{text: text, source: source}, at)
		return (<-records)["route"].(string)
	}
	now := time.Now()
	if r := route("Delta, je pense qu'on devrait supporter les deux modes, l'appel ou le bouton.", "", now); r != "channel" {
		t.Fatalf("the computer's copy: %s", r)
	}
	if r := route("Delta, je pense qu'on devrait supporter les deux modes : l'appel ou le bouton « je t'écoute ».", "remote", now.Add(2*time.Second)); r != "duplicate" {
		t.Fatalf("the phone's copy: %s", r)
	}
	if r := route("Delta, je pense qu'on devrait supporter les deux modes, l'appel ou le bouton.", "remote", now.Add(20*time.Second)); r != "channel" {
		t.Fatalf("said again much later: %s", r)
	}

	later := now.Add(time.Hour)
	if r := route("Et la version du tableau de bord, on la change ?", "", later); r != "ignored" {
		t.Fatalf("without Delta: %s", r)
	}
	if r := route("Et la version du tableau de bord, on la change ?", "remote", later.Add(2*time.Second)); r != "channel" {
		t.Fatalf("the phone's copy of a sentence ignored here: %s", r)
	}
	if r := route("Oui.", "", later.Add(3*time.Second)); r != "channel" {
		t.Fatalf("an answer: %s", r)
	}
	if r := route("Oui.", "remote", later.Add(4*time.Second)); r == "duplicate" {
		t.Fatal("a short answer said on each device is two answers")
	}
}

func TestTheJournalSaysWhenWhisperRepeatedASentence(t *testing.T) {
	records := make(chan map[string]any, 4)
	m, _, out := startServer(t, func(m *mcpServer) {
		m.record = func(event string, fields map[string]any) {
			if event == "heard" {
				records <- fields
			}
		}
	})
	m.hear(utterance{text: "Delta, quel est le dépôt IX ? " + strings.Repeat("Parfait. Merci. ", 1) + strings.Repeat("C'est bon pour moi. ", 4), source: "remote"}, time.Now())
	if content := next(t, out)["params"].(map[string]any)["content"]; content != "Delta, quel est le dépôt IX ? Parfait. Merci. C'est bon pour moi." {
		t.Fatalf("Claude got %q", content)
	}
	if r := <-records; r["repeats_collapsed"] != 3 || r["text"] != "Delta, quel est le dépôt IX ? Parfait. Merci. C'est bon pour moi." {
		t.Fatalf("%v", r)
	}
}

// The window shows until when Delta listens without its name: each change of the conversation's end goes to it.
func TestTheWindowShowsHowLongDeltaListens(t *testing.T) {
	var ends []time.Time
	m, _, _ := startServer(t, func(m *mcpServer) { m.gate.changed = func(until time.Time) { ends = append(ends, until) } })
	now := time.Now()
	m.hear(utterance{text: "Delta, lance les tests."}, now)
	m.gate.open(now.Add(5 * time.Second)) // Delta answers
	m.hear(utterance{text: "Gaïa, note ça."}, now.Add(6*time.Second))
	m.hear(utterance{text: "et la météo"}, now.Add(7*time.Second))
	if len(ends) != 3 || !ends[0].Equal(now.Add(15*time.Second)) || !ends[1].Equal(now.Add(25*time.Second)) || !ends[2].IsZero() {
		t.Fatalf("%v", ends)
	}
}
