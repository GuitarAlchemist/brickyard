package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode"
)

// Claude Code's hooks tell the brain what its session is doing, so that the robot shows it: how full the conversation's
// context is, and when Claude Code compacts it, during which Claude hears nothing. Every hook runs the same command,
// whatever the event, which posts the hook's JSON to the brain:
//
//	curl -s -m 2 -H "X-Delta-Hook: 1" --data-binary @- http://127.0.0.1:8807/hook
//
// The header keeps web pages out: a browser cannot send it to another origin without asking first, and nobody answers.
const hookHeader = "X-Delta-Hook"

// hookPayload is what a hook's JSON says that Delta uses.
type hookPayload struct {
	SessionID      string `json:"session_id"`
	TranscriptPath string `json:"transcript_path"`
	Event          string `json:"hook_event_name"`
	Trigger        string `json:"trigger"` // PreCompact and PostCompact: "auto" or "manual"
	Source         string `json:"source"`  // SessionStart: "startup", "resume", "clear" or "compact"
}

// hooks follows one Claude Code session, the one this brain belongs to, from what its hooks post.
type hooks struct {
	own        func() string // the session's id, or "" when unknown, in which case every session's hooks count
	compacting func(trigger string)
	compacted  func(trigger string, seconds float64)
	context    func(used, limit int)
	logf       func(string, ...any)
	maxCompact time.Duration // after which a compaction that no hook ended is taken as over

	mu         sync.Mutex
	since      time.Time // when the compaction under way began; zero when none is
	told       bool      // the user heard that Claude is compacting, during this compaction
	used       int
	limit      int
	shown      time.Time               // when the window last heard the context's size
	failed     string                  // the last reason the context could not be read, said once
	limits     map[string]*compactions // by transcript
	transcript string                  // the session's transcript, as its hooks name it
}

// handle takes one hook's JSON, from this session or another.
func (h *hooks) handle(p hookPayload) {
	if own := h.own(); own != "" && p.SessionID != own {
		return
	}
	if p.TranscriptPath != "" {
		h.mu.Lock()
		h.transcript = p.TranscriptPath
		h.mu.Unlock()
	}
	switch {
	case p.Event == "PreCompact":
		h.mu.Lock()
		fresh := h.since.IsZero()
		h.since = time.Now()
		h.told = false
		h.mu.Unlock()
		if fresh {
			h.compacting(p.Trigger)
			began := time.Now()
			time.AfterFunc(h.maxCompact, func() {
				h.mu.Lock()
				stale := !h.since.IsZero() && !h.since.After(began)
				h.mu.Unlock()
				if stale {
					h.end("timeout")
				}
			})
		}
		return
	case p.Event == "PostCompact" || (p.Event == "SessionStart" && p.Source == "compact"):
		h.end(p.Trigger)
	}
	if p.TranscriptPath != "" {
		h.measure(p.TranscriptPath)
	}
}

// end closes the compaction under way, if any.
func (h *hooks) end(trigger string) {
	h.mu.Lock()
	since := h.since
	h.since = time.Time{}
	h.mu.Unlock()
	if !since.IsZero() {
		h.compacted(trigger, time.Since(since).Seconds())
	}
}

// busy says, once per compaction, what Delta tells the user who speaks to Claude while Claude Code compacts: Claude
// will answer, but not at once. It is "" otherwise.
func (h *hooks) busy() string {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.since.IsZero() || h.told {
		return ""
	}
	h.told = true
	return "Claude compacte sa mémoire. Il te répondra dans une minute environ."
}

