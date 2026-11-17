// Command telemetry writes a read-only snapshot of Delta for the observability panel (web/), into a folder the panel
// is opened from as a file: no server, no port, no credential. It reads the brain's journal and pid file and the
// scene's log, the same journal IX's delta@1 probe tails, and keeps only the fields named in read.go.
//
//	go run . -data <Delta's plugin data folder> -out <folder> [-every 5s] [-for 1m]
//
// Then open <folder>/index.html. Without -every it writes once; with it, it rewrites snapshot.js at that period in the
// foreground until Ctrl+C or -for runs out. It is not a service and starts nothing else.
package main

import (
	"context"
	_ "embed"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"runtime"
	"time"
)

//go:embed web/index.html
var indexHTML []byte

//go:embed web/build/panel.js
var panelJS []byte

// Limits on the producer's own pace and history, whatever its flags say.
const (
	MinEvery = time.Second
	MaxLimit = 200
)

func main() {
	data := flag.String("data", "", "Delta's plugin data folder (delta-events.jsonl, brain.pid, scene.log)")
	out := flag.String("out", "", "the folder to write index.html, panel.js and the snapshot into")
	every := flag.Duration("every", 0, "rewrite the snapshot at this period (at least 1s); 0 writes it once")
	limit := flag.Int("limit", 50, "utterances kept in the speech history (at most 200)")
	forTime := flag.Duration("for", 0, "stop after this long; 0 runs until Ctrl+C")
	flag.Parse()
	if *data == "" || *out == "" {
		fmt.Fprintln(os.Stderr, "telemetry: -data and -out are required")
		os.Exit(2)
	}
	if *every != 0 && *every < MinEvery {
		*every = MinEvery
	}
	*limit = max(1, min(*limit, MaxLimit))

	if err := os.MkdirAll(*out, 0o755); err != nil {
		fail(err)
	}
	for name, body := range map[string][]byte{"index.html": indexHTML, "panel.js": panelJS} {
		if err := writeAtomic(filepath.Join(*out, name), body); err != nil {
			fail(err)
		}
	}

	paths := InData(*data)
	once := func() {
		if err := write(*out, paths, *limit, *every); err != nil {
			fmt.Fprintln(os.Stderr, "telemetry:", err)
		}
	}
	once()
	fmt.Printf("telemetry: open %s\n", filepath.Join(*out, "index.html"))
	if *every == 0 {
		return
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	if *forTime > 0 {
		var cancel context.CancelFunc
		ctx, cancel = context.WithTimeout(ctx, *forTime)
		defer cancel()
	}
	tick := time.NewTicker(*every)
	defer tick.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-tick.C:
			once()
		}
	}
}

// write makes one snapshot and writes it as snapshot.json and as snapshot.js, the script the page loads from a file.
func write(dir string, p Paths, limit int, every time.Duration) error {
	start := time.Now()
	s := Read(p, limit, alive, start)
	s.EveryMS = every.Milliseconds()
	var m runtime.MemStats
	runtime.ReadMemStats(&m)
	s.Producer.HeapKB = m.HeapAlloc >> 10
	s.Producer.TookMS = float64(time.Since(start).Microseconds()) / 1000
	if err := s.Validate(); err != nil {
		return fmt.Errorf("snapshot not written: %w", err)
	}
	body, err := json.Marshal(s) // escapes <, > and &, so it is safe inside a script
	if err != nil {
		return err
	}
	if err := writeAtomic(filepath.Join(dir, "snapshot.json"), body); err != nil {
		return err
	}
	script := append(append([]byte("deltaTelemetryReceive("), body...), []byte(");\n")...)
	return writeAtomic(filepath.Join(dir, "snapshot.js"), script)
}

// writeAtomic replaces path whole, so the page never loads half a snapshot. A browser reading the file at that moment
// can hold it open on Windows, so the rename is tried a few times.
func writeAtomic(path string, body []byte) error {
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, body, 0o644); err != nil {
		return err
	}
	var err error
	for range 5 {
		if err = os.Rename(tmp, path); err == nil {
			return nil
		}
		time.Sleep(20 * time.Millisecond)
	}
	os.Remove(tmp)
	return err
}

func fail(err error) {
	fmt.Fprintln(os.Stderr, "telemetry:", err)
	os.Exit(1)
}
