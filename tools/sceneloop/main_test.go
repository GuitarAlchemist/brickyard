package main

import (
	"slices"
	"strings"
	"testing"
)

// A hidden picture never touches the live window's port nor asks Google, and runs no tour.
func TestHiddenStaysOffTheLiveWindowAndGoogle(t *testing.T) {
	for _, v := range Views {
		args := Hidden(v, "out", 24)
		i := slices.Index(args, "--listen")
		if i < 0 || args[i+1] != "8899" || slices.Contains(args, "8805") {
			t.Fatalf("%s: listens elsewhere than 8899: %v", v.Name, args)
		}

		if !slices.Contains(args, "--osm") || !slices.Contains(args, "--hidden") || slices.Contains(args, "--google-tiles") {
			t.Fatalf("%s: not hidden, or may ask Google: %v", v.Name, args)
		}
	}
}

// The judge runs on the plan: no API key reaches it, and it may only read.
func TestJudgeOnThePlanAndReadOnly(t *testing.T) {
	env := PlanOnly([]string{"PATH=x", "ANTHROPIC_API_KEY=k", "anthropic_auth_token=t", "HOME=h"})
	if len(env) != 2 || env[0] != "PATH=x" || env[1] != "HOME=h" {
		t.Fatalf("an API key passed: %v", env)
	}

	args := Judge(Shot{View: Views[0], Picture: "hall/peek-02.png"}, "sonnet")
	i := slices.Index(args, "--allowedTools")
	if i < 0 || args[i+1] != "Read" || !slices.Contains(args, "--strict-mcp-config") || !slices.Contains(args, "--no-session-persistence") {
		t.Fatalf("the judge may do more than read: %v", args)
	}

	if !strings.Contains(Prompt(Shot{View: Views[0], Picture: "hall/peek-02.png"}), "hall/peek-02.png") {
		t.Fatal("the prompt does not name the picture")
	}
}

func TestFPS(t *testing.T) {
	if f := FPS("x\nSCENE frames 482 in 13.53 s (36 fps); frame ms p50 18.640\n"); f != 36 {
		t.Fatalf("fps %v", f)
	}

	if FPS("nothing") != 0 {
		t.Fatal("fps from nothing")
	}
}

// The judge's answer: from the structured output, from a fenced result, and refused when it failed.
func TestAnswer(t *testing.T) {
	structured := `{"is_error":false,"result":"done","structured_output":{"findings":[{"title":"moire","where":"floor","why":"w","severity":4,"fix":"f","confidence":3}]}}`
	f, err := Answer([]byte(structured))
	if err != nil || len(f) != 1 || f[0].Severity != 4 {
		t.Fatalf("structured: %v %v", f, err)
	}

	fenced := "{\"is_error\":false,\"result\":\"```json\\n{\\\"findings\\\":[]}\\n```\"}"
	if f, err := Answer([]byte(fenced)); err != nil || len(f) != 0 {
		t.Fatalf("fenced: %v %v", f, err)
	}

	if _, err := Answer([]byte(`{"is_error":true,"result":"rate limited"}`)); err == nil {
		t.Fatal("a failed judge gave findings")
	}
}

func TestPick(t *testing.T) {
	v, err := pick("gym, rope")
	if err != nil || len(v) != 2 || v[0].Name != "gym" || v[1].Name != "rope" {
		t.Fatalf("%v %v", v, err)
	}

	if _, err := pick("nowhere"); err == nil {
		t.Fatal("an unknown view was picked")
	}
}