// measure reads how full the context is from the transcript, and tells the window when that changed, or when it has
// not told it for half a minute: a window opened since knows nothing.
func (h *hooks) measure(transcript string) {
	used, err := contextUse(transcript)
	h.mu.Lock()
	if err != nil {
		say := err.Error() != h.failed
		h.failed = err.Error()
		h.mu.Unlock()
		if say {
			h.logf("context: %v", err)
		}
		return
	}
	h.failed = ""
	if h.limits == nil {
		h.limits = map[string]*compactions{}
	}
	c := h.limits[transcript]
	if c == nil {
		c = &compactions{}
		h.limits[transcript] = c
	}
	h.mu.Unlock()
	limit := c.limit(transcript)
	if limit <= 0 {
		limit = defaultLimit()
	}
	h.mu.Lock()
	tell := used != h.used || limit != h.limit || time.Since(h.shown) >= 30*time.Second
	h.used, h.limit = used, limit
	if tell {
		h.shown = time.Now()
	}
	h.mu.Unlock()
	if tell {
		h.context(used, limit)
	}
}

// watch measures the context whenever the session's transcript grows, and every half minute, hooks or not. The
// transcript is the one the hooks name, or else the one Claude Code keeps for the session in its project's folder.
func (h *hooks) watch(every time.Duration) {
	size := int64(-1)
	var last time.Time
	for range time.Tick(every) {
		h.mu.Lock()
		path := h.transcript
		h.mu.Unlock()
		if path == "" {
			path = sessionTranscript(h.own())
		}
		info, err := os.Stat(path)
		if path == "" || err != nil || (info.Size() == size && time.Since(last) < 30*time.Second) {
			continue
		}
		size, last = info.Size(), time.Now()
		h.measure(path)
	}
}

// handler answers the hooks' posts.
func (h *hooks) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/hook", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost || r.Header.Get(hookHeader) == "" {
			http.Error(w, "a hook posts its JSON with the "+hookHeader+" header", http.StatusForbidden)
			return
		}
		var p hookPayload
		if err := json.NewDecoder(io.LimitReader(r.Body, 1<<20)).Decode(&p); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		w.WriteHeader(http.StatusNoContent) // nothing on curl's stdout, which some hooks add to Claude's context
		go h.handle(p)
	})
	return mux
}

// serve answers the hooks on addr. A brain that takes over waits for the one it replaces to free the port.
func (h *hooks) serve(addr string) {
	for attempt := 0; ; attempt++ {
		l, err := net.Listen("tcp", addr)
		if err != nil {
			if attempt == 30 {
				h.logf("hooks: %v; the robot will not show the context", err)
				return
			}
			time.Sleep(2 * time.Second)
			continue
		}
		h.logf("hooks: listening on %s", addr)
		h.logf("hooks: %v", http.Serve(l, h.handler()))
		return
	}
}

// claudeDir is where Claude Code keeps its sessions and transcripts.
func claudeDir() string {
	if dir := os.Getenv("CLAUDE_CONFIG_DIR"); dir != "" {
		return dir
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return ""
	}
	return filepath.Join(home, ".claude")
}

// ownSession is the id of the Claude Code session that started this brain, from the file Claude Code keeps for each of
// its processes, or "" if there is none.
func ownSession() string {
	data, err := os.ReadFile(filepath.Join(claudeDir(), "sessions", strconv.Itoa(os.Getppid())+".json"))
	if err != nil {
		return ""
	}
	var s struct {
		SessionID string `json:"sessionId"`
	}
	json.Unmarshal(data, &s)
	return s.SessionID
}

// sessionTranscript is where Claude Code keeps a session's transcript, in its project's folder; "" without a session.
func sessionTranscript(id string) string {
	cwd, err := os.Getwd()
	if id == "" || err != nil {
		return ""
	}
	return filepath.Join(claudeDir(), "projects", projectFolder(cwd), id+".jsonl")
}

// projectFolder is the name of a project's folder in Claude Code's: its directory with every character but ASCII
// letters and digits made a dash.
func projectFolder(dir string) string {
	return strings.Map(func(r rune) rune {
		if r < 128 && (unicode.IsLetter(r) || unicode.IsDigit(r)) {
			return r
		}
		return '-'
	}, dir)
}

