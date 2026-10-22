package main

import (
	"context"
	"encoding/json"
	"errors"
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

// spoken collects what the triage made Delta say, with its cost.
type spoken struct {
	mu    sync.Mutex
	lines []string
}

func (s *spoken) speak(text string, _ int, cost string) (string, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.lines = append(s.lines, cost+": "+text)
	return "1 word, about 1 s", nil
}

func (s *spoken) all() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return strings.Join(s.lines, " | ")
}

func fixed(v verdict, err error) triage {
	return func(context.Context, []string, string) (verdict, error) { return v, err }
}

func TestTriageAnswersSmallTalkAtNoCostAndClaudeSeesNothing(t *testing.T) {
	var s spoken
	var records []map[string]any
	m, _, out := startServer(t, func(m *mcpServer) {
		m.speak = s.speak
		m.triage = fixed(verdict{Route: "local", Answer: "Avec plaisir !"}, nil)
		m.record = func(event string, fields map[string]any) { fields["event"] = event; records = append(records, fields) }
	})
	if where := m.hear(utterance{text: "Merci Delta, c'est cool."}, time.Now()); where != "local" {
		t.Fatalf("went to %s", where)
	}
	silent(t, out)
	if got := s.all(); got != "free: Avec plaisir !" {
		t.Fatalf("said %q", got)
	}
	if len(records) != 2 || records[0]["event"] != "say" || records[0]["by"] != "local" ||
		records[1]["event"] != "heard" || records[1]["route"] != "local" || records[1]["triage"] != "local" {
		t.Fatalf("%v", records)
	}
}

func TestTriageDropsWhatIsNotForDelta(t *testing.T) {
	var s spoken
	m, _, out := startServer(t, func(m *mcpServer) {
		m.speak = s.speak
		m.triage = fixed(verdict{Route: "ignore"}, nil)
	})
	if where := m.hear(utterance{text: "Delta, ok Gaia, tais-toi."}, time.Now()); where != "dropped" {
		t.Fatalf("went to %s", where)
	}
	silent(t, out)
	if s.all() != "" {
		t.Fatalf("said %q", s.all())
	}
}

func TestTriageNeedsNoModelForFillersAndArtefacts(t *testing.T) {
	asked := 0
	m, _, out := startServer(t, func(m *mcpServer) {
		m.triage = func(context.Context, []string, string) (verdict, error) {
			asked++
			return verdict{Route: "claude"}, nil
		}
		m.gate.off = true
	})
	for _, text := range []string{"Ah!", "Hmm.", "Euh... ben", "하게.", "..."} {
		if where := m.hear(utterance{text: text}, time.Now()); where != "dropped" {
			t.Errorf("%q went to %s", text, where)
		}
	}
	// What is said to another session stops at the wake gate, even with the gate off.
	for _, text := range []string{"Gaïa, lance le watcher.", "Non, Gaia, je parle à Delta."} {
		if where := m.hear(utterance{text: text}, time.Now()); where != "ignored" {
			t.Errorf("%q went to %s", text, where)
		}
	}
	silent(t, out)
	if asked != 0 {
		t.Fatalf("the model was asked %d times", asked)
	}
}

func TestClaudeGetsWhatTheTriageCannotSort(t *testing.T) {
	for name, tr := range map[string]triage{
		"a failure": fixed(verdict{}, errors.New("ollama: connection refused")),
		"a slow model": func(ctx context.Context, _ []string, _ string) (verdict, error) {
			<-ctx.Done()
			return verdict{}, ctx.Err()
		},
		"a request": fixed(verdict{Route: "claude"}, nil),
	} {
		t.Run(name, func(t *testing.T) {
			m, _, out := startServer(t, func(m *mcpServer) { m.triage = tr; m.triageMax = 20 * time.Millisecond })
			start := time.Now()
			if where := m.hear(utterance{text: "Delta, lance les tests."}, start); where != "channel" {
				t.Fatalf("went to %s", where)
			}
			if time.Since(start) > time.Second {
				t.Fatal("the triage held the transcript too long")
			}
			if p := next(t, out)["params"].(map[string]any); p["content"] != "Delta, lance les tests." {
				t.Fatalf("%v", p)
			}
		})
	}
}

