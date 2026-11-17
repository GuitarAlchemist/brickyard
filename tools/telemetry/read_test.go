package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

var now = time.Date(2026, 11, 17, 9, 0, 0, 0, time.UTC)

func fixture(t *testing.T, journal, pid, scene string) Paths {
	t.Helper()
	dir := t.TempDir()
	p := InData(dir)
	for path, body := range map[string]string{p.Journal: journal, p.PIDFile: pid, p.SceneLog: scene} {
		if body == "-" {
			continue // the file is not there
		}
		if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	return p
}

func line(fields string) string { return "{" + fields + "}\n" }

const started = `"event":"start","journal_seq":1,"pid":42,"t_ms":1763370000000,"version":"v0.6.1"`

func always(int) bool { return true }
func never(int) bool  { return false }

func TestWhatWasSaidNeverReachesTheSnapshot(t *testing.T) {
	journal := line(started) +
		line(`"event":"heard","journal_seq":2,"pid":42,"t_ms":1763370001000,"asr_ms":812,"route":"claude","text":"mon mot de passe est tournesol","utterance_id":"7"`) +
		line(`"event":"pairing","journal_seq":3,"pid":42,"t_ms":1763370002000,"url":"https://pair.example/abc","seconds":30`) +
		line(`"event":"say","journal_seq":4,"pid":42,"t_ms":1763370003000,"request":"dis bonjour"`)
	s := Read(fixture(t, journal, "42", "-"), 50, always, now)
	body, _ := json.Marshal(s)
	for _, word := range []string{"tournesol", "pair.example", "bonjour", "utterance_id"} {
		if strings.Contains(string(body), word) {
			t.Fatalf("the snapshot carries %q: %s", word, body)
		}
	}
	if len(s.Speech.Recent) != 1 || s.Speech.Recent[0].MS != 812 || s.Speech.Recent[0].Route != "claude" {
		t.Fatalf("speech %+v", s.Speech)
	}
}

func TestSpeechHistoryIsBounded(t *testing.T) {
	var b strings.Builder
	b.WriteString(line(started))
	for i := range 120 {
		b.WriteString(line(fmt.Sprintf(`"event":"heard","journal_seq":%d,"pid":42,"t_ms":%d,"asr_ms":%d`, i+2, 1763370000000+int64(i)*1000, i)))
	}
	s := Read(fixture(t, b.String(), "42", "-"), 50, always, now)
	if len(s.Speech.Recent) != 50 || s.Speech.Recent[0].MS != 70 || s.Speech.Recent[49].MS != 119 {
		t.Fatalf("kept %d, from %d to %d", len(s.Speech.Recent), s.Speech.Recent[0].MS, s.Speech.Recent[len(s.Speech.Recent)-1].MS)
	}
	if err := s.Validate(); err != nil {
		t.Fatal(err)
	}
}

func TestUtterancesFurtherBackThanABurst(t *testing.T) {
	pairing := line(`"event":"pairing","journal_seq":9,"pid":42,"t_ms":1763370009000,"seconds":30`)
	burst := func(bytes int) string { return strings.Repeat(pairing, bytes/len(pairing)+1) }
	heard := line(`"event":"heard","journal_seq":3,"pid":42,"t_ms":1763370003000,"asr_ms":640`)

	s := Read(fixture(t, line(started)+heard+burst(1<<20), "42", "-"), 50, always, now)
	if len(s.Speech.Recent) != 1 || s.Speech.Recent[0].MS != 640 {
		t.Fatalf("behind a burst of 1 MiB: %+v", s.Speech.Recent)
	}
	if s.Producer.ReadBytes > MaxTailBytes+64 {
		t.Fatalf("read %d bytes", s.Producer.ReadBytes)
	}

	s = Read(fixture(t, line(started)+heard+burst(MaxTailBytes+(1<<20)), "42", "-"), 50, always, now)
	if len(s.Speech.Recent) != 0 || s.Producer.ReadBytes > MaxTailBytes+64 {
		t.Fatalf("beyond %d bytes: %d utterances, %d bytes read", MaxTailBytes, len(s.Speech.Recent), s.Producer.ReadBytes)
	}
}

func TestBrainState(t *testing.T) {
	stoppedAfter := line(started) + line(`"event":"stop","journal_seq":2,"pid":42,"t_ms":1763370005000,"replaced_by":43`)
	cases := []struct {
		name, journal, pid string
		alive              func(int) bool
		state              string
		status             Status
	}{
		{"running", line(started), "42", always, "running", Live},
		{"its process is gone", line(started), "42", never, "stopped", Live},
		{"it wrote its stop", stoppedAfter, "42", always, "stopped", Live},
		{"no pid file", line(started), "-", always, "unknown", Live},
		{"nothing at all", "-", "-", always, "unknown", Missing},
	}
	for _, c := range cases {
		b := Read(fixture(t, c.journal, c.pid, "-"), 50, c.alive, now).Brain
		if b.State != c.state || b.Status != c.status {
			t.Errorf("%s: state %q status %q, want %q %q", c.name, b.State, b.Status, c.state, c.status)
		}
	}
	b := Read(fixture(t, line(started), "42", "-"), 50, always, now).Brain
	if b.Version != "v0.6.1" || b.Since != "2025-11-17T09:00:00Z" || b.PID != 42 {
		t.Errorf("brain %+v", b)
	}
}

func TestMissingSourcesAreMarkedNotInvented(t *testing.T) {
	s := Read(fixture(t, "-", "-", "-"), 50, always, now)
	if err := s.Validate(); err != nil {
		t.Fatal(err)
	}
	for _, src := range s.Sources {
		if src.Status != Missing || src.UpdatedAt != "" {
			t.Errorf("source %+v", src)
		}
	}
	if s.Speech.Status != Missing || len(s.Speech.Recent) != 0 || s.Scene.Status != Missing || s.Scene.WorkingSetMB != 0 {
		t.Errorf("speech %+v scene %+v", s.Speech, s.Scene)
	}
	if s.Frames.Status != Unavailable || s.GPUMemory.Status != Unavailable {
		t.Errorf("frame rate %+v, GPU memory %+v", s.Frames, s.GPUMemory)
	}
}

func TestSceneMemoryLines(t *testing.T) {
	cases := map[string][3]int{
		"SCENE memory at 7 s: working set 380 MB, private 300 MB, managed 40 MB, peak 381 MB":                                                  {380, 300, 7},
		"SCENE memory grows: working set 900 MB, private 800 MB, managed 600 MB at 40060 s":                                                    {900, 800, 40060},
		"SCENE memory peak: the working set peaked at 4592 MB between 31 s and 40000 s; now working set 170 MB, private 460 MB, managed 50 MB": {170, 460, 40000},
	}
	for text, want := range cases {
		sc := Read(fixture(t, "-", "-", "SCENE listening on 127.0.0.1:8805\n"+text+"\nSCENE something else\n"), 50, always, now).Scene
		if sc.Status != Live || sc.WorkingSetMB != want[0] || sc.PrivateMB != want[1] || sc.SceneSeconds != want[2] || sc.LogUpdatedAt == "" {
			t.Errorf("%s: %+v", text, sc)
		}
	}
	sc := Read(fixture(t, "-", "-", "dotnet watch building\n"), 50, always, now).Scene
	if sc.Status != Unavailable || sc.WorkingSetMB != 0 {
		t.Errorf("a log without memory lines: %+v", sc)
	}
}

func TestTailReadsOnlyTheEnd(t *testing.T) {
	path := filepath.Join(t.TempDir(), "big.jsonl")
	body := strings.Repeat("x", TailBytes) + "\n" + strings.Repeat("y", 100) + "\nlast\n"
	if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
	got, _, err := tail(path, TailBytes)
	if err != nil || int64(len(got)) > TailBytes || strings.Contains(string(got), "x") || !strings.HasSuffix(string(got), "last\n") {
		t.Fatalf("%d bytes, err %v", len(got), err)
	}
}

func TestValidateRefuses(t *testing.T) {
	good := Read(fixture(t, line(started), "42", "-"), 50, always, now)
	if err := good.Validate(); err != nil {
		t.Fatal(err)
	}
	for name, change := range map[string]func(*Snapshot){
		"schema":   func(s *Snapshot) { s.Schema = "delta-telemetry@0" },
		"time":     func(s *Snapshot) { s.Generated = "yesterday" },
		"status":   func(s *Snapshot) { s.Scene.Status = "fine" },
		"state":    func(s *Snapshot) { s.Brain.State = "busy" },
		"limit":    func(s *Snapshot) { s.Speech.Limit = 0; s.Speech.Recent = []Latency{{MS: 1}} },
		"every_ms": func(s *Snapshot) { s.EveryMS = -1 },
	} {
		s := good
		change(&s)
		if s.Validate() == nil {
			t.Errorf("%s: accepted", name)
		}
	}
}

func TestWriteIsAScriptThePageCanLoad(t *testing.T) {
	out := t.TempDir()
	p := fixture(t, line(started), "42", "-")
	if err := write(out, p, 50, 5*time.Second); err != nil {
		t.Fatal(err)
	}
	script, err := os.ReadFile(filepath.Join(out, "snapshot.js"))
	if err != nil {
		t.Fatal(err)
	}
	text := strings.TrimSpace(string(script))
	if !strings.HasPrefix(text, "deltaTelemetryReceive(") || !strings.HasSuffix(text, ");") {
		t.Fatalf("%.80s", text)
	}
	var s Snapshot
	if err := json.Unmarshal([]byte(text[len("deltaTelemetryReceive("):len(text)-2]), &s); err != nil {
		t.Fatal(err)
	}
	if s.EveryMS != 5000 || s.Validate() != nil {
		t.Fatalf("%+v", s)
	}
	if entries, _ := os.ReadDir(out); len(entries) != 2 {
		t.Fatalf("left behind: %v", entries)
	}
}
