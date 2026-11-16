package main

import (
	"strings"
	"time"
)

// Delta answers a few hand gestures that a camera process posts to the brain: POST /gesture on the hooks' address,
// with the hooks' X-Delta-Hook header, {"gesture":"thumbs_up","hand":"right","confidence":0.92,"t_ms":<Unix ms>}.
// Each is recorded as a "gesture" event in the journal, which IX's delta@1 carries as it is. The camera process is
// hands/delta_hands.py, which the user starts: the brain never turns the camera on.

// gesture is what the camera saw: one of gestures, which hand, how sure it is (0 to 1), and when.
type gesture struct {
	Gesture    string  `json:"gesture"`
	Hand       string  `json:"hand"`
	Confidence float64 `json:"confidence"`
	SeenMs     int64   `json:"t_ms"`
}

// gestures are the gestures the brain knows, and what each does. A gesture without an action is only recorded.
var gestures = map[string]struct {
	mood, step string // the face it brings; the step through the shelf it takes while the shelf is open
	listen     bool   // it opens the conversation: the next sentence needs no « Delta »
	hearts     bool   // hearts rise from Delta, as when the user says « je t'aime »
}{
	"wave":        {mood: "joy", listen: true},
	"thumbs_up":   {mood: "joy"},
	"thumbs_down": {mood: "sorry"},
	"point_up":    {step: "up"},
	"point_down":  {step: "down"},
	"swipe_left":  {step: "back"},
	"swipe_right": {step: "enter"},
	"open_palm":   {},
	"fist":        {},
	"i_love_you":  {mood: "joy", hearts: true}, // the sign: thumb, index and little finger up
}

// gestureSure is the confidence below which a gesture is recorded but not acted on.
const gestureSure = 0.6

// gesture answers one gesture, and returns what it did, such as "mood joy" or "shelf down", or "".
func (m *mcpServer) gesture(g gesture, now time.Time) string {
	// seen_ms, not t_ms: the journal's own t_ms is when the brain recorded the line
	fields := map[string]any{"gesture": g.Gesture, "hand": g.Hand, "confidence": g.Confidence, "seen_ms": g.SeenMs}
	var done []string
	defer func() {
		fields["does"] = strings.Join(done, ", ")
		m.note("gesture", fields)
	}()
	what, known := gestures[g.Gesture]
	if !known {
		fields["why"] = "unknown"
		return ""
	}
	if g.Confidence < gestureSure {
		fields["why"] = "unsure"
		return ""
	}
	if what.mood != "" && m.show != nil {
		line := map[string]any{"mood": what.mood}
		if what.hearts {
			line["hearts"] = true
			done = append(done, "hearts")
		}
		m.show(line)
		done = append(done, "mood "+what.mood)
	}
	if what.step != "" && m.show != nil {
		if m.shelfOpen(now) {
			m.walkShelf(what.step, now)
			done = append(done, "shelf "+what.step)
		} else {
			fields["why"] = "the shelf is closed"
		}
	}
	if what.listen {
		m.gate.open(now)
		done = append(done, "listening")
	}
	return strings.Join(done, ", ")
}