// When Claude has just asked something, even a bare "ok" is an answer for Claude, whatever a small model would think.
func TestAnAnswerToClaudesQuestionSkipsTheTriage(t *testing.T) {
	asked := 0
	m, in, out := startServer(t, func(m *mcpServer) {
		m.speak = func(string, int, string) (string, error) { return "4 words, about 2 s", nil }
		m.triage = func(context.Context, []string, string) (verdict, error) {
			asked++
			return verdict{Route: "local", Answer: "D'accord."}, nil
		}
	})
	call(in, 1, "say", `{"text":"Je lance la migration sur la base de test ?"}`)
	next(t, out)
	if where := m.hear(utterance{text: "Delta, ok."}, time.Now()); where != "channel" {
		t.Fatalf("went to %s", where)
	}
	next(t, out)
	if asked != 0 {
		t.Fatal("the triage was asked")
	}
	// The question is answered: the next small talk is the triage's again.
	if where := m.hear(utterance{text: "Merci."}, time.Now()); where != "local" || asked != 1 {
		t.Fatalf("went to %s, triage asked %d times", where, asked)
	}
}

func TestClaudeReadsWhatTheTriageAnsweredWithTheNextEvent(t *testing.T) {
	var s spoken
	var seen [][]string
	verdicts := []verdict{{Route: "local", Answer: "Salut !"}, {Route: "claude"}}
	m, _, out := startServer(t, func(m *mcpServer) {
		m.speak = s.speak
		m.triage = func(_ context.Context, recent []string, _ string) (verdict, error) {
			seen = append(seen, recent)
			if len(verdicts) == 0 {
				return verdict{}, errors.New("no verdict left")
			}
			v := verdicts[0]
			verdicts = verdicts[1:]
			return v, nil
		}
	})
	now := time.Now()
	m.hear(utterance{text: "Salut Delta !"}, now)
	m.hear(utterance{text: "Quel est le dépôt IX ?"}, now.Add(time.Second))
	meta := next(t, out)["params"].(map[string]any)["meta"].(map[string]any)
	if meta["handled_locally"] != "User: Salut Delta ! / Delta: Salut !" {
		t.Fatalf("%v", meta)
	}
	if len(seen) != 2 || len(seen[0]) != 0 || strings.Join(seen[1], "\n") != "User: Salut Delta !\nDelta: Salut !" {
		t.Fatalf("the triage saw %q", seen)
	}
	m.hear(utterance{text: "Et le dépôt GA ?"}, now.Add(2*time.Second)) // the triage has no verdict left: Claude gets it
	if meta := next(t, out)["params"].(map[string]any)["meta"].(map[string]any); meta["handled_locally"] != nil {
		t.Fatalf("told twice: %v", meta)
	}
}

func TestParseVerdict(t *testing.T) {
	for content, want := range map[string]string{
		`{"route":"local","answer":" Salut ! "}`:   "local:Salut !",
		`{"route":"claude","answer":"je regarde"}`: "claude:",
		`{"route":"ignore","answer":""}`:           "ignore:",
		`{"route":"maybe","answer":""}`:            "error",
		`Salut !`:                                  "error",
	} {
		v, err := parseVerdict(content)
		got := v.Route + ":" + v.Answer
		if err != nil {
			got = "error"
		}
		if got != want {
			t.Errorf("%s: %s, not %s", content, got, want)
		}
	}
}