// transcriptLine is what Delta reads of a transcript's line.
type transcriptLine struct {
	Type        string `json:"type"`
	Subtype     string `json:"subtype"`
	IsSidechain bool   `json:"isSidechain"`
	Compact     *struct {
		Trigger    string `json:"trigger"`
		PreTokens  int    `json:"preTokens"`
		PostTokens int    `json:"postTokens"`
	} `json:"compactMetadata"`
	Message *struct {
		Usage *struct {
			Input         int `json:"input_tokens"`
			CacheRead     int `json:"cache_read_input_tokens"`
			CacheCreation int `json:"cache_creation_input_tokens"`
			Output        int `json:"output_tokens"`
		} `json:"usage"`
	} `json:"message"`
}

// contextUse is how many tokens the conversation holds: what Claude's last answer read and wrote, from the end of the
// transcript, or what a compaction since then left.
func contextUse(path string) (int, error) {
	f, err := os.Open(path)
	if err != nil {
		return 0, err
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		return 0, err
	}
	const tail = 4 << 20
	from := max(0, info.Size()-tail)
	buf := make([]byte, info.Size()-from)
	if _, err := f.ReadAt(buf, from); err != nil && err != io.EOF {
		return 0, err
	}
	lines := bytes.Split(buf, []byte{'\n'})
	if from > 0 {
		lines = lines[1:] // the first is cut
	}
	for i := len(lines) - 1; i >= 0; i-- {
		line := lines[i]
		if !bytes.Contains(line, []byte(`"usage"`)) && !bytes.Contains(line, []byte("compact_boundary")) {
			continue
		}
		var l transcriptLine
		if json.Unmarshal(line, &l) != nil {
			continue
		}
		if l.Subtype == "compact_boundary" && l.Compact != nil && l.Compact.PostTokens > 0 {
			return l.Compact.PostTokens, nil
		}
		if l.Type == "assistant" && !l.IsSidechain && l.Message != nil && l.Message.Usage != nil {
			u := l.Message.Usage
			return u.Input + u.CacheRead + u.CacheCreation + u.Output, nil
		}
	}
	return 0, fmt.Errorf("no answer with its token counts in the last %d MB of %s", tail>>20, path)
}

// compactions remembers where Claude Code last compacted a transcript by itself, which is the context's limit as this
// session's model and settings make it. It reads only what the transcript gained since it last looked.
type compactions struct {
	mu     sync.Mutex
	offset int64
	at     int // the tokens before the last automatic compaction, 0 before any
}

func (c *compactions) limit(path string) int {
	c.mu.Lock()
	defer c.mu.Unlock()
	f, err := os.Open(path)
	if err != nil {
		return c.at
	}
	defer f.Close()
	if info, err := f.Stat(); err == nil && info.Size() < c.offset {
		c.offset, c.at = 0, 0 // a new transcript under the same name
	}
	if _, err := f.Seek(c.offset, io.SeekStart); err != nil {
		return c.at
	}
	r := bufio.NewReaderSize(f, 1<<16)
	for {
		line, err := r.ReadBytes('\n')
		if err != nil {
			return c.at // a line without its newline is read again next time
		}
		c.offset += int64(len(line))
		if !bytes.Contains(line, []byte("compact_boundary")) {
			continue
		}
		var l transcriptLine
		if json.Unmarshal(line, &l) == nil && l.Subtype == "compact_boundary" && l.Compact != nil &&
			l.Compact.Trigger == "auto" && l.Compact.PreTokens > 0 {
			c.at = l.Compact.PreTokens
		}
	}
}

// defaultLimit is where Claude Code will compact before it has done so once: the share of the context window that
// CLAUDE_AUTOCOMPACT_PCT_OVERRIDE sets, 95 % without it, of CLAUDE_CODE_AUTO_COMPACT_WINDOW or of a million tokens.
func defaultLimit() int {
	pct := 95
	if v, err := strconv.Atoi(os.Getenv("CLAUDE_AUTOCOMPACT_PCT_OVERRIDE")); err == nil && v >= 1 && v <= 100 {
		pct = v
	}
	window := 1_000_000
	if v, err := strconv.Atoi(os.Getenv("CLAUDE_CODE_AUTO_COMPACT_WINDOW")); err == nil && v > 0 {
		window = v
	}
	return window * pct / 100
}
