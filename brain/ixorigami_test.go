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

// The origami samples and IX's own reports on them, recorded from ix-mcp by TestRecordIXOrigami with DELTA_IX_MCP set.
var origamiSamples = []struct{ file, input, report string }{
	{"testdata/origami/half-then-quarter.origami", "sequence", "testdata/ix-origami-half-then-quarter.json"},
	{"testdata/origami/wrong-expectation.origami", "sequence", "testdata/ix-origami-wrong-expectation.json"},
	{"testdata/origami/flipped-crease.fold", "fold", "testdata/ix-origami-flipped-crease.json"},
	{"testdata/origami/crane.fold", "fold", "testdata/ix-origami-crane.json"},           // Rabbit Ear's layout, see NOTICE.md
	{"testdata/origami/miura-4x3.fold", "fold", "testdata/ix-origami-miura-4x3.json"},   // tools/miura -m 4 -n 3
	{"testdata/origami/miura-12x8.fold", "fold", "testdata/ix-origami-miura-12x8.json"}, // tools/miura, its defaults
}

func TestRecordIXOrigami(t *testing.T) {
	exe := os.Getenv("DELTA_IX_MCP")
	if exe == "" {
		t.Skip("DELTA_IX_MCP names ix-mcp to record IX's reports")
	}
	for _, s := range origamiSamples {
		given, err := os.ReadFile(s.file)
		if err != nil {
			t.Fatal(err)
		}
		arguments := map[string]any{"sequence": string(given)}
		if s.input == "fold" {
			arguments = map[string]any{"fold": json.RawMessage(given)}
		}
		ctx, cancel := context.WithTimeout(context.Background(), ixCallSeconds*time.Second)
		answer, err := ixCall(ctx, exe, "ix_origami_check", arguments)
		cancel()
		if err != nil {
			t.Fatal(err)
		}
		var pretty bytes.Buffer
		if err := json.Indent(&pretty, answer, "", " "); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(s.report, append(pretty.Bytes(), '\n'), 0o644); err != nil {
			t.Fatal(err)
		}
	}
}

func origamiOf(t *testing.T, i int) ([]byte, []byte) {
	t.Helper()
	given, err := os.ReadFile(origamiSamples[i].file)
	if err != nil {
		t.Fatal(err)
	}
	report, err := os.ReadFile(origamiSamples[i].report)
	if err != nil {
		t.Fatal(err)
	}
	return given, report
}

var origamiTime = time.Date(2026, 11, 14, 7, 0, 0, 0, time.UTC)

func TestTheTableKeepsIXsVerdicts(t *testing.T) {
	docs := make([]origamiDoc, len(origamiSamples))
	for i, s := range origamiSamples {
		given, report := origamiOf(t, i)
		doc, err := origamiFromIX(s.input, given, report, "file-name", origamiTime)
		if err != nil {
			t.Fatalf("%s: %v", s.file, err)
		}
		docs[i] = doc
	}
	half, wrong, flipped, crane := docs[0], docs[1], docs[2], docs[3]
	// Rabbit Ear's crane holds its folded form at the top level and its crease pattern in the frame: the pattern is the
	// unit square, the folded form the narrower shape, and the 838 orders IX checked come with it.
	low, high := [2]float64{9, 9}, [2]float64{-9, -9}
	for _, v := range crane.Folded {
		low, high = [2]float64{min(low[0], v[0]), min(low[1], v[1])}, [2]float64{max(high[0], v[0]), max(high[1], v[1])}
	}
	square := 0
	for _, v := range crane.Pattern.Vertices {
		if (v[0] == 0 || v[0] == 1) && (v[1] == 0 || v[1] == 1) {
			square++
		}
	}
	if crane.Name != "crane" || !*crane.OK || len(crane.Findings) != 0 || crane.MaxLayers != 28 || len(crane.Pattern.Faces) != 59 ||
		len(crane.Orders) != 838 || square != 4 || low[1] < 0.08 || high[1] > 0.92 {
		t.Fatalf("crane: ok %v, %d findings, %d layers, %d faces, %d orders, %d corners of the square, folded within %v..%v",
			*crane.OK, len(crane.Findings), crane.MaxLayers, len(crane.Pattern.Faces), len(crane.Orders), square, low, high)
	}
	if len(half.Orders) == 0 || half.Orders[0][2] == 0 {
		t.Fatalf("half-then-quarter: the replayed fold's orders are kept, %v", half.Orders)
	}
	if half.Name != "half-then-quarter" || !*half.OK || half.Holds == nil || !*half.Holds || len(half.Findings) != 0 || half.MaxLayers != 4 ||
		len(half.Pattern.Edges) != 12 || len(half.Folded) != len(half.Pattern.Vertices) || half.Input != "sequence" {
		t.Fatalf("half-then-quarter: %+v", half)
	}
	if !*wrong.OK || wrong.Holds == nil || *wrong.Holds || len(wrong.Findings) != 1 || wrong.Findings[0].Rule != "expectation" ||
		wrong.Findings[0].Value != 4 || wrong.Findings[0].Detail != "ply 3 → 2" || len(wrong.Findings[0].IDs) != 0 {
		t.Fatalf("wrong-expectation: %+v", wrong.Findings)
	}
	if *flipped.OK || flipped.Holds != nil || flipped.Name != "flipped-crease" || strings.Join(flipped.RejectedBy, ",") != "adjacency" || len(flipped.Findings) != 2 {
		t.Fatalf("flipped-crease: %+v", flipped)
	}
	adj, mae := flipped.Findings[0], flipped.Findings[1]
	if adj.Rule != "adjacency" || adj.Where != "edge" || len(adj.IDs) != 1 || adj.IDs[0] != 7 || flipped.Pattern.Assignment[7] != "V" ||
		mae.Rule != "maekawa" || mae.Where != "vertex" || len(mae.IDs) != 1 || mae.IDs[0] != 3 {
		t.Fatalf("flipped-crease findings: %+v", flipped.Findings)
	}
}

