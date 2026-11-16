package main

import (
	"bytes"
	"context"
	"encoding/json"
	"math"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// IX's own readings of the knot board's ropes, recorded from ix-mcp by TestRecordIXCordage with DELTA_IX_MCP set: one
// file a knot, its answer to ix_knot {"ropes": ..., "over": "height"}.
const ixCordageDir = "testdata/ix-cordage"

func TestRecordIXCordage(t *testing.T) {
	exe := os.Getenv("DELTA_IX_MCP")
	if exe == "" {
		t.Skip("DELTA_IX_MCP names ix-mcp to record IX's readings")
	}
	if err := os.MkdirAll(ixCordageDir, 0o755); err != nil {
		t.Fatal(err)
	}
	for _, k := range cordageGallery {
		ctx, cancel := context.WithTimeout(context.Background(), ixCallSeconds*time.Second)
		answer, err := ixKnotDrawing(ctx, exe, k.ropes())
		cancel()
		if err != nil {
			t.Fatalf("%s: %v", k.ID, err)
		}
		var pretty bytes.Buffer
		if err := json.Indent(&pretty, answer, "", " "); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(filepath.Join(ixCordageDir, k.ID+".json"), append(pretty.Bytes(), '\n'), 0o644); err != nil {
			t.Fatal(err)
		}
	}
}

// Each knot of the board is one rope with free ends, within the floor's bounds, its ends above the rest so that IX's
// closing arc over everything gives back the knot drawn.
func TestTheKnotBoardsRopesHaveFreeEndsAbove(t *testing.T) {
	if len(cordageGallery) == 0 || len(cordageGallery) > 16 {
		t.Fatalf("%d knots on the board", len(cordageGallery))
	}
	for _, k := range cordageGallery {
		ropes := k.ropes()
		if len(ropes) != 1 || ropes[0].Closed {
			t.Fatalf("%s: %d ropes, closed %v", k.ID, len(ropes), len(ropes) == 1 && ropes[0].Closed)
		}
		pts := ropes[0].Points
		if len(pts) < 8 || len(pts) > knotsMaxPoints {
			t.Fatalf("%s: %d points", k.ID, len(pts))
		}
		lo, hi := extent(pts)
		if size := math.Max(hi[0]-lo[0], math.Max(hi[1]-lo[1], hi[2]-lo[2])); math.Abs(size-1) > 1e-3 {
			t.Errorf("%s: largest extent %.4f, not 1", k.ID, size)
		}
		body := pts[3 : len(pts)-3]
		_, bodyHi := extent(body)
		if pts[0][2] <= bodyHi[2] || pts[len(pts)-1][2] <= bodyHi[2] {
			t.Errorf("%s: an end at %.3f or %.3f is not above the knot's top %.3f", k.ID, pts[0][2], pts[len(pts)-1][2], bodyHi[2])
		}
		if room := clearance(ropes); room < 0.01 {
			t.Errorf("%s: the rope comes within %.4f of itself", k.ID, room)
		}
	}
}

func TestJonesOfASumIsTheProduct(t *testing.T) {
	trefoil := map[float64]int{-4: -1, -3: 1, -1: 1}
	// The square knot's polynomial is its own mirror's; the granny's is the trefoil's squared.
	square := jonesProduct(trefoil, jonesMirror(trefoil))
	if !samePolynomial(square, jonesMirror(square)) {
		t.Errorf("the square knot's polynomial %v is not its mirror's", square)
	}
	granny := jonesProduct(trefoil, trefoil)
	want := map[float64]int{-8: 1, -7: -2, -6: 1, -5: -2, -4: 2, -2: 1}
	if !samePolynomial(granny, want) {
		t.Errorf("the granny's polynomial %v, want %v", granny, want)
	}
}

func recordedCordage(k knotSpec, _ []knotRope) ([]byte, error) {
	return os.ReadFile(filepath.Join(ixCordageDir, k.ID+".json"))
}

// The board's document from IX's recorded readings, without calling IX: for the scene's --cordage, DELTA_CORDAGE_OUT
// naming where to write it.
func TestWriteCordageFromRecordings(t *testing.T) {
	out := os.Getenv("DELTA_CORDAGE_OUT")
	if out == "" {
		t.Skip("DELTA_CORDAGE_OUT names where to write the board's document")
	}
	doc, err := galleryOf(cordageGallery, recordedCordage, time.Now())
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

func TestEveryKnotOfTheBoardClosesIntoTheTablesKnot(t *testing.T) {
	doc, err := galleryOf(cordageGallery, recordedCordage, time.Date(2026, 11, 16, 5, 0, 0, 0, time.UTC))
	if err != nil {
		t.Fatal(err)
	}
	if doc.Format != "delta-ix-knots" || doc.V != 1 || len(doc.Exhibits) != len(cordageGallery) {
		t.Fatalf("envelope %+v", doc)
	}
	for _, e := range doc.Exhibits {
		if !e.Verified {
			t.Errorf("%s: IX reads %s, the tables say %s for %s", e.ID, e.Jones, e.Expected, e.Rolfsen)
		}
		if len(e.Ropes) != 1 || e.Ropes[0].Closed || e.Components != 1 {
			t.Errorf("%s: %d ropes, %d components", e.ID, len(e.Ropes), e.Components)
		}
	}
}
