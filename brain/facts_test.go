package main

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestAsksAFactAboutWhatTheSheetHolds(t *testing.T) {
	for text, want := range map[string]bool{
		"Delta, quelle heure est-il ?":            true,
		"Delta, il est quelle heure":              false, // no question mark, and it does not open on a question word
		"Delta, quelle heure il est":              true,
		"Delta, c'est quoi ta version ?":          true,
		"Delta, c'est quoi X ?":                   true, // Whisper writes IX as X
		"Delta, Whisper tourne sur quoi ?":        true,
		"Delta, tu peux relire la PR de brutal ?": false,
		"Delta, mets l'heure dans les notes.":     false,
		"What time is it, Delta?":                 true,
		"Delta, what's the weather?":              false,
	} {
		if got := asksAFact(text); got != want {
			t.Errorf("asksAFact(%q) = %v", text, got)
		}
	}
}

func TestTheSheetSaysWhatAVoiceCanRead(t *testing.T) {
	for v, want := range map[string]string{
		"0.4.0":                             "0.4.0",
		"dev-v0.4.0-8-g83b7585":             "0.4.0, a development build 8 commits after it",
		"dev-v0.4.0-8-g83b7585-dirty-21000": "0.4.0, a development build 8 commits after it",
		"dev-v0.4.0":                        "0.4.0, a development build",
		"dev-a86712d":                       "a development build",
	} {
		if got := spokenVersion(v); got != want {
			t.Errorf("spokenVersion(%q) = %q", v, got)
		}
	}
	at := time.Date(2026, 10, 22, 21, 5, 0, 0, time.Local)
	if got := spokenNow(at, "fr"); got != "jeudi 22 octobre 2026, 21 h 05" {
		t.Errorf("%q", got)
	}
	if got := spokenNow(at, "en"); got != "Thursday, October 22, 2026, 9:05 PM" {
		t.Errorf("%q", got)
	}
}

// A question the sheet answers is answered at no cost, and Claude reads the exchange with the next event; one it does
// not answer goes to Claude; any other sentence does not wait for the fact model.
func TestDeltaAnswersAFactFromItsSheet(t *testing.T) {
	var s spoken
	var sheets []string
	known := factVerdict{Known: true, Answer: "Il est 21 h 05."}
	m, _, out := startServer(t, func(m *mcpServer) {
		m.speak = s.speak
		m.version = "dev-v0.4.0-8-g83b7585"
		m.hearsOn = func() string { return "on the graphics card" }
		m.smallModel = "qwen3:8b"
		m.triage = fixed(verdict{Route: "claude"}, nil)
		m.facts = func(_ context.Context, sheet, text string) (factVerdict, error) {
			sheets = append(sheets, sheet)
			return known, nil
		}
	})
	now := time.Now()
	if where := m.hear(utterance{text: "Delta, quelle heure est-il ?"}, now); where != "local" {
		t.Fatalf("went to %s", where)
	}
	silent(t, out)
	if s.all() != "free: Il est 21 h 05." {
		t.Fatalf("said %q", s.all())
	}
	sheet := sheets[0]
	for _, line := range []string{"- Now: ", "Delta's version: 0.4.0, a development build 8 commits after it.",
		"Delta hears with Whisper (whisper.cpp), on the graphics card.", "qwen3:8b", "GuitarAlchemist/ix"} {
		if !strings.Contains(sheet, line) {
			t.Errorf("the sheet lacks %q:\n%s", line, sheet)
		}
	}

	known = factVerdict{}
	if where := m.hear(utterance{text: "Delta, quelle heure est-il à Tokyo ?"}, now.Add(time.Second)); where != "channel" {
		t.Fatalf("a question the sheet does not answer went to %s", where)
	}
	if meta := next(t, out)["params"].(map[string]any)["meta"].(map[string]any); !strings.Contains(meta["handled_locally"].(string), "Il est 21 h 05.") {
		t.Fatalf("Claude does not read the local answer: %v", meta)
	}
	asked := len(sheets)
	if where := m.hear(utterance{text: "Delta, tu peux relire la PR ?"}, now.Add(2*time.Second)); where != "channel" || len(sheets) != asked {
		t.Fatalf("went to %s, the fact model asked %d times", where, len(sheets)-asked)
	}
	next(t, out)
}

func TestOllamaFactsAsksForTypedJSON(t *testing.T) {
	var body map[string]any
	var raw string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		data, _ := io.ReadAll(r.Body)
		raw = string(data)
		json.Unmarshal(data, &body)
		w.Write([]byte(`{"message":{"role":"assistant","content":"{\"known\":true,\"answer\":\" Il est 21 h 05. \"}"},"done":true}`))
	}))
	defer server.Close()
	f, err := ollamaFacts(server.URL, "qwen3:8b", languages["fr"])(context.Background(), "- Now: jeudi.", "Quelle heure est-il ?")
	if err != nil || !f.Known || f.Answer != "Il est 21 h 05." {
		t.Fatalf("%+v %v", f, err)
	}
	if format := raw[strings.Index(raw, `"format"`):]; strings.Index(format, `"known"`) > strings.Index(format, `"answer"`) {
		t.Fatalf("the schema asks for the answer before known: %s", format)
	}
	messages := body["messages"].([]any)
	system := messages[0].(map[string]any)["content"].(string)
	user := messages[1].(map[string]any)["content"].(string)
	if body["model"] != "qwen3:8b" || body["think"] != false || !strings.Contains(system, "a French speaker") ||
		!strings.HasSuffix(system, "- Now: jeudi.") || user != `Question: "Quelle heure est-il ?"` {
		t.Fatalf("%v", body)
	}
}
