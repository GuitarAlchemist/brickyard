package main

import (
	"bytes"
	"crypto/rand"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"maps"
	"math"
	"mime/multipart"
	"net"
	"net/http"
	"os"
	"os/exec"
	"slices"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// earsRate is the only rate whisper.cpp takes: 16 kHz mono.
const earsRate = 16000

// earsPatience is how long a transcription may take before the server is taken for hung: the CPU build hears a
// sentence in about 2 s, the GPU build in 0.2 s, and a graphics driver that stopped answering never does.
const earsPatience = 30 * time.Second

// ears is whisper.cpp's server, run by the brain on a loopback port and kept warm, so that each utterance costs only
// its transcription. On the author's machine the server and its model are IX's (whisper.cpp b5454 and
// ggml-large-v3-turbo-q5_0), used as they are. Given several servers, such as a GPU build and then the CPU build, the
// ears run the first that starts and hears, and the next when the one running dies.
type ears struct {
	url       string
	mu        sync.Mutex
	cmd       *exec.Cmd
	dead      chan struct{} // closed once cmd has exited
	server    string        // the server running now
	rest      []string      // the servers to fall back on, in order
	failed    []string      // why the servers before this one did not start
	run       func(server string) error
	fell      func(server, why string) // told when another server takes over, if set
	patience  time.Duration            // how long a transcription may take before the server is taken for hung
	languages func() []string          // the codes Delta hears now, one or two, or nil for the server's own (-l)
}

// utterance is something heard: its text, when the speaker stopped (or the line was read), and how long the
// transcription took.
type utterance struct {
	text   string
	ended  time.Time
	asr    time.Duration
	source string  // "" for this computer's microphone, "remote" for another device (remote.go)
	lang   string  // the language it was spoken in, as Whisper heard it, or "" when nobody asked
	level  float64 // how loud its voiced frames were (voicedLevel): the closer the microphone, the louder
}

// startEars runs whisper-server on the loopback port, transcribing lang ("en" or "fr"): the first of servers that
// starts and hears a second of silence, which loads the model's weights and kernels. The others are its fallbacks.
func startEars(servers []string, model, lang string, port, threads int, prompt string, audioCtx int) (*ears, error) {
	addr := "127.0.0.1:" + strconv.Itoa(port)
	if c, err := net.DialTimeout("tcp", addr, 200*time.Millisecond); err == nil {
		c.Close()
		return nil, fmt.Errorf("ears: %s is already taken", addr)
	}
	// -nf: no temperature fallback. With it, a mumbled reply took 31.6 s to come back as "Euh... Oui."; a voice loop
	// would rather have a rough transcript at once.
	args := []string{"-m", model, "-l", lang, "-t", strconv.Itoa(threads), "--host", "127.0.0.1", "--port", strconv.Itoa(port), "--prompt", prompt, "-nf"}
	if audioCtx > 0 {
		args = append(args, "-ac", strconv.Itoa(audioCtx))
	}
	path := earsPath()
	args = append(args, "--request-path", path)
	e := &ears{url: "http://" + addr + path, patience: earsPatience}
	e.run = func(server string) error { return e.start(server, args, addr) }
	for i, server := range servers {
		if err := e.run(server); err != nil {
			e.failed = append(e.failed, err.Error())
			continue
		}
		e.rest = servers[i+1:]
		return e, nil
	}
	return nil, fmt.Errorf("%s", strings.Join(e.failed, "; "))
}

// earsPath is the path every route of whisper-server sits under (its --request-path), drawn at each start and known to
// the brain alone. whisper.cpp answers every request with Access-Control-Allow-Origin: * and checks neither Origin nor
// Host, so a web page reaches its loopback port and could read what /inference makes of its audio, or post a file's
// path to /load, which tells whether that file exists and exits on one that is no model, leaving Delta deaf. Under a
// path it cannot guess, it finds neither. Both servers Delta runs take --request-path: IX's b5454, which setup.ps1
// installs, and the CUDA build.
func earsPath() string {
	b := make([]byte, 16)
	rand.Read(b)
	return "/" + hex.EncodeToString(b)
}

// start runs one server and waits until it hears. Its caller holds mu, or alone has e.
func (e *ears) start(server string, args []string, addr string) error {
	cmd := exec.Command(server, args...)
	hideConsole(cmd)
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("ears: %w", err)
	}
	killWithMe(cmd.Process)
	dead := make(chan struct{})
	go func() {
		cmd.Wait()
		close(dead)
	}()
	e.cmd, e.dead, e.server = cmd, dead, server
	for deadline := time.Now().Add(60 * time.Second); time.Now().Before(deadline); time.Sleep(100 * time.Millisecond) {
		select {
		case <-dead: // a GPU build without its libraries stops at once
			return fmt.Errorf("ears: %s stopped: %v", server, cmd.ProcessState)
		default:
		}
		if c, err := net.DialTimeout("tcp", addr, 200*time.Millisecond); err == nil {
			c.Close()
			if _, _, err := e.ask(make([]float32, earsRate)); err != nil {
				e.stop()
				return fmt.Errorf("ears: %s: %w", server, err)
			}
			return nil
		}
	}
	e.stop()
	return fmt.Errorf("ears: %s did not open %s within 60 s", server, addr)
}

