// Command brain is Delta's voice loop. What it hears goes to a local language model, and each sentence of the answer
// goes to a local voice as soon as the sentence ends, so that Delta starts speaking before the model has finished.
// Go's standard library only; nothing leaves the machine.
//
//	brain -piper <piper.exe> -voice <model.onnx> [-model qwen3:8b] [-scene 127.0.0.1:8805] [-mute]
//	      [-whisper <whisper-server.exe> [-whisper-model <ggml.bin>] [-mic | -hear a.wav,b.wav]]
//	brain -mcp [-home <dir>] [-no-scene] [-no-wake] [-hooks 127.0.0.1:8807] [-fx warm|none]
//
// With -mcp, Claude is the brain instead of the local model: the brain is an MCP server for Claude Code (mcp.go,
// voicemcp.go), which hears through the microphone and speaks through Delta's say tool.
//
// Each line on standard input is something heard. With -whisper, the brain also runs whisper.cpp's server on
// 127.0.0.1 (-ears-port) to transcribe: -mic listens on the default microphone, cutting utterances at 700 ms of quiet
// and hearing nothing while Delta answers; -hear transcribes WAV files instead, one after another, for tests. For each
// answer it prints how long the transcription took and when the first token, the first sentence and the first audio
// came, counted from the end of what was heard.
package main

import (
	"bufio"
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync/atomic"
	"time"
)

