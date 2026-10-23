package main

import (
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
)

// Delta can be reached from another device through a gateway on this computer, such as IX's remote/ over WebRTC. The
// gateway posts what the device heard to POST /hear and plays what Delta says from GET /said. Both sit on the hooks'
// address behind localOnly and the hooks' header, and also need X-Delta-Remote: the secret this brain wrote at start
// into remote.secret, in the user's own local application data, which other accounts cannot read.
//
//	POST /hear  a 16 kHz mono 16-bit WAV of at most 30 s; 202 {"utterance_id","text","route"}, 503 until Delta hears
//	GET  /said  server-sent events, one per sentence as it starts playing: {"say_id","text","seconds","t_ms","wav_b64"}
//	POST /link  {"url"}, the https link of at most 106 bytes that a phone opens to pair with the gateway; 204
//
// A sentence from /hear is routed as one addressed to Delta, since holding the device's button addressed it. Its heard
// line and channel event carry source "remote". A chime tells the room when Delta says nothing back at once, nor is
// speaking: a sentence for Claude gets Delta's acknowledgement instead, and small talk its answer. What answers it plays
// on the device only, while a device listening to /said sent X-Delta-Plays: own, saying that it plays the answers to
// its own sentences; without it, the computer plays every answer, as in 0.5.0. Delta's window shows
// the link from /link as a QR code behind its phone icon, while the pairing code in it lives.

const remoteHeader = "X-Delta-Remote"

// playsHeader is how a device listening to /said says, with "own", that it plays the answers to its own sentences.
const playsHeader = "X-Delta-Plays"

// pairingSeconds is how long a pairing code from IX's gateway lives.
const pairingSeconds = 120

// maxHeard is 30 s of 16-bit samples at whisper's rate, and a WAV header.
const maxHeard = 30*earsRate*2 + 1024

type remote struct {
	secret string
	route  func(u utterance) (id int, route string) // routes a transcript as hear does
	chime  func()                                   // tells the room a sentence came from another device
	pair   func(rows []string)                      // shows a pairing link's QR code, one string of 0 and 1 per row

	mu         sync.Mutex
	transcribe func(samples []float32) (text, lang string, err error) // nil until Delta's ears are ready
	subs       map[chan []byte]bool                                   // the devices listening to /said: whether each plays its answers
	answers    map[int]replyTo                                        // whom each sentence about to be said answers, by say_id
	heard      map[int]bool                                           // the utterances that came through /hear, the last 64
}

// replyTo is whom a sentence answers, as its say note gives it: the utterance_id, "" for none, and by, who wrote it
// (claude, local or ack).
type replyTo struct{ utterance, by string }

// attribute keeps whom a say call answers, from its say note (mcp.go), for publish to send with each of its sentences.
func (r *remote) attribute(say map[string]any) {
	id, _ := say["say_id"].(int)
	utterance, _ := say["utterance_id"].(string)
	by, _ := say["by"].(string)
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.answers == nil {
		r.answers = map[int]replyTo{}
	}
	r.answers[id] = replyTo{utterance, by}
	for old := range r.answers {
		if old <= id-64 { // said long ago, or never, such as an acknowledgement the mouth dropped
			delete(r.answers, old)
		}
	}
}

// hearWith gives /hear Delta's ears, once whisper-server is ready.
func (r *remote) hearWith(transcribe func(samples []float32) (text, lang string, err error)) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.transcribe = transcribe
}

func (r *remote) handle(mux *http.ServeMux) {
	mux.HandleFunc("/hear", r.hear)
	mux.HandleFunc("/said", r.said)
	mux.HandleFunc("/link", r.link)
}

// allowed answers 403 unless the request has the method, the hooks' header and the secret.
func (r *remote) allowed(w http.ResponseWriter, req *http.Request, method string) bool {
	if req.Method != method || req.Header.Get(hookHeader) == "" || r.secret == "" ||
		subtle.ConstantTimeCompare([]byte(req.Header.Get(remoteHeader)), []byte(r.secret)) != 1 {
		http.Error(w, "a remote gateway sends the "+hookHeader+" header and the secret of remote.secret in "+remoteHeader,
			http.StatusForbidden)
		return false
	}
	return true
}

func (r *remote) hear(w http.ResponseWriter, req *http.Request) {
	if !r.allowed(w, req, http.MethodPost) {
		return
	}
	data, err := io.ReadAll(io.LimitReader(req.Body, maxHeard+1))
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if len(data) > maxHeard {
		http.Error(w, "at most 30 s of speech", http.StatusRequestEntityTooLarge)
		return
	}
	sound, err := parseWav(data, "the posted WAV")
	if err != nil || sound.rate != earsRate {
		http.Error(w, "a 16 kHz mono 16-bit WAV, please", http.StatusBadRequest)
		return
	}
	r.mu.Lock()
	transcribe := r.transcribe
	r.mu.Unlock()
	if transcribe == nil {
		http.Error(w, "Delta cannot hear yet: its ears are loading, or missing", http.StatusServiceUnavailable)
		return
	}
	start := time.Now()
	text, lang := "", ""
	if voiced(sound.samples) >= minVoiced { // Whisper invents words over silence, such as a page whose sound was off sends
		if text, lang, err = transcribe(sound.samples); err != nil {
			http.Error(w, "transcription: "+err.Error(), http.StatusBadGateway)
			return
		}
	}
	u := utterance{text: strings.TrimSpace(text), ended: start, asr: time.Since(start), source: "remote", lang: lang}
	id, where := 0, "nothing"
	if u.text != "" {
		id, where = r.route(u)
		if r.chime != nil && where != "channel" && where != "local" && where != "duplicate" {
			r.chime()
		}
	}
	shown, _ := collapseRepeats(u.text) // as the session gets it
	answer := map[string]any{"utterance_id": "", "text": shown, "route": where}
	if id > 0 {
		answer["utterance_id"] = strconv.Itoa(id)
		r.mu.Lock()
		if r.heard == nil {
			r.heard = map[int]bool{}
		}
		r.heard[id] = true
		for old := range r.heard {
			if old <= id-64 {
				delete(r.heard, old)
			}
		}
		r.mu.Unlock()
	}
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusAccepted)
	json.NewEncoder(w).Encode(answer)
}

