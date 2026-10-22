package main

import (
	"encoding/binary"
	"fmt"
	"math"
	"os"
)

// Delta's voice can go through the "warm" effect: IX's voice, which the author chose by ear on 2026-10-17 from
// prototypes in GuitarAlchemist/claude-plugins (plugins/voice/service/fx.go at fb2b2f5), here on Delta's float samples.
// It is the same Piper voice, fr_FR-siwis-medium, made closer and warmer:
//   - body at 210 Hz (+3.75 dB) and a little less box at 450 Hz (-0.5 dB);
//   - presence at 3.2 kHz (+2 dB), and the top softened from 7.5 kHz (-2.75 dB);
//   - a gentle RMS compressor (-26 dBFS, 2.4:1);
//   - a touch of tanh saturation;
//   - a Freeverb room of size 0.175 with damping 0.575, 3 % wet after a 15 ms pre-delay;
//   - +6 dB of make-up gain, which keeps the level of the dry voice.
//
// One warm runs through all the sentences of one say call, so that they flow as one signal, and its tail rings after
// the last one.
type warm struct {
	eq        []*biquad
	env, envC float64
	pre       *delay
	combs     []*comb
	allpasses []*allpass
	tailLen   int
}

// effect makes a fresh effect for each say call, by name: "warm", or "none" and "" for none.
func effect(name string) (func(rate int) *warm, error) {
	switch name {
	case "", "none":
		return nil, nil
	case "warm":
		return newWarm, nil
	}
	return nil, fmt.Errorf("unknown voice effect %q; want warm or none", name)
}

func newWarm(rate int) *warm {
	k := float64(rate) / 44100
	w := &warm{
		eq: []*biquad{
			shelf(rate, 210, 3.75, false),
			peak(rate, 450, -0.5, 1.0),
			peak(rate, 3200, 2.0, 0.85),
			shelf(rate, 7500, -2.75, true),
		},
		envC:    math.Exp(-1 / (0.010 * float64(rate))),
		pre:     newDelay(int(0.015 * float64(rate))),
		tailLen: int(0.3 * float64(rate)),
	}
	const room, damp = 0.175, 0.575
	for _, n := range []float64{1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617} {
		w.combs = append(w.combs, &comb{line: newDelay(int(n * k)), fb: room*0.28 + 0.7, dmp: damp * 0.4})
	}
	for _, n := range []float64{556, 441, 341, 225} {
		w.allpasses = append(w.allpasses, &allpass{line: newDelay(int(n * k))})
	}
	return w
}

func (w *warm) sample(x float64) float64 {
	for _, f := range w.eq {
		x = f.step(x)
	}
	w.env = w.envC*w.env + (1-w.envC)*x*x
	if over := 10*math.Log10(math.Max(w.env, 1e-12)) + 26; over > 0 {
		x *= math.Pow(10, -over*(1-1/2.4)/20)
	}
	x = 0.8*x + 0.2*math.Tanh(1.9*x)/math.Tanh(1.9)
	in := w.pre.read() * 0.015
	w.pre.write(x)
	var room float64
	for _, c := range w.combs {
		room += c.step(in)
	}
	for _, a := range w.allpasses {
		room = a.step(room)
	}
	return 2.0 * (0.97*x + 0.03*3*room)
}

// process applies the effect to samples, in place, clipped to -1..1.
func (w *warm) process(samples []float32) {
	for i, s := range samples {
		samples[i] = float32(math.Max(-1, math.Min(1, w.sample(float64(s)))))
	}
}

// tail is what still rings once the input has ended.
func (w *warm) tail() []float32 {
	t := make([]float32, w.tailLen)
	w.process(t)
	return t
}

// biquad is one RBJ "Audio EQ Cookbook" filter, in transposed direct form II.
type biquad struct{ b0, b1, b2, a1, a2, z1, z2 float64 }