// where says where the server running now transcribes, as its folder names it: a build in a folder named for CUDA or
// Vulkan is taken for the graphics card's.
func (e *ears) where() string {
	e.mu.Lock()
	server := strings.ToLower(e.server)
	e.mu.Unlock()
	if strings.Contains(server, "cuda") || strings.Contains(server, "vulkan") {
		return "on the graphics card"
	}
	return "on the processor"
}

// hear transcribes 16 kHz mono samples. When the server died or hung, the next one takes over and hears them, as it
// does a sentence that failed while another sentence's failure brought the next one.
func (e *ears) hear(samples []float32) (string, error) {
	text, _, err := e.hearIn(samples)
	return text, err
}

// hearIn is hear, which also returns the language the samples were spoken in, among those e.languages gives.
func (e *ears) hearIn(samples []float32) (text, lang string, err error) {
	e.mu.Lock()
	asked := e.server
	e.mu.Unlock()
	text, lang, err = e.ask(samples)
	if err != nil && e.fallBack(asked, err) {
		return e.ask(samples)
	}
	return text, lang, err
}

// fallBack runs the next server if the one asked died, or hung while another can take over, and says whether another
// runs now.
func (e *ears) fallBack(asked string, why error) bool {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.server != asked {
		return true
	}
	if e.dead == nil {
		return false
	}
	gone := fmt.Sprintf("%s died: %v", e.server, why)
	var timeout net.Error
	if errors.As(why, &timeout) && timeout.Timeout() && len(e.rest) > 0 {
		gone = fmt.Sprintf("%s did not answer within %v", e.server, e.patience)
		e.stop()
	}
	select {
	case <-e.dead:
	case <-time.After(time.Second): // it runs: the request failed, not the server
		return false
	}
	for len(e.rest) > 0 {
		server := e.rest[0]
		e.rest = e.rest[1:]
		if err := e.run(server); err != nil {
			gone += "; " + err.Error()
			continue
		}
		if e.fell != nil {
			e.fell(server, gone)
		}
		return true
	}
	return false
}

// ask posts the samples to the server running now, in the language Delta hears, or in either of its two: then Whisper
// detects the language, and when it hears a third, which short sentences can fool it into, the samples are heard
// again in the likelier of the two.
func (e *ears) ask(samples []float32) (text, lang string, err error) {
	var codes []string
	if e.languages != nil {
		codes = e.languages()
	}
	if len(codes) < 2 {
		if len(codes) == 1 {
			lang = codes[0]
		}
		out, err := e.post(samples, lang)
		return cleanHeard(out.Text), lang, err
	}
	out, err := e.post(samples, "auto")
	if err != nil {
		return "", "", err
	}
	lang = likeliest(codes, out.Probabilities)
	if top := likeliest(slices.Collect(maps.Keys(out.Probabilities)), out.Probabilities); top != "" && !slices.Contains(codes, top) {
		if out, err = e.post(samples, lang); err != nil {
			return "", "", err
		}
	}
	return cleanHeard(out.Text), lang, nil
}

// heardOut is what whisper-server answers: the text, and with verbose_json, how likely each language is.
type heardOut struct {
	Text          string             `json:"text"`
	Error         string             `json:"error"`
	Probabilities map[string]float64 `json:"language_probabilities"`
}