// version is set by release.ps1 (-ldflags -X main.version=...).
var version = "dev"

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
	whisper := flag.String("whisper", "", "whisper.cpp's whisper-server executable, to listen")
	whisperModel := flag.String("whisper-model", "", "a whisper.cpp model (default: ggml-large-v3-turbo-q5_0.bin beside -whisper)")
	earsPort := flag.Int("ears-port", 8806, "the loopback port for whisper-server")
	threads := flag.Int("threads", min(16, runtime.NumCPU()), "whisper-server's CPU threads")
	useMic := flag.Bool("mic", false, "listen on the default microphone (needs -whisper)")
	micLevels := flag.Bool("mic-levels", false, "with -mic, print the microphone's level every 2 s")
	hearFiles := flag.String("hear", "", "comma-separated WAV files to transcribe as if heard, then exit (needs -whisper)")
	transcribe := flag.Bool("transcribe", false, "with -hear, only transcribe the files and time it")
	// 768 is about 15 s, the longest utterance the endpoint lets through; on the CPU it took the transcription of 2 to
	// 3 s of French from 4.9-7.0 s down to 1.6-2.4 s, with the same words.
	audioCtx := flag.Int("audio-ctx", 768, "whisper's audio context, 0 for the model's 30 s window (1500)")
	mcpMode := flag.Bool("mcp", false, "be an MCP server for Claude Code on stdin and stdout, Claude being the brain")
	home := flag.String("home", "", "with -mcp, where setup.ps1 installed Piper, a voice and whisper.cpp")
	noScene := flag.Bool("no-scene", false, "with -mcp, do not open Delta's window")
	noWake := flag.Bool("no-wake", false, "with -mcp, send everything heard to Claude, not only what follows \"Delta\"")
	journalFile := flag.String("journal", "", "with -mcp, append what Delta hears and says to this file, one JSON line each")
	triageModel := flag.String("triage", "", "with -mcp, an Ollama model that sorts what Delta hears before Claude: it answers small talk itself, "+
		"drops what is not for Delta and passes the rest to Claude")
	hooksAddr := flag.String("hooks", "127.0.0.1:8807", "with -mcp, where Claude Code's hooks post what the session does, such as a compaction (hooks.go); empty for nowhere")
	fx := flag.String("fx", "warm", "with -mcp, the effect on Delta's voice: warm, IX's closer and warmer voice (fx.go), or none")
	showVersion := flag.Bool("version", false, "print the version and exit")
	flag.Parse()
	if *showVersion {
		fmt.Println(version)
		return
	}
	if *whisperModel == "" && *whisper != "" {
		*whisperModel = filepath.Join(filepath.Dir(*whisper), "ggml-large-v3-turbo-q5_0.bin")
	}
	if *mcpMode {
		addr := *sceneAddr
		if addr == "" {
			addr = "127.0.0.1:8805"
		}
		runMCP(mcpConfig{home: *home, piper: *piper, voice: *voiceModel, whisper: *whisper, whisperModel: *whisperModel,
			scene: addr, journal: *journalFile, ollama: *ollama, triage: *triageModel, hooks: *hooksAddr, fx: *fx, noScene: *noScene, mute: *mute, wakeOff: *noWake, threads: *threads, audioCtx: *audioCtx})
		return
	}
	if *transcribe {
		e, err := startEars(*whisper, *whisperModel, *earsPort, *threads, "Delta, Brickyard.", *audioCtx)
		if err != nil {
			fail(err)
		}
		defer e.close()
		for _, path := range append([]string{""}, strings.Split(*hearFiles, ",")...) {
			samples := make([]float32, earsRate) // first, a second of silence loads the weights and kernels
			if path != "" {
				w, err := readWav(path)
				if err != nil {
					fail(err)
				}
				samples = resample(w.samples, w.rate, earsRate)
			}
			t0 := time.Now()
			text, err := e.hear(samples)
			if err != nil {
				fail(err)
			}
			line, _ := json.Marshal(map[string]any{"file": filepath.Base(path), "seconds": float64(len(samples)) / earsRate,
				"heard": text, "asr_ms": time.Since(t0).Milliseconds()})
			fmt.Println(string(line))
		}
		return
	}
	if *piper == "" || *voiceModel == "" {
		fmt.Fprintln(os.Stderr, "brain: -piper and -voice are required; check the voice's licence")
		os.Exit(2)
	}
	if (*useMic || *hearFiles != "") && *whisper == "" {
		fmt.Fprintln(os.Stderr, "brain: -mic and -hear need -whisper")
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

	var e *ears
	if *whisper != "" {
		start = time.Now()
		if e, err = startEars(*whisper, *whisperModel, *earsPort, *threads, "Delta, Brickyard.", *audioCtx); err != nil {
			fail(err)
		}
		defer e.close()
		if _, err := e.hear(make([]float32, earsRate)); err != nil { // loads the model's weights and kernels
			fail(err)
		}
		fmt.Printf("ears ready on %s in %d ms\n", e.url, time.Since(start).Milliseconds())
	}

	s := dialScene(*sceneAddr)
	s.send(map[string]any{"state": "idle"})
	history := []message{{"system", persona}}
	respond := func(u utterance) {
		if u.text == "" {
			s.send(map[string]any{"state": "idle"})
			return
		}
		history = append(history, message{"user", u.text})
		t, err := answer(*ollama, *model, history, v, s, *mute, u.ended)
		if err != nil {
			fmt.Fprintln(os.Stderr, "brain:", err)
			history = history[:len(history)-1]
			return
		}
		history = append(history, message{"assistant", t.answer})
		if len(history) > 13 { // the persona and the last six exchanges
			history = append(history[:1], history[len(history)-12:]...)
		}
		line, _ := json.Marshal(map[string]any{
			"heard": u.text, "answer": t.answer, "asr_ms": u.asr.Milliseconds(),
			"first_token_ms": t.firstToken.Milliseconds(), "first_sentence_ms": t.firstSentence.Milliseconds(),
			"first_audio_ms": t.firstAudio.Milliseconds(), "done_ms": t.done.Milliseconds(),
		})
		fmt.Println(string(line))
	}

	if *hearFiles != "" {
		for _, path := range strings.Split(*hearFiles, ",") {
			w, err := readWav(path)
			if err != nil {
				fail(err)
			}
			ended := time.Now()
			text, err := e.hear(resample(w.samples, w.rate, earsRate))
			if err != nil {
				fail(err)
			}
			respond(utterance{text: text, ended: ended, asr: time.Since(ended)})
		}
		return
	}

	var busy atomic.Bool
	heard := make(chan utterance)
	go func() {
		in := bufio.NewScanner(os.Stdin)
		for in.Scan() {
			if text := strings.TrimSpace(in.Text()); text != "" {
				heard <- utterance{text: text, ended: time.Now()}
			}
		}
		if !*useMic {
			close(heard)
		}
	}()
	if *useMic {
		go listen(e, s, &busy, heard, *micLevels)
	}
	for u := range heard {
		busy.Store(true)
		respond(u)
		time.Sleep(300 * time.Millisecond) // the room's echo of the last word is not heard as speech
		busy.Store(false)
	}
}

// answer runs one turn, timed from t0: the model's tokens are cut into sentences, each sentence is synthesised while
// the model goes on, and the sound is played in order.
func answer(url, model string, history []message, v *voice, s *scene, mute bool, t0 time.Time) (turn, error) {
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
