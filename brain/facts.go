package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

// Delta answers a few questions of fact itself, from a sheet the brain writes at the moment of the question: the date
// and the time, Delta's version, where Whisper runs, the small model, what Brickyard and IX are. The triage's model
// reads the sheet and says whether it answers the question; when it does, its short answer is said at no cost, and
// Claude reads the exchange with the next event. What the sheet does not hold, and anything that asks for an action,
// still goes to Claude. A rule picks the questions first, so that other sentences do not wait for a second model call.
// Measured with qwen3:8b warm: 230 to 550 ms for an answer, about 160 ms for a question the sheet does not answer.
type factModel func(ctx context.Context, sheet, text string) (factVerdict, error)

type factVerdict struct {
	Known  bool   `json:"known"`  // the sheet answers the question
	Answer string `json:"answer"` // with known: one short spoken sentence
}

// factSchema puts known before answer, so that the model decides before it writes, as verdictSchema does.
var factSchema = json.RawMessage(`{"type":"object","properties":{` +
	`"known":{"type":"boolean"},"answer":{"type":"string"}},"required":["known","answer"]}`)

// factMax is how long a question waits for the fact model before it goes to Claude.
const factMax = 3 * time.Second

const factTemplate = `You answer a question %s asked Delta, a voice assistant, only from the fact sheet below. The question is a Whisper transcript of speech: it may be misheard.

If the sheet answers it, known is true and answer is one short spoken %s sentence that answers it: no list, no markdown, no path or address. Otherwise known is false and answer is empty: when the sheet does not hold the answer, when the sentence asks for an action, an opinion, or anything about files, code, the session or the conversation, or when you are unsure. Never guess, and never state what the sheet does not.

Fact sheet:
%s`

// ollamaFacts asks a local Ollama model whether the sheet answers a question, as JSON constrained by factSchema.
func ollamaFacts(url, model string, l language) factModel {
	speaker := "an English speaker"
	if l.code == "fr" {
		speaker = "a French speaker"
	}
	return func(ctx context.Context, sheet, text string) (factVerdict, error) {
		quoted, _ := json.Marshal(text)
		body, _ := json.Marshal(map[string]any{
			"model": model, "stream": false, "think": false, "keep_alive": "30m", "format": factSchema,
			"options": map[string]any{"temperature": 0, "num_predict": 120, "num_ctx": 2048},
			"messages": []message{{Role: "system", Content: fmt.Sprintf(factTemplate, speaker, l.name, sheet)},
				{Role: "user", Content: "Question: " + string(quoted)}},
		})
		req, err := http.NewRequestWithContext(ctx, "POST", url+"/api/chat", bytes.NewReader(body))
		if err != nil {
			return factVerdict{}, err
		}
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			return factVerdict{}, err
		}
		defer resp.Body.Close()
		data, _ := io.ReadAll(io.LimitReader(resp.Body, 64*1024))
		if resp.StatusCode != http.StatusOK {
			return factVerdict{}, fmt.Errorf("ollama: %s: %s", resp.Status, bytes.TrimSpace(data))
		}
		var r struct {
			Message message `json:"message"`
		}
		if err := json.Unmarshal(data, &r); err != nil {
			return factVerdict{}, fmt.Errorf("ollama: %w", err)
		}
		var f factVerdict
		if err := json.Unmarshal([]byte(r.Message.Content), &f); err != nil {
			return factVerdict{}, fmt.Errorf("facts: not a verdict: %q", r.Message.Content)
		}
		f.Answer = strings.TrimSpace(f.Answer)
		return f, nil
	}
}

// factWords are the subjects the sheet covers, as plainWords writes them.
var factWords = map[string]bool{
	"heure": true, "heures": true, "date": true, "jour": true, "aujourd": true, "version": true, "whisper": true,
	"ix": true, "x": true, "brickyard": true, "depot": true, "modele": true, "ollama": true, "qwen": true,
	"time": true, "day": true, "today": true, "repository": true, "repo": true, "model": true,
}

// questionWords open a question that Whisper wrote without its question mark.
var questionWords = map[string]bool{
	"quelle": true, "quel": true, "quels": true, "quelles": true, "quand": true, "qui": true, "comment": true,
	"combien": true, "est": true, "c": true, "qu": true, "what": true, "which": true, "when": true, "who": true,
	"how": true, "is": true, "are": true,
}

