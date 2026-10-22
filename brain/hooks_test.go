package main

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

// transcript writes lines to a file as Claude Code writes its transcript, one JSON object a line.
func transcript(t *testing.T, path string, lines ...string) {
	t.Helper()
	f, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	for _, l := range lines {
		if _, err := f.WriteString(l); err != nil {
			t.Fatal(err)
		}
	}
}

const (
	userLine      = `{"type":"user","message":{"role":"user","content":"Delta, compte jusqu'à trois."}}` + "\n"
	answerLine    = `{"type":"assistant","isSidechain":false,"message":{"usage":{"input_tokens":100,"cache_read_input_tokens":200000,"cache_creation_input_tokens":3000,"output_tokens":400}}}` + "\n"
	subagentLine  = `{"type":"assistant","isSidechain":true,"message":{"usage":{"input_tokens":9,"cache_read_input_tokens":9,"cache_creation_input_tokens":9,"output_tokens":9}}}` + "\n"
	manualCompact = `{"type":"system","subtype":"compact_boundary","compactMetadata":{"trigger":"manual","preTokens":1000,"postTokens":800}}` + "\n"
	autoCompact   = `{"type":"system","subtype":"compact_boundary","compactMetadata":{"trigger":"auto","preTokens":392630,"postTokens":28596}}` + "\n"
)

func TestTheContextIsWhatTheLastAnswerReadAndWrote(t *testing.T) {
	path := filepath.Join(t.TempDir(), "s.jsonl")
	transcript(t, path, userLine, answerLine, subagentLine, userLine)
	if used, err := contextUse(path); err != nil || used != 203500 {
		t.Fatalf("%d, %v: a subagent's answer is not the session's context", used, err)
	}
	transcript(t, path, autoCompact)
	if used, _ := contextUse(path); used != 28596 {
		t.Fatalf("%d after a compaction", used)
	}
	transcript(t, path, userLine, answerLine)
	if used, _ := contextUse(path); used != 203500 {
		t.Fatalf("%d after the next answer", used)
	}
	empty := filepath.Join(t.TempDir(), "empty.jsonl")
	transcript(t, empty, userLine)
	if _, err := contextUse(empty); err == nil {
		t.Fatal("a transcript without an answer has no context size")
	}
}

func TestTheLimitIsWhereClaudeCodeLastCompactedByItself(t *testing.T) {
	path := filepath.Join(t.TempDir(), "s.jsonl")
	var c compactions
	transcript(t, path, userLine, answerLine)
	if got := c.limit(path); got != 0 {
		t.Fatalf("%d before any compaction", got)
	}
	transcript(t, path, autoCompact, manualCompact)
	if got := c.limit(path); got != 392630 {
		t.Fatalf("%d: a manual compaction says nothing of the limit", got)
	}
	half := `{"type":"system","subtype":"compact_boundary","compactMetadata":{"trigger":"auto","preTokens":500000,`
	transcript(t, path, half)
	if got := c.limit(path); got != 392630 {
		t.Fatalf("%d from a line still being written", got)
	}
	transcript(t, path, `"postTokens":30000}}`+"\n")
	if got := c.limit(path); got != 500000 {
		t.Fatalf("%d once the line ended", got)
	}
	os.WriteFile(path, []byte(userLine), 0o600)
	if got := c.limit(path); got != 0 {
		t.Fatalf("%d in a new transcript under the same name", got)
	}
}

func TestTheDefaultLimitFollowsClaudeCodesSettings(t *testing.T) {
	t.Setenv("CLAUDE_AUTOCOMPACT_PCT_OVERRIDE", "40")
	t.Setenv("CLAUDE_CODE_AUTO_COMPACT_WINDOW", "")
	if got := defaultLimit(); got != 400000 {
		t.Fatalf("%d", got)
	}
	t.Setenv("CLAUDE_CODE_AUTO_COMPACT_WINDOW", "200000")
	if got := defaultLimit(); got != 80000 {
		t.Fatalf("%d", got)
	}
	t.Setenv("CLAUDE_AUTOCOMPACT_PCT_OVERRIDE", "lots")
	if got := defaultLimit(); got != 190000 {
		t.Fatalf("%d", got)
	}
}

// seen records what the hooks made the brain do.
type seen struct {
	mu     sync.Mutex
	events []string
}

func (s *seen) add(e string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.events = append(s.events, e)
}

func (s *seen) all() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return strings.Join(s.events, " | ")
}

func testHooks(s *seen, own string) *hooks {
	return &hooks{own: func() string { return own }, logf: func(string, ...any) {}, maxCompact: time.Minute,
		compacting: func(trigger string) { s.add("compacting " + trigger) },
		compacted:  func(trigger string, _ float64) { s.add("compacted " + trigger) },
		context:    func(used, limit int) { s.add("context " + strconv.Itoa(used) + "/" + strconv.Itoa(limit)) },
	}
}

