package main

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"slices"
	"strings"
	"sync"
	"time"
	"unicode/utf8"
)

// Delta's requests to another assistant on this computer, such as Gaia, and their answers. They sit on the hooks'
// address beside /hear, behind the same localOnly, hooks' header and X-Delta-Remote secret (remote.allowed), so that
// only a local agent of the user's that reads remote.secret takes part. The brain keeps its own requests, the last
// maxAsks of them, and is not a queue for the other side: an agent carries a request to its assistant and the answer
// back.
//
//	POST /ask     {"to","text","mute","ttl_s"}: a request from Delta, for the canary until a spoken sentence makes one;
//	              201 {"request_id","brain_id","expires"}
//	GET  /asks    server-sent events: each live request at connection, then each change of a request's state
//	POST /answer  {"request_id","by","text","mood"}: 202 {"request_id","say_id","state"} once, accepted rather than
//	              spoken; 200 and "duplicate" for the same text again, which is not said twice; 409 for another text,
//	              410 once expired or for another brain's request, 404 for an unknown one, 403 from another assistant
//	              than the one asked, 400 for an empty or long text or an unknown mood
//
// A request's state goes asked → accepted → speaking → spoken, or asked → answered for a mute request, whose answer the
// brain keeps without saying it; asked → expired when nothing answered in time; accepted → error when Delta could not
// say it. Its id begins with the brain's own id, so that a brain started since refuses the answer.
//
// The window shows how the bridge to Gaia stands (bridge): offline while no agent listens to /asks; listening while
// one does but Gaia has answered nothing yet, since an agent that listens proves no answer; connected once she
// answered the latest request that ended; degraded when that one expired or could not be said.
const (
	maxAsks       = 64
	maxAskText    = 1000
	askTTL        = 120 * time.Second
	maxAskTTL     = 600 * time.Second
	askListeners  = 8
	askEventQueue = 32
)

// askable are the assistants Delta may ask, by the name the request and the answer give.
var askable = []string{"gaia"}

type ask struct {
	ID      string    `json:"request_id"`
	Brain   string    `json:"brain_id"`
	To      string    `json:"to"`
	Text    string    `json:"text"`
	Mute    bool      `json:"mute"`
	State   string    `json:"state"`
	Asked   time.Time `json:"-"`
	Expires time.Time `json:"-"`
	Answer  string    `json:"answer,omitempty"`
	SayID   int       `json:"say_id,omitempty"`
	Error   string    `json:"error,omitempty"`
}

type asks struct {
	brain string
	now   func() time.Time
	say   func(text, by, mood, requestID string) (sayID int, err error) // the say tool's path (mcpServer.sayAs)
	note  func(event string, fields map[string]any)                     // the journal, or nil
	shown func(bridge string)                                           // the window's view of the bridge, at each change, or nil

	mu     sync.Mutex
	n      int
	all    map[string]*ask
	order  []string // by age, for the bound
	subs   map[chan []byte]bool
	bridge string // the last one shown

	showing sync.Mutex
}

func newAsks() *asks {
	b := make([]byte, 6)
	rand.Read(b)
	return &asks{brain: hex.EncodeToString(b), now: time.Now, all: map[string]*ask{}, bridge: "offline"}
}

// bridged is how the bridge to Gaia stands, as the window shows it. The caller holds mu.
func (a *asks) bridged() string {
	if len(a.subs) == 0 {
		return "offline"
	}
	for i := len(a.order) - 1; i >= 0; i-- {
		switch a.all[a.order[i]].State {
		case "expired", "error":
			return "degraded"
		case "accepted", "speaking", "spoken", "answered":
			return "connected"
		}
	}
	return "listening"
}

// show tells the window how the bridge stands, when that changed; one at a time, so that the last word is the latest.
func (a *asks) show() {
	a.showing.Lock()
	defer a.showing.Unlock()
	a.mu.Lock()
	now := a.bridged()
	changed := now != a.bridge
	a.bridge = now
	a.mu.Unlock()
	if changed && a.shown != nil {
		a.shown(now)
	}
}

func (a *asks) handle(mux *http.ServeMux, allowed func(http.ResponseWriter, *http.Request, string) bool) {
	mux.HandleFunc("/ask", func(w http.ResponseWriter, req *http.Request) {
		if allowed(w, req, http.MethodPost) {
			a.ask(w, req)
		}
	})
	mux.HandleFunc("/asks", func(w http.ResponseWriter, req *http.Request) {
		if allowed(w, req, http.MethodGet) {
			a.stream(w, req)
		}
	})
	mux.HandleFunc("/answer", func(w http.ResponseWriter, req *http.Request) {
		if allowed(w, req, http.MethodPost) {
			a.answer(w, req)
		}
	})
}

