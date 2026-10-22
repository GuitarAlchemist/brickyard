package main

import (
	"encoding/binary"
	"io"
	"math"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

func TestEndpointCutsAnUtterance(t *testing.T) {
	var cut endpoint
	frame := func(level float32) []float32 {
		f := make([]float32, frameSamples)
		for i := range f {
			f[i] = level * float32(1-2*(i%2))
		}
		return f
	}
	var events []speechEvent
	var got []float32
	feed := func(n int, level float32) {
		for range n {
			event, speech := cut.feed(frame(level))
			if event != noSpeechEvent {
				events = append(events, event)
			}
			if speech != nil {
				got = speech
			}
		}
	}
	feed(40, 0.002) // the room, measured, then quiet
	feed(25, 0.2)   // half a second of speech
	feed(40, 0.002) // quiet: the cut comes 700 ms in
	if len(events) != 2 || events[0] != speechStarted || events[1] != speechEnded {
		t.Fatalf("events %v", events)
	}
	// The 300 ms before the start (which comes on the third loud frame), the rest of the speech, 700 ms of quiet.
	if frames := len(got) / frameSamples; frames != 15+(25-3)+35 {
		t.Fatalf("%d frames", frames)
	}

	events, got = nil, nil
	feed(5, 0.2) // a knock
	feed(40, 0.002)
	if len(events) != 2 || events[1] != speechEnded || got != nil {
		t.Fatalf("a knock: %v, %d samples", events, len(got))
	}
}

func TestEarsHear(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		file, _, err := r.FormFile("file")
		if err != nil || r.URL.Path != "/inference" || r.FormValue("response_format") != "json" {
			t.Errorf("%s %v %q", r.URL.Path, err, r.FormValue("response_format"))
			return
		}
		data, _ := io.ReadAll(file)
		if string(data[:4]) != "RIFF" || binary.LittleEndian.Uint32(data[24:]) != earsRate {
			t.Errorf("not a 16 kHz WAV")
		}
		io.WriteString(w, `{"text":" Bonjour   Delta. \n"}`)
	}))
	defer server.Close()
	text, err := (&ears{url: server.URL}).hear(make([]float32, 1600))
	if err != nil || text != "Bonjour Delta." {
		t.Fatalf("%q %v", text, err)
	}
}

func TestCleanHeardDropsWhatWhisperInvents(t *testing.T) {
	for in, want := range map[string]string{
		" Sous-titres réalisés par la communauté d'Amara.org": "",
		"[Musique]":                        "",
		"(rires)":                          "",
		" ... ":                            "",
		" Bonjour  Delta, tu m'entends ? ": "Bonjour Delta, tu m'entends ?",
	} {
		if got := cleanHeard(in); got != want {
			t.Errorf("%q: got %q", in, got)
		}
	}
}

func TestResampleAndWavRoundTrip(t *testing.T) {
	in := make([]float32, 22050)
	for i := range in {
		in[i] = float32(i) / 22050
	}
	out := resample(in, 22050, earsRate)
	if len(out) != earsRate || math.Abs(float64(out[8000])-0.5) > 1e-3 {
		t.Fatalf("%d samples, middle %v", len(out), out[8000])
	}
	path := filepath.Join(t.TempDir(), "r.wav")
	if err := os.WriteFile(path, encodeWav(out, earsRate), 0o644); err != nil {
		t.Fatal(err)
	}
	w, err := readWav(path)
	if err != nil || w.rate != earsRate || len(w.samples) != len(out) || math.Abs(float64(w.samples[8000]-out[8000])) > 1e-4 {
		t.Fatalf("%v: rate %d, %d samples", err, w.rate, len(w.samples))
	}
}
