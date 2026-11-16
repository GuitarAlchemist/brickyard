package main

import (
	"bytes"
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// The crane folded step by step (tools/crane), and IX's reports on each step's FOLD with the census, recorded from
// ix-mcp by TestRecordIXOrigamiSteps with DELTA_IX_MCP set.
const (
	craneSteps        = "testdata/origami/crane.steps.json"
	craneStepsReports = "testdata/ix-origami-crane-steps.json"
)

func craneManifest(t *testing.T) origamiStepsManifest {
	t.Helper()
	body, err := os.ReadFile(craneSteps)
	if err != nil {
		t.Fatal(err)
	}
	var m origamiStepsManifest
	if err := json.Unmarshal(body, &m); err != nil {
		t.Fatal(err)
	}
	return m
}

func TestRecordIXOrigamiSteps(t *testing.T) {
	exe := os.Getenv("DELTA_IX_MCP")
	if exe == "" {
		t.Skip("DELTA_IX_MCP names ix-mcp to record IX's reports")
	}
	var reports []json.RawMessage
	for _, s := range craneManifest(t).Steps {
		ctx, cancel := context.WithTimeout(context.Background(), ixCallSeconds*time.Second)
		answer, err := ixCall(ctx, exe, "ix_origami_check", map[string]any{"fold": s.Fold, "census": true})
		cancel()
		if err != nil {
			t.Fatal(err)
		}
		var compact bytes.Buffer
		if err := json.Compact(&compact, answer); err != nil {
			t.Fatal(err)
		}
		reports = append(reports, compact.Bytes())
	}
	var out bytes.Buffer
	out.WriteString("[\n")
	for i, r := range reports {
		out.Write(r)
		if i < len(reports)-1 {
			out.WriteString(",")
		}
		out.WriteString("\n")
	}
	out.WriteString("]\n")
	if err := os.WriteFile(craneStepsReports, out.Bytes(), 0o644); err != nil {
		t.Fatal(err)
	}
}

// recordedIX answers each step's FOLD with IX's recorded report on it, in turn; edit may change a report first.
func recordedIX(t *testing.T, edit func(i int, report map[string]any)) func(json.RawMessage) ([]byte, error) {
	t.Helper()
	body, err := os.ReadFile(craneStepsReports)
	if err != nil {
		t.Fatal(err)
	}
	var reports []json.RawMessage
	if err := json.Unmarshal(body, &reports); err != nil {
		t.Fatal(err)
	}
	i := 0
	return func(json.RawMessage) ([]byte, error) {
		r := reports[i]
		if edit != nil {
			var m map[string]any
			_ = json.Unmarshal(r, &m)
			edit(i, m)
			r, _ = json.Marshal(m)
		}
		i++
		return r, nil
	}
}

func craneStepsDoc(t *testing.T, m origamiStepsManifest, edit func(int, map[string]any)) (origamiDoc, error) {
	t.Helper()
	body, err := json.Marshal(m)
	if err != nil {
		t.Fatal(err)
	}
	return origamiStepsDoc(body, recordedIX(t, edit), origamiTime)
}

// IX checks every step of the crane, each stated layer order forced by its rules (the census rejects every pair
// flipped alone); the motion rests exactly at each step's folded state, and bends the paper only at the petal and
// reverse folds, by what it says.
func TestTheTableChecksEveryStepOfTheCrane(t *testing.T) {
	doc, err := craneStepsDoc(t, craneManifest(t), nil)
	if err != nil {
		t.Fatal(err)
	}
	if !*doc.OK || len(doc.Steps) != 21 || doc.MaxLayers != 28 || doc.Input != "steps" || doc.Name != "crane" ||
		len(doc.Pattern.Faces) != 75 || len(doc.Motion.Frames) < 200 {
		t.Fatalf("ok %v, %d steps, %d layers, input %q, %d faces, %d frames", *doc.OK, len(doc.Steps), doc.MaxLayers, doc.Input,
			len(doc.Pattern.Faces), len(doc.Motion.Frames))
	}
	last := doc.Steps[20]
	if last.Orders != 838 || last.Census != [2]int{838, 838} || last.Faces != 59 || last.Vertices == 0 || last.Fr != "Rabattre l'aile" {
		t.Fatalf("the wing: %+v", last)
	}
	bends := map[int]bool{11: true, 14: true, 17: true, 18: true, 19: true}
	for i, s := range doc.Steps {
		if !s.OK || s.Census[0] != s.Census[1] || s.Orders != s.Census[1] || len(s.Findings) != 0 || len(s.Layers) != 75 {
			t.Fatalf("step %d (%s): %+v", i, s.En, s)
		}
		if i > 0 && s.At <= doc.Steps[i-1].At {
			t.Fatalf("step %d rests at frame %d, before step %d's %d", i, s.At, i-1, doc.Steps[i-1].At)
		}
		// Rounded to four places, a rigid move still shows a few ten-thousandths; the collapse bends 2.5e-4.
		if bends[i] != (s.Flex > 5e-4) {
			t.Fatalf("step %d (%s) bends the paper %.4f", i, s.En, s.Flex)
		}
	}
	if doc.Steps[17].Flex < 0.01 || doc.Steps[17].Flex > origamiMaxFlex {
		t.Fatalf("the tail's reverse fold bends %.4f", doc.Steps[17].Flex)
	}
}

func TestTheStepsRefuseWhatIXDidNotCheck(t *testing.T) {
	cases := []struct {
		name string
		edit func(m *origamiStepsManifest)
		want string
	}{
		{"a rest off the step's folded state", func(m *origamiStepsManifest) {
			m.Frames[m.Steps[9].At][10][0] += 0.01
		}, "the motion rests with vertex 10"},
		{"a frame that stretches the paper", func(m *origamiStepsManifest) {
			m.Frames[m.Steps[17].At-3][20][0] += 0.3
			m.Frames[m.Steps[17].At-3][20][1] += 0.3
		}, "more than the paper bends"},
		{"a last step short of the last frame", func(m *origamiStepsManifest) {
			m.Steps[20].At--
		}, "must rest at the first frame"},
		{"steps out of order", func(m *origamiStepsManifest) {
			m.Steps[5].At = m.Steps[4].At
		}, "must rest at the first frame"},
		{"a layer for each face", func(m *origamiStepsManifest) {
			m.Steps[3].Layers = m.Steps[3].Layers[1:]
		}, "gives 74 layers for 75 faces"},
		{"a step's words", func(m *origamiStepsManifest) {
			m.Steps[2].Fr = "Déplier\nmaintenant"
		}, "control character"},
		{"a step that folds what the sheet does not hold", func(m *origamiStepsManifest) {
			var fold map[string]any
			_ = json.Unmarshal(m.Steps[1].Fold, &fold)
			fold["vertices_coords"].([]any)[0] = []any{0.123, 0}
			m.Steps[1].Fold, _ = json.Marshal(fold)
		}, "none of the sheet's"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			m := craneManifest(t)
			c.edit(&m)
			if _, err := craneStepsDoc(t, m, nil); err == nil || !strings.Contains(err.Error(), c.want) {
				t.Fatalf("got %v, want an error with %q", err, c.want)
			}
		})
	}
	// IX rejecting a step is a verdict, not a refusal: the step says so, and the whole is not ok.
	doc, err := craneStepsDoc(t, craneManifest(t), func(i int, r map[string]any) {
		if i == 9 {
			r["ok"], r["rejected_by"] = false, []any{"taco_taco"}
		}
	})
	if err != nil || *doc.OK || doc.Steps[9].OK || !doc.Steps[8].OK || !doc.Steps[10].OK {
		t.Fatalf("%v: ok %v, step 9 %+v", err, doc.OK, doc.Steps[9])
	}
}