// asksAFact is a question about a subject the sheet covers: only those wait for the fact model.
func asksAFact(text string) bool {
	words := plainWords(text)
	for len(words) > 0 && (words[0] == "delta" || words[0] == "ok" || words[0] == "dis" || words[0] == "hey") {
		words = words[1:]
	}
	if len(words) == 0 || !strings.Contains(text, "?") && !questionWords[words[0]] {
		return false
	}
	for _, w := range words {
		if factWords[w] {
			return true
		}
	}
	return false
}

// factSheet is what the brain knows for sure at now, in lines.
func (m *mcpServer) factSheet(now time.Time) string {
	lines := []string{
		"Now: " + spokenNow(now, m.lang.code) + ".",
		"Delta's version: " + spokenVersion(m.version) + ".",
	}
	if m.hearsOn != nil {
		lines = append(lines, "Delta hears with Whisper (whisper.cpp), "+m.hearsOn()+".")
	}
	if m.smallModel != "" {
		lines = append(lines, "Delta's small model, which sorts what it hears and answers this: "+m.smallModel+
			", through Ollama, on this computer.")
	}
	lines = append(lines,
		"Brickyard: Delta's own code, a voice for Claude Code on Windows: a Go brain, a 3D window in C# and a Claude "+
			"Code plugin. Repository: GuitarAlchemist/brickyard on GitHub.",
		"IX: a Rust workspace of machine-learning and math algorithms and AI governance, which Claude Code uses as "+
			"skills. Repository: GuitarAlchemist/ix on GitHub. Part of GuitarAlchemist, with tars, ga and Demerzel.")
	return "- " + strings.Join(lines, "\n- ")
}

// fact answers a question from the sheet when the model finds the answer there, and returns "local"; else "", and
// the sentence goes to Claude.
func (m *mcpServer) fact(text string, fields map[string]any) string {
	if m.facts == nil || !asksAFact(text) {
		return ""
	}
	ctx, cancel := context.WithTimeout(context.Background(), factMax)
	defer cancel()
	start := time.Now()
	f, err := m.facts(ctx, m.factSheet(time.Now()), text)
	fields["fact_ms"] = time.Since(start).Milliseconds()
	switch {
	case err != nil:
		fields["fact"] = "failed: " + err.Error()
		return ""
	case !f.Known || f.Answer == "":
		fields["fact"] = "not on the sheet"
		return ""
	case promises(f.Answer):
		fields["fact"] = "overruled: the answer promised an action"
		fields["promise"] = f.Answer
		return ""
	}
	fields["fact"] = "answered"
	m.answerLocally(text, f.Answer, "", fields)
	return "local"
}

// spokenVersion is Delta's version as a voice can say it: "0.4.0", or "0.4.0, a development build 8 commits after
// it" for dev-v0.4.0-8-g83b7585, which a voice would spell out.
func spokenVersion(v string) string {
	rest, dev := strings.CutPrefix(v, "dev-")
	if !dev {
		return v
	}
	parts := strings.Split(strings.TrimPrefix(rest, "v"), "-")
	if strings.HasPrefix(rest, "v") && len(parts) >= 2 && parts[1] != "" && strings.Trim(parts[1], "0123456789") == "" {
		return parts[0] + ", a development build " + parts[1] + " commits after it"
	}
	if strings.HasPrefix(rest, "v") && len(parts) == 1 {
		return parts[0] + ", a development build"
	}
	return "a development build"
}

var (
	daysFr   = []string{"dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi"}
	monthsFr = []string{"janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre",
		"novembre", "décembre"}
)

// spokenNow is the day and the time in Delta's language: « jeudi 22 octobre 2026, 21 h 12 », or "Thursday, October
// 22, 2026, 9:12 PM".
func spokenNow(t time.Time, code string) string {
	if code == "fr" {
		return fmt.Sprintf("%s %d %s %d, %d h %02d", daysFr[t.Weekday()], t.Day(), monthsFr[t.Month()-1], t.Year(), t.Hour(), t.Minute())
	}
	return t.Format("Monday, January 2, 2006, 3:04 PM")
}
