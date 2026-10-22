package main

import (
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

// takeOver records this brain in path as the one that listens, and calls stop once another brain has recorded itself
// there. Claude Code can start a brain for a reconnection while the old one keeps running: both then hear the same
// microphone, send every sentence twice and share the CPU for Whisper. The newer brain wins.
func takeOver(path string, pid int, every time.Duration, stop func(other string)) error {
	me := strconv.Itoa(pid)
	if err := os.WriteFile(path, []byte(me), 0o644); err != nil {
		return err
	}
	go func() {
		tick := time.NewTicker(every)
		defer tick.Stop()
		for range tick.C {
			data, err := os.ReadFile(path)
			if err != nil {
				continue
			}
			if other := strings.TrimSpace(string(data)); other != "" && other != me {
				stop(other)
				return
			}
		}
	}()
	return nil
}

// brainLock is the file every brain on this machine records itself in: %LOCALAPPDATA%\delta\brain.pid on Windows,
// whatever -home the brain runs with, so that the installed plugin and a dev build exclude each other too.
func brainLock() (string, error) {
	dir, err := os.UserCacheDir()
	if err != nil {
		return "", err
	}
	dir = filepath.Join(dir, "delta")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", err
	}
	return filepath.Join(dir, "brain.pid"), nil
}
