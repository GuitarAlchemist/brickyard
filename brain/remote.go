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
//
// A sentence from /hear is routed as one addressed to Delta, since holding the device's button addressed it. Its heard
// line and channel event carry source "remote". A chime tells the room when Delta says nothing back at once, nor is
// speaking: a sentence for Claude gets Delta's acknowledgement instead, and small talk its answer.

const remoteHeader = "X-Delta-Remote"

// maxHeard is 30 s of 16-bit samples at whisper's rate, and a WAV header.
const maxHeard = 30*earsRate*2 + 1024

type remote struct {
	secret string
	route  func(u utterance) (id int, route string) // routes a transcript as hear does
	chime  func()                                   // tells the room a sentence came from another device

	mu         sync.Mutex
	transcribe func(samples []float32) (string, error) // nil until Delta's ears are ready
	subs       map[chan []byte]struct{}                // the devices listening to /said
}

// hearWith gives /hear Delta's ears, once whisper-server is ready.
func (r *remote) hearWith(transcribe func(samples []float32) (string, error)) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.transcribe = transcribe
}

func (r *remote) handle(mux *http.ServeMux) {
	mux.HandleFunc("/hear", r.hear)
	mux.HandleFunc("/said", r.said)
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
	text, err := transcribe(sound.samples)
	if err != nil {
		http.Error(w, "transcription: "+err.Error(), http.StatusBadGateway)
		return
	}
	u := utterance{text: strings.TrimSpace(text), ended: start, asr: time.Since(start), source: "remote"}
	id, where := 0, "nothing"
	if u.text != "" {
		id, where = r.route(u)
		if r.chime != nil && where != "channel" && where != "local" {
			r.chime()
		}
	}
	answer := map[string]any{"utterance_id": "", "text": u.text, "route": where}
	if id > 0 {
		answer["utterance_id"] = strconv.Itoa(id)
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
		r.subs = map[chan []byte]struct{}{}
	}
	r.subs[ch] = struct{}{}
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

// listened says whether a device listens to /said.
func (r *remote) listened() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.subs) > 0
}

// publish sends one sentence to every device listening, as it starts playing here; a device too slow to take it
// misses it rather than holding Delta's voice back.
func (r *remote) publish(sayID int, text string, sound wav, path string) {
	if !r.listened() {
		return
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return
	}
	event, _ := json.Marshal(map[string]any{"say_id": sayID, "text": text, "seconds": sound.seconds,
		"t_ms": time.Now().UnixMilli(), "wav_b64": base64.StdEncoding.EncodeToString(data)})
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
