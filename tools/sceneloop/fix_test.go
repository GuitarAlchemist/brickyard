package main

import (
	"image"
	"image/color"
	"image/png"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
)

// The worker edits files only: no shell, no MCP server, no session kept, on the plan.
func TestWorkerHasOnlyTheFileTools(t *testing.T) {
	args := Worker("p", "opus", "run")
	i := slices.Index(args, "--allowedTools")
	if i < 0 || args[i+1] != "Read,Edit,Write,Glob,Grep" || strings.Contains(args[i+1], "Bash") {
		t.Fatalf("the worker's tools: %v", args)
	}

	if !slices.Contains(args, "--strict-mcp-config") || !slices.Contains(args, "--no-session-persistence") || !slices.Contains(args, "dontAsk") {
		t.Fatalf("the worker may do more: %v", args)
	}

	p := WorkerPrompt(Finding{Title: "flat wall", View: "hall"}, Views[0], "before.png", []string{"the build failed: x"})
	if !strings.Contains(p, "flat wall") || !strings.Contains(p, "before.png") || !strings.Contains(p, "the build failed: x") {
		t.Fatalf("the prompt: %s", p)
	}
}

// Only the scene and the gallery, never the lock file or Google's tiles, and not too much.
func TestScope(t *testing.T) {
	ok := Changes("3\t1\tscene/Program.cs\n2\t0\tgallery/Blocks.fs\n", "")
	if r := Scope(ok); len(r) != 0 {
		t.Fatalf("refused: %v", r)
	}

	for _, bad := range []string{"1\t0\tbrain/main.go\n", "1\t0\tscene/packages.lock.json\n", "1\t1\tscene/GoogleStreets.cs\n", "500\t0\tscene/Program.cs\n"} {
		if r := Scope(Changes(bad, "")); len(r) == 0 {
			t.Fatalf("let through: %q", bad)
		}
	}

	if r := Scope(Changes("", "")); len(r) == 0 {
		t.Fatal("an empty change passed")
	}

	if c := Changes("", "remote/x.js\n"); len(Scope(c)) == 0 {
		t.Fatal("a new file outside the scene passed")
	}
}

// The blind verdict counts for the change only when it chose the picture that was the change.
func TestPrefers(t *testing.T) {
	if !Prefers(Blind{Better: "a"}, true) || Prefers(Blind{Better: "a"}, false) || !Prefers(Blind{Better: "b"}, false) || Prefers(Blind{Better: "same"}, true) {
		t.Fatal("prefers")
	}

	b, err := BlindAnswer([]byte(`{"is_error":false,"structured_output":{"better":"b","fixed":true,"worse":"","why":"w"}}`))
	if err != nil || b.Better != "b" || !b.Fixed {
		t.Fatalf("%v %v", b, err)
	}
}

func TestSlower(t *testing.T) {
	before := []Shot{{View: Views[0], FPS: 60}, {View: Views[1], FPS: 0}}
	after := []Shot{{View: Views[0], FPS: 50}, {View: Views[1], FPS: 10}}
	if s := Slower(before, after); len(s) != 1 || !strings.Contains(s[0], "hall") {
		t.Fatalf("%v", s)
	}
}

func TestDifference(t *testing.T) {
	dir := t.TempDir()
	save := func(name string, c uint8) string {
		img := image.NewRGBA(image.Rect(0, 0, 8, 8))
		for y := range 8 {
			for x := range 8 {
				img.Set(x, y, color.RGBA{c, c, c, 255})
			}
		}

		p := filepath.Join(dir, name)
		f, _ := os.Create(p)
		_ = png.Encode(f, img)
		f.Close()
		return p
	}

	a, b := save("a.png", 10), save("b.png", 30)
	if d := Difference(a, a); d != 0 {
		t.Fatalf("same %v", d)
	}

	if d := Difference(a, b); d != 20 {
		t.Fatalf("different %v", d)
	}

	if d := Difference(a, filepath.Join(dir, "none.png")); d != 255 {
		t.Fatalf("missing %v", d)
	}
}

func TestCommitMessageAndSlug(t *testing.T) {
	f := Finding{View: "gym", Title: "Lamps light nothing!", Severity: 4, Confidence: 3}
	if s := Slug(f); s != "gym-lamps-light-nothing" {
		t.Fatalf("slug %q", s)
	}

	m := CommitMessage(f, "made them glow", []string{"gym: the change"})
	if !strings.HasPrefix(m, "sceneloop: Lamps light nothing! (gym)") || !strings.Contains(m, "Claude-Session:") || !strings.Contains(m, "gym: the change") {
		t.Fatalf("%s", m)
	}
}
