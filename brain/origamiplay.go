package main

// brain -origami-play steers the origami table when it shows a fold step by step (ixorigamisteps.go): next, prev, auto,
// or a step's number. It writes one small document, %LOCALAPPDATA%\delta\ix-origami-play.json, which the office's
// module watches (IxOrigami.cs, Player): each new writing is one command, from the step the table shows. The module
// looks at most once a second, so two commands within a second count as the last.
//
//	{"format": "delta-origami-play", "v": 1, "go": "next" | "prev" | "auto" | "step", "step": k}

import (
	"encoding/json"
	"fmt"
	"io"
	"path/filepath"
	"strconv"
)

const origamiPlayFormat = "delta-origami-play"

type origamiPlay struct {
	Format string `json:"format"`
	V      int    `json:"v"`
	Go     string `json:"go"`
	Step   int    `json:"step"` // with go "step"; 0 otherwise
}

func defaultOrigamiPlayPath() string {
	return filepath.Join(filepath.Dir(defaultAtelierPath()), "ix-origami-play.json")
}

// origamiPlayOf reads a command as the flag gives it.
func origamiPlayOf(command string) (origamiPlay, error) {
	play := origamiPlay{Format: origamiPlayFormat, V: 1, Go: command}
	switch command {
	case "next", "prev", "auto":
		return play, nil
	}
	k, err := strconv.Atoi(command)
	if err != nil || k < 0 || k >= origamiMaxSteps {
		return origamiPlay{}, fmt.Errorf("%q is not next, prev, auto or a step from 0 to %d", command, origamiMaxSteps-1)
	}
	play.Go, play.Step = "step", k
	return play, nil
}

// runOrigamiPlay is brain -origami-play.
func runOrigamiPlay(command, out string, stdout, stderr io.Writer) int {
	play, err := origamiPlayOf(command)
	if err != nil {
		fmt.Fprintf(stderr, "origami-play: %v\n", err)
		return 2
	}
	if out == "" {
		out = defaultOrigamiPlayPath()
	}
	body, _ := json.Marshal(play)
	if err := writeDocument(out, body); err != nil {
		fmt.Fprintf(stderr, "origami-play: %v\n", err)
		return 1
	}
	fmt.Fprintf(stdout, "origami-play: %s, written to %s\n", command, out)
	return 0
}
