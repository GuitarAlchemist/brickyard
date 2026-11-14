package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// IX's own answer to ix_knot {"name": "figure-eight", "geometry": true}, recorded from ix-mcp by
// TestRecordIXFigureEight with DELTA_IX_MCP set to its path.
const ixFigureEight = "testdata/ix-knot-figure-eight.json"

func TestRecordIXFigureEight(t *testing.T) {
	exe := os.Getenv("DELTA_IX_MCP")
	if exe == "" {
		t.Skip("DELTA_IX_MCP names ix-mcp to record IX's answer")
	}
	ctx, cancel := context.WithTimeout(context.Background(), ixCallSeconds*time.Second)
	defer cancel()
	answer, err := ixKnot(ctx, exe, "figure-eight")
	if err != nil {
		t.Fatal(err)
	}
	var pretty bytes.Buffer
	if err := json.Indent(&pretty, answer, "", " "); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(ixFigureEight, append(pretty.Bytes(), '\n'), 0o644); err != nil {
		t.Fatal(err)
	}
}

func figureEight(t *testing.T) []byte {
	t.Helper()
	b, err := os.ReadFile(ixFigureEight)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func TestTheAtelierKeepsIXsFigureEight(t *testing.T) {
	answer := figureEight(t)
	doc, err := atelierFromIX(answer, time.Date(2026, 11, 14, 7, 0, 0, 0, time.UTC))
	if err != nil {
		t.Fatal(err)
	}
	k := doc.Knot
	if doc.Format != "delta-ix-geometry" || doc.V != 1 || doc.Source != "ix_knot" || doc.At != "2026-11-14T07:00:00Z" || doc.Error != "" {
		t.Fatalf("envelope %+v", doc)
	}
	if k.ID != "figure-eight" || k.Closure != "4_1" || k.Crossings != 4 || k.Writhe != 0 || k.Fr != "Nœud en huit" || k.Jones != "t^-2 - t^-1 + 1 - t + t^2" {
		t.Fatalf("knot %+v", *k)
	}
	var raw struct {
		Geometry struct {
			Radius float64 `json:"radius"`
			Ropes  []struct {
				Points [][]float64 `json:"points"`
			} `json:"ropes"`
		} `json:"geometry"`
	}
	if err := json.Unmarshal(answer, &raw); err != nil {
		t.Fatal(err)
	}
	if len(doc.Ropes) != len(raw.Geometry.Ropes) || len(doc.Ropes[0].Points) != len(raw.Geometry.Ropes[0].Points) || doc.Radius != raw.Geometry.Radius {
		t.Fatalf("%d ropes of %d points, radius %v; IX gave %d of %d, %v", len(doc.Ropes), len(doc.Ropes[0].Points), doc.Radius,
			len(raw.Geometry.Ropes), len(raw.Geometry.Ropes[0].Points), raw.Geometry.Radius)
	}
	for j, p := range raw.Geometry.Ropes[0].Points { // IX's path, only rounded: nothing of the knot is recomputed here
		for c := range 3 {
			if d := doc.Ropes[0].Points[j][c] - p[c]; d > 5e-5 || d < -5e-5 {
				t.Fatalf("point %d moved by %v", j, d)
			}
		}
	}
	body, _ := json.Marshal(doc)
	if len(body) > 64<<10 {
		t.Fatalf("the document is %d bytes", len(body))
	}
}

// The document the office's module is tested on (widget-tests) is the one the brain writes for IX's recorded answer,
// so that the two sides of the contract cannot drift apart. DELTA_UPDATE_FIXTURES=1 writes it again.
func TestTheModuleIsTestedOnTheBrainsDocument(t *testing.T) {
	doc, err := atelierFromIX(figureEight(t), time.Date(2026, 11, 14, 7, 0, 0, 0, time.UTC))
	if err != nil {
		t.Fatal(err)
	}
	body, _ := json.Marshal(doc)
	body = append(body, '\n')
	fixture := filepath.Join("..", "widget-tests", "testdata", "ix-atelier-figure-eight.json")
	if os.Getenv("DELTA_UPDATE_FIXTURES") == "1" {
		if err := os.WriteFile(fixture, body, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	kept, err := os.ReadFile(fixture)
	kept = bytes.ReplaceAll(kept, []byte("\r\n"), []byte("\n")) // a Windows checkout may write it with CRLF
	if err != nil || !bytes.Equal(kept, body) {
		t.Fatalf("%s is not the brain's document for IX's figure-eight (%v); DELTA_UPDATE_FIXTURES=1 writes it", fixture, err)
	}
}

// Each damage to IX's answer is refused with why, and nothing is written as a knot.
func TestTheAtelierRefusesBadGeometry(t *testing.T) {
	cases := []struct {
		name, want string
		damage     func(a map[string]any)
	}{
		{"no geometry", "no geometry", func(a map[string]any) { delete(a, "geometry") }},
		{"a flat point", "2 coordinates", func(a map[string]any) { points(a)[5] = []any{1.0, 2.0} }},
		{"a point far away", "beyond", func(a map[string]any) { points(a)[7].([]any)[1] = 1e6 }},
		{"a number as text", "not a knot", func(a map[string]any) { points(a)[7].([]any)[1] = "7" }},
		{"no radius", "radius", func(a map[string]any) { geometry(a)["radius"] = 0.0 }},
		{"nine ropes", "9 ropes", func(a map[string]any) {
			rope := geometry(a)["ropes"].([]any)[0]
			geometry(a)["ropes"] = []any{rope, rope, rope, rope, rope, rope, rope, rope, rope}
		}},
		{"too many points", "more than 4096", func(a map[string]any) {
			p := points(a)
			for len(p) <= 4096 {
				p = append(p, p...)
			}
			geometry(a)["ropes"].([]any)[0].(map[string]any)["points"] = p
		}},
		{"one point", "fewer than 2", func(a map[string]any) { geometry(a)["ropes"].([]any)[0].(map[string]any)["points"] = points(a)[:1] }},
		{"no ropes", "0 ropes", func(a map[string]any) { geometry(a)["ropes"] = []any{} }},
		{"a control character", "control", func(a map[string]any) { a["fr"] = "Nœud\nen huit" }},
		{"a long name", "longer", func(a map[string]any) { a["en"] = strings.Repeat("x", 97) }},
		{"crossings beyond", "beyond", func(a map[string]any) { a["crossings"] = 99.0 }},
		{"no writhe", "writhe", func(a map[string]any) { delete(a, "writhe") }},
		{"not JSON", "not a knot", nil},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			var a map[string]any
			if err := json.Unmarshal(figureEight(t), &a); err != nil {
				t.Fatal(err)
			}
			body := []byte("IX is down")
			if c.damage != nil {
				c.damage(a)
				body, _ = json.Marshal(a)
			}
			_, err := atelierFromIX(body, time.Now())
			if err == nil || !strings.Contains(err.Error(), c.want) {
				t.Fatalf("got %v, want an error with %q", err, c.want)
			}
		})
	}
}

func geometry(a map[string]any) map[string]any { return a["geometry"].(map[string]any) }
func points(a map[string]any) []any {
	return geometry(a)["ropes"].([]any)[0].(map[string]any)["points"].([]any)
}

// A fake ix-mcp: the test binary itself, run with BRAIN_FAKE_IX set to how it behaves. "answer" answers initialize,
// then ix_knot with the recorded figure-eight, but only when asked for it with geometry; "refuse" answers ix_knot as a
// tool error; "exit" stops before reading anything; "mute" reads and never answers.
func fakeIX() {
	mode := os.Getenv("BRAIN_FAKE_IX")
	if mode == "exit" {
		fmt.Fprintln(os.Stderr, "ix-mcp: cannot open its catalogue")
		os.Exit(3)
	}
	in := bufio.NewScanner(os.Stdin)
	in.Buffer(nil, 1<<20)
	for in.Scan() {
		var msg struct {
			ID     *int   `json:"id"`
			Method string `json:"method"`
			Params struct {
				Name      string         `json:"name"`
				Arguments map[string]any `json:"arguments"`
			} `json:"params"`
		}
		if json.Unmarshal(in.Bytes(), &msg) != nil || msg.ID == nil || mode == "mute" {
			continue
		}
		var result any
		switch {
		case msg.Method == "initialize":
			result = map[string]any{"protocolVersion": "2025-06-18", "capabilities": map[string]any{"tools": map[string]any{}}, "serverInfo": map[string]any{"name": "fake-ix"}}
		case msg.Method == "tools/call" && mode == "answer" && msg.Params.Name == "ix_origami_check" && (msg.Params.Arguments["sequence"] != nil || msg.Params.Arguments["fold"] != nil):
			answer, err := os.ReadFile(os.Getenv("BRAIN_FAKE_IX_ORIGAMI"))
			if err != nil {
				os.Exit(4)
			}
			result = map[string]any{"content": []any{map[string]any{"type": "text", "text": string(answer)}}}
		case msg.Method == "tools/call" && mode == "refuse":
			result = map[string]any{"isError": true, "content": []any{map[string]any{"type": "text", "text": "unknown knot"}}}
		case msg.Method == "tools/call" && msg.Params.Name == "ix_knot" && msg.Params.Arguments["name"] == "figure-eight" && msg.Params.Arguments["geometry"] == true:
			answer, err := os.ReadFile(os.Getenv("BRAIN_FAKE_IX_ANSWER"))
			if err != nil {
				os.Exit(4)
			}
			result = map[string]any{"content": []any{map[string]any{"type": "text", "text": string(answer)}}}
		default:
			result = map[string]any{"isError": true, "content": []any{map[string]any{"type": "text", "text": "asked without geometry"}}}
		}
		fmt.Fprintf(os.Stdout, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{}}\n") // a notification first, as a server may
		line, _ := json.Marshal(map[string]any{"jsonrpc": "2.0", "id": *msg.ID, "result": result})
		os.Stdout.Write(append(line, '\n'))
	}
	os.Exit(0)
}

func fakeIXAs(t *testing.T, mode string) {
	t.Helper()
	abs, err := filepath.Abs(ixFigureEight)
	if err != nil {
		t.Fatal(err)
	}
	t.Setenv("BRAIN_FAKE_IX", mode)
	t.Setenv("BRAIN_FAKE_IX_ANSWER", abs)
}

func TestTheAtelierAsksIXOverMCPAndWritesTheKnot(t *testing.T) {
	fakeIXAs(t, "answer")
	out := filepath.Join(t.TempDir(), "delta", "ix-atelier.json")
	var stdout, stderr bytes.Buffer
	if code := runIXKnot("figure-eight", os.Args[0], out, &stdout, &stderr); code != 0 {
		t.Fatalf("exit %d: %s", code, stderr.String())
	}
	var doc atelierDoc
	body, err := os.ReadFile(out)
	if err != nil || json.Unmarshal(body, &doc) != nil {
		t.Fatalf("%v: %s", err, body)
	}
	if doc.Knot == nil || doc.Knot.Crossings != 4 || doc.Error != "" || !strings.Contains(stdout.String(), "4 crossings") {
		t.Fatalf("%s / %s", body[:min(len(body), 300)], stdout.String())
	}
	if _, err := os.Stat(out + ".tmp"); !os.IsNotExist(err) {
		t.Fatal("the temporary document was left behind")
	}
}

// When IX cannot answer, the stand's document says why, in place of the knot it held: never an old knot shown as new.
func TestTheAtelierSaysWhenIXIsUnavailable(t *testing.T) {
	cases := []struct{ mode, exe, want string }{
		{"exit", "", "IX unavailable"},
		{"refuse", "", "IX refused ix_knot: unknown knot"},
		{"answer", filepath.Join(os.TempDir(), "no-such-ix-mcp.exe"), "IX unavailable"},
	}
	for _, c := range cases {
		t.Run(c.mode+c.exe, func(t *testing.T) {
			fakeIXAs(t, c.mode)
			out := filepath.Join(t.TempDir(), "ix-atelier.json")
			if err := writeAtelier(out, atelierDoc{Format: atelierFormat, V: 1, Knot: &atelierKnot{ID: "old"}}); err != nil {
				t.Fatal(err)
			}
			exe := c.exe
			if exe == "" {
				exe = os.Args[0]
			}
			var stdout, stderr bytes.Buffer
			if code := runIXKnot("figure-eight", exe, out, &stdout, &stderr); code != 1 {
				t.Fatalf("exit %d", code)
			}
			var doc atelierDoc
			body, _ := os.ReadFile(out)
			if json.Unmarshal(body, &doc) != nil || doc.Knot != nil || !strings.HasPrefix(doc.Error, c.want) || doc.V != 1 {
				t.Fatalf("%s", body)
			}
		})
	}
}

func TestTheAtelierGivesUpOnAMuteIX(t *testing.T) {
	fakeIXAs(t, "mute")
	ctx, cancel := context.WithTimeout(context.Background(), 700*time.Millisecond)
	defer cancel()
	start := time.Now()
	_, err := ixKnot(ctx, os.Args[0], "figure-eight")
	if err == nil || !strings.Contains(err.Error(), "IX unavailable: no answer") {
		t.Fatalf("got %v", err)
	}
	if waited := time.Since(start); waited > 5*time.Second {
		t.Fatalf("waited %v", waited)
	}
}
