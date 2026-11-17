package main

import (
	"fmt"
	"time"
)

// Schema names the snapshot's contract. web/src/contract.js checks the same fields; a change to either is a change to
// both, and to the schema's number.
const Schema = "delta-telemetry@1"

// Status says how far a value can be trusted. Live: read now from its source. Missing: its source is not there.
// Unavailable: no source this tool may read carries it. Stale and disconnected are the page's to decide, from the
// snapshot's age and from whether it could load one at all.
type Status string

const (
	Live        Status = "live"
	Missing     Status = "missing"
	Unavailable Status = "unavailable"
)

// Snapshot is everything the panel shows. Every value carries the source it was read from; nothing is estimated.
type Snapshot struct {
	Schema    string    `json:"schema"`
	Generated string    `json:"generated_at"` // RFC 3339, the producer's clock
	EveryMS   int64     `json:"every_ms"`     // the producer's period, 0 when it ran once
	Sources   []Source  `json:"sources"`
	Brain     Brain     `json:"brain"`
	Speech    Speech    `json:"speech"`
	Scene     Scene     `json:"scene"`
	Frames    Withheld  `json:"frame_rate"`
	GPUMemory Withheld  `json:"gpu_memory"`
	Producer  Footprint `json:"producer"`
}

// Source is one file the snapshot was read from: its base name only, and when it was last written.
type Source struct {
	Name      string `json:"name"`
	File      string `json:"file"`
	Status    Status `json:"status"`
	UpdatedAt string `json:"updated_at,omitempty"`
}

// Brain is Delta's brain as its journal and its pid file tell it. State: running (its pid file names a process that
// is there, and the journal's last start for that pid has no stop after it), stopped, or unknown.
type Brain struct {
	Status  Status `json:"status"`
	State   string `json:"state"`
	PID     int    `json:"pid,omitempty"`
	Version string `json:"version,omitempty"`
	Since   string `json:"since,omitempty"`
	Mic     *Mic   `json:"mic,omitempty"`
	Source  string `json:"source"`
}

// Mic is the microphone as the journal last recorded it.
type Mic struct {
	On bool   `json:"on"`
	At string `json:"at"`
}

// Speech is how long recognition took for the last utterances heard, newest last, at most Limit of them. What was
// said is never read into it.
type Speech struct {
	Status Status    `json:"status"`
	Recent []Latency `json:"recent"`
	Limit  int       `json:"limit"`
	Source string    `json:"source"`
}

// Latency is one utterance's recognition time.
type Latency struct {
	At    string `json:"at"`
	MS    int64  `json:"ms"`
	Route string `json:"route,omitempty"`
}

// Scene is the native scene's last memory reading from its log. Its lines carry seconds since the scene started, not a
// wall clock, so the reading is dated by those seconds and by when the log was last written, which is no earlier.
type Scene struct {
	Status       Status `json:"status"`
	WorkingSetMB int    `json:"working_set_mb"` // these three are meaningful when Status is live
	PrivateMB    int    `json:"private_mb"`
	SceneSeconds int    `json:"scene_seconds"`
	LogUpdatedAt string `json:"log_updated_at,omitempty"`
	Source       string `json:"source"`
}

// Withheld is a value the panel names but cannot show, and why.
type Withheld struct {
	Status Status `json:"status"`
	Why    string `json:"why"`
}

// Footprint is what producing this snapshot cost.
type Footprint struct {
	ReadBytes int64   `json:"read_bytes"`
	TookMS    float64 `json:"took_ms"`
	HeapKB    uint64  `json:"heap_kb"`
}

// Validate checks what the page relies on, so that a producer bug fails here rather than as a blank panel.
func (s Snapshot) Validate() error {
	if s.Schema != Schema {
		return fmt.Errorf("schema %q, want %q", s.Schema, Schema)
	}
	if _, err := time.Parse(time.RFC3339, s.Generated); err != nil {
		return fmt.Errorf("generated_at: %w", err)
	}
	if s.EveryMS < 0 {
		return fmt.Errorf("every_ms %d", s.EveryMS)
	}
	if len(s.Speech.Recent) > s.Speech.Limit {
		return fmt.Errorf("speech: %d utterances over the limit of %d", len(s.Speech.Recent), s.Speech.Limit)
	}
	for _, st := range []Status{s.Brain.Status, s.Speech.Status, s.Scene.Status, s.Frames.Status, s.GPUMemory.Status} {
		if st != Live && st != Missing && st != Unavailable {
			return fmt.Errorf("status %q", st)
		}
	}
	switch s.Brain.State {
	case "running", "stopped", "unknown":
	default:
		return fmt.Errorf("brain state %q", s.Brain.State)
	}
	return nil
}