func TestACompactionShowsFromItsStartToItsEnd(t *testing.T) {
	var s seen
	h := testHooks(&s, "s1")
	path := filepath.Join(t.TempDir(), "s.jsonl")
	transcript(t, path, userLine, answerLine)
	h.handle(hookPayload{SessionID: "s2", Event: "PreCompact", Trigger: "auto", TranscriptPath: path})
	if got := s.all(); got != "" {
		t.Fatalf("another session's compaction showed: %s", got)
	}
	h.handle(hookPayload{SessionID: "s1", Event: "PreCompact", Trigger: "auto", TranscriptPath: path})
	h.handle(hookPayload{SessionID: "s1", Event: "PreCompact", Trigger: "auto", TranscriptPath: path})
	if got := h.busy(); !strings.Contains(got, "compacte") {
		t.Fatalf("the user who speaks while Claude compacts hears %q", got)
	}
	if got := h.busy(); got != "" {
		t.Fatalf("said twice in one compaction: %q", got)
	}
	transcript(t, path, autoCompact)
	h.handle(hookPayload{SessionID: "s1", Event: "PostCompact", Trigger: "auto", TranscriptPath: path})
	h.handle(hookPayload{SessionID: "s1", Event: "SessionStart", Source: "compact", TranscriptPath: path})
	if got := s.all(); got != "compacting auto | compacted auto | context 28596/392630" {
		t.Fatalf("%s", got)
	}
	if got := h.busy(); got != "" {
		t.Fatalf("after the compaction: %q", got)
	}
}

func TestACompactionNoHookEndedEndsByItself(t *testing.T) {
	var s seen
	h := testHooks(&s, "")
	h.maxCompact = 20 * time.Millisecond
	h.handle(hookPayload{SessionID: "any", Event: "PreCompact", Trigger: "manual"})
	deadline := time.Now().Add(2 * time.Second)
	for s.all() != "compacting manual | compacted timeout" {
		if time.Now().After(deadline) {
			t.Fatalf("%s", s.all())
		}
		time.Sleep(5 * time.Millisecond)
	}
}

func TestOnlyAHookPostReachesTheBrain(t *testing.T) {
	var s seen
	h := testHooks(&s, "")
	srv := httptest.NewServer(h.handler())
	defer srv.Close()
	body := `{"session_id":"s1","hook_event_name":"PreCompact","trigger":"auto"}`
	r, err := http.Post(srv.URL+"/hook", "text/plain", strings.NewReader(body))
	if err != nil {
		t.Fatal(err)
	}
	r.Body.Close()
	if r.StatusCode != http.StatusForbidden {
		t.Fatalf("a post without the header, as a web page would send it: %d", r.StatusCode)
	}
	req, _ := http.NewRequest(http.MethodPost, srv.URL+"/hook", strings.NewReader(body))
	req.Header.Set(hookHeader, "1")
	if r, err = http.DefaultClient.Do(req); err != nil {
		t.Fatal(err)
	}
	r.Body.Close()
	if r.StatusCode != http.StatusNoContent {
		t.Fatalf("%d", r.StatusCode)
	}
	deadline := time.Now().Add(2 * time.Second)
	for s.all() != "compacting auto" {
		if time.Now().After(deadline) {
			t.Fatalf("%q", s.all())
		}
		time.Sleep(5 * time.Millisecond)
	}
}

func TestTheProjectFolderIsTheDirectoryWithDashes(t *testing.T) {
	if got := projectFolder(`C:\tmp\delta-live`); got != "C--tmp-delta-live" {
		t.Fatal(got)
	}
	if got := projectFolder(`C:\Users\me\source\repos\app`); got != "C--Users-me-source-repos-app" {
		t.Fatal(got)
	}
}

func TestSpeakingToClaudeWhileItCompactsIsAnsweredAtOnce(t *testing.T) {
	var s spoken
	told := false
	m, _, out := startServer(t, func(m *mcpServer) {
		m.speak = s.speak
		m.busy = func() string {
			if told {
				return ""
			}
			told = true
			return "Claude compacte sa mémoire."
		}
	})
	if where := m.hear(utterance{text: "Delta, où en est la compilation ?"}, time.Now()); where != "channel" {
		t.Fatalf("went to %s", where)
	}
	if p := next(t, out)["params"].(map[string]any); p["content"] != "Delta, où en est la compilation ?" {
		t.Fatalf("Claude must still read it once back: %v", p)
	}
	if got := s.all(); got != "free: Claude compacte sa mémoire." {
		t.Fatalf("said %q", got)
	}
}
