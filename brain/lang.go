package main

import (
	"fmt"
	"slices"
	"strings"
)

// language is what Delta hears and speaks. English is the default and French the other language it knows. It sets
// Whisper's -l, the Piper voice setup.ps1 installs, the language Claude is asked to answer in, and the standalone
// mode's persona.
type language struct {
	code    string   // Whisper's -l, and "language" in delta.json
	name    string   // as Claude is told it
	voice   string   // the Piper voice setup.ps1 installs for it, under <home>\voices
	hello   string   // said once at start-up, which loads the voice model
	acks    []string // what Delta says at once when a sentence goes to Claude, in turn
	persona string   // the standalone mode's system prompt
}

var languages = map[string]language{
	"en": {code: "en", name: "English", voice: "en_US-ljspeech-medium.onnx", hello: "Hello.",
		acks: []string{"Let me check.", "One second.", "Let me see.", "Let me think."},
		persona: "You are Delta, the little robot who runs Brickyard, a software factory driven by voice. " +
			"You speak English, aloud: one or two short sentences, warm and precise, with no lists, no markdown and no " +
			"emoji. For now you can only talk: you cannot build, run or change anything yet. " +
			"If someone asks you to, say so simply and offer to talk about it."},
	"fr": {code: "fr", name: "French", voice: "fr_FR-siwis-medium.onnx", hello: "Bonjour.",
		acks: []string{"Je regarde.", "Une seconde.", "Voyons voir.", "Je réfléchis."},
		persona: "Tu es Delta, le petit robot qui dirige Brickyard, une usine à logiciels qu'on pilote à la voix. " +
			"Tu parles français, à l'oral : une ou deux phrases courtes, chaleureuses et précises, sans liste, sans markdown, " +
			"sans emoji. Pour l'instant tu sais seulement converser : tu ne peux encore rien construire, lancer ni modifier. " +
			"Si on te le demande, dis-le simplement et propose d'en parler."},
}

// language is Delta's own language now: the window's words, and what it says unprompted. English when none was set.
func (m *mcpServer) language() language {
	return m.languages()[0]
}

// languages is what Delta hears and speaks now: its own language, then the other one in a bilingual mode, in which
// it answers each sentence in the language it was spoken in.
func (m *mcpServer) languages() []language {
	m.langMu.Lock()
	defer m.langMu.Unlock()
	if len(m.langs) > 0 {
		return m.langs
	}
	if m.lang.code == "" {
		return []language{languages["en"]}
	}
	return []language{m.lang}
}

// speakIn changes the languages Delta hears and speaks, as the window asks.
func (m *mcpServer) speakIn(ls []language) {
	m.langMu.Lock()
	m.langs = ls
	m.langMu.Unlock()
}

// languagesOf reads a language setting: a code, such as "fr", or two joined by "+", such as "fr+en", for a bilingual
// Delta whose own language comes first. An unknown code gives English and an error that says so.
func languagesOf(setting string) ([]language, error) {
	var ls []language
	for _, code := range strings.Split(setting, "+") {
		l, err := languageOf(strings.TrimSpace(code))
		if err != nil {
			return []language{l}, err
		}
		if !slices.ContainsFunc(ls, func(o language) bool { return o.code == l.code }) {
			ls = append(ls, l)
		}
	}
	return ls, nil
}

// settingOf writes languages as languagesOf reads them.
func settingOf(ls []language) string {
	codes := make([]string, len(ls))
	for i, l := range ls {
		codes[i] = l.code
	}
	return strings.Join(codes, "+")
}

// codesOf is the codes of languages, for Whisper.
func codesOf(ls []language) []string {
	return strings.Split(settingOf(ls), "+")
}

// languageOf returns the language with this code, English when there is none. An unknown code gives English and an
// error that says so.
func languageOf(code string) (language, error) {
	if code == "" {
		code = "en"
	}
	if l, ok := languages[code]; ok {
		return l, nil
	}
	return languages["en"], fmt.Errorf("Delta speaks en or fr, not %q: it speaks English", code)
}