func (f *biquad) step(x float64) float64 {
	y := f.b0*x + f.z1
	f.z1 = f.b1*x - f.a1*y + f.z2
	f.z2 = f.b2*x - f.a2*y
	return y
}

func normalized(b0, b1, b2, a0, a1, a2 float64) *biquad {
	return &biquad{b0: b0 / a0, b1: b1 / a0, b2: b2 / a0, a1: a1 / a0, a2: a2 / a0}
}

// shelf boosts or cuts below f0 (high false) or above it (high true), with slope 1.
func shelf(rate int, f0, gainDB float64, high bool) *biquad {
	a := math.Pow(10, gainDB/40)
	w := 2 * math.Pi * f0 / float64(rate)
	cw, alpha := math.Cos(w), math.Sin(w)/2*math.Sqrt2
	s := -1.0
	if high {
		s = 1
	}
	ra := 2 * math.Sqrt(a) * alpha
	return normalized(
		a*((a+1)+s*(a-1)*cw+ra),
		-s*2*a*((a-1)+s*(a+1)*cw),
		a*((a+1)+s*(a-1)*cw-ra),
		(a+1)-s*(a-1)*cw+ra,
		s*2*((a-1)-s*(a+1)*cw),
		(a+1)-s*(a-1)*cw-ra)
}

// peak boosts or cuts a band around f0.
func peak(rate int, f0, gainDB, q float64) *biquad {
	a := math.Pow(10, gainDB/40)
	w := 2 * math.Pi * f0 / float64(rate)
	alpha := math.Sin(w) / (2 * q)
	return normalized(1+alpha*a, -2*math.Cos(w), 1-alpha*a, 1+alpha/a, -2*math.Cos(w), 1-alpha/a)
}

// delay is a ring buffer of n samples: read returns the sample written n steps ago.
type delay struct {
	buf []float64
	i   int
}

func newDelay(n int) *delay { return &delay{buf: make([]float64, max(n, 1))} }

func (d *delay) read() float64 { return d.buf[d.i] }

func (d *delay) write(x float64) {
	d.buf[d.i] = x
	d.i = (d.i + 1) % len(d.buf)
}

// comb is Freeverb's feedback comb with a low-pass in its loop.
type comb struct {
	line           *delay
	store, fb, dmp float64
}

func (c *comb) step(x float64) float64 {
	out := c.line.read()
	c.store = out*(1-c.dmp) + c.store*c.dmp
	c.line.write(x + c.store*c.fb)
	return out
}

// allpass is Freeverb's allpass diffuser, with gain 0.5.
type allpass struct{ line *delay }

func (a *allpass) step(x float64) float64 {
	out := a.line.read()
	a.line.write(x + out*0.5)
	return out - x
}

// writeWav writes samples as a 16-bit mono PCM file.
func writeWav(path string, rate int, samples []float32) error {
	data := make([]byte, 44+2*len(samples))
	copy(data, "RIFF")
	binary.LittleEndian.PutUint32(data[4:], uint32(36+2*len(samples)))
	copy(data[8:], "WAVEfmt ")
	binary.LittleEndian.PutUint32(data[16:], 16)
	binary.LittleEndian.PutUint16(data[20:], 1) // PCM
	binary.LittleEndian.PutUint16(data[22:], 1) // mono
	binary.LittleEndian.PutUint32(data[24:], uint32(rate))
	binary.LittleEndian.PutUint32(data[28:], uint32(rate*2))
	binary.LittleEndian.PutUint16(data[32:], 2)
	binary.LittleEndian.PutUint16(data[34:], 16)
	copy(data[36:], "data")
	binary.LittleEndian.PutUint32(data[40:], uint32(2*len(samples)))
	for i, s := range samples {
		v := math.Round(float64(s) * 32767)
		binary.LittleEndian.PutUint16(data[44+2*i:], uint16(int16(math.Max(-32768, math.Min(32767, v)))))
	}
	return os.WriteFile(path, data, 0o600)
}
