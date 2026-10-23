package main

import (
	"strings"
	"time"
	"unicode"
)

// The computer's microphone and a phone in the same room can both hear one sentence, the phone's copy a second or a
// few later, once its button is released and its sound sent. The copy that comes second is not routed again.

// twiceWithin is how far apart two copies of a sentence come at most.
const twiceWithin = 8 * time.Second

// routed is a sentence that went somewhere, to Claude or to the triage, for a copy from the other device to be known.
type routed struct {
	words  []string
	source string
	at     time.Time
}

// sameSentence says whether two transcripts are one sentence heard twice: the same words but one in five at most,
// whatever Whisper made of the punctuation and the case. Short sentences, such as "oui", said on each device are two.
func sameSentence(a, b []string) bool {
	if len(a) < 3 || len(b) < 3 {
		return false
	}
	count := map[string]int{}
	for _, w := range a {
		count[w]++
	}
	common := 0
	for _, w := range b {
		if count[w] > 0 {
			count[w]--
			common++
		}
	}
	return 5*common >= 4*max(len(a), len(b))
}

// sentenceWords are a transcript's words, in lower case, without punctuation.
func sentenceWords(text string) []string {
	return strings.FieldsFunc(strings.ToLower(text), func(r rune) bool { return !unicode.IsLetter(r) && !unicode.IsDigit(r) })
}

// heardTwice says whether a sentence is a copy of one the other device heard lately. The caller holds m.mu.
func (m *mcpServer) heardTwice(u utterance, now time.Time) bool {
	words := sentenceWords(u.text)
	for _, r := range m.lately {
		if r.source != u.source && now.Sub(r.at) < twiceWithin && sameSentence(r.words, words) {
			return true
		}
	}
	return false
}

// routedNow keeps a sentence that went somewhere for twiceWithin.
func (m *mcpServer) routedNow(u utterance, now time.Time) {
	m.mu.Lock()
	defer m.mu.Unlock()
	kept := m.lately[:0]
	for _, r := range m.lately {
		if now.Sub(r.at) < twiceWithin {
			kept = append(kept, r)
		}
	}
	m.lately = append(kept, routed{sentenceWords(u.text), u.source, now})
}