// The documents the office's module is tested on are the brain's, for IX's recorded reports.
// DELTA_UPDATE_FIXTURES=1 writes them again.
func TestTheTableModuleIsTestedOnTheBrainsDocuments(t *testing.T) {
	for i, s := range origamiSamples {
		given, report := origamiOf(t, i)
		doc, err := origamiFromIX(s.input, given, report, strings.TrimSuffix(filepath.Base(s.file), filepath.Ext(s.file)), origamiTime)
		if err != nil {
			t.Fatal(err)
		}
		if doc.Pose, err = origamiPoseFor(s.file, doc); err != nil {
			t.Fatal(err)
		}
		if doc.Motion, err = origamiMotionFor(s.file, doc); err != nil {
			t.Fatal(err)
		}
		body, _ := json.Marshal(doc)
		body = append(body, '\n')
		fixture := filepath.Join("..", "widget-tests", "testdata", "ix-origami-"+strings.TrimSuffix(filepath.Base(s.file), filepath.Ext(s.file))+".json")
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
}

// The crane's pose is read from beside its fold; a pose that names what is not there, or turns too far, is refused,
// and a fold without one has none.
func TestTheTablePosesOnlyWhatIsThere(t *testing.T) {
	given, report := origamiOf(t, 3)
	doc, err := origamiFromIX("fold", given, report, "crane", origamiTime)
	if err != nil {
		t.Fatal(err)
	}
	pose, err := origamiPoseFor(origamiSamples[3].file, doc)
	if err != nil || pose == nil || pose.Ridge != [2]int{15, 9} || len(pose.Parts) != 3 || !pose.Parts[2].Spread || pose.Parts[0].To != 80 {
		t.Fatalf("crane's pose: %+v, %v", pose, err)
	}
	if none, err := origamiPoseFor(origamiSamples[2].file, doc); none != nil || err != nil {
		t.Fatalf("a fold without a pose file: %+v, %v", none, err)
	}
	dir := t.TempDir()
	for _, c := range []struct{ name, pose, want string }{
		{"a ridge vertex beyond", `{"ridge":[15,99],"parts":[{"faces":[0],"to":80}]}`, "not two apart"},
		{"a ridge of one point", `{"ridge":[9,12],"parts":[{"faces":[0],"to":80}]}`, "not two apart"},
		{"a face beyond", `{"ridge":[15,9],"parts":[{"faces":[59],"to":80}]}`, "names face 59"},
		{"a face in two parts", `{"ridge":[15,9],"parts":[{"faces":[0],"to":80},{"faces":[0],"to":80}]}`, "already in a part"},
		{"too far", `{"ridge":[15,9],"parts":[{"faces":[0],"to":200}]}`, "not 0 to 180"},
		{"no parts", `{"ridge":[15,9],"parts":[]}`, "not 1 to 8"},
		{"an unknown field", `{"ridge":[15,9],"parts":[{"faces":[0],"to":80}],"simulated":true}`, "not one"},
	} {
		file := filepath.Join(dir, "crane.fold")
		if err := os.WriteFile(filepath.Join(dir, "crane.pose.json"), []byte(c.pose), 0o644); err != nil {
			t.Fatal(err)
		}
		if _, err := origamiPoseFor(file, doc); err == nil || !strings.Contains(err.Error(), c.want) {
			t.Errorf("%s: got %v, want %q", c.name, err, c.want)
		}
	}
	flat := doc
	flat.Orders = nil
	if _, err := origamiPoseFor(origamiSamples[3].file, flat); err == nil {
		t.Error("a pose without the orders to layer the faces is refused")
	}
}

// The Miura-ori that tools/miura writes is valid according to IX, and its motion is carried when every face keeps its
// shape in every frame; a frame that stretches a face, or that does not hold the pattern's vertices, is refused.
func TestTheTableTakesOnlyARigidMotion(t *testing.T) {
	given, report := origamiOf(t, 4)
	doc, err := origamiFromIX("fold", given, report, "miura", origamiTime)
	if err != nil {
		t.Fatal(err)
	}
	if !*doc.OK || len(doc.Findings) != 0 || doc.MaxLayers != 12 || len(doc.Pattern.Faces) != 12 || len(doc.Orders) != 66 || doc.Name != "miura-ori 4x3" {
		t.Fatalf("the Miura-ori: ok %v, %d findings, %d layers, %d faces, %d orders, %q", *doc.OK, len(doc.Findings), doc.MaxLayers, len(doc.Pattern.Faces), len(doc.Orders), doc.Name)
	}
	motion, err := origamiMotionFor(origamiSamples[4].file, doc)
	if err != nil || motion == nil || len(motion.Frames) != 25 || len(motion.Frames[4]) != 20 {
		t.Fatalf("its motion: %+v, %v", motion, err)
	}
	if flat := motion.Frames[0]; flat[19][0] != doc.Pattern.Vertices[19][0] || flat[19][2] != 0 {
		t.Errorf("the motion starts from the flat sheet: vertex 19 at %v, in the pattern %v", flat[19], doc.Pattern.Vertices[19])
	}
	if none, err := origamiMotionFor(origamiSamples[3].file, doc); none != nil || err != nil {
		t.Fatalf("a fold without a motion file: %+v, %v", none, err)
	}
	raw, err := os.ReadFile(strings.TrimSuffix(origamiSamples[4].file, ".fold") + ".motion.json")
	if err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()
	for _, c := range []struct {
		name, want string
		damage     func(m map[string]any)
	}{
		{"a face stretched", "does not keep its shape", func(m map[string]any) {
			v := m["frames"].([]any)[2].([]any)[6].([]any)
			v[2] = v[2].(float64) + 0.01
		}},
		{"a vertex missing", "has 19 vertices", func(m map[string]any) {
			f := m["frames"].([]any)
			f[3] = f[3].([]any)[:19]
		}},
		{"one frame", "not 2 to 64", func(m map[string]any) { m["frames"] = m["frames"].([]any)[:1] }},
		{"a vertex far away", "beyond", func(m map[string]any) { m["frames"].([]any)[1].([]any)[0].([]any)[0] = 99.0 }},
		{"an unknown field", "not one", func(m map[string]any) { m["simulated"] = true }},
	} {
		var m map[string]any
		if err := json.Unmarshal(raw, &m); err != nil {
			t.Fatal(err)
		}
		c.damage(m)
		body, _ := json.Marshal(m)
		if err := os.WriteFile(filepath.Join(dir, "miura.motion.json"), body, 0o644); err != nil {
			t.Fatal(err)
		}
		if _, err := origamiMotionFor(filepath.Join(dir, "miura.fold"), doc); err == nil || !strings.Contains(err.Error(), c.want) {
			t.Errorf("%s: got %v, want %q", c.name, err, c.want)
		}
	}
}

// Each damage to IX's report, or to the FOLD given, is refused with why.
func TestTheTableRefusesBadReports(t *testing.T) {
	cases := []struct {
		name, want string
		sample     int
		damage     func(report, fold map[string]any)
	}{
		{"no verdict", "no verdict", 2, func(r, _ map[string]any) { delete(r, "ok") }},
		{"an edge IX names beyond the pattern", "names edge 99", 2, func(r, _ map[string]any) {
			r["layers"].(map[string]any)["adjacency"].(map[string]any)["violations"] = []any{99.0}
		}},
		{"a vertex IX names beyond the pattern", "names vertex 40", 2, func(r, _ map[string]any) {
			r["local_theorems"].(map[string]any)["maekawa_failing"] = []any{40.0}
		}},
		{"a vertex far away", "beyond", 2, func(_, f map[string]any) { f["vertices_coords"].([]any)[2] = []any{1.0, 99.0} }},
		{"a vertex in 3D", "3 coordinates", 2, func(_, f map[string]any) { f["vertices_coords"].([]any)[2] = []any{1.0, 0.5, 0.0} }},
		{"an unknown assignment", "not M, V", 2, func(_, f map[string]any) { f["edges_assignment"].([]any)[1] = "X" }},
		{"an edge to nowhere", "joins no two", 2, func(_, f map[string]any) { f["edges_vertices"].([]any)[1] = []any{0.0, 30.0} }},
		{"assignments missing", "assignments", 2, func(_, f map[string]any) {
			f["edges_assignment"] = f["edges_assignment"].([]any)[:3]
		}},
		{"a face to nowhere", "face 1 names vertex 30", 2, func(_, f map[string]any) { f["faces_vertices"].([]any)[1] = []any{0.0, 1.0, 30.0} }},
		{"a folded state of other vertices", "folded state has 8", 2, func(_, f map[string]any) {
			frame := f["file_frames"].([]any)[0].(map[string]any)
			frame["vertices_coords"] = frame["vertices_coords"].([]any)[:8]
		}},
		{"too many edges", "edges with", 2, func(_, f map[string]any) {
			e, a := f["edges_vertices"].([]any), f["edges_assignment"].([]any)
			for len(e) <= origamiMaxEdges {
				e, a = append(e, e...), append(a, a...)
			}
			f["edges_vertices"], f["edges_assignment"] = e, a
		}},
		{"a control character in the name", "control", 2, func(_, f map[string]any) { f["file_title"] = "flipped\ncrease" }},
		{"a sequence without its replay", "no replayed sequence", 0, func(r, _ map[string]any) { delete(r, "sequence") }},
		{"an order of a face beyond", "face order 3 is [0, 59, 1]", 3, func(_, f map[string]any) { f["faceOrders"].([]any)[3] = []any{0.0, 59.0, 1.0} }},
		{"an order of a face with itself", "face order 0 is [2, 2, 1]", 3, func(_, f map[string]any) { f["faceOrders"].([]any)[0] = []any{2.0, 2.0, 1.0} }},
		{"an order that is no side", "face order 1 is [0, 2, 2]", 3, func(_, f map[string]any) { f["faceOrders"].([]any)[1] = []any{0.0, 2.0, 2.0} }},
		{"an order in fractions", "whole numbers", 3, func(_, f map[string]any) { f["faceOrders"].([]any)[1] = []any{0.0, 2.5, 1.0} }},
		{"too many orders", "more than 4096", 3, func(_, f map[string]any) {
			o := f["faceOrders"].([]any)
			for len(o) <= origamiMaxOrders {
				o = append(o, o...)
			}
			f["faceOrders"] = o
		}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			given, report := origamiOf(t, c.sample)
			var r, f map[string]any
			if json.Unmarshal(report, &r) != nil {
				t.Fatal("report")
			}
			if origamiSamples[c.sample].input == "fold" && json.Unmarshal(given, &f) != nil {
				t.Fatal("fold")
			}
			c.damage(r, f)
			report, _ = json.Marshal(r)
			if f != nil {
				given, _ = json.Marshal(f)
			}
			_, err := origamiFromIX(origamiSamples[c.sample].input, given, report, "x", origamiTime)
			if err == nil || !strings.Contains(err.Error(), c.want) {
				t.Fatalf("got %v, want an error with %q", err, c.want)
			}
		})
	}
}

