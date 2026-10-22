package main

import (
	"encoding/json"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestDeltaMovesItsWindowWhenAsked(t *testing.T) {
	for text, want := range map[string]string{
		"Delta, maximise ta fenêtre.":                    "maximize",
		"Delta, minimise ta fenêtre.":                    "minimize",
		"Delta, mets-toi en haut à gauche.":              "top-left",
		"Delta, mets toi en bas à droite.":               "bottom-right",
		"Delta, va à droite.":                            "right",
		"Delta, place-toi au centre.":                    "center",
		"Delta, déplace ta fenêtre en haut.":             "top",
		"Delta, cache-toi dans la zone de notification.": "tray",
		"Delta, cache-toi.":                              "tray",
		"Delta, reviens.":                                "show",
		"Delta, passe en plein écran.":                   "fullscreen",
		"Delta, reste au premier plan.":                  "on-top",
		"Delta, ne reste plus au premier plan.":          "not-on-top",
		"Delta, fais-toi plus petit.":                    "smaller",
		"Delta, restaure ta fenêtre.":                    "restore",
		"Delta, mets-toi en haut a gauche":               "top-left",

		"Delta, maximize your window.":          "maximize",
		"Delta, minimize.":                      "minimize",
		"Delta, move to the top left.":          "top-left",
		"Delta, go to the bottom right corner.": "bottom-right",
		"Delta, go to the right.":               "right",
		"Delta, move left.":                     "left",
		"Delta, put yourself in the middle.":    "center",
		"Delta, move your window to the top.":   "top",
		"Delta, hide in the notification area.": "tray",
		"Delta, hide.":                          "tray",
		"Delter, hide yourself, please.":        "tray",
		"Delta, come back.":                     "show",
		"Delta, go full screen.":                "fullscreen",
		"Delta, stay on top.":                   "on-top",
		"Delta, don't stay on top any more.":    "not-on-top",
		"Delta, make yourself smaller.":         "smaller",
		"Delta, bigger.":                        "larger",
		"Delta, restore your window.":           "restore",
	} {
		if got := windowCommand(text); got != want {
			t.Errorf("%q: %q, want %q", text, got, want)
		}
		if windowActions[want]["en"] == "" || windowActions[want]["fr"] == "" {
			t.Errorf("%s: Delta has nothing to say for it in English or in French", want)
		}
	}
}

func TestOtherSentencesLeaveTheWindowAlone(t *testing.T) {
	for _, text := range []string{
		"Delta, mets le fichier à gauche dans le dossier.",
		"Delta, qu'est-ce qu'il y a en haut de la liste ?",
		"Delta, lance les tests.",
		"Delta, mets-toi en haut à gauche, puis ouvre le README et lance les tests du cerveau.",
		"Delta, quel temps fait-il ?",
		"Delta, move the file to the left folder.",
		"Delta, what's at the top of the list?",
		"Delta, put the button on the right.",
		"Delta, go ahead, all right.",
		"Delta, hide the terminal.",
		"Delta, restore the backup.",
		"Delta, make the font bigger.",
		"Delta, move to the top left, then open the README and run the brain tests.",
	} {
		if got := windowCommand(text); got != "" {
			t.Errorf("%q: %q", text, got)
		}
	}
}

func TestDeltaOpensAndWalksItsShelf(t *testing.T) {
	for _, c := range []struct {
		text string
		open bool
		want string
	}{
		{"Delta, montre-moi les sessions.", false, "sessions"},
		{"Delta, affiche les plugins.", false, "plugins"},
		{"Delta, montre les marketplaces.", false, "marketplaces"},
		{"Delta, ouvre ton menu.", false, "open"},
		{"Delta, ferme le menu.", true, "close"},
		{"Delta, descends.", true, "down"},
		{"Delta, monte.", true, "up"},
		{"Delta, ouvre.", true, "enter"},
		{"Delta, retour.", true, "back"},
		{"Delta, descends.", false, ""},
		{"Delta, ouvre le fichier README.", false, ""},
		{"Delta, reviens.", true, ""},
		{"Delta, ferme toutes les sessions.", false, ""},
		{"Delta, combien de plugins as-tu ?", false, ""},

		{"Delta, show me the sessions.", false, "sessions"},
		{"Delta, list the plugins.", false, "plugins"},
		{"Delta, show the marketplaces.", false, "marketplaces"},
		{"Delta, open your menu.", false, "open"},
		{"Delta, close the menu.", true, "close"},
		{"Delta, go down.", true, "down"},
		{"Down.", true, "down"},
		{"Delta, go up.", true, "up"},
		{"Delta, open.", true, "enter"},
		{"Delta, go back.", true, "back"},
		{"Delta, go down.", false, ""},
		{"Delta, open the README file.", true, ""},
		{"Delta, come back.", true, ""},
		{"Delta, close all the sessions.", false, ""},
		{"Delta, how many plugins do you have?", false, ""},
		{"Delta, what's up?", true, ""},
	} {
		if got := shelfCommand(c.text, c.open); got != c.want {
			t.Errorf("%q (open %v): %q, want %q", c.text, c.open, got, c.want)
		}
	}
}

// scenery collects the lines the brain sends Delta's window.
type scenery struct {
	mu    sync.Mutex
	lines []string
}

func (s *scenery) show(msg map[string]any) {
	data, _ := json.Marshal(msg)
	s.mu.Lock()
	defer s.mu.Unlock()
	s.lines = append(s.lines, string(data))
}

func (s *scenery) all() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return strings.Join(s.lines, " ")
}

