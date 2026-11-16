package main

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestOnlyAGesturePostReachesTheBrain(t *testing.T) {
	got := make(chan gesture, 1)
	h := &hooks{logf: t.Logf, gesture: func(g gesture) { got <- g }}
	srv := httptest.NewServer(h.handler())
	defer srv.Close()
	post := func(body string, header bool) int {
		req, _ := http.NewRequest(http.MethodPost, srv.URL+"/gesture", strings.NewReader(body))
		if header {
			req.Header.Set(hookHeader, "1")
		}
		r, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		r.Body.Close()
		return r.StatusCode
	}
	thumb := `{"gesture":"thumbs_up","hand":"right","confidence":0.92,"t_ms":1792703544186}`
	if code := post(thumb, false); code != http.StatusForbidden {
		t.Fatalf("a post without the header, as a web page would send it: %d", code)
	}
	if code := post(`{"gesture":"moonwalk","confidence":0.9}`, true); code != http.StatusBadRequest {
		t.Fatalf("an unknown gesture: %d", code)
	}
	if code := post(thumb, true); code != http.StatusNoContent {
		t.Fatalf("%d", code)
	}
	select {
	case g := <-got:
		if g.Gesture != "thumbs_up" || g.Hand != "right" || g.Confidence != 0.92 || g.SeenMs != 1792703544186 {
			t.Fatalf("%+v", g)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("the gesture never reached the brain")
	}
}

func TestDeltaAnswersGestures(t *testing.T) {
	var shown scenery
	var records []map[string]any
	m, _, out := startServer(t, func(m *mcpServer) {
		m.show = shown.show
		m.record = func(event string, fields map[string]any) { fields["event"] = event; records = append(records, fields) }
	})
	now := time.Now()
	for _, c := range []struct {
		g    gesture
		does string
	}{
		{gesture{Gesture: "thumbs_up", Hand: "right", Confidence: 0.9, SeenMs: 1}, "mood joy"},
		{gesture{Gesture: "thumbs_down", Confidence: 0.9}, "mood sorry"},
		{gesture{Gesture: "thumbs_up", Confidence: 0.3}, ""},  // unsure: recorded, not acted on
		{gesture{Gesture: "point_down", Confidence: 0.9}, ""}, // the shelf is closed
		{gesture{Gesture: "fist", Confidence: 0.9}, ""},       // known, without an action yet
	} {
		if does := m.gesture(c.g, now); does != c.does {
			t.Fatalf("%+v did %q, want %q", c.g, does, c.does)
		}
	}
	m.walkShelf("sessions", now)
	for g, want := range map[string]string{"point_down": "shelf down", "point_up": "shelf up", "swipe_right": "shelf enter", "swipe_left": "shelf back"} {
		if does := m.gesture(gesture{Gesture: g, Confidence: 0.9}, now); does != want {
			t.Fatalf("%s with the shelf open did %q, want %q", g, does, want)
		}
	}
	if got := shown.all(); !strings.HasPrefix(got, `{"mood":"joy"} {"mood":"sorry"} {"shelf":"sessions"}`) {
		t.Fatalf("showed %s", got)
	}
	first := records[0]
	if first["event"] != "gesture" || first["gesture"] != "thumbs_up" || first["hand"] != "right" || first["confidence"] != 0.9 ||
		first["seen_ms"] != int64(1) || first["does"] != "mood joy" {
		t.Fatalf("%v", first)
	}
	if _, ok := first["t_ms"]; ok {
		t.Fatalf("the camera's time would replace the journal's own t_ms: %v", first)
	}
	if records[2]["does"] != "" || records[2]["why"] != "unsure" {
		t.Fatalf("%v", records[2])
	}

	if does := m.gesture(gesture{Gesture: "wave", Confidence: 0.9}, now.Add(time.Hour)); does != "mood joy, listening" {
		t.Fatalf("a wave did %q", does)
	}
	if where := m.hear(utterance{text: "Quelle heure est-il ?"}, now.Add(time.Hour+time.Second)); where != "channel" {
		t.Fatalf("after a wave, a sentence without « Delta » went to %s", where)
	}
	next(t, out)
}
