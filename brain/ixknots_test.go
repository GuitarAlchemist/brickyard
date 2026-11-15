package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// IX's own readings of the knots' floor's exhibits, recorded from ix-mcp by TestRecordIXKnots with DELTA_IX_MCP set:
// one file an exhibit, its answer to ix_knot {"ropes": ..., "over": "height"}.
const ixKnotsDir = "testdata/ix-knots"

func TestRecordIXKnots(t *testing.T) {
	exe := os.Getenv("DELTA_IX_MCP")
	if exe == "" {
		t.Skip("DELTA_IX_MCP names ix-mcp to record IX's readings")
	}
	if err := os.MkdirAll(ixKnotsDir, 0o755); err != nil {
		t.Fatal(err)
	}
	for _, k := range knotGallery {
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
		if err := os.WriteFile(filepath.Join(ixKnotsDir, k.ID+".json"), append(pretty.Bytes(), '\n'), 0o644); err != nil {
			t.Fatal(err)
		}
	}
}

func recordedKnot(k knotSpec, _ []knotRope) ([]byte, error) {
	return os.ReadFile(filepath.Join(ixKnotsDir, k.ID+".json"))
}

func TestEveryExhibitOfTheKnotsFloorIsTheTablesKnot(t *testing.T) {
	doc, err := knotsGallery(recordedKnot, time.Date(2026, 11, 14, 7, 0, 0, 0, time.UTC))
	if err != nil {
		t.Fatal(err)
	}
	if doc.Format != "delta-ix-knots" || doc.V != 1 || doc.Source != "ix_knot" || doc.At != "2026-11-14T07:00:00Z" || len(doc.Exhibits) != len(knotGallery) {
		t.Fatalf("envelope %+v", doc)
	}
	minCrossings := map[string]int{"3_1": 3, "4_1": 4, "5_1": 5, "5_2": 5, "6_1": 6, "7_1": 7, "8_19": 8, "L6a4": 6}
	for _, e := range doc.Exhibits {
		if !e.Verified {
			t.Errorf("%s: IX reads %s, the tables say %s", e.ID, e.Jones, e.Expected)
		}
		// A diagram has at least the knot's crossing number of crossings; the Borromean rings are three rings.
		if e.Crossings < minCrossings[e.Rolfsen] || (e.ID == "borromean") != (e.Components == 3) || e.Components != len(e.Ropes) {
			t.Errorf("%s: %d crossings, %d components", e.ID, e.Crossings, e.Components)
		}
		if e.Radius <= 0.015 || e.Radius > 0.035 {
			t.Errorf("%s: a tube of %g, too thin to see or thicker than the gallery's", e.ID, e.Radius)
		}
		// The tube keeps clear of itself: no two passages closer than its diameter.
		if room := clearance(e.Ropes); room < 2*e.Radius {
			t.Errorf("%s: passages %g apart, a tube of %g", e.ID, room, e.Radius)
		}
		points := 0
		for _, r := range e.Ropes {
			points += len(r.Points)
			for _, p := range r.Points {
				for _, v := range p {
					if v < -0.5 || v > 0.5 {
						t.Fatalf("%s: a point %v outside the unit cube", e.ID, p)
					}
				}
			}
		}
		if points > knotsMaxPoints {
			t.Errorf("%s: %d points", e.ID, points)
		}
	}
}

func TestAKnotIXReadsOtherwiseIsNotVerified(t *testing.T) {
	trefoil := knotGallery[0]
	answer, err := recordedKnot(trefoil, nil)
	if err != nil {
		t.Fatal(err)
	}
	// The figure-eight's polynomial is not the trefoil's, nor its mirror's.
	e, err := exhibitFromIX(knotGallery[1], trefoil.ropes(), answer)
	if err != nil {
		t.Fatal(err)
	}
	if e.Verified || e.Mirror {
		t.Fatalf("the trefoil's reading verified as the figure-eight: %+v", e)
	}
	// A reading that lacks the polynomial is refused, not shown as a knot.
	if _, err := exhibitFromIX(trefoil, trefoil.ropes(), []byte(`{"components":1,"crossings":3,"writhe":3}`)); err == nil {
		t.Fatal("a reading without its Jones polynomial was taken")
	}
	// A mirror is a mirror: the trefoil read with every crossing the other way round is still the trefoil.
	mirrored := strings.NewReplacer(`"text": "-t^-4 + t^-3 + t^-1"`, `"text": "t + t^3 - t^4"`).Replace(string(answer))
	var a map[string]any
	if err := json.Unmarshal([]byte(mirrored), &a); err != nil {
		t.Fatal(err)
	}
	a["jones"].(map[string]any)["terms"] = []any{[]any{1.0, 1.0}, []any{3.0, 1.0}, []any{4.0, -1.0}}
	body, _ := json.Marshal(a)
	if e, err := exhibitFromIX(trefoil, trefoil.ropes(), body); err != nil || !e.Verified || !e.Mirror {
		t.Fatalf("the mirror trefoil: %+v, %v", e, err)
	}
}

func TestTheKnotsFloorSaysWhyWhenIXCannotRead(t *testing.T) {
	doc, err := knotsGallery(func(k knotSpec, _ []knotRope) ([]byte, error) {
		if k.ID == "stevedore" {
			return nil, errors.New("IX unavailable: no answer within 30 s")
		}
		return recordedKnot(k, nil)
	}, time.Now())
	if err == nil || !strings.Contains(err.Error(), "stevedore: IX unavailable") {
		t.Fatalf("%v, %d exhibits", err, len(doc.Exhibits))
	}
}

func TestJonesTextIsWrittenAsIXWritesIt(t *testing.T) {
	for want, p := range map[string]map[float64]int{
		"t^-2 - t^-1 + 1 - t + t^2":        {-2: 1, -1: -1, 0: 1, 1: -1, 2: 1},
		"-t^-4 + t^-3 + t^-1":              {-4: -1, -3: 1, -1: 1},
		"t - t^2 + 2t^3 - t^4 + t^5 - t^6": {1: 1, 2: -1, 3: 2, 4: -1, 5: 1, 6: -1},
	} {
		if got := jonesText(p); got != want {
			t.Errorf("%v: %q, not %q", p, got, want)
		}
	}
}