// post asks the server to transcribe the samples in a language, "auto" to detect it, or "" for the server's own.
func (e *ears) post(samples []float32, lang string) (heardOut, error) {
	var body bytes.Buffer
	form := multipart.NewWriter(&body)
	file, _ := form.CreateFormFile("file", "heard.wav")
	file.Write(encodeWav(samples, earsRate))
	format := "json"
	if lang == "auto" {
		format = "verbose_json"
	}
	form.WriteField("response_format", format)
	form.WriteField("temperature", "0.0")
	if lang != "" {
		form.WriteField("language", lang)
	}
	form.Close()
	var out heardOut
	client := http.Client{Timeout: e.patience}
	resp, err := client.Post(e.url+"/inference", form.FormDataContentType(), &body)
	if err != nil {
		return out, err
	}
	defer resp.Body.Close()
	if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
		return out, fmt.Errorf("ears: %s: %w", resp.Status, err)
	}
	if out.Error != "" {
		return out, fmt.Errorf("ears: %s", out.Error)
	}
	return out, nil
}

// likeliest is the code among codes that Whisper found likeliest, the first when it gave no probabilities.
func likeliest(codes []string, probabilities map[string]float64) string {
	best := ""
	for _, code := range codes {
		if best == "" || probabilities[code] > probabilities[best] {
			best = code
		}
	}
	return best
}

func (e *ears) close() {
	e.mu.Lock()
	defer e.mu.Unlock()
	e.rest = nil
	e.stop()
}

// stop kills the server running now and waits for it to exit.
func (e *ears) stop() {
	if e.cmd != nil {
		e.cmd.Process.Kill()
		<-e.dead
	}
}

// collapseRepeats keeps one copy of a sentence that Whisper wrote three times in a row or more, as it can over a
// button held on silence, and says how many copies it removed. A sentence ends with . ! ? or …, and two copies stay,
// since people repeat themselves.
func collapseRepeats(text string) (string, int) {
	var parts []string
	runes := []rune(text)
	start := 0
	for i := 0; i < len(runes); i++ {
		if strings.ContainsRune(".!?…", runes[i]) {
			j := i + 1
			for j < len(runes) && strings.ContainsRune(".!?…", runes[j]) {
				j++
			}
			parts = append(parts, strings.TrimSpace(string(runes[start:j])))
			start, i = j, j-1
		}
	}
	if rest := strings.TrimSpace(string(runes[start:])); rest != "" {
		parts = append(parts, rest)
	}
	var kept []string
	removed := 0
	for i := 0; i < len(parts); {
		key := strings.Join(sentenceWords(parts[i]), " ")
		j := i + 1
		for j < len(parts) && strings.Join(sentenceWords(parts[j]), " ") == key {
			j++
		}
		if j-i >= 3 && key != "" {
			kept = append(kept, parts[i])
			removed += j - i - 1
		} else {
			kept = append(kept, parts[i:j]...)
		}
		i = j
	}
	if removed == 0 {
		return text, 0
	}
	return strings.Join(kept, " "), removed
}

// cleanHeard folds the transcript's spaces and drops what Whisper writes over silence or noise: a sound in brackets
// ("[Musique]", "[Music]") and the subtitle credits and sign-offs its training data was full of.
func cleanHeard(text string) string {
	text = strings.Join(strings.Fields(text), " ")
	lower := strings.ToLower(text)
	for _, credit := range []string{"sous-titr", "amara.org", "subtitles by", "thanks for watching", "thank you for watching"} {
		if strings.Contains(lower, credit) {
			return ""
		}
	}
	for _, pair := range []string{"[]", "()", "**"} {
		if strings.HasPrefix(text, pair[:1]) && strings.HasSuffix(text, pair[1:]) {
			return ""
		}
	}
	if !hasWords(text) {
		return ""
	}
	return text
}

// partialEvery is how often, in 20 ms frames of speech, the caption's partial transcript is renewed: every second.
const partialEvery = 50

