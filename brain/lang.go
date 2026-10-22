package main

import "fmt"

// language is what Delta hears and speaks. English is the default and French the other language it knows. It sets
// Whisper's -l, the Piper voice setup.ps1 installs, the language Claude is asked to answer in, and the standalone
// mode's persona.
type language struct {
	code    string // Whisper's -l, and "language" in delta.json
	name    string // as Claude is told it
	voice   string // the Piper voice setup.ps1 installs for it, under <home>\voices
	hello   string // said once at start-up, which loads the voice model
	persona string // the standalone mode's system prompt
}

var languages = map[string]language{
	"en": {code: "en", name: "English", voice: "en_US-ljspeech-medium.onnx", hello: "Hello.",
		persona: "You are Delta, the little robot who runs Brickyard, a software factory driven by voice. " +
			"You speak English, aloud: one or two short sentences, warm and precise, with no lists, no markdown and no " +
			"emoji. For now you can only talk: you cannot build, run or change anything yet. " +
			"If someone asks you to, say so simply and offer to talk about it."},
	"fr": {code: "fr", name: "French", voice: "fr_FR-siwis-medium.onnx", hello: "Bonjour.",
		persona: "Tu es Delta, le petit robot qui dirige Brickyard, une usine à logiciels qu'on pilote à la voix. " +
			"Tu parles français, à l'oral : une ou deux phrases courtes, chaleureuses et précises, sans liste, sans markdown, " +
			"sans emoji. Pour l'instant tu sais seulement converser : tu ne peux encore rien construire, lancer ni modifier. " +
			"Si on te le demande, dis-le simplement et propose d'en parler."},
}

// language is the language the server hears and speaks, English when none was set.
func (m *mcpServer) language() language {
	if m.lang.code == "" {
		return languages["en"]
	}
	return m.lang
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
