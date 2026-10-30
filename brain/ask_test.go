package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"strings"
	"sync"
	"testing"
	"time"
)

// askServer is a brain whose /ask, /asks and /answer a test drives, with its journal and what it said.
type askServer struct {
	url   string
	asks  *asks
	mu    sync.Mutex
	notes []string
	said  []string
}

func newAskServer(t *testing.T) *askServer {
	t.Helper()
	s := &askServer{asks: newAsks()}
	s.asks.note = func(event string, fields map[string]any) {
		s.mu.Lock()
		defer s.mu.Unlock()
		s.notes = append(s.notes, fmt.Sprintf("%s %s %v", event, fields["state"], fields["request_id"]))
	}
	s.asks.say = func(text, by, mood, requestID string) (int, error) {
		s.mu.Lock()
		defer s.mu.Unlock()
		s.said = append(s.said, by+": "+text+" ("+requestID+")")
		return len(s.said), nil
	}
	s.url = remoteServer(t, &remote{secret: "s3cret", asks: s.asks}).URL
	return s
}

func (s *askServer) post(t *testing.T, path, body string) (int, map[string]any) {
	t.Helper()
	req, _ := http.NewRequest(http.MethodPost, s.url+path, strings.NewReader(body))
	req.Header.Set(hookHeader, "1")
	req.Header.Set(remoteHeader, "s3cret")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	var got map[string]any
	json.NewDecoder(resp.Body).Decode(&got)
	return resp.StatusCode, got
}

func (s *askServer) request(t *testing.T, body string) string {
	t.Helper()
	status, got := s.post(t, "/ask", body)
	id, _ := got["request_id"].(string)
	if status != http.StatusCreated || !strings.HasPrefix(id, s.asks.brain+"-") || got["brain_id"] != s.asks.brain {
		t.Fatalf("ask %s: %d %v", body, status, got)
	}
	return id
}

// The canary: a mute request from Delta, which an agent reads from /asks, and Gaia's answer to it, which the brain
// keeps under the same id without saying it.
func TestGaiaAnswersAMuteRequestFromDelta(t *testing.T) {
	s := newAskServer(t)
	id := s.request(t, `{"to":"gaia","text":"canari 7f3a","mute":true}`)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	req, _ := http.NewRequestWithContext(ctx, http.MethodGet, s.url+"/asks", nil)
	req.Header.Set(hookHeader, "1")
	req.Header.Set(remoteHeader, "s3cret")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	events := bufio.NewScanner(resp.Body)
	next := func() map[string]any {
		for events.Scan() {
			if line, ok := strings.CutPrefix(events.Text(), "data: "); ok {
				var e map[string]any
				json.Unmarshal([]byte(line), &e)
				return e
			}
		}
		t.Fatal("/asks ended")
		return nil
	}
	if e := next(); e["request_id"] != id || e["state"] != "asked" || e["text"] != "canari 7f3a" || e["to"] != "gaia" || e["mute"] != true {
		t.Fatalf("an agent reads %v", e)
	}
	status, got := s.post(t, "/answer", `{"request_id":"`+id+`","by":"gaia","text":"canari 7f3a reçu"}`)
	if status != http.StatusAccepted || got["state"] != "answered" {
		t.Fatalf("answer: %d %v", status, got)
	}
	if e := next(); e["request_id"] != id || e["state"] != "answered" || e["answer"] != "canari 7f3a reçu" {
		t.Fatalf("then %v", e)
	}
	if len(s.said) != 0 || strings.Join(s.notes, "; ") != "ask asked "+id+"; answer answered "+id {
		t.Errorf("said %v, noted %v", s.said, s.notes)
	}
}

// An answer is said once as the say tool says it, its state follows the mouth, and a second post of the same answer
// is not said again; another answer, another assistant, an unknown request and a long text are refused.
func TestAnAnswerIsSaidOnceAndOnlyForItsRequest(t *testing.T) {
	s := newAskServer(t)
	id := s.request(t, `{"to":"gaia","text":"Quelle heure est-il ?"}`)
	answer := `{"request_id":"` + id + `","by":"gaia","text":"Il est midi.","mood":"joy"}`
	for _, c := range []struct {
		body   string
		status int
	}{
		{`{"request_id":"` + id + `","by":"ix","text":"Il est midi."}`, http.StatusForbidden},
		{`{"request_id":"` + id + `","by":"gaia","text":"` + strings.Repeat("a", maxAskText+1) + `"}`, http.StatusBadRequest},
		{`{"request_id":"` + id + `","by":"gaia","text":"Il est midi.","mood":"angry"}`, http.StatusBadRequest},
		{`{"request_id":"` + s.asks.brain + `-99","by":"gaia","text":"Il est midi."}`, http.StatusNotFound},
		{`{"request_id":"0123456789ab-1","by":"gaia","text":"Il est midi."}`, http.StatusGone},
		{answer, http.StatusAccepted},
		{answer, http.StatusOK},
		{`{"request_id":"` + id + `","by":"gaia","text":"Il est une heure."}`, http.StatusConflict},
	} {
		if status, got := s.post(t, "/answer", c.body); status != c.status {
			t.Errorf("%.80s: %d %v, want %d", c.body, status, got, c.status)
		}
	}
	if len(s.said) != 1 || s.said[0] != "gaia: Il est midi. ("+id+")" {
		t.Errorf("said %v", s.said)
	}
	for _, event := range []string{"speaking", "spoken"} {
		s.asks.said(event, map[string]any{"say_id": 1})
		if state := s.asks.all[id].State; state != event {
			t.Errorf("after the mouth's %s, the request is %s", event, state)
		}
	}
	if status, got := s.post(t, "/answer", answer); status != http.StatusOK || got["duplicate"] != true || got["state"] != "spoken" {
		t.Errorf("the same answer again: %d %v", status, got)
	}
}