func reply(w http.ResponseWriter, status int, body any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(body)
}

func refuse(w http.ResponseWriter, status int, why string) {
	reply(w, status, map[string]string{"error": why})
}

func (a *asks) ask(w http.ResponseWriter, req *http.Request) {
	var body struct {
		To   string `json:"to"`
		Text string `json:"text"`
		Mute bool   `json:"mute"`
		TTL  int    `json:"ttl_s"`
	}
	if err := json.NewDecoder(io.LimitReader(req.Body, 8192)).Decode(&body); err != nil {
		refuse(w, http.StatusBadRequest, `{"to","text","mute","ttl_s"}, please`)
		return
	}
	text := strings.TrimSpace(body.Text)
	ttl := time.Duration(body.TTL) * time.Second
	switch {
	case !slices.Contains(askable, body.To):
		refuse(w, http.StatusBadRequest, "Delta asks only "+strings.Join(askable, ", "))
		return
	case text == "" || utf8.RuneCountInString(text) > maxAskText:
		refuse(w, http.StatusBadRequest, fmt.Sprintf("a text of 1 to %d characters, please", maxAskText))
		return
	case body.TTL == 0:
		ttl = askTTL
	case ttl < time.Second || ttl > maxAskTTL:
		refuse(w, http.StatusBadRequest, fmt.Sprintf("ttl_s from 1 to %d, please", int(maxAskTTL.Seconds())))
		return
	}
	a.mu.Lock()
	a.sweep()
	a.n++
	now := a.now()
	r := &ask{ID: fmt.Sprintf("%s-%d", a.brain, a.n), Brain: a.brain, To: body.To, Text: text, Mute: body.Mute, State: "asked",
		Asked: now, Expires: now.Add(ttl)}
	a.all[r.ID] = r
	a.order = append(a.order, r.ID)
	a.bound()
	a.tell(r)
	a.mu.Unlock()
	a.record("ask", r)
	time.AfterFunc(ttl, a.expire)
	reply(w, http.StatusCreated, map[string]string{"request_id": r.ID, "brain_id": a.brain, "expires": r.Expires.UTC().Format(time.RFC3339)})
}

func (a *asks) answer(w http.ResponseWriter, req *http.Request) {
	var body struct {
		ID   string `json:"request_id"`
		By   string `json:"by"`
		Text string `json:"text"`
		Mood string `json:"mood"`
	}
	if err := json.NewDecoder(io.LimitReader(req.Body, 8192)).Decode(&body); err != nil || body.ID == "" {
		refuse(w, http.StatusBadRequest, `{"request_id","by","text","mood"}, please`)
		return
	}
	text := strings.TrimSpace(body.Text)
	a.mu.Lock()
	a.sweep()
	r := a.all[body.ID]
	switch {
	case r == nil && !strings.HasPrefix(body.ID, a.brain+"-"):
		a.mu.Unlock()
		refuse(w, http.StatusGone, "another brain asked this: Delta has started since")
		return
	case r == nil:
		a.mu.Unlock()
		refuse(w, http.StatusNotFound, "Delta asked nothing by this id, or forgot it")
		return
	case body.By != r.To:
		a.mu.Unlock()
		refuse(w, http.StatusForbidden, "this request is for "+r.To)
		return
	case text == "" || utf8.RuneCountInString(text) > maxAskText:
		a.mu.Unlock()
		refuse(w, http.StatusBadRequest, fmt.Sprintf("a text of 1 to %d characters, please", maxAskText))
		return
	case body.Mood != "" && !slices.Contains(moods, body.Mood):
		a.mu.Unlock()
		refuse(w, http.StatusBadRequest, "Delta has no "+body.Mood+" face")
		return
	case r.Answer != "" && r.Answer == text:
		state := map[string]any{"request_id": r.ID, "say_id": r.SayID, "state": r.State, "duplicate": true}
		a.mu.Unlock()
		reply(w, http.StatusOK, state)
		return
	case r.Answer != "":
		a.mu.Unlock()
		refuse(w, http.StatusConflict, "this request has another answer")
		return
	case r.State == "expired":
		a.mu.Unlock()
		refuse(w, http.StatusGone, "this request expired")
		return
	}
	r.Answer = text
	r.State = "accepted"
	if r.Mute {
		r.State = "answered"
	}
	a.tell(r)
	a.mu.Unlock()
	a.record("answer", r)
	if !r.Mute {
		id, err := a.say(text, body.By, body.Mood, r.ID)
		a.mu.Lock()
		r.SayID = id
		if err != nil {
			r.State, r.Error = "error", err.Error()
			a.tell(r)
		}
		a.mu.Unlock()
		if err != nil {
			a.record("answer", r)
		}
	}
	a.mu.Lock()
	state := map[string]any{"request_id": r.ID, "say_id": r.SayID, "state": r.State}
	a.mu.Unlock()
	a.show()
	reply(w, http.StatusAccepted, state)
}

