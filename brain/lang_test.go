package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
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
