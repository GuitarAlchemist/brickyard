package main

import (
	"bufio"
	"context"
	"encoding/json"
	"io"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode"
)

// mcpInstructions is what Claude Code hands Claude when Delta connects.
const mcpInstructions = `Delta is a small robot in a window on the user's screen: it hears the user's microphone and speaks aloud. ` +
	`When this session loads Delta as a channel, what the user says to Delta arrives as a <channel> event from delta, with an utterance_id. ` +
	`It is a Whisper transcript of spoken French, so expect misheard words ("Cloud Code" is Claude Code). Treat it as the user speaking to you, ` +
	`and confirm anything destructive or surprising before doing it. ` +
	`Answer aloud with Delta's say tool, in French: one to three short spoken sentences, without markdown, lists, code or URLs. ` +
	`Pass say the utterance_id you are answering. ` +
	`Only what you pass to say is heard, not your written replies. Before long work, say in a few words what you are about to do, then say how it went. ` +
	`Never say that something is done unless you did it. For a question of fact, check it with your tools before answering, and say in a few words ` +
	`where the answer comes from; if you could not check it, say so. ` +
	`In a session without Delta's channel events, call Delta's listen tool to hear the user; the delta:talk skill runs that conversation. ` +
	`Delta's code is github.com/GuitarAlchemist/brickyard.`

var mcpTools = []map[string]any{
	{
		"name": "say",
		"description": "Say this aloud in Delta's voice, in French, with Delta speaking on screen. Returns at once: the sentences are " +
			"spoken in order while you go on. Write it to be heard: short sentences, no markdown, lists, code or URLs.",
		"inputSchema": map[string]any{
			"type": "object",
			"properties": map[string]any{
				"text":         map[string]any{"type": "string", "description": "What Delta says, in French"},
				"utterance_id": map[string]any{"type": "string", "description": "The utterance_id this answers, if it answers one"},
			},
			"required": []string{"text"},
		},
	},
	{
		"name": "listen",
		"description": "Wait until the user says something to Delta, and return what Whisper heard: spoken French, some words " +
			"possibly misheard. For sessions that do not receive Delta's channel events.",
		"inputSchema": map[string]any{
			"type": "object",
			"properties": map[string]any{"timeout_seconds": map[string]any{
				"type": "number", "description": "How long to wait, 5 to 300 seconds; 60 by default"}},
		},
	},
}

// mcpServer is Delta as an MCP server: JSON-RPC 2.0, one message a line on stdin and stdout. Claude calls say and
// listen; what Delta hears goes to a pending listen call or, when the user addressed Delta, to the session as a
// channel event.
type mcpServer struct {
	version   string
	speak     func(text string, sayID int, cost string) (string, error) // queues the text and says how long it will take
	ready     func() error                                              // why Delta cannot hear yet, or nil
	pushed    func()                                                    // after a transcript went to the session as an event
	busy      func() string                                             // what Delta says when Claude cannot answer soon, if anything
	record    func(event string, fields map[string]any)                 // the journal, if any
	listenMin time.Duration
	listenMax time.Duration
	gate      wakeGate
	triage    triage        // sorts what the wake gate lets through before Claude sees it, if set
	about     string        // appended to the instructions: what only the running brain knows, such as its journal
	triageMax time.Duration // after which a transcript goes to Claude without the triage's verdict

	outMu sync.Mutex
	out   io.Writer

	mu      sync.Mutex
	waiting []listener               // pending listen calls, oldest first
	cancels map[string]chan struct{} // by request id
	heard   int
	said    int
	recent  []string // the conversation's last lines, for the triage
	local   []string // the lines the triage handled since the last channel event, for Claude to know
	asked   bool     // Claude's last words asked the user something: the answer goes to Claude untriaged
}

// What Delta's words cost, which its window shows: "free" from the model on this computer, "paid" from Claude.
const (
	free = "free"
	paid = "paid"
)

type listener struct {
	key string
	ch  chan heardText // buffered: the router never blocks on it
}

type heardText struct {
	id   int
	text string
}