func TestOllamaTriageAsksForAVerdictInJSON(t *testing.T) {
	var body map[string]any
	var raw string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/chat" {
			http.NotFound(w, r)
			return
		}
		data, _ := io.ReadAll(r.Body)
		raw = string(data)
		json.Unmarshal(data, &body)
		w.Write([]byte(`{"message":{"role":"assistant","content":"{\"route\":\"local\",\"answer\":\"Cool !\"}"},"done":true}`))
	}))
	defer server.Close()
	v, err := ollamaTriage(server.URL, "qwen3:8b")(context.Background(), []string{"Delta: Bonjour."}, "Cool.")
	if err != nil || v.Route != "local" || v.Answer != "Cool !" {
		t.Fatalf("%+v %v", v, err)
	}
	if format := raw[strings.Index(raw, `"format"`):]; strings.Index(format, `"route"`) > strings.Index(format, `"answer"`) {
		t.Fatalf("the schema asks for the answer before the route: %s", format)
	}
	messages := body["messages"].([]any)
	user := messages[1].(map[string]any)["content"].(string)
	if body["model"] != "qwen3:8b" || body["stream"] != false || body["think"] != false || body["format"] == nil ||
		!strings.Contains(user, "Delta: Bonjour.") || !strings.HasSuffix(user, `New sentence: "Cool."`) {
		t.Fatalf("%v", body)
	}
}

func TestNamesAnotherAssistantInsteadOfDelta(t *testing.T) {
	for text, want := range map[string]bool{
		"Ok, Claude, est-ce que tu peux...":            false, // Claude is Delta's brain
		"Gaia, tais-toi.":                              true,
		"Ok Gaia, tais-toi parce que Delta parle.":     true, // Delta comes too late to be called
		"Delta, demande à Claude de lancer les tests.": false,
		"Quand je parle à Claude, c'est à Claude":      false, // fifth word: a sentence about Claude, for the model
		"Codex, relis la PR.":                          true,
		"X, tu peux lancer ta sonde ?":                 true,
	} {
		if got := namesAnother(text); got != want {
			t.Errorf("namesAnother(%q) = %v", text, got)
		}
	}
}

// « Ok, Claude, ... » is meant for Delta's brain: it opens the gate like « Delta » and skips the small model.
func TestCallingClaudeGoesStraightToClaude(t *testing.T) {
	asked := 0
	m, _, out := startServer(t, func(m *mcpServer) {
		m.triage = func(context.Context, []string, string) (verdict, error) {
			asked++
			return verdict{Route: "ignore"}, nil
		}
	})
	for _, text := range []string{"Ok, Claude, essaie d'améliorer tous les points de la rétro.", "Cloud Code, lance les tests."} {
		if where := m.hear(utterance{text: text}, time.Now().Add(time.Hour)); where != "channel" {
			t.Fatalf("%q went to %s", text, where)
		}
		next(t, out)
	}
	if asked != 0 {
		t.Fatalf("the model was asked %d times", asked)
	}
}

func TestAQuestionTheModelKeptGoesToClaude(t *testing.T) {
	var s spoken
	m, _, out := startServer(t, func(m *mcpServer) {
		m.speak = s.speak
		m.triage = fixed(verdict{Route: "local", Answer: "Oui, c'est moi."}, nil)
	})
	text := "Ah d'accord, donc quand le voyant est vert, c'est toi-même qui réfléchis, c'est ça ?"
	if where := m.hear(utterance{text: "Delta, " + text}, time.Now()); where != "channel" {
		t.Fatalf("went to %s", where)
	}
	next(t, out)
	if s.all() != "" {
		t.Fatalf("said %q", s.all())
	}
}

func TestAParrotIsNotSaid(t *testing.T) {
	var s spoken
	var heard map[string]any
	m, _, out := startServer(t, func(m *mcpServer) {
		m.speak = s.speak
		m.triage = fixed(verdict{Route: "local", Answer: "C'est trop bien, ce que tu fais."}, nil)
		m.record = func(event string, fields map[string]any) {
			if event == "heard" {
				heard = fields
			}
		}
	})
	if where := m.hear(utterance{text: "Delta, c'est trop bien ce que tu fais."}, time.Now()); where != "local" {
		t.Fatalf("went to %s", where)
	}
	silent(t, out)
	if s.all() != "" || heard["parrot"] != "C'est trop bien, ce que tu fais." {
		t.Fatalf("said %q, journal %v", s.all(), heard)
	}
}

