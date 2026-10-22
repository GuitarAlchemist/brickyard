//go:build !windows

package main

import (
	"errors"
	"os"
)

// play is silent off Windows for now.
func play(path string) {}

// Off Windows there is no microphone yet, and children are not tied to the brain.
type mic struct{}

func openMic(frames chan<- []float32) (*mic, error) {
	return nil, errors.New("mic: Windows only for now")
}

func (m *mic) close() {}

func killWithMe(p *os.Process) {}
