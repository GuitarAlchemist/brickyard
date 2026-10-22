package main

import (
	"bytes"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"mime/multipart"
	"net"
	"net/http"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"sync/atomic"
	"time"
)

// earsRate is the only rate whisper.cpp takes: 16 kHz mono.
const earsRate = 16000

// ears is whisper.cpp's server, run by the brain on a loopback port and kept warm, so that each utterance costs only
// its transcription. The server and its model are IX's (%LOCALAPPDATA%\voice-listen: whisper.cpp b5454 and
// ggml-large-v3-turbo-q5_0), used as they are.
type ears struct {
	url string
	cmd *exec.Cmd
}

// utterance is something heard: its text, when the speaker stopped (or the line was read), and how long the
// transcription took.
type utterance struct {
	text  string
	ended time.Time
	asr   time.Duration
}

func startEars(server, model string, port, threads int, prompt string, audioCtx int) (*ears, error) {
	addr := "127.0.0.1:" + strconv.Itoa(port)
	if c, err := net.DialTimeout("tcp", addr, 200*time.Millisecond); err == nil {
		c.Close()
		return nil, fmt.Errorf("ears: %s is already taken", addr)
	}
	args := []string{"-m", model, "-l", "fr", "-t", strconv.Itoa(threads), "--host", "127.0.0.1", "--port", strconv.Itoa(port), "--prompt", prompt}
	if audioCtx > 0 {
		args = append(args, "-ac", strconv.Itoa(audioCtx))
	}
	cmd := exec.Command(server, args...)
	if err := cmd.Start(); err != nil {
		return nil, err
	}
	killWithMe(cmd.Process)
	e := &ears{url: "http://" + addr, cmd: cmd}
	for deadline := time.Now().Add(60 * time.Second); time.Now().Before(deadline); time.Sleep(100 * time.Millisecond) {
		if c, err := net.DialTimeout("tcp", addr, 200*time.Millisecond); err == nil {
			c.Close()
			return e, nil
		}
	}
	e.close()
	return nil, fmt.Errorf("ears: whisper-server did not open %s within 60 s", addr)
}

// hear transcribes 16 kHz mono samples.
func (e *ears) hear(samples []float32) (string, error) {
	var body bytes.Buffer
	form := multipart.NewWriter(&body)
	file, _ := form.CreateFormFile("file", "heard.wav")
	file.Write(encodeWav(samples, earsRate))
	form.WriteField("response_format", "json")
	form.WriteField("temperature", "0.0")
	form.Close()
	resp, err := http.Post(e.url+"/inference", form.FormDataContentType(), &body)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	var out struct {
		Text  string `json:"text"`
		Error string `json:"error"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
		return "", fmt.Errorf("ears: %s: %w", resp.Status, err)
	}
	if out.Error != "" {
		return "", fmt.Errorf("ears: %s", out.Error)
	}
	return cleanHeard(out.Text), nil
}

func (e *ears) close() {
	e.cmd.Process.Kill()
	e.cmd.Wait()
}

// cleanHeard folds the transcript's spaces and drops what Whisper writes over silence or noise: a sound in brackets
// ("[Musique]") and the subtitle credits its French training data was full of.
func cleanHeard(text string) string {
	text = strings.Join(strings.Fields(text), " ")
	lower := strings.ToLower(text)
	if strings.Contains(lower, "sous-titr") || strings.Contains(lower, "amara.org") {
		return ""
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

// listen turns the microphone into utterances: it tells the scene when someone starts speaking, transcribes what they
// said once they stop, and hears nothing while Delta is busy answering.
func listen(e *ears, s *scene, busy *atomic.Bool, out chan<- utterance) {
	frames := make(chan []float32, 64)
	m, err := openMic(frames)
	if err != nil {
		fmt.Fprintln(os.Stderr, "brain:", err)
		return
	}
	defer m.close()
	fmt.Println("listening on the default microphone")
	var cut endpoint
	for frame := range frames {
		if busy.Load() {
			cut.reset()
			continue
		}
		event, speech := cut.feed(frame)
		switch event {
		case speechStarted:
			s.send(map[string]any{"state": "listening"})
		case speechEnded:
			if speech == nil {
				s.send(map[string]any{"state": "idle"})
				continue
			}
			busy.Store(true)
			ended := time.Now().Add(-endFrames * 20 * time.Millisecond) // the speaker stopped when the quiet began
			s.send(map[string]any{"state": "thinking"})
			t0 := time.Now()
			text, err := e.hear(speech)
			if err != nil {
				fmt.Fprintln(os.Stderr, "brain:", err)
			}
			out <- utterance{text: text, ended: ended, asr: time.Since(t0)}
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