func TestTheTableAsksIXOverMCP(t *testing.T) {
	fakeIXAs(t, "answer")
	abs, _ := filepath.Abs(origamiSamples[2].report)
	t.Setenv("BRAIN_FAKE_IX_ORIGAMI", abs)
	out := filepath.Join(t.TempDir(), "ix-origami.json")
	var stdout, stderr bytes.Buffer
	if code := runIXOrigami(origamiSamples[2].file, os.Args[0], out, &stdout, &stderr); code != 0 {
		t.Fatalf("exit %d: %s", code, stderr.String())
	}
	var doc origamiDoc
	body, _ := os.ReadFile(out)
	if json.Unmarshal(body, &doc) != nil || doc.OK == nil || *doc.OK || doc.Error != "" || !strings.Contains(stdout.String(), "rejected by adjacency") {
		t.Fatalf("%s / %s", body, stdout.String())
	}

	fakeIXAs(t, "exit") // IX gone: the table says so, in place of the verdict it held
	if code := runIXOrigami(origamiSamples[0].file, os.Args[0], out, &stdout, &stderr); code != 1 {
		t.Fatalf("exit %d", code)
	}
	body, _ = os.ReadFile(out)
	doc = origamiDoc{}
	if json.Unmarshal(body, &doc) != nil || doc.Pattern != nil || !strings.HasPrefix(doc.Error, "IX unavailable") {
		t.Fatalf("%s", body)
	}

	var bad bytes.Buffer // a file that is neither is refused before IX is asked
	notFold := filepath.Join(t.TempDir(), "notes.txt")
	_ = os.WriteFile(notFold, []byte("fold it in half"), 0o644)
	if code := runIXOrigami(notFold, os.Args[0], out, &stdout, &bad); code != 2 || !strings.Contains(bad.String(), "neither") {
		t.Fatalf("exit %d: %s", code, bad.String())
	}
}