func (r *remote) said(w http.ResponseWriter, req *http.Request) {
	if !r.allowed(w, req, http.MethodGet) {
		return
	}
	flusher, ok := w.(http.Flusher)
	if !ok {
		http.Error(w, "no streaming here", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.WriteHeader(http.StatusOK)
	flusher.Flush()
	ch := make(chan []byte, 16)
	r.mu.Lock()
	if r.subs == nil {
		r.subs = map[chan []byte]bool{}
	}
	r.subs[ch] = req.Header.Get(playsHeader) == "own"
	r.mu.Unlock()
	defer func() {
		r.mu.Lock()
		delete(r.subs, ch)
		r.mu.Unlock()
	}()
	alive := time.NewTicker(15 * time.Second)
	defer alive.Stop()
	for {
		select {
		case <-req.Context().Done():
			return
		case event := <-ch:
			if _, err := fmt.Fprintf(w, "data: %s\n\n", event); err != nil {
				return
			}
		case <-alive.C:
			if _, err := io.WriteString(w, ": alive\n\n"); err != nil {
				return
			}
		}
		flusher.Flush()
	}
}

// link takes the link a phone opens to pair with the gateway, which IX's posts at each of its starts. The link holds
// the pairing code, a credential while it lives: it goes to Delta's window as a QR code, and nowhere else.
func (r *remote) link(w http.ResponseWriter, req *http.Request) {
	if !r.allowed(w, req, http.MethodPost) {
		return
	}
	var body struct {
		URL string `json:"url"`
	}
	if err := json.NewDecoder(io.LimitReader(req.Body, 4096)).Decode(&body); err != nil {
		http.Error(w, `{"url"}, please`, http.StatusBadRequest)
		return
	}
	if u, err := url.Parse(body.URL); err != nil || u.Scheme != "https" || u.Host == "" {
		http.Error(w, "an https link, please", http.StatusBadRequest)
		return
	}
	rows, err := qrEncode(body.URL)
	if err != nil {
		http.Error(w, fmt.Sprintf("a link of at most %d bytes, please", qrMaxBytes), http.StatusBadRequest)
		return
	}
	if r.pair != nil {
		r.pair(rows)
	}
	w.WriteHeader(http.StatusNoContent)
}

// aloud says whether the computer plays a say call: not when it answers a sentence from another device while a device
// listening to /said plays the answers to its own sentences, since the two would echo a moment apart. A device that
// does not say so, such as a gateway whose page plays only after a tap, leaves every answer to the computer.
func (r *remote) aloud(sayID int) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	utterance, _ := strconv.Atoi(r.answers[sayID].utterance)
	if !r.heard[utterance] {
		return true
	}
	for _, own := range r.subs {
		if own {
			return false
		}
	}
	return true
}

// listened says whether a device listens to /said.
func (r *remote) listened() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.subs) > 0
}

// publish sends one sentence to every device listening, as it starts playing here, with the utterance it answers; a
// device too slow to take it misses it rather than holding Delta's voice back.
func (r *remote) publish(sayID int, text string, sound wav, path string) {
	r.mu.Lock()
	a := r.answers[sayID] // kept for the call's next sentences, until attribute forgets it
	r.mu.Unlock()
	if !r.listened() {
		return
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return
	}
	event, _ := json.Marshal(map[string]any{"say_id": sayID, "utterance_id": a.utterance, "by": a.by, "text": text,
		"seconds": sound.seconds, "t_ms": time.Now().UnixMilli(), "wav_b64": base64.StdEncoding.EncodeToString(data)})
	r.mu.Lock()
	defer r.mu.Unlock()
	for ch := range r.subs {
		select {
		case ch <- event:
		default:
		}
	}
}

// newRemoteSecret writes a new secret for /hear and /said into the user's local application data, beside brain.pid,
// and returns it with the file's path. Each brain writes its own at start; the gateway reads the file at each request.
func newRemoteSecret() (secret, path string, err error) {
	dir, err := os.UserCacheDir()
	if err != nil {
		return "", "", err
	}
	dir = filepath.Join(dir, "delta")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return "", "", err
	}
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", "", err
	}
	secret, path = hex.EncodeToString(b), filepath.Join(dir, "remote.secret")
	return secret, path, os.WriteFile(path, []byte(secret), 0o600)
}

// chimeFile writes the two short rising notes that tell the room a sentence came from another device.
func chimeFile(dir string) (string, error) {
	const rate = 22050
	var samples []float32
	for _, f := range []float64{880, 1320} {
		n := rate * 9 / 100
		for i := range n {
			t := float64(i) / rate
			attack := math.Min(1, float64(i)/(0.005*rate))
			samples = append(samples, float32(0.25*attack*math.Exp(-25*t)*math.Sin(2*math.Pi*f*t)))
		}
	}
	path := filepath.Join(dir, "delta-chime.wav")
	return path, writeWav(path, rate, samples)
}