// The document the office's module plays the crane from is the brain's. DELTA_UPDATE_FIXTURES=1 writes it again.
func TestTheStepsModuleIsTestedOnTheBrainsDocument(t *testing.T) {
	doc, err := craneStepsDoc(t, craneManifest(t), nil)
	if err != nil {
		t.Fatal(err)
	}
	body, _ := json.Marshal(doc)
	body = append(body, '\n')
	fixture := filepath.Join("..", "widget-tests", "testdata", "ix-origami-crane-steps.json")
	if os.Getenv("DELTA_UPDATE_FIXTURES") == "1" {
		if err := os.WriteFile(fixture, body, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	kept, err := os.ReadFile(fixture)
	kept = bytes.ReplaceAll(kept, []byte("\r\n"), []byte("\n"))
	if err != nil || !bytes.Equal(kept, body) {
		t.Fatalf("%s is not the brain's document (%v); DELTA_UPDATE_FIXTURES=1 writes it", fixture, err)
	}
}

func TestTheTablePlaysTheStepsItIsTold(t *testing.T) {
	out := filepath.Join(t.TempDir(), "delta", "ix-origami-play.json")
	for _, c := range []struct{ command, want string }{
		{"next", `{"format":"delta-origami-play","v":1,"go":"next","step":0}`},
		{"auto", `{"format":"delta-origami-play","v":1,"go":"auto","step":0}`},
		{"7", `{"format":"delta-origami-play","v":1,"go":"step","step":7}`},
		{"0", `{"format":"delta-origami-play","v":1,"go":"step","step":0}`}, // written: the module refuses a step without its number
	} {
		var stdout, stderr bytes.Buffer
		if code := runOrigamiPlay(c.command, out, &stdout, &stderr); code != 0 {
			t.Fatalf("%s: exit %d: %s", c.command, code, stderr.String())
		}
		body, _ := os.ReadFile(out)
		if strings.TrimSpace(string(body)) != c.want {
			t.Fatalf("%s: %s, want %s", c.command, body, c.want)
		}
	}
	for _, bad := range []string{"-1", "64", "suivante", "3.5"} {
		var stdout, stderr bytes.Buffer
		if code := runOrigamiPlay(bad, out, &stdout, &stderr); code != 2 {
			t.Fatalf("%s: exit %d", bad, code)
		}
	}
}

func TestTheStepsAreAskedOfIXOverMCP(t *testing.T) {
	fakeIXAs(t, "answer")
	abs, _ := filepath.Abs("testdata/ix-origami-crane.json") // every step answered as the crane: ok, which is all this needs
	t.Setenv("BRAIN_FAKE_IX_ORIGAMI", abs)
	out := filepath.Join(t.TempDir(), "ix-origami.json")
	var stdout, stderr bytes.Buffer
	if code := runIXOrigami(craneSteps, os.Args[0], out, &stdout, &stderr); code != 0 {
		t.Fatalf("exit %d: %s", code, stderr.String())
	}
	var doc origamiDoc
	body, _ := os.ReadFile(out)
	if json.Unmarshal(body, &doc) != nil || doc.OK == nil || !*doc.OK || len(doc.Steps) != 21 || doc.Error != "" ||
		!strings.Contains(stdout.String(), "21 steps (every step ok)") {
		t.Fatalf("%.300s / %s", body, stdout.String())
	}

	fakeIXAs(t, "exit") // IX gone mid-way: no step is shown as checked
	if code := runIXOrigami(craneSteps, os.Args[0], out, &stdout, &stderr); code != 1 {
		t.Fatalf("exit %d", code)
	}
	body, _ = os.ReadFile(out)
	doc = origamiDoc{}
	if json.Unmarshal(body, &doc) != nil || doc.Steps != nil || !strings.Contains(doc.Error, "IX unavailable") {
		t.Fatalf("%s", body)
	}
}