// A request expires when nothing answers it in time, and an answer that comes after is refused, not said.
func TestALateAnswerIsRefused(t *testing.T) {
	s := newAskServer(t)
	now := time.Now()
	s.asks.now = func() time.Time { return now }
	id := s.request(t, `{"to":"gaia","text":"Tu es là ?","ttl_s":5}`)
	now = now.Add(6 * time.Second)
	s.asks.expire()
	if status, got := s.post(t, "/answer", `{"request_id":"`+id+`","by":"gaia","text":"Oui."}`); status != http.StatusGone {
		t.Errorf("a late answer: %d %v", status, got)
	}
	if len(s.said) != 0 || s.notes[len(s.notes)-1] != "ask expired "+id {
		t.Errorf("said %v, noted %v", s.said, s.notes)
	}
}

// The window learns how the bridge stands, once at each change: an agent that listens is not yet Gaia, her answer
// connects her, a request that expires unanswered degrades the bridge, and the agent leaving takes it offline.
func TestTheWindowShowsHowTheBridgeToGaiaStands(t *testing.T) {
	s := newAskServer(t)
	now := time.Now()
	s.asks.now = func() time.Time { return now }
	shown := make(chan string, 8)
	s.asks.shown = func(bridge string) { shown <- bridge }
	expect := func(want string) {
		t.Helper()
		select {
		case got := <-shown:
			if got != want {
				t.Errorf("the window shows %s, want %s", got, want)
			}
		case <-time.After(2 * time.Second):
			t.Fatalf("the window was not told %s", want)
		}
	}
	ctx, cancel := context.WithCancel(context.Background())
	req, _ := http.NewRequestWithContext(ctx, http.MethodGet, s.url+"/asks", nil)
	req.Header.Set(hookHeader, "1")
	req.Header.Set(remoteHeader, "s3cret")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	expect("listening")
	id := s.request(t, `{"to":"gaia","text":"Tu es là ?","mute":true,"ttl_s":5}`)
	s.post(t, "/answer", `{"request_id":"`+id+`","by":"gaia","text":"Oui."}`)
	expect("connected")
	s.post(t, "/answer", `{"request_id":"`+id+`","by":"gaia","text":"Oui."}`) // a duplicate changes nothing
	s.request(t, `{"to":"gaia","text":"Et maintenant ?","mute":true,"ttl_s":5}`)
	now = now.Add(6 * time.Second)
	s.asks.expire()
	expect("degraded")
	cancel()
	expect("offline")
	select {
	case extra := <-shown:
		t.Errorf("the window was told %s once more", extra)
	default:
	}
}

// What a request may be, who may post it, and how many the brain keeps.
func TestDeltaAsksWithinBounds(t *testing.T) {
	s := newAskServer(t)
	for _, body := range []string{`{"to":"ix","text":"Bonjour"}`, `{"to":"gaia","text":"  "}`, `{"to":"gaia","text":"Bonjour","ttl_s":601}`,
		`{"to":"gaia","text":"` + strings.Repeat("é", maxAskText+1) + `"}`, `not json`} {
		if status, _ := s.post(t, "/ask", body); status != http.StatusBadRequest {
			t.Errorf("%.60s: %d", body, status)
		}
	}
	req, _ := http.NewRequest(http.MethodPost, s.url+"/ask", bytes.NewReader([]byte(`{"to":"gaia","text":"Bonjour"}`)))
	req.Header.Set(hookHeader, "1")
	if resp, err := http.DefaultClient.Do(req); err != nil || resp.StatusCode != http.StatusForbidden {
		t.Errorf("without the secret: %v %v", resp.StatusCode, err)
	}
	first := s.request(t, `{"to":"gaia","text":"Bonjour","mute":true}`)
	for range maxAsks + 5 {
		s.request(t, `{"to":"gaia","text":"Bonjour","mute":true}`)
	}
	if len(s.asks.all) != maxAsks || s.asks.all[first] != nil {
		t.Errorf("the brain keeps %d requests, the first among them: %v", len(s.asks.all), s.asks.all[first] != nil)
	}
}