func TestDeltaMovesItsWindowWithoutClaude(t *testing.T) {
	var s spoken
	var shown scenery
	var records []map[string]any
	m, _, out := startServer(t, func(m *mcpServer) {
		m.lang = languages["fr"]
		m.speak = s.speak
		m.show = shown.show
		m.record = func(event string, fields map[string]any) { fields["event"] = event; records = append(records, fields) }
	})
	now := time.Now()
	if where := m.hear(utterance{text: "Delta, mets-toi en haut à gauche."}, now); where != "window" {
		t.Fatalf("went to %s", where)
	}
	silent(t, out)
	if got := shown.all(); got != `{"window":"top-left"}` {
		t.Fatalf("showed %s", got)
	}
	if got := s.all(); got != "free: Je me mets en haut à gauche." {
		t.Fatalf("said %q", got)
	}
	if last := records[len(records)-1]; last["event"] != "heard" || last["route"] != "window" || last["window"] != "top-left" {
		t.Fatalf("%v", last)
	}
	m.hear(utterance{text: "Delta, quel temps fait-il ?"}, now.Add(time.Second))
	meta := next(t, out)["params"].(map[string]any)["meta"].(map[string]any)
	if meta["handled_locally"] != "User: Delta, mets-toi en haut à gauche. / Delta: Je me mets en haut à gauche." {
		t.Fatalf("%v", meta)
	}
}

func TestWithoutAWindowClaudeGetsTheRequest(t *testing.T) {
	m, _, out := startServer(t, nil)
	if where := m.hear(utterance{text: "Delta, maximise ta fenêtre."}, time.Now()); where != "channel" {
		t.Fatalf("went to %s", where)
	}
	next(t, out)
}

func TestDeltaWalksItsShelfWithoutClaude(t *testing.T) {
	var s spoken
	var shown scenery
	m, _, out := startServer(t, func(m *mcpServer) {
		m.lang = languages["fr"]
		m.speak = s.speak
		m.show = shown.show
	})
	now := time.Now()
	for i, text := range []string{
		"Delta, montre-moi les sessions.",
		"Descends.", // the shelf keeps the conversation open: no wake word
		"Reviens en arrière.",
		"Ouvre.",
		"Delta, ferme le menu.",
	} {
		if where := m.hear(utterance{text: text}, now.Add(time.Duration(i)*time.Second)); where != "shelf" {
			t.Fatalf("%q went to %s", text, where)
		}
	}
	silent(t, out)
	if got := shown.all(); got != `{"shelf":"sessions"} {"shelf":"down"} {"shelf":"back"} {"shelf":"enter"} {"shelf":"close"}` {
		t.Fatalf("showed %s", got)
	}
	if got := s.all(); got != "free: Voici les sessions. | free: Je range le menu." {
		t.Fatalf("said %q", got)
	}
	if where := m.hear(utterance{text: "Delta, descends."}, now.Add(10*time.Second)); where != "channel" {
		t.Fatalf("a closed shelf took %s", where)
	}
	next(t, out)

	m.hear(utterance{text: "Delta, affiche les plugins."}, now.Add(20*time.Second))
	if where := m.hear(utterance{text: "Delta, descends."}, now.Add(70*time.Second)); where != "channel" {
		t.Fatalf("a shelf the scene closed by itself took %s", where)
	}
}

func TestDeltaAnswersItsWindowAndShelfInEnglishByDefault(t *testing.T) {
	var s spoken
	var shown scenery
	m, _, _ := startServer(t, func(m *mcpServer) {
		m.speak = s.speak
		m.show = shown.show
	})
	now := time.Now()
	for i, c := range []struct{ text, route string }{
		{"Delta, move to the top left.", "window"},
		{"Delta, show me the sessions.", "shelf"},
		{"Go down.", "shelf"},
		{"Delta, mets-toi à droite.", "window"}, // a French request still works in an English session
		{"Delta, close the menu.", "shelf"},
	} {
		if where := m.hear(utterance{text: c.text}, now.Add(time.Duration(i)*time.Second)); where != c.route {
			t.Fatalf("%q went to %s", c.text, where)
		}
	}
	if got := shown.all(); got != `{"window":"top-left"} {"shelf":"sessions"} {"shelf":"down"} {"window":"right"} {"shelf":"close"}` {
		t.Fatalf("showed %s", got)
	}
	if got := s.all(); got != "free: Moving to the top left. | free: Here are the sessions. | free: Moving to the right. | free: Putting my menu away." {
		t.Fatalf("said %q", got)
	}
}
