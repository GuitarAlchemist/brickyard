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

// wav is a 16-bit mono PCM file's length and loudness envelope: one RMS value, 0 to 1, per 20 ms.
type wav struct {
	seconds  float64
	envelope []float32
}

func readWav(path string) (wav, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return wav{}, err
	}
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
			n := size / 2
			step := rate / 50
			w := wav{seconds: float64(n) / float64(rate)}
			for i := 0; i < n; i += step {
				sum := 0.0
				end := min(i+step, n)
				for j := i; j < end; j++ {
					s := float64(int16(binary.LittleEndian.Uint16(data[body+2*j:]))) / 32768
					sum += s * s
				}
				w.envelope = append(w.envelope, float32(math.Sqrt(sum/float64(end-i))))
			}
			return w, nil
		}
		at = body + size + size%2
	}
	return wav{}, fmt.Errorf("%s: no audio", path)
}