type rpcMessage struct {
	ID     json.RawMessage `json:"id,omitempty"`
	Method string          `json:"method,omitempty"`
	Params json.RawMessage `json:"params,omitempty"`
}

func (m *mcpServer) serve(in io.Reader) error {
	lines := bufio.NewScanner(in)
	lines.Buffer(make([]byte, 64*1024), 16*1024*1024)
	for lines.Scan() {
		var msg rpcMessage
		if err := json.Unmarshal(lines.Bytes(), &msg); err != nil {
			m.send(map[string]any{"jsonrpc": "2.0", "id": nil, "error": map[string]any{"code": -32700, "message": "parse error"}})
			continue
		}
		m.handle(msg)
	}
	return lines.Err()
}

func (m *mcpServer) handle(msg rpcMessage) {
	if len(msg.ID) == 0 { // a notification: no answer
		if msg.Method == "notifications/cancelled" {
			var p struct {
				RequestID json.RawMessage `json:"requestId"`
			}
			json.Unmarshal(msg.Params, &p)
			m.mu.Lock()
			if c, ok := m.cancels[string(p.RequestID)]; ok {
				close(c)
				delete(m.cancels, string(p.RequestID))
			}
			m.mu.Unlock()
		}
		return
	}
	switch msg.Method {
	case "initialize":
		var p struct {
			ProtocolVersion string `json:"protocolVersion"`
		}
		json.Unmarshal(msg.Params, &p)
		if p.ProtocolVersion == "" {
			p.ProtocolVersion = "2025-06-18"
		}
		m.reply(msg.ID, map[string]any{
			"protocolVersion": p.ProtocolVersion,
			"capabilities": map[string]any{
				"tools":        map[string]any{},
				"experimental": map[string]any{"claude/channel": map[string]any{}},
			},
			"serverInfo":   map[string]any{"name": "delta", "version": m.version},
			"instructions": mcpInstructions + m.about,
		})
	case "ping":
		m.reply(msg.ID, map[string]any{})
	case "tools/list":
		m.reply(msg.ID, map[string]any{"tools": mcpTools})
	case "tools/call":
		cancel := make(chan struct{})
		m.mu.Lock()
		if m.cancels == nil {
			m.cancels = map[string]chan struct{}{}
		}
		m.cancels[string(msg.ID)] = cancel
		m.mu.Unlock()
		go func() {
			m.call(msg.ID, msg.Params, cancel)
			m.mu.Lock()
			delete(m.cancels, string(msg.ID))
			m.mu.Unlock()
		}()
	default:
		m.fail(msg.ID, -32601, "method not found: "+msg.Method)
	}
}

func (m *mcpServer) call(id, params json.RawMessage, cancel <-chan struct{}) {
	var p struct {
		Name      string          `json:"name"`
		Arguments json.RawMessage `json:"arguments"`
	}
	json.Unmarshal(params, &p)
	switch p.Name {
	case "say":
		var a struct {
			Text        string          `json:"text"`
			UtteranceID json.RawMessage `json:"utterance_id"` // a string, as in the channel's meta, or a number
		}
		json.Unmarshal(p.Arguments, &a)
		if strings.TrimSpace(a.Text) == "" {
			m.toolResult(id, "Nothing to say: text is empty.", true)
			return
		}
		answers := strings.Trim(string(a.UtteranceID), `"`)
		m.mu.Lock()
		m.said++
		sayID := m.said
		m.remember("Delta: " + a.Text)
		m.asked = strings.HasSuffix(strings.TrimSpace(a.Text), "?")
		m.mu.Unlock()
		m.note("say", map[string]any{"say_id": sayID, "utterance_id": answers, "request": string(id), "text": a.Text, "by": "claude"})
		status, err := m.speak(a.Text, sayID, paid)
		if err != nil {
			m.toolResult(id, err.Error(), true)
			return
		}
		which := "say " + strconv.Itoa(sayID)
		if answers != "" {
			which += ", answering utterance " + answers
		}
		m.toolResult(id, "Delta is saying it ("+which+"): "+status+".", false)
	case "listen":
		var a struct {
			Timeout float64 `json:"timeout_seconds"`
		}
		json.Unmarshal(p.Arguments, &a)
		if m.ready != nil {
			if err := m.ready(); err != nil {
				m.toolResult(id, err.Error(), true)
				return
			}
		}
		wait := time.Duration(a.Timeout * float64(time.Second))
		if a.Timeout <= 0 {
			wait = 60 * time.Second
		}
		wait = min(max(wait, m.listenMin), m.listenMax)
		l := listener{key: string(id), ch: make(chan heardText, 1)}
		m.mu.Lock()
		m.waiting = append(m.waiting, l)
		m.mu.Unlock()
		timer := time.NewTimer(wait)
		defer timer.Stop()
		select {
		case h := <-l.ch:
			m.toolResult(id, heardResult(h), false)
		case <-timer.C:
			if !m.stopWaiting(l.key) { // the router took this call as the timer fired: its transcript is on the way
				m.toolResult(id, heardResult(<-l.ch), false)
				return
			}
			m.toolResult(id, "Nothing heard in "+strconv.Itoa(int(wait.Seconds()))+" s.", false)
		case <-cancel:
			m.stopWaiting(l.key) // a cancelled request gets no answer
		}
	default:
		m.fail(id, -32602, "unknown tool: "+p.Name)
	}
}

