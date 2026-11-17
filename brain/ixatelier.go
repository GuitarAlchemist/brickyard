package main

// The IX workshop ("Atelier IX") in Delta's hall: a knot that IX computed, shown on a stand that turns it. IX does the
// mathematics; Delta only draws what IX returns. brain -ix-knot NAME asks IX's MCP server (ix-mcp, the same executable
// Claude Code runs over stdio, started for this one call and closed after it) for the catalogue knot with its 3D path,
// checks what comes back against the bounds below, and writes it as one versioned document,
// %LOCALAPPDATA%\delta\ix-atelier.json, which the office's widget module reads and redraws without the window
// restarting. When IX cannot answer, the document says why instead (an "error"), so that the stand shows IX as
// unavailable rather than an old knot as new.
//
// The document, format "delta-ix-geometry", v 1:
//
//	{"format", "v": 1, "source": "ix_knot", "at": RFC 3339,
//	 "knot": {"id", "en", "fr", "closure", "family", "crossings", "writhe", "abok", "jones"},
//	 "radius": the rope's, "ropes": [{"closed", "points": [[x, y, z], ...]}]}
//
// or {"format", "v": 1, "source", "at", "error": why}. Its bounds, which the module checks again: at most 8 ropes and
// 4096 points in all, each point three finite numbers within ±1000, the radius in (0, 100], texts of at most 96
// characters without control characters, crossings within [0, 64] and the writhe within ±64.

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"
	"unicode"
)

const (
	atelierFormat    = "delta-ix-geometry"
	atelierVersion   = 1
	atelierMaxRopes  = 8
	atelierMaxPoints = 4096
	atelierMaxCoord  = 1000
	atelierMaxRadius = 100
	atelierMaxText   = 96
	atelierMaxCross  = 64
	ixAnswerMax      = 4 << 20 // bytes in one line from ix-mcp
	ixCallSeconds    = 30
)

type atelierKnot struct {
	ID        string `json:"id"`
	En        string `json:"en"`
	Fr        string `json:"fr"`
	Closure   string `json:"closure"`
	Family    string `json:"family"`
	Crossings int    `json:"crossings"`
	Writhe    int    `json:"writhe"`
	Abok      int    `json:"abok,omitempty"`
	Jones     string `json:"jones"`
}

type atelierRope struct {
	Closed bool         `json:"closed"`
	Points [][3]float64 `json:"points"`
}

type atelierDoc struct {
	Format string        `json:"format"`
	V      int           `json:"v"`
	Source string        `json:"source"`
	At     string        `json:"at"`
	Knot   *atelierKnot  `json:"knot,omitempty"`
	Radius float64       `json:"radius,omitempty"`
	Ropes  []atelierRope `json:"ropes,omitempty"`
	Error  string        `json:"error,omitempty"`
}

// What ix_knot answers with geometry: true, as far as the stand needs it.
type ixKnotAnswer struct {
	ID        string `json:"id"`
	En        string `json:"en"`
	Fr        string `json:"fr"`
	Closure   string `json:"closure"`
	Family    string `json:"family"`
	Crossings *int   `json:"crossings"`
	Writhe    *int   `json:"writhe"`
	Abok      int    `json:"abok"`
	Jones     struct {
		Text string `json:"text"`
	} `json:"jones"`
	Geometry *struct {
		Radius float64 `json:"radius"`
		Ropes  []struct {
			Closed bool        `json:"closed"`
			Points [][]float64 `json:"points"`
		} `json:"ropes"`
	} `json:"geometry"`
}

