// Command brain is Delta's voice loop. What it hears goes to a local language model, and each sentence of the answer
// goes to a local voice as soon as the sentence ends, so that Delta starts speaking before the model has finished.
// Go's standard library only; nothing leaves the machine.
//
//	brain -piper <piper.exe> -voice <model.onnx> [-model qwen3:8b] [-scene 127.0.0.1:8805] [-mute]
//
// Each line on standard input is something heard. For each answer it prints when the first token, the first sentence
// and the first audio came, counted from the moment the line was read.
package main

import (
	"bufio"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"strings"
	"time"
)

const persona = "Tu es Delta, le petit robot qui dirige Brickyard, une usine à logiciels qu'on pilote à la voix. " +
	"Tu parles français, à l'oral : une ou deux phrases courtes, chaleureuses et précises, sans liste, sans markdown, " +
	"sans emoji. Pour l'instant tu sais seulement converser : tu ne peux encore rien construire, lancer ni modifier. " +
	"Si on te le demande, dis-le simplement et propose d'en parler."

type turn struct {
	firstToken, firstSentence, firstAudio, done time.Duration
	answer                                      string
}

func main() {
	ollama := flag.String("ollama", "http://127.0.0.1:11434", "Ollama's address")
	model := flag.String("model", "qwen3:8b", "the language model")
	piper := flag.String("piper", "", "Piper's executable (required)")
	voiceModel := flag.String("voice", "", "a Piper voice, .onnx with its .onnx.json (required)")
	sceneAddr := flag.String("scene", "", "the scene's address, to show Delta's state")
	mute := flag.Bool("mute", false, "synthesise but do not play")
	flag.Parse()
	if *piper == "" || *voiceModel == "" {
		fmt.Fprintln(os.Stderr, "brain: -piper and -voice are required; check the voice's licence")
		os.Exit(2)
	}
	dir, err := os.MkdirTemp("", "brickyard-voice-")
	if err != nil {
		fail(err)
	}
	defer os.RemoveAll(dir)

	start := time.Now()
	v, err := startVoice(*piper, *voiceModel, dir, "--sentence_silence", "0.1")
	if err != nil {
		fail(err)
	}
	defer v.close()
	if _, err := v.say("Bonjour."); err != nil {
		fail(err)
	}
	voiceReady := time.Since(start)
	start = time.Now()
	if err := chat(context.Background(), *ollama, *model, []message{{"user", "ok"}}, func(string) {}); err != nil {
		fail(err)
	}
	fmt.Printf("ready: voice %d ms, %s loaded in %d ms\n", voiceReady.Milliseconds(), *model, time.Since(start).Milliseconds())

	s := dialScene(*sceneAddr)
	s.send(map[string]any{"state": "idle"})
	history := []message{{"system", persona}}
	in := bufio.NewScanner(os.Stdin)
	for in.Scan() {
		heard := strings.TrimSpace(in.Text())
		if heard == "" {
			continue
		}
		history = append(history, message{"user", heard})
		t, err := answer(*ollama, *model, history, v, s, *mute)
		if err != nil {
			fmt.Fprintln(os.Stderr, "brain:", err)
			history = history[:len(history)-1]
			continue
		}
		history = append(history, message{"assistant", t.answer})
		if len(history) > 13 { // the persona and the last six exchanges
			history = append(history[:1], history[len(history)-12:]...)
		}
		line, _ := json.Marshal(map[string]any{
			"heard": heard, "answer": t.answer, "first_token_ms": t.firstToken.Milliseconds(),
			"first_sentence_ms": t.firstSentence.Milliseconds(), "first_audio_ms": t.firstAudio.Milliseconds(),
			"done_ms": t.done.Milliseconds(),
		})
		fmt.Println(string(line))
	}
}

// answer runs one turn: the model's tokens are cut into sentences, each sentence is synthesised while the model goes
// on, and the sound is played in order.
func answer(url, model string, history []message, v *voice, s *scene, mute bool) (turn, error) {
	t0 := time.Now()
	var t turn
	s.send(map[string]any{"state": "thinking"})
	toSay := make(chan string, 16)
	toPlay := make(chan string, 16)
	played := make(chan struct{})
	go func() { // the voice: one sentence at a time
		for sentence := range toSay {
			path, err := v.say(sentence)
			if err != nil {
				fmt.Fprintln(os.Stderr, "voice:", err)
				continue
			}
			if t.firstAudio == 0 {
				t.firstAudio = time.Since(t0)
			}
			toPlay <- path
		}
		close(toPlay)
	}()
	go func() { // the speaker, and the scene's mouth
		for path := range toPlay {
			w, err := readWav(path)
			if err == nil {
				s.send(map[string]any{"state": "speaking", "seconds": w.seconds, "envelope": w.envelope})
			}
			if mute {
				continue
			}
			play(path)
		}
		close(played)
	}()

	var all strings.Builder
	var cut sentences
	err := chat(context.Background(), url, model, history, func(token string) {
		if t.firstToken == 0 {
			t.firstToken = time.Since(t0)
		}
		all.WriteString(token)
		for _, sentence := range cut.feed(token) {
			if t.firstSentence == 0 {
				t.firstSentence = time.Since(t0)
			}
			toSay <- sentence
		}
	})
	if rest := cut.flush(); rest != "" {
		if t.firstSentence == 0 {
			t.firstSentence = time.Since(t0)
		}
		toSay <- rest
	}
	close(toSay)
	<-played
	t.done = time.Since(t0)
	t.answer = strings.TrimSpace(all.String())
	s.send(map[string]any{"state": "idle"})
	return t, err
}

func fail(err error) {
	fmt.Fprintln(os.Stderr, "brain:", err)
	os.Exit(1)
}