func heardResult(h heardText) string {
	return "The user said (utterance " + strconv.Itoa(h.id) + "): " + h.text
}

// stopWaiting removes a pending listen call, and reports whether it was still pending.
func (m *mcpServer) stopWaiting(key string) bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	for i, l := range m.waiting {
		if l.key == key {
			m.waiting = append(m.waiting[:i], m.waiting[i+1:]...)
			return true
		}
	}
	return false
}

// hear routes one transcript: to the oldest pending listen call (Claude asked, so no wake word is needed), else, when
// the user addressed Delta, through the triage if there is one, then to the session as a channel event. It returns
// where it went: "listen", "channel", "ignored" by the wake gate, or "dropped" or "local" by the triage.
func (m *mcpServer) hear(u utterance, now time.Time) string {
	m.mu.Lock()
	m.heard++
	h := heardText{id: m.heard, text: u.text}
	route := "channel"
	fields := map[string]any{"utterance_id": strconv.Itoa(h.id), "text": u.text, "asr_ms": u.asr.Milliseconds()}
	defer func() {
		fields["route"] = route
		m.note("heard", fields)
	}()
	if len(m.waiting) > 0 {
		l := m.waiting[0]
		m.waiting = m.waiting[1:]
		m.remember("User: " + u.text)
		m.asked = false
		m.mu.Unlock()
		l.ch <- h
		m.gate.open(now)
		route = "listen"
		fields["reason"] = "Claude was listening"
		return route
	}
	m.mu.Unlock()
	admitted, why := m.gate.admit(u.text, now)
	fields["reason"] = why
	if !admitted {
		route = "ignored"
		return route
	}
	if m.triage != nil {
		if r := m.sort(u.text, fields); r != "" {
			route = r
			return route
		}
	}
	meta := map[string]string{"utterance_id": strconv.Itoa(h.id), "asr_ms": strconv.FormatInt(u.asr.Milliseconds(), 10)}
	m.mu.Lock()
	if len(m.local) > 0 {
		meta["handled_locally"] = strings.Join(m.local, " / ")
		m.local = nil
	}
	m.remember("User: " + u.text)
	m.asked = false
	m.mu.Unlock()
	m.notify("notifications/claude/channel", map[string]any{"content": u.text, "meta": meta})
	if m.pushed != nil {
		m.pushed()
	}
	if m.busy != nil {
		if notice := m.busy(); notice != "" { // Claude Code is compacting: Claude will answer, but late
			m.mu.Lock()
			m.said++
			sayID := m.said
			m.remember("Delta: " + notice)
			m.mu.Unlock()
			m.note("say", map[string]any{"say_id": sayID, "utterance_id": fields["utterance_id"], "text": notice, "by": "local"})
			if _, err := m.speak(notice, sayID, free); err != nil {
				fields["say_error"] = err.Error()
			}
		}
	}
	return route
}