// atelierFromIX checks ix_knot's answer and keeps what the stand shows, the points rounded to 0.1 mm of a 1 m knot.
func atelierFromIX(answer []byte, now time.Time) (atelierDoc, error) {
	var a ixKnotAnswer
	if err := json.Unmarshal(answer, &a); err != nil {
		return atelierDoc{}, fmt.Errorf("IX's answer is not a knot: %v", err)
	}
	if a.Geometry == nil {
		return atelierDoc{}, errors.New("IX's answer has no geometry")
	}
	for name, text := range map[string]string{"id": a.ID, "en": a.En, "fr": a.Fr, "closure": a.Closure, "family": a.Family, "jones": a.Jones.Text} {
		if err := atelierText(name, text); err != nil {
			return atelierDoc{}, err
		}
	}
	if a.ID == "" || a.Crossings == nil || a.Writhe == nil {
		return atelierDoc{}, errors.New("IX's answer lacks the knot's id, crossings or writhe")
	}
	if *a.Crossings < 0 || *a.Crossings > atelierMaxCross || *a.Writhe < -atelierMaxCross || *a.Writhe > atelierMaxCross {
		return atelierDoc{}, fmt.Errorf("%d crossings and a writhe of %d are beyond the stand's ±%d", *a.Crossings, *a.Writhe, atelierMaxCross)
	}
	r := a.Geometry.Radius
	if math.IsNaN(r) || r <= 0 || r > atelierMaxRadius {
		return atelierDoc{}, fmt.Errorf("a rope radius of %v is outside (0, %d]", r, atelierMaxRadius)
	}
	if len(a.Geometry.Ropes) == 0 || len(a.Geometry.Ropes) > atelierMaxRopes {
		return atelierDoc{}, fmt.Errorf("%d ropes, not 1 to %d", len(a.Geometry.Ropes), atelierMaxRopes)
	}
	doc := atelierDoc{
		Format: atelierFormat, V: atelierVersion, Source: "ix_knot", At: now.UTC().Format(time.RFC3339),
		Knot:   &atelierKnot{ID: a.ID, En: a.En, Fr: a.Fr, Closure: a.Closure, Family: a.Family, Crossings: *a.Crossings, Writhe: *a.Writhe, Abok: a.Abok, Jones: a.Jones.Text},
		Radius: round4(r),
	}
	total := 0
	for i, rope := range a.Geometry.Ropes {
		total += len(rope.Points)
		if total > atelierMaxPoints {
			return atelierDoc{}, fmt.Errorf("more than %d points", atelierMaxPoints)
		}
		if len(rope.Points) < 2 {
			return atelierDoc{}, fmt.Errorf("rope %d has %d points, fewer than 2", i, len(rope.Points))
		}
		kept := atelierRope{Closed: rope.Closed, Points: make([][3]float64, len(rope.Points))}
		for j, p := range rope.Points {
			if len(p) != 3 {
				return atelierDoc{}, fmt.Errorf("rope %d, point %d has %d coordinates, not 3", i, j, len(p))
			}
			for k, v := range p {
				if math.IsNaN(v) || math.IsInf(v, 0) || math.Abs(v) > atelierMaxCoord {
					return atelierDoc{}, fmt.Errorf("rope %d, point %d: %v is beyond ±%d", i, j, v, atelierMaxCoord)
				}
				kept.Points[j][k] = round4(v)
			}
		}
		doc.Ropes = append(doc.Ropes, kept)
	}
	return doc, nil
}

func atelierText(name, text string) error {
	if len([]rune(text)) > atelierMaxText {
		return fmt.Errorf("the knot's %s is longer than %d characters", name, atelierMaxText)
	}
	if strings.IndexFunc(text, unicode.IsControl) >= 0 {
		return fmt.Errorf("the knot's %s holds a control character", name)
	}
	return nil
}

func round4(v float64) float64 { return math.Round(v*1e4) / 1e4 }

// atelierError is the document that says why there is no knot: the stand shows IX as unavailable.
func atelierError(why string, now time.Time) atelierDoc {
	if r := []rune(why); len(r) > 300 {
		why = string(r[:299]) + "…"
	}
	why = strings.Map(func(c rune) rune {
		if unicode.IsControl(c) {
			return ' '
		}
		return c
	}, why)
	return atelierDoc{Format: atelierFormat, V: atelierVersion, Source: "ix_knot", At: now.UTC().Format(time.RFC3339), Error: why}
}

// writeAtelier writes the document whole beside its place, then moves it there, so that the module never reads half.
func writeAtelier(path string, doc atelierDoc) error {
	body, err := json.Marshal(doc)
	if err != nil {
		return err
	}
	return writeDocument(path, body)
}

// writeDocument writes a document whole beside its place, then moves it there.
func writeDocument(path string, body []byte) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, append(body, '\n'), 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

// defaultAtelierPath is where the office's module looks: %LOCALAPPDATA%\delta\ix-atelier.json on Windows.
func defaultAtelierPath() string {
	dir, err := os.UserCacheDir()
	if err != nil {
		dir = os.TempDir()
	}
	return filepath.Join(dir, "delta", "ix-atelier.json")
}

// ixKnot asks ix-mcp for a catalogue knot with its geometry.
func ixKnot(ctx context.Context, exe, name string) ([]byte, error) {
	return ixCall(ctx, exe, "ix_knot", map[string]any{"name": name, "geometry": true})
}

