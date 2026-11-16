package main

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// IX's own answers for the 21 drawings, recorded from ix-mcp by TestRecordIXMarins with DELTA_IX_MCP set: one file a
// knot, ix_knot's answer with its geometry.
const ixMarinsDir = "testdata/ix-marins"

func TestRecordIXMarins(t *testing.T) {
	exe := os.Getenv("DELTA_IX_MCP")
	if exe == "" {
		t.Skip("DELTA_IX_MCP names ix-mcp to record IX's answers")
	}
	if err := os.MkdirAll(ixMarinsDir, 0o755); err != nil {
		t.Fatal(err)
	}
	for _, e := range marinsCatalogue {
		d, err := marinsDrawingOf(e.ID)
		if err != nil {
			t.Fatal(err)
		}
		ctx, cancel := context.WithTimeout(context.Background(), ixCallSeconds*time.Second)
		answer, err := ixCall(ctx, exe, "ix_knot", ixMarinsArguments(d))
		cancel()
		if err != nil {
			t.Fatalf("%s: %v", e.ID, err)
		}
		if err := os.WriteFile(filepath.Join(ixMarinsDir, e.ID+".json"), append(answer, '\n'), 0o644); err != nil {
			t.Fatal(err)
		}
	}
}

func recordedMarins(d marinsDrawing) ([]byte, error) {
	return os.ReadFile(filepath.Join(ixMarinsDir, d.ID+".json"))
}

// Every drawing of the deck is there, once, in its family, with its posts among its ropes.
func TestTheCatalogueIsTheDecksTwentyOneKnots(t *testing.T) {
	families := map[string]int{}
	seen := map[string]bool{}
	for _, e := range marinsCatalogue {
		if seen[e.ID] {
			t.Errorf("%s twice", e.ID)
		}
		seen[e.ID] = true
		families[e.Family]++
		d, err := marinsDrawingOf(e.ID)
		if err != nil {
			t.Fatal(err)
		}
		for _, p := range d.Posts {
			if p < 0 || p >= len(d.Ropes) {
				t.Errorf("%s: post %d of %d ropes", e.ID, p, len(d.Ropes))
			}
		}
		if (e.Family == "hitches") != (len(d.Posts) == 1) {
			t.Errorf("%s: a %s with %d posts", e.ID, e.Family, len(d.Posts))
		}
	}
	if len(marinsCatalogue) != 21 || families["loops"] != 9 || families["bends"] != 6 || families["hitches"] != 6 {
		t.Fatalf("%d knots, families %v", len(marinsCatalogue), families)
	}
	entries, err := marinsDrawings.ReadDir("marins")
	if err != nil || len(entries) != 21 {
		t.Fatalf("%d drawings embedded (%v)", len(entries), err)
	}
}

// IX finds in each drawing the deck's crossings and writhe, and no two parts of rope closer than a diameter.
func TestEveryKnotIsTheDecksAsIXReadsIt(t *testing.T) {
	doc, err := marinsOf(recordedMarins, time.Date(2026, 11, 16, 1, 0, 0, 0, time.UTC))
	if err != nil {
		t.Fatal(err)
	}
	if doc.Format != marinsFormat || doc.V != 1 || len(doc.Knots) != 21 {
		t.Fatalf("envelope %s v%d, %d knots", doc.Format, doc.V, len(doc.Knots))
	}
	for _, k := range doc.Knots {
		if !k.Verified {
			t.Errorf("%s: IX finds %d crossings, writhe %d, clearance %.3f", k.ID, k.Crossings, k.Writhe, k.Clearance)
		}
		d, _ := marinsDrawingOf(k.ID)
		if len(k.Ropes) != len(d.Ropes) || k.Components != len(d.Ropes) && k.Family != "loops" {
			t.Errorf("%s: %d ropes, %d components, %d drawn", k.ID, len(k.Ropes), k.Components, len(d.Ropes))
		}
		for i, r := range k.Ropes {
			if r.Closed != d.Ropes[i].Closed || len(r.Points) < len(d.Ropes[i].Points) {
				t.Errorf("%s rope %d: closed %v, %d points for %d drawn", k.ID, i, r.Closed, len(r.Points), len(d.Ropes[i].Points))
			}
		}
	}
}

// A wrong answer is refused or not verified: the deck's numbers are what IX must find.
func TestAnAnswerThatIsNotTheDecksIsNotVerified(t *testing.T) {
	e := marinsCatalogue[2] // the bowline
	d, err := marinsDrawingOf(e.ID)
	if err != nil {
		t.Fatal(err)
	}
	good, err := recordedMarins(d)
	if err != nil {
		t.Fatal(err)
	}
	var a map[string]any
	if err := json.Unmarshal(good, &a); err != nil {
		t.Fatal(err)
	}
	edit := func(f func(map[string]any)) []byte {
		var c map[string]any
		_ = json.Unmarshal(good, &c)
		f(c)
		b, _ := json.Marshal(c)
		return b
	}
	k, err := marinsKnotFromIX(e, d, good)
	if err != nil || !k.Verified {
		t.Fatalf("the recorded bowline: %v, verified %v", err, k.Verified)
	}
	k, err = marinsKnotFromIX(e, d, edit(func(c map[string]any) { c["crossings"] = 6.0 }))
	if err != nil || k.Verified {
		t.Errorf("six crossings: %v, verified %v", err, k.Verified)
	}
	k, err = marinsKnotFromIX(e, d, edit(func(c map[string]any) { c["geometry"].(map[string]any)["min_clearance"] = 0.8 }))
	if err != nil || k.Verified {
		t.Errorf("a clearance of 0.8: %v, verified %v", err, k.Verified)
	}
	for name, bad := range map[string][]byte{
		"no geometry":  edit(func(c map[string]any) { delete(c, "geometry") }),
		"no crossings": edit(func(c map[string]any) { delete(c, "crossings") }),
		"a far point": edit(func(c map[string]any) {
			c["geometry"].(map[string]any)["ropes"].([]any)[0].(map[string]any)["points"].([]any)[0] = []any{1e6, 0.0, 0.0}
		}),
		"not json": []byte("{"),
	} {
		if _, err := marinsKnotFromIX(e, d, bad); err == nil {
			t.Errorf("%s: taken", name)
		}
	}
}

// The document from IX's recorded answers, without calling IX: DELTA_MARINS_OUT names where to write it.
func TestWriteMarinsFromRecordings(t *testing.T) {
	out := os.Getenv("DELTA_MARINS_OUT")
	if out == "" {
		t.Skip("DELTA_MARINS_OUT names where to write the knots' document")
	}
	doc, err := marinsOf(recordedMarins, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	body, err := json.Marshal(doc)
	if err != nil {
		t.Fatal(err)
	}
	if err := writeDocument(out, body); err != nil {
		t.Fatal(err)
	}
}