// listen turns the microphone into utterances: it tells the scene when someone starts speaking, transcribes what they
// said once they stop, and hears nothing while Delta is busy answering.
// hearing, if set, is told +1 as a sentence starts and -1 when it ends without reaching out, for a phone's copy of it
// to wait for this one (twice.go).
func listen(e *ears, s *scene, busy, muted *atomic.Bool, hearing func(delta int), out chan<- utterance, levels bool) {
	frames := make(chan []float32, 64)
	m, err := openMic(frames)
	if err != nil {
		fmt.Fprintln(os.Stderr, "brain:", err)
		return
	}
	defer m.close()
	fmt.Println("listening on the default microphone")
	var cut endpoint
	inSpeech := false
	heard := func(delta int) {
		if hearing != nil {
			hearing(delta)
		}
	}
	peak, count := 0.0, 0
	meter, ticks, shown := 0.0, 0, 0.0 // the window's microphone meter, the loudest frame of every 100 ms
	// While someone speaks, the caption shows what they have said so far, transcribed again every partialEvery: only
	// while transcribing is quick, as on a GPU, since the sentence's own transcript waits for the partial one.
	var said atomic.Int64 // changes as each sentence starts and ends, so that a late partial transcript is dropped
	var partial atomic.Bool
	next, quick := 0, true
	for frame := range frames {
		if levels { // every 2 s, the loudest frame and the room's noise as the endpoint measures it
			peak, count = max(peak, rms(frame)), count+1
			if count == 100 {
				fmt.Fprintf(os.Stderr, "mic: peak %.4f, noise %.4f\n", peak, cut.noise)
				peak, count = 0, 0
			}
		}
		deaf := busy.Load() || (muted != nil && muted.Load())
		if meter, ticks = max(meter, rms(frame)), ticks+1; ticks == 5 {
			if deaf {
				meter = 0
			}
			if meter > 0 || shown > 0 {
				s.send(map[string]any{"mic_level": math.Round(meter*1e4) / 1e4})
			}
			meter, ticks, shown = 0, 0, meter
		}
		if deaf { // while Delta speaks, and while the user turned it off
			cut.reset()
			if inSpeech {
				inSpeech = false
				said.Add(1)
				heard(-1)
			}
			continue
		}
		event, speech := cut.feed(frame)
		switch event {
		case speechStarted:
			inSpeech = true
			said.Add(1)
			next = partialEvery
			heard(1)
			s.send(map[string]any{"state": "listening"})
		case noSpeechEvent:
			if !inSpeech || !quick || len(cut.speech) < next*frameSamples || !partial.CompareAndSwap(false, true) {
				continue
			}
			next = len(cut.speech)/frameSamples + partialEvery
			samples, sentence := slices.Clone(cut.speech), said.Load()
			go func() {
				defer partial.Store(false)
				if text, _, err := e.ask(samples); err == nil && text != "" && said.Load() == sentence {
					s.send(map[string]any{"heard": map[string]any{"text": shorten(text, 400), "to": "hearing"}})
				}
			}()
		case speechEnded:
			inSpeech = false
			said.Add(1)
			if speech == nil {
				heard(-1)
				s.send(map[string]any{"state": "idle"})
				continue
			}
			busy.Store(true)
			ended := time.Now().Add(-endFrames * 20 * time.Millisecond) // the speaker stopped when the quiet began
			s.send(map[string]any{"state": "thinking"})
			t0 := time.Now()
			text, lang, err := e.hearIn(speech)
			if err != nil {
				fmt.Fprintln(os.Stderr, "brain:", err)
			}
			quick = time.Since(t0) < time.Second
			out <- utterance{text: text, ended: ended, asr: time.Since(t0), lang: lang, level: voicedLevel(speech)}
		}
	}
}

// encodeWav is 16-bit mono PCM.
func encodeWav(samples []float32, rate int) []byte {
	data := make([]byte, 44+2*len(samples))
	copy(data, "RIFF")
	binary.LittleEndian.PutUint32(data[4:], uint32(36+2*len(samples)))
	copy(data[8:], "WAVEfmt ")
	binary.LittleEndian.PutUint32(data[16:], 16)
	binary.LittleEndian.PutUint16(data[20:], 1)
	binary.LittleEndian.PutUint16(data[22:], 1)
	binary.LittleEndian.PutUint32(data[24:], uint32(rate))
	binary.LittleEndian.PutUint32(data[28:], uint32(rate*2))
	binary.LittleEndian.PutUint16(data[32:], 2)
	binary.LittleEndian.PutUint16(data[34:], 16)
	copy(data[36:], "data")
	binary.LittleEndian.PutUint32(data[40:], uint32(2*len(samples)))
	for i, s := range samples {
		binary.LittleEndian.PutUint16(data[44+2*i:], uint16(int16(max(-1, min(s, 1))*32767)))
	}
	return data
}

// resample changes the rate by linear interpolation, which is enough for speech going to Whisper.
func resample(samples []float32, from, to int) []float32 {
	if from == to || len(samples) == 0 {
		return samples
	}
	out := make([]float32, int(int64(len(samples))*int64(to)/int64(from)))
	for i := range out {
		x := float64(i) * float64(from) / float64(to)
		j := int(x)
		a, b := samples[j], samples[min(j+1, len(samples)-1)]
		out[i] = a + (b-a)*float32(x-float64(j))
	}
	return out
}
