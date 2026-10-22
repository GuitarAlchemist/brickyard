package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"regexp"
	"strings"
	"time"
	"unicode"
)

// A triage sorts what Delta hears before Claude sees it, with a small model that runs on this computer and costs no
// tokens: small talk gets a short answer from that model, what is not meant for Delta is dropped, and everything else
// goes to Claude. When the model fails or is slow, the transcript goes to Claude, so that nothing is lost.
type triage func(ctx context.Context, recent []string, text string) (verdict, error)

type verdict struct {
	Route  string `json:"route"`  // "claude", "local" or "ignore"
	Answer string `json:"answer"` // with "local": what Delta says back, possibly nothing
}

// triagePrompt is the triage's instructions for the language Delta hears and speaks. The French one is word for word
// the prompt the corpus was measured with (testdata).
func triagePrompt(l language) string {
	speaker, fillers, acks := "an English speaker", `"hmm", "uh", "um"`, `"ok", "cool", "got it", "thanks"`
	if l.code == "fr" {
		speaker, fillers, acks = "a French speaker", `"hmm", "ah", "euh"`, `"ok", "cool", "d'accord", "merci"`
	}
	return fmt.Sprintf(triageTemplate, speaker, fillers, acks, l.name)
}

const triageTemplate = `You sort what %s says near Delta, a voice assistant. Delta's real brain is Claude, a capable but costly agent that works on the user's computer. You decide whether Claude must see the sentence. The sentence is a Whisper transcript of speech: it may be misheard, cut, or repeated by mistake.

Choose one route:
- "ignore": the sentence is not for Delta, or carries nothing. The user speaks to someone else: a person, a pet, or another assistant named in the sentence such as Gaia; a television or a call; a filler sound alone such as %s; or a transcription artefact such as another script or a lone syllable. answer is empty.
- "local": a social turn addressed to Delta that needs no tool, no fact and no action: a greeting, thanks, praise, an acknowledgement such as %s, a goodbye, a "wait, I'll be back". answer is Delta's own short, friendly spoken %s reply to it, in other words than the user's, or empty when a bare acknowledgement needs no reply. Never promise or claim an action in it, never state a fact.
- "claude": everything else: any question, a request, an instruction, an idea, a plan or an opinion to discuss, anything about code, files, music, mail, the weather or the calendar. answer is empty.

When unsure between "local" and "claude", choose "claude".`

// verdictSchema constrains the model's output. It is written out rather than marshalled from a map, because Ollama
// generates the properties in the schema's order, and Go sorts a map's keys: with "answer" first, the model wrote an
// answer before choosing its route, and chose badly.
var verdictSchema = json.RawMessage(`{"type":"object","properties":{` +
	`"route":{"type":"string","enum":["ignore","local","claude"]},"answer":{"type":"string"}},"required":["route","answer"]}`)

// ollamaTriage asks a local Ollama model for a verdict, as JSON constrained by verdictSchema, in the language Delta
// hears and speaks.
func ollamaTriage(url, model string, l language) triage {
	system := triagePrompt(l)
	return func(ctx context.Context, recent []string, text string) (verdict, error) {
		quoted, _ := json.Marshal(text)
		prompt := "Recent conversation:\n" + strings.Join(recent, "\n") + "\n\nNew sentence: " + string(quoted)
		if len(recent) == 0 {
			prompt = "New sentence: " + string(quoted)
		}
		body, _ := json.Marshal(map[string]any{
			"model": model, "stream": false, "think": false, "keep_alive": "30m", "format": verdictSchema,
			"options":  map[string]any{"temperature": 0, "num_predict": 80, "num_ctx": 2048},
			"messages": []message{{Role: "system", Content: system}, {Role: "user", Content: prompt}},
		})
		req, err := http.NewRequestWithContext(ctx, "POST", url+"/api/chat", bytes.NewReader(body))
		if err != nil {
			return verdict{}, err
		}
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			return verdict{}, err
		}
		defer resp.Body.Close()
		data, _ := io.ReadAll(io.LimitReader(resp.Body, 64*1024))
		if resp.StatusCode != http.StatusOK {
			return verdict{}, fmt.Errorf("ollama: %s: %s", resp.Status, bytes.TrimSpace(data))
		}
		var r struct {
			Message message `json:"message"`
		}
		if err := json.Unmarshal(data, &r); err != nil {
			return verdict{}, fmt.Errorf("ollama: %w", err)
		}
		return parseVerdict(r.Message.Content)
	}
}