// sort asks the triage what to do with a transcript the wake gate let through, after rules that need no model: a
// sentence that calls Claude goes to Claude, and fillers, artefacts and sentences that call another assistant are
// dropped. After the model, a question it kept goes to Claude anyway, and an answer that parrots the user is not said. It returns "dropped" or
// "local" when Claude need not see it, and "" when Claude must: when Claude's last words asked a question, when the triage fails or
// is slow, and when the triage says so. A local answer costs nothing, and Claude reads those lines with the next event.
func (m *mcpServer) sort(text string, fields map[string]any) string {
	m.mu.Lock()
	asked := m.asked
	recent := append([]string(nil), m.recent...)
	m.mu.Unlock()
	if callsClaude(text) {
		fields["triage"] = "called Claude"
		return ""
	}
	if asked {
		fields["triage"] = "skipped: Claude asked a question"
		return ""
	}
	if carriesNothing(text) {
		fields["triage"] = "nothing to read"
		return "dropped"
	}
	if namesAnother(text) {
		fields["triage"] = "for another assistant"
		return "dropped"
	}
	var v verdict
	if acknowledges(text) { // the model answered « Ok. » and « Cool. » with « De rien. »: they need no answer
		fields["triage"] = "acknowledgement"
		v.Route = "local"
	} else {
		wait := m.triageMax
		if wait <= 0 {
			wait = 4 * time.Second
		}
		ctx, cancel := context.WithTimeout(context.Background(), wait)
		defer cancel()
		start := time.Now()
		var err error
		v, err = m.triage(ctx, recent, text)
		fields["triage_ms"] = time.Since(start).Milliseconds()
		if err != nil {
			fields["triage"] = "failed: " + err.Error()
			return ""
		}
		fields["triage"] = v.Route
	}
	switch v.Route {
	case "ignore":
		return "dropped"
	case "local":
		if strings.Contains(text, "?") {
			fields["triage"] = "local overruled: a question goes to Claude"
			return ""
		}
		if fields["triage"] != "acknowledgement" && !social(text) {
			fields["triage"] = "local overruled: not small talk"
			fields["local_answer"] = v.Answer
			return ""
		}
		if promises(v.Answer) {
			fields["triage"] = "local overruled: the answer promised an action"
			fields["promise"] = v.Answer
			return ""
		}
		if parrots(v.Answer, text) {
			fields["parrot"] = v.Answer
			v.Answer = ""
		}
		m.mu.Lock()
		m.remember("User: " + text)
		m.local = append(m.local, "User: "+text)
		sayID := 0
		if v.Answer != "" {
			m.said++
			sayID = m.said
			m.remember("Delta: " + v.Answer)
			m.local = append(m.local, "Delta: "+v.Answer)
		}
		m.local = m.local[max(0, len(m.local)-8):]
		m.mu.Unlock()
		if v.Answer != "" {
			m.note("say", map[string]any{"say_id": sayID, "utterance_id": fields["utterance_id"], "text": v.Answer, "by": "local"})
			if _, err := m.speak(v.Answer, sayID, free); err != nil {
				fields["say_error"] = err.Error()
			}
		}
		return "local"
	}
	return ""
}

// remember keeps the conversation's last six lines, for the triage. The caller holds m.mu.
func (m *mcpServer) remember(line string) {
	m.recent = append(m.recent, line)
	m.recent = m.recent[max(0, len(m.recent)-6):]
}

func (m *mcpServer) note(event string, fields map[string]any) {
	if m.record != nil {
		m.record(event, fields)
	}
}

func (m *mcpServer) send(msg map[string]any) {
	line, err := json.Marshal(msg)
	if err != nil {
		return
	}
	m.outMu.Lock()
	defer m.outMu.Unlock()
	m.out.Write(append(line, '\n'))
}

func (m *mcpServer) reply(id json.RawMessage, result any) {
	m.send(map[string]any{"jsonrpc": "2.0", "id": id, "result": result})
}

func (m *mcpServer) fail(id json.RawMessage, code int, message string) {
	m.send(map[string]any{"jsonrpc": "2.0", "id": id, "error": map[string]any{"code": code, "message": message}})
}

