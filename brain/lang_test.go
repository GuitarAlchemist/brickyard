package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestDeltaSpeaksEnglishUnlessToldOtherwise(t *testing.T) {
	for code, want := range map[string]string{"": "en", "en": "en", "fr": "fr"} {
		l, err := languageOf(code)
		if err != nil || l.code != want {
			t.Errorf("languageOf(%q) = %q, %v; want %q", code, l.code, err, want)
		}
	}
	if l, err := languageOf("de"); err == nil || l.code != "en" {
		t.Errorf(`languageOf("de") = %q, %v; want English and an error`, l.code, err)
	}
}

func TestClaudeIsToldDeltasLanguage(t *testing.T) {
	m := &mcpServer{}
	if got := instructions(m.language()); !strings.Contains(got, "spoken English by default") || strings.Contains(got, "spoken French") {
		t.Errorf("an unset language should ask for English: %s", got)
	}
	fr, _ := languageOf("fr")
	if got := instructions(fr); !strings.Contains(got, "spoken French by default") || !strings.Contains(got, "in the language the event's lang names") {
		t.Errorf("French instructions: %s", got)
	}
	for _, tool := range toolsFor(fr) {
		if tool["name"] == "say" && !strings.Contains(tool["description"].(string), "(French by default)") {
			t.Errorf("say should speak French: %v", tool["description"])
		}
	}
}

func TestTheLanguageInDeltaJSONPicksTheVoice(t *testing.T) {
	home := t.TempDir()
	c := mcpConfig{home: home}
	homePaths(&c)
	if c.lang != "" || filepath.Base(c.voice) != "en_US-ljspeech-medium.onnx" {
		t.Errorf("no language: lang %q, voice %s; want the English voice", c.lang, c.voice)
	}
	os.WriteFile(filepath.Join(home, "delta.json"), []byte(`{"language": "fr"}`), 0o644)
	c = mcpConfig{home: home}
	homePaths(&c)
	if c.lang != "fr" || filepath.Base(c.voice) != "fr_FR-siwis-medium.onnx" {
		t.Errorf(`"language": "fr": lang %q, voice %s; want the French voice`, c.lang, c.voice)
	}
	c = mcpConfig{home: home, lang: "en"}
	homePaths(&c)
	if c.lang != "en" || filepath.Base(c.voice) != "en_US-ljspeech-medium.onnx" {
		t.Errorf("-lang en over delta.json's fr: lang %q, voice %s", c.lang, c.voice)
	}
}

// The window can set Delta to one language or to two, its own first, and delta.json keeps the setting, with its other
// keys as they were, for the next brain.
func TestDeltaCanBeBilingual(t *testing.T) {
	ls, err := languagesOf("fr+en")
	if err != nil || len(ls) != 2 || ls[0].code != "fr" || ls[1].code != "en" || settingOf(ls) != "fr+en" {
		t.Fatalf("fr+en: %v %v", ls, err)
	}
	if ls, err := languagesOf("en+en"); err != nil || settingOf(ls) != "en" {
		t.Errorf("en+en: %v %v", ls, err)
	}
	if _, err := languagesOf("fr+de"); err == nil {
		t.Error("a language Delta does not know is refused")
	}
	m := &mcpServer{lang: languages["fr"]}
	m.speakIn(ls)
	if m.language().code != "fr" || len(m.languages()) != 2 {
		t.Errorf("%v", m.languages())
	}
	home := t.TempDir()
	path := filepath.Join(home, "delta.json")
	os.WriteFile(path, []byte(`{"voice":"v.onnx","language":"fr"}`), 0o644)
	if err := saveLanguage(home, "en+fr"); err != nil {
		t.Fatal(err)
	}
	data, _ := os.ReadFile(path)
	if !strings.Contains(string(data), `"language": "en+fr"`) || !strings.Contains(string(data), `"voice": "v.onnx"`) {
		t.Errorf("%s", data)
	}
}

// A phone cannot click the window's language button, so Delta changes its languages when asked aloud, and leaves to
// Claude a sentence that says no or only speaks about a language.
func TestDeltaChangesItsLanguageWhenAsked(t *testing.T) {
	fr, frEn, en, enFr := []language{languages["fr"]}, []language{languages["fr"], languages["en"]}, []language{languages["en"]}, []language{languages["en"], languages["fr"]}
	for _, c := range []struct {
		text string
		now  []language
		want string
	}{
		{"Delta, parle seulement français.", frEn, "fr"},
		{"En français, s'il te plaît.", frEn, "fr"},
		{"Ok, continue en français, s'il te plaît.", frEn, "fr"},
		{"Continue en français.", enFr, "fr+en"},
		{"Ne parle qu'en français.", enFr, "fr"},
		{"Parle anglais.", fr, "en+fr"},
		{"Speak English only.", frEn, "en"},
		{"Let's continue in English.", fr, "en+fr"},
		{"In French, please.", en, "fr+en"},
		{"Passe en mode bilingue.", fr, "fr+en"},
		{"Be bilingual.", en, "en+fr"},
		{"Parle français et anglais.", en, "fr+en"},
		{"Parle français.", fr, "fr"},

		{"Ne parle plus anglais.", frEn, ""},
		{"Stop speaking English.", enFr, ""},
		{"Ok, tu es bilingue ?", frEn, ""},
		{"Comment on dit bonjour en anglais ?", fr, ""},
		{"Il y a un petit défaut dans l'interface : on ne peut pas cliquer sur le bouton français-anglais.", frEn, ""},
		{"Delta, mets-toi en haut à gauche.", fr, ""},
	} {
		if got := languageCommand(c.text, c.now); got != c.want {
			t.Errorf("%q in %s: %q, want %q", c.text, settingOf(c.now), got, c.want)
		}
	}
	for _, setting := range languageSettings {
		if ls, err := languagesOf(setting); err != nil || settingOf(ls) != setting || languageAnswers[setting] == "" {
			t.Errorf("%s: %v %v %q", setting, ls, err, languageAnswers[setting])
		}
	}
}

func TestDeltaChangesItsLanguageWithoutClaude(t *testing.T) {
	var s spoken
	var changed []string
	var records []map[string]any
	m, in, out := startServer(t, func(m *mcpServer) {
		m.lang = languages["fr"]
		m.langs = []language{languages["fr"], languages["en"]}
		m.speak = s.speak
		m.relanguage = func(ls []language, by string) { m.speakIn(ls); changed = append(changed, settingOf(ls)+" by "+by) }
		m.record = func(event string, fields map[string]any) { fields["event"] = event; records = append(records, fields) }
	})
	now := time.Now()
	if where := m.hear(utterance{text: "Delta, parle seulement français.", lang: "fr"}, now); where != "language" {
		t.Fatalf("went to %s", where)
	}
	silent(t, out)
	if got := s.all(); got != "free: D'accord, je parle seulement français." {
		t.Fatalf("said %q", got)
	}
	if last := records[len(records)-1]; last["route"] != "language" || last["language"] != "fr" {
		t.Fatalf("%v", last)
	}
	call(in, 1, "language", `{"setting":"en+fr"}`)
	if r := next(t, out); isError(r) {
		t.Fatalf("%v", r)
	}
	call(in, 2, "language", `{"setting":"de"}`)
	if r := next(t, out); !isError(r) {
		t.Fatalf("an unknown language: %v", r)
	}
	if got := strings.Join(changed, ", "); got != "fr by voice, en+fr by claude" {
		t.Fatalf("changed %s", got)
	}
	if got := settingOf(m.languages()); got != "en+fr" {
		t.Fatalf("speaks %s", got)
	}
}
