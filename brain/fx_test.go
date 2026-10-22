package main

import (
	"fmt"
	"math"
	"path/filepath"
	"testing"
)

func TestTheWarmVoiceKeepsTheSentenceAndAddsATail(t *testing.T) {
	const rate = 22050
	in := make([]float32, rate/2)
	for i := range in {
		in[i] = float32(0.3 * math.Sin(2*math.Pi*220*float64(i)/rate))
	}
	out := append([]float32(nil), in...)
	w := newWarm(rate)
	w.process(out)
	var dry, wet float64
	for i := range in {
		dry += float64(in[i]) * float64(in[i])
		wet += float64(out[i]) * float64(out[i])
		if out[i] < -1 || out[i] > 1 {
			t.Fatalf("sample %d is %f, outside -1..1", i, out[i])
		}
	}
	if ratio := math.Sqrt(wet / dry); ratio < 0.5 || ratio > 2.5 {
		t.Fatalf("the warm voice is %.2f times as loud as the dry one", ratio)
	}
	tail := w.tail()
	if len(tail) != int(0.3*rate) {
		t.Fatalf("a tail of %d samples", len(tail))
	}
	var ring float64
	for _, s := range tail {
		ring = math.Max(ring, math.Abs(float64(s)))
	}
	if ring == 0 {
		t.Fatal("the room does not ring after the sentence")
	}
}

func TestVoiceEffectsByName(t *testing.T) {
	for _, name := range []string{"", "none"} {
		if fx, err := effect(name); fx != nil || err != nil {
			t.Fatalf("%q: %v", name, err)
		}
	}
	if fx, err := effect("warm"); fx == nil || err != nil {
		t.Fatalf("warm: %v", err)
	}
	if _, err := effect("hot"); err == nil {
		t.Fatal("an unknown effect is refused")
	}
}

func TestAWrittenWavReadsBack(t *testing.T) {
	path := filepath.Join(t.TempDir(), "a.wav")
	samples := []float32{0, 0.5, -0.5, 1, -1}
	if err := writeWav(path, 22050, samples); err != nil {
		t.Fatal(err)
	}
	w, err := readWav(path)
	if err != nil {
		t.Fatal(err)
	}
	if w.rate != 22050 || len(w.samples) != len(samples) {
		t.Fatalf("%d Hz, %d samples", w.rate, len(w.samples))
	}
	for i, s := range samples {
		if math.Abs(float64(w.samples[i]-s)) > 1.0/16384 {
			t.Fatalf("sample %d: %f, not %f", i, w.samples[i], s)
		}
	}
}

func TestTheWarmVoiceRunsThroughEverySentenceOfASay(t *testing.T) {
	const rate = 22050
	dir := t.TempDir()
	var paths []string
	for i := range 2 {
		in := make([]float32, rate/4)
		for j := range in {
			in[j] = float32(0.3 * math.Sin(2*math.Pi*220*float64(j)/rate))
		}
		path := filepath.Join(dir, fmt.Sprint(i, ".wav"))
		if err := writeWav(path, rate, in); err != nil {
			t.Fatal(err)
		}
		paths = append(paths, path)
	}
	fx := newWarm(rate)
	for i, path := range paths {
		w, err := readWav(path)
		if err != nil {
			t.Fatal(err)
		}
		last := i == len(paths)-1
		w, err = warmed(path, w, fx, last)
		if err != nil {
			t.Fatal(err)
		}
		again, err := readWav(path)
		if err != nil {
			t.Fatal(err)
		}
		want := rate / 4
		if last {
			want += int(0.3 * rate)
		}
		if len(w.samples) != want || len(again.samples) != want || w.seconds != again.seconds || len(w.envelope) != len(again.envelope) {
			t.Fatalf("sentence %d: %d samples, %d on disk, want %d", i, len(w.samples), len(again.samples), want)
		}
	}
}
