package main

import "math"

// endpoint cuts a microphone's 20 ms frames into utterances. The first half second measures the room's noise. Speech
// starts after three frames well above it and ends after 700 ms of quiet; the 300 ms before the start are kept, so that
// the first sound is not clipped. An utterance is cut at 15 s, and one with less than 240 ms of speech is dropped as a
// knock or a cough.
type endpoint struct {
	frames  int
	noise   float64
	before  [][]float32
	speech  []float32
	loud    int
	quiet   int
	voiced  int
	talking bool
}

const (
	frameSamples = earsRate / 50
	calibrate    = 25
	startFrames  = 3
	endFrames    = 35
	keepFrames   = 15
	maxFrames    = 15 * 50
	minVoiced    = 12
)

type speechEvent int

const (
	noSpeechEvent speechEvent = iota
	speechStarted
	speechEnded // with the utterance, or nil when it was too short
)

func (e *endpoint) feed(frame []float32) (speechEvent, []float32) {
	level := rms(frame)
	if e.frames < calibrate {
		e.frames++
		e.noise += (level - e.noise) / float64(e.frames)
		return noSpeechEvent, nil
	}
	loud := level > math.Max(3*e.noise, 0.01)
	if !e.talking {
		if loud {
			e.loud++
		} else {
			e.loud = 0
		}
		// The noise follows a quieter room at once and a louder one slowly, so that speech does not raise it.
		rate := 0.01
		if level < e.noise {
			rate = 0.2
		}
		e.noise += (level - e.noise) * rate
		e.before = append(e.before, frame)
		if len(e.before) > keepFrames {
			e.before = e.before[1:]
		}
		if e.loud < startFrames {
			return noSpeechEvent, nil
		}
		e.talking, e.voiced, e.quiet = true, e.loud, 0
		for _, f := range e.before {
			e.speech = append(e.speech, f...)
		}
		e.before = nil
		return speechStarted, nil
	}
	e.speech = append(e.speech, frame...)
	if loud {
		e.quiet = 0
		e.voiced++
	} else {
		e.quiet++
	}
	if e.quiet < endFrames && len(e.speech) < maxFrames*frameSamples {
		return noSpeechEvent, nil
	}
	speech, voiced := e.speech, e.voiced
	e.reset()
	if voiced < minVoiced {
		return speechEnded, nil
	}
	return speechEnded, speech
}

// reset forgets the utterance in progress and keeps the room's noise.
func (e *endpoint) reset() {
	e.before, e.speech = nil, nil
	e.loud, e.quiet, e.voiced = 0, 0, 0
	e.talking = false
}

func rms(frame []float32) float64 {
	if len(frame) == 0 {
		return 0
	}
	sum := 0.0
	for _, s := range frame {
		sum += float64(s) * float64(s)
	}
	return math.Sqrt(sum / float64(len(frame)))
}