// ixCall calls one of IX's tools in one short MCP conversation with ix-mcp over stdio (newline-delimited JSON-RPC):
// initialize, initialized, tools/call; the text of the call's answer is IX's. The process is closed after it, or
// killed when ctx ends.
func ixCall(ctx context.Context, exe, tool string, arguments any) ([]byte, error) {
	cmd := exec.CommandContext(ctx, exe)
	cmd.WaitDelay = 2 * time.Second
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return nil, err
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return nil, err
	}
	var stderr bytes.Buffer
	cmd.Stderr = &limitedWriter{w: &stderr, left: 4096}
	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("IX unavailable: %v", err)
	}
	defer func() {
		_ = stdin.Close()
		_ = cmd.Wait()
	}()
	args, err := json.Marshal(arguments)
	if err != nil {
		return nil, err
	}
	call, _ := json.Marshal(map[string]any{"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": map[string]any{"name": tool, "arguments": json.RawMessage(args)}})
	for _, line := range []string{
		`{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"delta-brain","version":"` + version + `"}}}`,
		`{"jsonrpc":"2.0","method":"notifications/initialized"}`,
		string(call),
	} {
		if _, err := io.WriteString(stdin, line+"\n"); err != nil {
			return nil, fmt.Errorf("IX unavailable: it stopped reading (%v)%s", err, ixSaid(&stderr))
		}
	}
	reader := bufio.NewReaderSize(stdout, 64<<10)
	for {
		line, err := readLine(reader, ixAnswerMax)
		if err != nil {
			if ctx.Err() != nil {
				return nil, fmt.Errorf("IX unavailable: no answer within %d s", ixCallSeconds)
			}
			return nil, fmt.Errorf("IX unavailable: %v%s", err, ixSaid(&stderr))
		}
		var msg struct {
			ID     *int            `json:"id"`
			Result json.RawMessage `json:"result"`
			Error  *struct {
				Message string `json:"message"`
			} `json:"error"`
		}
		if json.Unmarshal(line, &msg) != nil || msg.ID == nil || *msg.ID != 2 {
			continue // initialize's answer, a notification or a log line
		}
		if msg.Error != nil {
			return nil, fmt.Errorf("IX refused %s: %s", tool, msg.Error.Message)
		}
		var result struct {
			IsError bool `json:"isError"`
			Content []struct {
				Type string `json:"type"`
				Text string `json:"text"`
			} `json:"content"`
		}
		if err := json.Unmarshal(msg.Result, &result); err != nil || len(result.Content) == 0 {
			return nil, fmt.Errorf("IX answered %s without content", tool)
		}
		if result.IsError {
			return nil, fmt.Errorf("IX refused %s: %s", tool, result.Content[0].Text)
		}
		return []byte(result.Content[0].Text), nil
	}
}

func readLine(r *bufio.Reader, max int) ([]byte, error) {
	var line []byte
	for {
		part, isPrefix, err := r.ReadLine()
		if err != nil {
			return nil, err
		}
		line = append(line, part...)
		if len(line) > max {
			return nil, fmt.Errorf("a line of more than %d bytes", max)
		}
		if !isPrefix {
			return line, nil
		}
	}
}

func ixSaid(stderr *bytes.Buffer) string {
	s := strings.TrimSpace(stderr.String())
	if s == "" {
		return ""
	}
	if i := strings.LastIndexByte(s, '\n'); i >= 0 {
		s = s[i+1:]
	}
	return " (" + s + ")"
}

type limitedWriter struct {
	w    io.Writer
	left int
}

func (l *limitedWriter) Write(p []byte) (int, error) {
	n := len(p)
	if l.left > 0 {
		k := min(len(p), l.left)
		_, _ = l.w.Write(p[:k])
		l.left -= k
	}
	return n, nil
}

// ixExecutable is ix-mcp: the one given, else the one on the PATH.
func ixExecutable(exe string) (string, error) {
	if exe != "" {
		return exe, nil
	}
	found, err := exec.LookPath("ix-mcp")
	if err != nil {
		return "", errors.New("IX unavailable: no ix-mcp on the PATH; pass -ix-mcp with its path")
	}
	return found, nil
}

// runIXKnot is brain -ix-knot: the knot, or why there is none, written for the stand. 0 when the knot is there.
func runIXKnot(name, exe, out string, stdout, stderr io.Writer) int {
	now := time.Now()
	if out == "" {
		out = defaultAtelierPath()
	}
	fail := func(why string) int {
		if err := writeAtelier(out, atelierError(why, now)); err != nil {
			fmt.Fprintf(stderr, "ix-knot: %s; and the stand's document could not be written: %v\n", why, err)
			return 1
		}
		fmt.Fprintf(stderr, "ix-knot: %s; the stand says so (%s)\n", why, out)
		return 1
	}
	exe, err := ixExecutable(exe)
	if err != nil {
		return fail(err.Error())
	}
	ctx, cancel := context.WithTimeout(context.Background(), ixCallSeconds*time.Second)
	defer cancel()
	answer, err := ixKnot(ctx, exe, name)
	if err != nil {
		return fail(err.Error())
	}
	doc, err := atelierFromIX(answer, now)
	if err != nil {
		return fail("IX's geometry refused: " + err.Error())
	}
	if err := writeAtelier(out, doc); err != nil {
		fmt.Fprintf(stderr, "ix-knot: %v\n", err)
		return 1
	}
	points := 0
	for _, r := range doc.Ropes {
		points += len(r.Points)
	}
	fmt.Fprintf(stdout, "ix-knot: %s (%s), %d crossings, writhe %d, %d points in %d rope(s), written to %s\n",
		doc.Knot.ID, doc.Knot.Closure, doc.Knot.Crossings, doc.Knot.Writhe, points, len(doc.Ropes), out)
	return 0
}
