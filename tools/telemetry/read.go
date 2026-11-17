package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// TailBytes is what one snapshot reads first of each log: the journal grows by megabytes, and only its end matters.
// When that end holds fewer utterances than the history keeps, as when a burst of other events fills it, the journal
// is read again further back, doubling up to MaxTailBytes.
const (
	TailBytes    = 256 << 10
	MaxTailBytes = 4 << 20
)

// journalLine is the whole of what this tool takes from a line of the brain's journal (brain/voicemcp.go). Fields
// outside it, such as what was heard or said, are never decoded, so they cannot reach a snapshot.
type journalLine struct {
	Event   string `json:"event"`
	Seq     int64  `json:"journal_seq"`
	PID     int    `json:"pid"`
	TMS     int64  `json:"t_ms"`
	Version string `json:"version"`
	ASRMS   *int64 `json:"asr_ms"`
	Route   string `json:"route"`
	On      *bool  `json:"on"`
}

// Paths are the files a snapshot is read from: the brain's journal and pid file, and the scene's log.
type Paths struct {
	Journal, PIDFile, SceneLog string
}

// InData names the files in the folder Delta keeps them in (its plugin data folder).
func InData(dir string) Paths {
	return Paths{
		Journal:  filepath.Join(dir, "delta-events.jsonl"),
		PIDFile:  filepath.Join(dir, "brain.pid"),
		SceneLog: filepath.Join(dir, "scene.log"),
	}
}

// tail reads at most max bytes from the end of path, dropping a first line cut in two. It also returns when the file
// was last written.
func tail(path string, max int64) ([]byte, time.Time, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, time.Time{}, err
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		return nil, time.Time{}, err
	}
	from := info.Size() - max
	if from < 0 {
		from = 0
	}
	buf := make([]byte, info.Size()-from)
	if _, err := f.ReadAt(buf, from); err != nil && !errors.Is(err, io.EOF) {
		return nil, time.Time{}, err
	}
	if from > 0 {
		if i := bytes.IndexByte(buf, '\n'); i >= 0 {
			buf = buf[i+1:]
		} else {
			buf = nil
		}
	}
	return buf, info.ModTime(), nil
}

func stamp(t time.Time) string { return t.UTC().Format(time.RFC3339) }

func msStamp(ms int64) string { return stamp(time.UnixMilli(ms)) }

func source(name, path string, err error, at time.Time) Source {
	s := Source{Name: name, File: filepath.Base(path), Status: Live}
	if err != nil {
		s.Status = Missing
		return s
	}
	s.UpdatedAt = stamp(at)
	return s
}

// Read makes one snapshot from p. alive says whether a pid names a running process; limit bounds the utterances kept.
func Read(p Paths, limit int, alive func(int) bool, now time.Time) Snapshot {
	s := Snapshot{
		Schema:    Schema,
		Generated: stamp(now),
		Frames: Withheld{Status: Unavailable,
			Why: "the live scene writes its frame rate only when it closes"},
		GPUMemory: Withheld{Status: Unavailable,
			Why: "neither the brain nor the scene records the GPU's memory"},
	}

	journal, journalAt, jerr := tail(p.Journal, TailBytes)
	lines := parseJournal(journal)
	for reach := int64(TailBytes); jerr == nil && heardIn(lines) < limit && int64(len(journal)) >= reach-maxLine && reach < MaxTailBytes; {
		reach *= 2
		journal, journalAt, jerr = tail(p.Journal, reach)
		lines = parseJournal(journal)
	}
	s.Sources = append(s.Sources, source("brain journal", p.Journal, jerr, journalAt))
	pidText, perr := os.ReadFile(p.PIDFile)
	pidAt := time.Time{}
	if info, err := os.Stat(p.PIDFile); err == nil {
		pidAt = info.ModTime()
	}
	s.Sources = append(s.Sources, source("brain pid file", p.PIDFile, perr, pidAt))
	scene, sceneAt, serr := tail(p.SceneLog, TailBytes)
	s.Sources = append(s.Sources, source("scene log", p.SceneLog, serr, sceneAt))
	s.Producer.ReadBytes = int64(len(journal) + len(pidText) + len(scene))

	pid := 0
	if perr == nil {
		pid, _ = strconv.Atoi(strings.TrimSpace(string(pidText)))
	}
	s.Brain = brainFrom(lines, pid, perr, jerr, alive)
	s.Speech = speechFrom(lines, limit, jerr)
	s.Scene = sceneFrom(scene, sceneAt, serr)
	return s
}