func parseVerdict(content string) (verdict, error) {
	var v verdict
	if err := json.Unmarshal([]byte(content), &v); err != nil {
		return verdict{}, fmt.Errorf("triage: not a verdict: %q", content)
	}
	v.Answer = strings.TrimSpace(v.Answer)
	switch v.Route {
	case "claude", "ignore":
		v.Answer = ""
	case "local":
	default:
		return verdict{}, fmt.Errorf("triage: unknown route %q", v.Route)
	}
	return v, nil
}

// warmTriage loads the model, so that the first transcript does not wait the 10 s or more a cold load takes.
func warmTriage(t triage, l language) error {
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	_, err := t(ctx, nil, l.hello+" Delta.")
	return err
}

// carriesNothing is a transcript no model needs to read: no Latin letter (Whisper's artefacts in another script, or
// punctuation alone), or filler sounds alone.
func carriesNothing(text string) bool {
	words := strings.FieldsFunc(strings.ToLower(text), func(r rune) bool { return !unicode.IsLetter(r) })
	for _, w := range words {
		latin := false
		for _, r := range w {
			if unicode.In(r, unicode.Latin) {
				latin = true
				break
			}
		}
		if latin && !fillers[w] {
			return false
		}
	}
	return true
}

// fillers, acks, greetings, smallTalk and the promises are French and English together, whatever the session's
// language: a French word in an English session is read the same.
var fillers = map[string]bool{"ah": true, "oh": true, "eh": true, "hein": true, "euh": true, "heu": true, "hum": true,
	"hmm": true, "hm": true, "mmh": true, "mm": true, "bah": true, "ben": true, "uh": true, "um": true, "oups": true,
	"er": true, "erm": true, "uhm": true, "huh": true, "mhm": true, "oops": true, "whoa": true}

// namesAnother is a transcript spoken to another session than Delta's, as addressee reads it: the user speaks to Gaia
// or Codex, not to Delta. The wake gate drops these already; with -no-wake, the triage still does.
func namesAnother(text string) bool {
	to := addressee(text)
	return to != "" && to != "Delta" && to != "Claude"
}

// callsClaude is a transcript that calls Claude among its first three words, as Whisper writes it: « Claude », or
// « Cloud Code » misheard. It goes straight to Claude, past the wake gate and the triage.
func callsClaude(text string) bool {
	words := strings.FieldsFunc(strings.ToLower(text), func(r rune) bool { return !unicode.IsLetter(r) })
	for i, w := range words {
		if i == 3 {
			break
		}
		if w == "claude" || (w == "cloud" && i+1 < len(words) && words[i+1] == "code") {
			return true
		}
	}
	return false
}

var greetings = map[string]bool{"salut": true, "bonjour": true, "bonsoir": true, "coucou": true, "hello": true,
	"bonne": true, "nuit": true, "journée": true, "soirée": true, "au": true, "revoir": true, "à": true, "plus": true,
	"bientôt": true, "tard": true,
	"hi": true, "hey": true, "good": true, "morning": true, "afternoon": true, "evening": true, "night": true, "bye": true,
	"goodbye": true, "see": true, "you": true, "later": true, "soon": true, "take": true, "care": true}

// parrots is an answer that mostly repeats what the user said: qwen3:8b echoed « Ok cool. » and a whole question back.
// Four in five of the words they hold between them are shared.
func parrots(answer, text string) bool {
	split := func(s string) map[string]bool {
		set := map[string]bool{}
		for _, w := range strings.FieldsFunc(strings.ToLower(s), func(r rune) bool { return !unicode.IsLetter(r) }) {
			if w != "delta" { // « Delta, ok cool. » answered « Ok cool. » is still a parrot
				set[w] = true
			}
		}
		return set
	}
	a, t := split(answer), split(text)
	greeting := true // « Salut ! » to « Salut Delta ! » is a greeting returned, not a parrot
	for w := range a {
		greeting = greeting && greetings[w]
	}
	if len(a) == 0 || greeting {
		return false
	}
	shared := 0
	for w := range a {
		if t[w] {
			shared++
		}
	}
	return float64(shared) >= 0.8*float64(len(a)+len(t)-shared)
}