func (m *mcpServer) toolResult(id json.RawMessage, text string, isError bool) {
	m.reply(id, map[string]any{"content": []map[string]any{{"type": "text", "text": text}}, "isError": isError})
}

func (m *mcpServer) notify(method string, params any) {
	m.send(map[string]any{"jsonrpc": "2.0", "method": method, "params": params})
}

// wakeGate lets a transcript reach the session when it calls Delta or Claude, Delta's brain, or when it comes soon
// after: within follow of such a call, for the rest of what the user says, or within window of Delta's last words, for
// an answer. A sentence let through without a call extends neither, so that talk that goes on and on does not all go
// to Claude, and a sentence for someone else ends the conversation: the same microphone hears the author talk to other
// sessions, Brutal in a terminal and Gaïa among them.
type wakeGate struct {
	mu     sync.Mutex
	off    bool          // let everything through but what is said to someone else
	window time.Duration // after Delta's last words
	follow time.Duration // after the user called Delta
	until  time.Time
}

// admit says whether a transcript is for Delta, and why.
func (g *wakeGate) admit(text string, now time.Time) (bool, string) {
	to := addressee(text)
	g.mu.Lock()
	defer g.mu.Unlock()
	switch {
	case to == "Delta" || to == "Claude":
		g.extend(now.Add(g.follow))
		return true, "called " + to
	case to != "":
		g.until = time.Time{}
		return false, "spoken to " + to
	case g.off:
		return true, "wake gate off"
	case now.Before(g.until):
		return true, "in the conversation"
	}
	return false, "not addressed to Delta"
}

// open keeps the conversation going for a window from now: Delta has just spoken, or Claude has just listened.
func (g *wakeGate) open(now time.Time) {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.extend(now.Add(g.window))
}

func (g *wakeGate) extend(to time.Time) {
	if to.After(g.until) {
		g.until = to
	}
}

// otherListeners are the other sessions the author speaks to near Delta's microphone, as Whisper writes their names:
// Brutal is the Claude session in a terminal that builds Brickyard, and Whisper writes IX, said « ixe », as X. Claude
// is not one of them: the author calls Delta's brain Claude (2026-10-22), and « Ok, Claude, … » is for it.
var otherListeners = map[string]string{"brutal": "Brutal", "gaia": "Gaïa", "indigo": "Indigo", "amber": "Amber",
	"codex": "Codex", "ix": "IX", "x": "IX"}

// notForDelta are the words by which the user tells Delta that a sentence is not for it.
var notForDelta = []string{" te parle pas ", " pas a toi ", " pas pour toi "}

var foldAccents = strings.NewReplacer("à", "a", "â", "a", "ä", "a", "é", "e", "è", "e", "ê", "e", "ë", "e", "î", "i",
	"ï", "i", "ô", "o", "ö", "o", "ù", "u", "û", "u", "ü", "u", "ç", "c")

// addressee says whom a sentence is spoken to: "Delta" or "Claude" when one of its first three words calls Delta or
// its brain (« Cloud Code » as Whisper may write it), another listener's name when one comes first or when the user
// says they speak to that listener (« je parle à Brutal »), "someone else" when the user tells Delta it is not for it,
// and "" when it calls no one.
func addressee(text string) string {
	words := strings.FieldsFunc(foldAccents.Replace(strings.ToLower(text)), func(r rune) bool { return !unicode.IsLetter(r) })
	called := ""
	for i, w := range words[:min(3, len(words))] {
		if w == "delta" {
			called = "Delta"
			break
		}
		if w == "claude" || (w == "cloud" && i+1 < len(words) && words[i+1] == "code") {
			called = "Claude"
			break
		}
		if who, ok := otherListeners[w]; ok {
			return who
		}
	}
	joined := " " + strings.Join(words, " ") + " "
	if strings.Contains(joined, " parle ") {
		for i, w := range words {
			if who, ok := otherListeners[w]; ok && i > 0 && (words[i-1] == "a" || words[i-1] == "au" || words[i-1] == "avec") {
				return who
			}
		}
	}
	for _, phrase := range notForDelta {
		if strings.Contains(joined, phrase) {
			return "someone else"
		}
	}
	return called
}