// said follows the mouth's speaking and spoken notes for the answers it says.
func (a *asks) said(event string, fields map[string]any) {
	id, _ := fields["say_id"].(int)
	if id == 0 || (event != "speaking" && event != "spoken") {
		return
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	for _, r := range a.all {
		if r.SayID == id && r.State != event && r.State != "spoken" {
			r.State = event
			a.tell(r)
		}
	}
}

// expire marks the requests nothing answered in time, as the timer of each calls it.
func (a *asks) expire() {
	a.mu.Lock()
	gone := a.sweep()
	a.mu.Unlock()
	for _, r := range gone {
		a.record("ask", r)
	}
	a.show()
}

// sweep marks as expired the unanswered requests past their time, and returns them. The caller holds mu.
func (a *asks) sweep() []*ask {
	var gone []*ask
	now := a.now()
	for _, id := range a.order {
		if r := a.all[id]; r.State == "asked" && now.After(r.Expires) {
			r.State = "expired"
			a.tell(r)
			gone = append(gone, r)
		}
	}
	return gone
}

// bound forgets the oldest requests beyond maxAsks, finished ones first. The caller holds mu.
func (a *asks) bound() {
	for len(a.order) > maxAsks {
		drop := 0
		for i, id := range a.order {
			if s := a.all[id].State; s != "asked" && s != "accepted" && s != "speaking" {
				drop = i
				break
			}
		}
		delete(a.all, a.order[drop])
		a.order = slices.Delete(a.order, drop, drop+1)
	}
}

// tell sends a request's state to the agents listening to /asks. The caller holds mu.
func (a *asks) tell(r *ask) {
	line := a.event(r)
	for ch := range a.subs {
		select {
		case ch <- line:
		default: // a listener that does not read loses events, not the brain
		}
	}
}

func (a *asks) event(r *ask) []byte {
	e := *r
	line, _ := json.Marshal(struct {
		ask
		AskedMS   int64 `json:"t_ms"`
		ExpiresMS int64 `json:"expires_ms"`
	}{e, r.Asked.UnixMilli(), r.Expires.UnixMilli()})
	return line
}

func (a *asks) record(event string, r *ask) {
	if a.note == nil {
		return
	}
	a.mu.Lock()
	fields := map[string]any{"request_id": r.ID, "brain_id": r.Brain, "to": r.To, "mute": r.Mute, "state": r.State,
		"expires": r.Expires.UTC().Format(time.RFC3339)}
	if event == "ask" {
		fields["text"] = r.Text
	} else {
		fields["text"], fields["say_id"] = r.Answer, r.SayID
	}
	if r.Error != "" {
		fields["error"] = r.Error
	}
	a.mu.Unlock()
	a.note(event, fields)
}

func (a *asks) stream(w http.ResponseWriter, req *http.Request) {
	flusher, ok := w.(http.Flusher)
	if !ok {
		http.Error(w, "no streaming here", http.StatusInternalServerError)
		return
	}
	ch := make(chan []byte, askEventQueue)
	a.mu.Lock()
	if len(a.subs) >= askListeners {
		a.mu.Unlock()
		refuse(w, http.StatusTooManyRequests, fmt.Sprintf("at most %d agents listen to /asks", askListeners))
		return
	}
	if a.subs == nil {
		a.subs = map[chan []byte]bool{}
	}
	a.sweep()
	var live [][]byte
	for _, id := range a.order {
		if r := a.all[id]; r.State == "asked" || r.State == "accepted" || r.State == "speaking" {
			live = append(live, a.event(r))
		}
	}
	a.subs[ch] = true
	a.mu.Unlock()
	a.show()
	defer func() {
		a.mu.Lock()
		delete(a.subs, ch)
		a.mu.Unlock()
		a.show()
	}()
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.WriteHeader(http.StatusOK)
	for _, line := range live {
		fmt.Fprintf(w, "data: %s\n\n", line)
	}
	flusher.Flush()
	alive := time.NewTicker(15 * time.Second)
	defer alive.Stop()
	for {
		select {
		case <-req.Context().Done():
			return
		case line := <-ch:
			if _, err := fmt.Fprintf(w, "data: %s\n\n", line); err != nil {
				return
			}
		case <-alive.C:
			if _, err := io.WriteString(w, ": alive\n\n"); err != nil {
				return
			}
		}
		flusher.Flush()
	}
}