// acknowledges is a transcript of acknowledgement words alone, such as « Ok. », « Cool. » or « D'accord, super. »:
// Delta keeps it, says nothing, and asks no model.
func acknowledges(text string) bool {
	words := strings.FieldsFunc(strings.ToLower(text), func(r rune) bool { return !unicode.IsLetter(r) })
	for _, w := range words {
		if !acks[w] && w != "delta" {
			return false
		}
	}
	return len(words) > 0
}

var acks = map[string]bool{"ok": true, "okay": true, "cool": true, "d": true, "accord": true, "super": true, "parfait": true,
	"génial": true, "top": true, "nickel": true, "ouais": true, "oui": true, "bien": true, "très": true, "bon": true,
	"ah": true, "oh": true, "alors": true, "entendu": true, "compris": true, "ça": true, "marche": true, "reçu": true, "vu": true,
	"alright": true, "all": true, "right": true, "sure": true, "yes": true, "yeah": true, "yep": true, "got": true, "it": true,
	"fine": true, "good": true, "great": true, "nice": true, "perfect": true, "understood": true, "noted": true, "sounds": true}

// promises is a local answer that takes on work only Claude can do: live, qwen3:8b kept « Ok, mais fais collaborer les
// deux. » and answered « D'accord, je m'en occupe. ». Such a sentence goes to Claude instead.
func promises(answer string) bool {
	a := " " + strings.Join(strings.FieldsFunc(strings.ToLower(answer), func(r rune) bool { return !unicode.IsLetter(r) }), " ") + " "
	for _, p := range []string{" je m en occupe ", " je m occupe ", " je vais ", " je lance ", " je fais ", " je regarde ",
		" je cherche ", " je vérifie ", " je verifie ", " je corrige ", " c est fait ", " je m y mets ", " je note ", " je te dis ",
		" i ll ", " i will ", " i m on it ", " on it ", " let me ", " i m going to ", " i m gonna ", " it s done ", " all done ",
		" i ve done ", " i m checking ", " i m looking ", " consider it done "} {
		if strings.Contains(a, p) {
			return true
		}
	}
	return false
}

// speakable rewrites what Delta's voice reads wrong, in the language with this code: Piper reads « IX », the name of a
// repository and a session, as a Roman numeral, « neuf » or "nine". The French voice gets « ixe », as the author says
// it, and the English voice "I X".
func speakable(text, code string) string {
	if code == "fr" {
		return romanIX.ReplaceAllString(text, "ixe")
	}
	return romanIX.ReplaceAllString(text, "I X")
}

var romanIX = regexp.MustCompile(`\bIX\b`)

// social is a sentence with a word of small talk in it: thanks, a greeting, praise, a goodbye, a « wait ». Only such a
// sentence may stay with the small model; live, it kept « Delta n'entend plus rien. » and answered « D'accord. ».
func social(text string) bool {
	for _, w := range strings.FieldsFunc(strings.ToLower(text), func(r rune) bool { return !unicode.IsLetter(r) }) {
		if smallTalk[w] {
			return true
		}
	}
	return false
}

// smile is the expression Delta answers small talk with: sleepy for good night, joy for the rest.
func smile(text string) string {
	line := " " + strings.Join(plainWords(text), " ") + " "
	for _, night := range []string{" bonne nuit ", " good night ", " goodnight ", " night night "} {
		if strings.Contains(line, night) {
			return "sleepy"
		}
	}
	return "joy"
}

var smallTalk = map[string]bool{"merci": true, "salut": true, "bonjour": true, "bonsoir": true, "coucou": true,
	"hello": true, "bravo": true, "génial": true, "super": true, "cool": true, "top": true, "parfait": true,
	"nickel": true, "excellent": true, "chapeau": true, "attends": true, "attendez": true, "reviens": true,
	"revoir": true, "bientôt": true, "nuit": true, "journée": true, "soirée": true, "bien": true,
	"thanks": true, "thank": true, "hi": true, "hey": true, "awesome": true, "great": true, "nice": true, "wonderful": true,
	"brilliant": true, "perfect": true, "wait": true, "hold": true, "bye": true, "goodbye": true, "later": true,
	"night": true, "morning": true, "evening": true}