func parseJournal(data []byte) []journalLine {
	var out []journalLine
	sc := bufio.NewScanner(bytes.NewReader(data))
	sc.Buffer(make([]byte, 64<<10), 4<<20)
	for sc.Scan() {
		var l journalLine
		if json.Unmarshal(sc.Bytes(), &l) == nil && l.Event != "" {
			out = append(out, l)
		}
	}
	return out
}

// maxLine is the longest journal line a read further back can expect to have dropped as cut in two.
const maxLine = 64 << 10

func heardIn(lines []journalLine) int {
	n := 0
	for _, l := range lines {
		if l.Event == "heard" && l.ASRMS != nil {
			n++
		}
	}
	return n
}

func brainFrom(lines []journalLine, pid int, perr, jerr error, alive func(int) bool) Brain {
	b := Brain{Status: Live, State: "unknown", Source: "brain pid file and journal"}
	if perr != nil && jerr != nil {
		b.Status = Missing
		return b
	}
	if pid > 0 {
		b.PID = pid
		stopped := false
		for _, l := range lines {
			if l.PID != pid {
				continue
			}
			switch l.Event {
			case "start":
				b.Version, b.Since, stopped = l.Version, msStamp(l.TMS), false
			case "stop":
				stopped = true
			}
		}
		switch {
		case !alive(pid) || stopped:
			b.State = "stopped"
		default:
			b.State = "running"
		}
	}
	for i := len(lines) - 1; i >= 0; i-- {
		if lines[i].Event == "mic" && lines[i].On != nil {
			b.Mic = &Mic{On: *lines[i].On, At: msStamp(lines[i].TMS)}
			break
		}
	}
	return b
}

func speechFrom(lines []journalLine, limit int, jerr error) Speech {
	sp := Speech{Status: Live, Recent: []Latency{}, Limit: limit, Source: "brain journal, heard events' asr_ms"}
	if jerr != nil {
		sp.Status = Missing
		return sp
	}
	for _, l := range lines {
		if l.Event == "heard" && l.ASRMS != nil {
			sp.Recent = append(sp.Recent, Latency{At: msStamp(l.TMS), MS: *l.ASRMS, Route: l.Route})
		}
	}
	if len(sp.Recent) > limit {
		sp.Recent = sp.Recent[len(sp.Recent)-limit:]
	}
	return sp
}

var (
	memoryLine = regexp.MustCompile(`working set (\d+) MB, private (\d+) MB`)
	memoryAt   = regexp.MustCompile(`(?:at|and) (\d+) s`)
)

// sceneFrom takes the last of the scene's memory lines (scene/MemoryWatch.cs).
func sceneFrom(log []byte, at time.Time, err error) Scene {
	sc := Scene{Status: Live, Source: "scene log, SCENE memory lines"}
	if err != nil {
		sc.Status = Missing
		return sc
	}
	sc.LogUpdatedAt = stamp(at)
	last := ""
	for _, line := range strings.Split(string(log), "\n") {
		if strings.HasPrefix(line, "SCENE memory") && memoryLine.MatchString(line) {
			last = line
		}
	}
	if last == "" {
		sc.Status = Unavailable
		return sc
	}
	m := memoryLine.FindStringSubmatch(last)
	sc.WorkingSetMB, _ = strconv.Atoi(m[1])
	sc.PrivateMB, _ = strconv.Atoi(m[2])
	if all := memoryAt.FindAllStringSubmatch(last, -1); len(all) > 0 {
		sc.SceneSeconds, _ = strconv.Atoi(all[len(all)-1][1])
	}
	return sc
}