func TestParrots(t *testing.T) {
	for pair, want := range map[[2]string]bool{
		{"Ok.", "Ok."}:                                             true,
		{"Ok cool.", "Ok cool."}:                                   true,
		{"Salut !", "Salut Delta !"}:                               false,
		{"Bonne nuit !", "Bonne nuit Delta."}:                      false,
		{"Ok.", "Delta, ok."}:                                      true,
		{"De rien, c'est parfait.", "Merci Delta, c'est parfait."}: false,
		{"", "Ok."}:                                                false,
	} {
		if got := parrots(pair[0], pair[1]); got != want {
			t.Errorf("parrots(%q, %q) = %v", pair[0], pair[1], got)
		}
	}
}

func TestANewerBrainTakesOver(t *testing.T) {
	path := filepath.Join(t.TempDir(), "brain.pid")
	stopped := make(chan string, 1)
	if err := takeOver(path, 100, 5*time.Millisecond, func(other string) { stopped <- other }); err != nil {
		t.Fatal(err)
	}
	select {
	case other := <-stopped:
		t.Fatalf("stopped by %s while alone", other)
	case <-time.After(30 * time.Millisecond):
	}
	os.WriteFile(path, []byte("200"), 0o644) // what the newer brain's takeOver writes
	select {
	case other := <-stopped:
		if other != "200" {
			t.Fatalf("stopped by %s", other)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("the older brain kept listening")
	}
}

// TestTriageCorpus runs the real model on sentences heard live, with what should have become of each. It needs Ollama:
// DELTA_TRIAGE_OLLAMA=http://127.0.0.1:11434 go test -run TriageCorpus -v (DELTA_TRIAGE_MODEL, default qwen3:8b).
// It fails when a sentence for Claude is lost, or when Delta answers a sentence meant for someone else; other misses
// cost tokens or a reply and are only logged.
func TestTriageCorpus(t *testing.T) {
	url := os.Getenv("DELTA_TRIAGE_OLLAMA")
	if url == "" {
		t.Skip("DELTA_TRIAGE_OLLAMA is not set")
	}
	model := os.Getenv("DELTA_TRIAGE_MODEL")
	if model == "" {
		model = "qwen3:8b"
	}
	data, err := os.ReadFile(filepath.Join("testdata", "triage-corpus.json"))
	if err != nil {
		t.Fatal(err)
	}
	var cases []struct {
		Recent []string `json:"recent"`
		Text   string   `json:"text"`
		Want   string   `json:"want"`
	}
	if err := json.Unmarshal(data, &cases); err != nil {
		t.Fatal(err)
	}
	tr := ollamaTriage(url, model)
	if err := warmTriage(tr); err != nil {
		t.Fatal(err)
	}
	right := 0
	for _, c := range cases {
		var s spoken
		m, _, _ := startServer(t, func(m *mcpServer) {
			m.speak = s.speak
			m.triage = tr
			m.triageMax = 10 * time.Second
			m.gate.off = true
		})
		m.recent = c.Recent
		if n := len(c.Recent); n > 0 && strings.HasPrefix(c.Recent[n-1], "Delta: ") {
			m.asked = strings.HasSuffix(c.Recent[n-1], "?")
		}
		got := map[string]string{"channel": "claude", "local": "local", "dropped": "ignore"}[m.hear(utterance{text: c.Text}, time.Now())]
		switch {
		case got == c.Want:
			right++
			t.Logf("ok    %-7s %q %s", got, c.Text, s.all())
		case c.Want == "claude" || (c.Want == "ignore" && got == "local"):
			t.Errorf("WRONG %-7s %q, want %s %s", got, c.Text, c.Want, s.all())
		default:
			t.Logf("miss  %-7s %q, want %s %s", got, c.Text, c.Want, s.all())
		}
	}
	t.Logf("%s: %d of %d right", model, right, len(cases))
}

func TestAnAcknowledgementIsKeptSilentlyWithoutTheModel(t *testing.T) {
	var s spoken
	asked := 0
	m, _, out := startServer(t, func(m *mcpServer) {
		m.speak = s.speak
		m.triage = func(context.Context, []string, string) (verdict, error) {
			asked++
			return verdict{Route: "local", Answer: "De rien."}, nil
		}
		m.gate.off = true
	})
	for _, text := range []string{"Ok.", "Cool.", "Ok cool.", "D'accord, super.", "Ah oui, d'accord Delta.", "Ok, ça marche."} {
		if where := m.hear(utterance{text: text}, time.Now()); where != "local" {
			t.Errorf("%q went to %s", text, where)
		}
	}
	silent(t, out)
	if asked != 0 || s.all() != "" {
		t.Fatalf("the model was asked %d times, Delta said %q", asked, s.all())
	}
	// An instruction is no acknowledgement: the model is asked, and its « local » is overruled, not being small talk.
	if where := m.hear(utterance{text: "Ok, lance les tests."}, time.Now()); where != "channel" || asked != 1 {
		t.Fatalf("an instruction went to %s, the model asked %d times", where, asked)
	}
	next(t, out)
}

func TestALocalAnswerThatPromisesWorkGoesToClaude(t *testing.T) {
	var s spoken
	m, _, out := startServer(t, func(m *mcpServer) {
		m.speak = s.speak
		m.triage = fixed(verdict{Route: "local", Answer: "D'accord, je m'en occupe."}, nil)
		m.gate.off = true
	})
	if where := m.hear(utterance{text: "Ok, mais fais collaborer les deux."}, time.Now()); where != "channel" {
		t.Fatalf("went to %s", where)
	}
	next(t, out)
	if s.all() != "" {
		t.Fatalf("said %q", s.all())
	}
	for answer, want := range map[string]bool{"Je vais regarder ça.": true, "C'est fait !": true, "Je vérifie.": true,
		"Avec plaisir !": false, "Bonne nuit.": false, "D'accord, je t'attends.": false} {
		if promises(answer) != want {
			t.Errorf("promises(%q) = %v", answer, !want)
		}
	}
}

func TestTheJournalNumbersItsLines(t *testing.T) {
	var buf strings.Builder
	j := &journal{f: &buf}
	j.record("start", map[string]any{"version": "test"})
	j.record("heard", map[string]any{"text": "Salut Delta !"})
	lines := strings.Split(strings.TrimSpace(buf.String()), "\n")
	if len(lines) != 2 {
		t.Fatalf("%q", buf.String())
	}
	for i, line := range lines {
		var e map[string]any
		if err := json.Unmarshal([]byte(line), &e); err != nil {
			t.Fatal(err)
		}
		if e["journal_seq"] != float64(i+1) || e["pid"] != float64(os.Getpid()) || e["t_ms"].(float64) < 1.7e12 {
			t.Fatalf("line %d: %v", i+1, e)
		}
	}
}

func TestSpeakableSaysIXAsX(t *testing.T) {
	for text, want := range map[string]string{
		"La session IX a répondu.":            "La session ixe a répondu.",
		"IX, puis GuitarAlchemist/IX et FIX.": "ixe, puis GuitarAlchemist/ixe et FIX.",
		"Rien à changer.":                     "Rien à changer.",
	} {
		if got := speakable(text); got != want {
			t.Errorf("speakable(%q) = %q", text, got)
		}
	}
}

func TestOnlySmallTalkStaysWithTheSmallModel(t *testing.T) {
	var s spoken
	m, _, out := startServer(t, func(m *mcpServer) {
		m.speak = s.speak
		m.triage = fixed(verdict{Route: "local", Answer: "D'accord."}, nil)
		m.gate.off = true
	})
	if where := m.hear(utterance{text: "Delta n'entend plus rien."}, time.Now()); where != "channel" {
		t.Fatalf("a report went to %s", where)
	}
	next(t, out)
	if where := m.hear(utterance{text: "Merci, c'est parfait."}, time.Now()); where != "local" {
		t.Fatalf("thanks went to %s", where)
	}
	silent(t, out)
}
