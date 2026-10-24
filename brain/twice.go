package main

import (
	"slices"
	"strings"
	"time"
	"unicode"
)

// The computer's microphone and a phone in the same room can both hear one sentence, the phone's copy a second or a
// few later, once its button is released and its sound sent. The copy that comes second is not routed again.
//
// When the user speaks into both at once, the two copies come a fraction of a second apart, each misheard its own way,
// as six pairs did live (0.16 to 0.48 s, pid 117856). A copy that comes while the other device still hears the
// sentence waits for that one, holdMax at most, and the louder of the two goes: its microphone was closer to the
// mouth, so its transcript is the better one, and the answer plays where the user spoke. Without a level between
// them, the longer transcript goes, then the first.

// twiceWithin is how far apart two copies of a sentence come at most.
const twiceWithin = 8 * time.Second

// copiesWithin is how far apart two copies come when the user speaks into both devices at once. Within it, two
// sentences from different devices are one unless they share no word: one speaker, one room.
const copiesWithin = 1500 * time.Millisecond

// heardCopy is a sentence waiting for the other device's copy of it.
type heardCopy struct {
	text, source string
	words        []string // foldedWords
	level        float64
	at           time.Time
	decided, won bool       // the other copy came, and which went
	twin         *heardCopy // that copy
}

// routed is a sentence that went somewhere, to Claude or to the triage, for a copy from the other device to be known.
type routed struct {
	words  []string
	folded []string // foldedWords, for a copy that came at once
	source string
	at     time.Time
}

// sameSentence says whether two transcripts are one sentence heard twice: the same words but one in five at most,
// whatever Whisper made of the punctuation and the case. Short sentences, such as "oui", said on each device are two.
func sameSentence(a, b []string) bool {
	if len(a) < 3 || len(b) < 3 {
		return false
	}
	return 5*commonWords(a, b) >= 4*max(len(a), len(b))
}

// likeSentence says whether two transcripts that came at once share a word, accents and digits folded: « OK pour le
// 1 go. » and « Pour le un goût. » are one sentence.
func likeSentence(a, b []string) bool {
	return commonWords(a, b) > 0
}

// commonWords counts the words of b that a has, each of a's once.
func commonWords(a, b []string) int {
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
	return common
}

// foldedWords are a transcript's words as likeSentence compares them: plainWords, with numbers up to ten in letters,
// as Whisper writes « un » one time and « 1 » the next.
func foldedWords(text string) []string {
	words := plainWords(text)
	for i, w := range words {
		if n, ok := digitWords[w]; ok {
			words[i] = n
		}
	}
	return words
}

var digitWords = map[string]string{"0": "zero", "1": "un", "2": "deux", "3": "trois", "4": "quatre", "5": "cinq",
	"6": "six", "7": "sept", "8": "huit", "9": "neuf", "10": "dix", "one": "un", "two": "deux", "three": "trois"}

// louder says whether copy a goes rather than copy b: louder by a tenth, else longer, else first.
func louder(a, b *heardCopy) bool {
	switch {
	case a.level > 1.1*b.level:
		return true
	case b.level > 1.1*a.level:
		return false
	case len(a.words) != len(b.words):
		return len(a.words) > len(b.words)
	}
	return !b.at.Before(a.at)
}

// sourceName is a source as the journal writes it.
func sourceName(source string) string {
	if source == "" {
		return "computer"
	}
	return source
}

// earsHear counts the sentences a source's ears are transcribing: +1 as one starts, -1 when it ends without being
// routed. A routed sentence leaves the count as it comes (awaitCopy).
func (m *mcpServer) earsHear(source string, delta int) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.inEars == nil {
		m.inEars = map[string]int{}
	}
	m.inEars[source] = max(0, m.inEars[source]+delta)
}

// awaitCopy pairs a sentence with the other device's copy of it: one that waits here already, or one that comes while
// this one waits, holdMax at most, because the other device was hearing a sentence. It returns that copy, if any, and
// whether this sentence goes rather than it.
func (m *mcpServer) awaitCopy(u utterance, now time.Time) (*heardCopy, bool) {
	me := &heardCopy{text: u.text, source: u.source, words: foldedWords(u.text), level: u.level, at: now}
	other := "remote"
	if u.source == "remote" {
		other = ""
	}
	m.mu.Lock()
	if m.inEars[u.source] > 0 { // this one is out of its ears
		m.inEars[u.source]--
	}
	for i, c := range m.copies { // each waits, holdMax at most, because this device was hearing a sentence
		if c.source == other && likeSentence(c.words, me.words) {
			m.copies = slices.Delete(m.copies, i, i+1)
			c.decided, c.won, c.twin = true, louder(c, me), me
			m.mu.Unlock()
			return c, !c.won
		}
	}
	if m.holdMax <= 0 || m.inEars[other] == 0 {
		m.mu.Unlock()
		return nil, true
	}
	m.copies = append(m.copies, me)
	m.mu.Unlock()
	for end := time.Now().Add(m.holdMax); ; time.Sleep(10 * time.Millisecond) {
		m.mu.Lock()
		if me.decided {
			m.mu.Unlock()
			return me.twin, me.won
		}
		if m.inEars[other] == 0 || !time.Now().Before(end) {
			if m.inEars[other] > 0 { // a count that never came down, so that the next sentence does not wait for it
				m.inEars[other] = 0
			}
			m.copies = slices.DeleteFunc(m.copies, func(c *heardCopy) bool { return c == me })
			m.mu.Unlock()
			return nil, true
		}
		m.mu.Unlock()
	}
}

// sentenceWords are a transcript's words, in lower case, without punctuation.
func sentenceWords(text string) []string {
	return strings.FieldsFunc(strings.ToLower(text), func(r rune) bool { return !unicode.IsLetter(r) && !unicode.IsDigit(r) })
}

// heardTwice says whether a sentence is a copy of one the other device heard lately. The caller holds m.mu.
func (m *mcpServer) heardTwice(u utterance, now time.Time) bool {
	words, folded := sentenceWords(u.text), foldedWords(u.text)
	for _, r := range m.lately {
		apart := now.Sub(r.at).Abs() // a copy that waited for the other came before it
		if r.source != u.source && (apart < twiceWithin && sameSentence(r.words, words) || apart < copiesWithin && likeSentence(r.folded, folded)) {
			return true
		}
	}
	return false
}

// routing keeps a sentence about to be routed for twiceWithin, as soon as it is heard: two microphones send their
// copies a fraction of a second apart, while the first may still wait for the triage. The caller holds m.mu.
func (m *mcpServer) routing(u utterance, now time.Time) {
	kept := m.lately[:0]
	for _, r := range m.lately {
		if now.Sub(r.at) < twiceWithin {
			kept = append(kept, r)
		}
	}
	m.lately = append(kept, routed{sentenceWords(u.text), foldedWords(u.text), u.source, now})
}

// forgetRouted takes back a sentence routing recorded, once the wake gate ignored it.
func (m *mcpServer) forgetRouted(u utterance, now time.Time) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.lately = slices.DeleteFunc(m.lately, func(r routed) bool { return r.at.Equal(now) && r.source == u.source })
}
