package main

import (
	"bufio"
	"encoding/binary"
	"fmt"
	"io"
	"math"
	"os"
	"os/exec"
	"strings"
	"sync"
)

// voice keeps one Piper process, so the model loads once: each line written to it becomes a WAV file in dir, and
// Piper prints that file's path.
type voice struct {
	mu    sync.Mutex
	cmd   *exec.Cmd
	in    io.WriteCloser
	paths *bufio.Scanner
}

func startVoice(piper, model, dir string, args ...string) (*voice, error) {
	cmd := exec.Command(piper, append([]string{"--model", model, "--output_dir", dir}, args...)...)
	hideConsole(cmd)
	in, err := cmd.StdinPipe()
	if err != nil {
		return nil, err
	}
	out, err := cmd.StdoutPipe()
	if err != nil {
		return nil, err
	}
	cmd.Stderr = io.Discard // its log; discarded so that a full pipe cannot stall it
	if err := cmd.Start(); err != nil {
		return nil, err
	}
	return &voice{cmd: cmd, in: in, paths: bufio.NewScanner(out)}, nil
}

// say synthesises one line and returns the WAV's path.
func (v *voice) say(text string) (string, error) {
	v.mu.Lock()
	defer v.mu.Unlock()
	line := strings.Join(strings.Fields(text), " ")
	if _, err := io.WriteString(v.in, line+"\n"); err != nil {
		return "", err
	}
	if !v.paths.Scan() {
		return "", fmt.Errorf("piper stopped: %v", v.paths.Err())
	}
	return strings.TrimSpace(v.paths.Text()), nil
}

func (v *voice) close() {
	v.in.Close()
	v.cmd.Wait()
}

// wav is a 16-bit mono PCM file: its samples (-1 to 1), rate and length, and its loudness envelope, one RMS value,
// 0 to 1, per 20 ms.
type wav struct {
	rate     int
	samples  []float32
	seconds  float64
	envelope []float32
}

func readWav(path string) (wav, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return wav{}, err
	}
	return parseWav(data, path)
}

// parseWav reads a 16-bit mono PCM file's bytes; its errors name the file by path.
func parseWav(data []byte, path string) (wav, error) {
	if len(data) < 12 || string(data[:4]) != "RIFF" || string(data[8:12]) != "WAVE" {
		return wav{}, fmt.Errorf("%s: not a WAV file", path)
	}
	rate := 0
	for at := 12; at+8 <= len(data); {
		id, size := string(data[at:at+4]), int(binary.LittleEndian.Uint32(data[at+4:]))
		body := at + 8
		if size < 0 || body+size > len(data) {
			break
		}
		switch id {
		case "fmt ":
			if size >= 16 && binary.LittleEndian.Uint16(data[body:]) == 1 && binary.LittleEndian.Uint16(data[body+2:]) == 1 &&
				binary.LittleEndian.Uint16(data[body+14:]) == 16 {
				rate = int(binary.LittleEndian.Uint32(data[body+4:]))
			}
		case "data":
			if rate == 0 {
				return wav{}, fmt.Errorf("%s: not 16-bit mono PCM", path)
			}
			w := wav{rate: rate, samples: make([]float32, size/2)}
			for i := range w.samples {
				w.samples[i] = float32(int16(binary.LittleEndian.Uint16(data[body+2*i:]))) / 32768
			}
			w.seconds = float64(len(w.samples)) / float64(rate)
			w.envelope = envelope(w.samples, rate/50)
			return w, nil
		}
		at = body + size + size%2
	}
	return wav{}, fmt.Errorf("%s: no audio", path)
}

// envelope is the RMS of each step of samples.
func envelope(samples []float32, step int) []float32 {
	var out []float32
	for i := 0; i < len(samples); i += step {
		end := min(i+step, len(samples))
		sum := 0.0
		for _, s := range samples[i:end] {
			sum += float64(s) * float64(s)
		}
		out = append(out, float32(math.Sqrt(sum/float64(end-i))))
	}
	return out
}
