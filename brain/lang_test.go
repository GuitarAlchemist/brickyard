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
	if got := instructions(m.language()); !strings.Contains(got, "in English") || strings.Contains(got, "French") {
		t.Errorf("an unset language should ask for English: %s", got)
	}
	fr, _ := languageOf("fr")
	if got := instructions(fr); !strings.Contains(got, "spoken French") || !strings.Contains(got, "in French") {
		t.Errorf("French instructions: %s", got)
	}
	for _, tool := range toolsFor(fr) {
		if tool["name"] == "say" && !strings.Contains(tool["description"].(string), "in French") {
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
