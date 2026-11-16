package main

// The knots' floor's sailors' knots: the 21 knots of the deck « Parler en nœuds » (loops and stoppers, bends, hitches),
// each drawn by hand from the way it is tied (brain/marins/<id>.json: points x, y and a height z, the parts in front
// higher, a post or ring as a rope of its own), as the deck drew them. brain -ix-marins sends each drawing to IX's
// ix_knot with its geometry: IX finds the crossings from the heights, refuses two strands that touch without crossing,
// and returns the rope's 3D path, its clearance (the least distance between two parts of rope that do not follow each
// other, in diameters), its writhe, components and the Jones polynomial of its closure. A knot is verified when IX
// finds the deck's crossings and writhe and a clearance of 1 or more (no two parts of rope pass through each other).
// What a closure says (« Jones de 6₃ », « V = 1 ») is the deck's reading of IX's polynomial, carried as text.
//
// The document, format "delta-ix-marins", v 1, by default %LOCALAPPDATA%\delta\ix-marins.json:
//
//	{"format", "v": 1, "source": "ix_knot", "at", "knots": [{"id", "en", "fr", "family": "loops"|"bends"|"hitches",
//	 "closure_en", "closure_fr", "crossings", "writhe", "components", "clearance", "jones", "verified", "radius",
//	 "posts": [rope index], "post_scale", "ropes": [{"closed", "points": [[x, y, z], ...]}]}]}
//
// or {"format", "v", "source", "at", "error": why}. IX failing for one knot fails the document, which then says why.

import (
	"context"
	"embed"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"strings"
	"time"
	"unicode"
)

const (
	marinsFormat    = "delta-ix-marins"
	marinsVersion   = 1
	marinsMaxPoints = 8192 // a knot's, over all its ropes
)

//go:embed marins/*.json
var marinsDrawings embed.FS

// A drawing as the deck made it.
type marinsDrawing struct {
	ID        string  `json:"id"`
	En        string  `json:"en"`
	Fr        string  `json:"fr"`
	Radius    float64 `json:"radius"`
	Over      string  `json:"over"`
	Posts     []int   `json:"posts"`
	PostScale float64 `json:"post_scale"`
	Ropes     []struct {
		Closed bool         `json:"closed"`
		Points [][3]float64 `json:"points"`
	} `json:"ropes"`
}

// What the deck found for each knot: its family, IX's crossings and writhe, and what its closure is.
type marinsEntry struct {
	ID        string
	Family    string
	Crossings int
	Writhe    int
	ClosureEn string
	ClosureFr string
}

// The 21 knots, in the deck's order: its three galleries.
var marinsCatalogue = []marinsEntry{
	{"figure-eight", "loops", 4, 0, "the knot 4_1", "le nœud 4_1"},
	{"heaving-line-knot", "loops", 18, 2, "a knot, its Jones of 11 terms", "un nœud, son Jones à 11 termes"},
	{"bowline", "loops", 7, 0, "Jones of 6_3", "Jones de 6_3"},
	{"slipped-bowline", "loops", 10, 2, "Jones of the right-handed trefoil", "Jones du trèfle droit"},
	{"laguis", "loops", 9, -3, "Jones of 8_20", "Jones de 8_20"},
	{"figure-eight-loop", "loops", 16, 0, "V = 1, it comes undone", "V = 1, il se défait"},
	{"bowline-on-a-bight", "loops", 16, 0, "V = 1, it comes undone", "V = 1, il se défait"},
	{"alpine-butterfly", "loops", 16, -2, "V = 1, it comes undone", "V = 1, il se défait"},
	{"sheepshank", "loops", 10, 2, "V = 1, it comes undone", "V = 1, il se défait"},
	{"sheet-bend", "bends", 7, -1, "linked once (Hopf)", "enlacées une fois (Hopf)"},
	{"double-sheet-bend", "bends", 13, -1, "linked once (Hopf)", "enlacées une fois (Hopf)"},
	{"reef-knot", "bends", 6, 0, "ends joined: 3_1 # 3_1*", "bouts joints : 3_1 # 3_1*"},
	{"zeppelin-bend", "bends", 12, 0, "two mirror trefoils, linked", "deux trèfles miroirs, enlacés"},
	{"flemish-bend", "bends", 16, 0, "two 4_1 knots, linked", "deux nœuds 4_1, enlacés"},
	{"heaving-line-bend", "bends", 10, 3, "linked once (Hopf)", "enlacées une fois (Hopf)"},
	{"round-turn-two-half-hitches", "hitches", 10, -6, "linked with the ring", "enlacé à l'anneau"},
	{"anchor-bend", "hitches", 12, -8, "linked with the ring", "enlacé à l'anneau"},
	{"clove-hitch", "hitches", 7, -6, "linked with the spar", "enlacé à l'espar"},
	{"constrictor", "hitches", 8, -8, "linked with the spar", "enlacé à l'espar"},
	{"rolling-hitch", "hitches", 11, -10, "linked with the spar", "enlacé à l'espar"},
	{"prusik", "hitches", 10, 0, "apart: it slides off the end", "séparé : il glisse hors du bout"},
}

func marinsDrawingOf(id string) (marinsDrawing, error) {
	var d marinsDrawing
	body, err := marinsDrawings.ReadFile("marins/" + id + ".json")
	if err != nil {
		return d, err
	}
	if err := json.Unmarshal(body, &d); err != nil {
		return d, fmt.Errorf("%s: %v", id, err)
	}
	if d.ID != id || len(d.Ropes) == 0 || d.Radius <= 0 {
		return d, fmt.Errorf("%s: not a drawing of it", id)
	}
	return d, nil
}

type marinsKnot struct {
	ID         string        `json:"id"`
	En         string        `json:"en"`
	Fr         string        `json:"fr"`
	Family     string        `json:"family"`
	ClosureEn  string        `json:"closure_en"`
	ClosureFr  string        `json:"closure_fr"`
	Crossings  int           `json:"crossings"`
	Writhe     int           `json:"writhe"`
	Components int           `json:"components"`
	Clearance  float64       `json:"clearance"`
	Jones      string        `json:"jones"`
	Verified   bool          `json:"verified"`
	Radius     float64       `json:"radius"`
	Posts      []int         `json:"posts"`
	PostScale  float64       `json:"post_scale"`
	Ropes      []atelierRope `json:"ropes"`
}

type marinsDoc struct {
	Format string       `json:"format"`
	V      int          `json:"v"`
	Source string       `json:"source"`
	At     string       `json:"at"`
	Knots  []marinsKnot `json:"knots,omitempty"`
	Error  string       `json:"error,omitempty"`
}

// What ix_knot answers for a drawing with geometry: true, as far as the floor needs it.
type ixMarinsAnswer struct {
	Crossings  *int `json:"crossings"`
	Writhe     *int `json:"writhe"`
	Components *int `json:"components"`
	Jones      struct {
		Text string `json:"text"`
	} `json:"jones"`
	Geometry *struct {
		Radius       float64  `json:"radius"`
		MinClearance *float64 `json:"min_clearance"`
		Ropes        []struct {
			Closed bool        `json:"closed"`
			Points [][]float64 `json:"points"`
		} `json:"ropes"`
	} `json:"geometry"`
}

// ixMarinsArguments: the drawing as ix_knot takes it, with its heights deciding the crossings and its geometry asked.
func ixMarinsArguments(d marinsDrawing) map[string]any {
	over := d.Over
	if over == "" {
		over = "height"
	}
	return map[string]any{"ropes": d.Ropes, "over": over, "radius": d.Radius, "geometry": true}
}

// marinsKnotFromIX checks IX's answer for one knot of the catalogue and keeps what the floor shows.
func marinsKnotFromIX(e marinsEntry, d marinsDrawing, answer []byte) (marinsKnot, error) {
	var a ixMarinsAnswer
	if err := json.Unmarshal(answer, &a); err != nil {
		return marinsKnot{}, fmt.Errorf("%s: IX's answer is not a knot: %v", e.ID, err)
	}
	if a.Crossings == nil || a.Writhe == nil || a.Components == nil || a.Geometry == nil || a.Geometry.MinClearance == nil {
		return marinsKnot{}, fmt.Errorf("%s: IX's answer lacks its crossings, writhe, components or geometry", e.ID)
	}
	// A polynomial may be longer than a name: 400 characters, without control characters.
	if len([]rune(a.Jones.Text)) > 400 || strings.IndexFunc(a.Jones.Text, unicode.IsControl) >= 0 {
		return marinsKnot{}, fmt.Errorf("%s: IX's polynomial is longer than 400 characters or holds a control character", e.ID)
	}
	g := a.Geometry
	clearance := *g.MinClearance
	if math.IsNaN(g.Radius) || g.Radius <= 0 || g.Radius > atelierMaxRadius || math.IsNaN(clearance) || clearance < 0 || clearance > 1000 {
		return marinsKnot{}, fmt.Errorf("%s: a radius of %v or a clearance of %v out of bounds", e.ID, g.Radius, clearance)
	}
	if len(g.Ropes) != len(d.Ropes) || len(g.Ropes) > atelierMaxRopes {
		return marinsKnot{}, fmt.Errorf("%s: IX returns %d ropes for %d drawn", e.ID, len(g.Ropes), len(d.Ropes))
	}
	k := marinsKnot{
		ID: e.ID, En: d.En, Fr: d.Fr, Family: e.Family, ClosureEn: e.ClosureEn, ClosureFr: e.ClosureFr,
		Crossings: *a.Crossings, Writhe: *a.Writhe, Components: *a.Components, Clearance: round4(clearance),
		Jones: a.Jones.Text, Radius: round4(g.Radius), Posts: d.Posts, PostScale: d.PostScale,
	}
	if k.Posts == nil {
		k.Posts = []int{}
	}
	for _, p := range k.Posts {
		if p < 0 || p >= len(g.Ropes) {
			return marinsKnot{}, fmt.Errorf("%s: a post names rope %d of %d", e.ID, p, len(g.Ropes))
		}
	}
	total := 0
	for i, rope := range g.Ropes {
		total += len(rope.Points)
		if len(rope.Points) < 2 || total > marinsMaxPoints {
			return marinsKnot{}, fmt.Errorf("%s: rope %d has %d points (%d in all, %d at most)", e.ID, i, len(rope.Points), total, marinsMaxPoints)
		}
		kept := atelierRope{Closed: rope.Closed, Points: make([][3]float64, len(rope.Points))}
		for j, p := range rope.Points {
			if len(p) != 3 {
				return marinsKnot{}, fmt.Errorf("%s: rope %d, point %d has %d coordinates", e.ID, i, j, len(p))
			}
			for c, v := range p {
				if math.IsNaN(v) || math.IsInf(v, 0) || math.Abs(v) > atelierMaxCoord {
					return marinsKnot{}, fmt.Errorf("%s: rope %d, point %d: %v is beyond ±%d", e.ID, i, j, v, atelierMaxCoord)
				}
				kept.Points[j][c] = round4(v)
			}
		}
		k.Ropes = append(k.Ropes, kept)
	}
	k.Verified = k.Crossings == e.Crossings && k.Writhe == e.Writhe && clearance >= 1
	return k, nil
}

// marinsOf: the catalogue as ask reads each drawing (IX, or its recorded answers).
func marinsOf(ask func(d marinsDrawing) ([]byte, error), now time.Time) (marinsDoc, error) {
	doc := marinsDoc{Format: marinsFormat, V: marinsVersion, Source: "ix_knot", At: now.UTC().Format(time.RFC3339)}
	for _, e := range marinsCatalogue {
		d, err := marinsDrawingOf(e.ID)
		if err != nil {
			return marinsDoc{}, err
		}
		answer, err := ask(d)
		if err != nil {
			return marinsDoc{}, fmt.Errorf("%s: %v", e.ID, err)
		}
		k, err := marinsKnotFromIX(e, d, answer)
		if err != nil {
			return marinsDoc{}, err
		}
		doc.Knots = append(doc.Knots, k)
	}
	return doc, nil
}

func defaultMarinsPath() string {
	dir, err := os.UserCacheDir()
	if err != nil {
		dir = os.TempDir()
	}
	return filepath.Join(dir, "delta", "ix-marins.json")
}

// runIXMarins is brain -ix-marins: the 21 knots as IX reads them, or why there are none. 0 when every knot is verified.
func runIXMarins(exe, out string, stdout, stderr io.Writer) int {
	if out == "" {
		out = defaultMarinsPath()
	}
	now := time.Now()
	write := func(doc marinsDoc) error {
		body, err := json.Marshal(doc)
		if err != nil {
			return err
		}
		return writeDocument(out, body)
	}
	fail := func(why string) int {
		if err := write(marinsDoc{Format: marinsFormat, V: marinsVersion, Source: "ix_knot", At: now.UTC().Format(time.RFC3339), Error: atelierError(why, now).Error}); err != nil {
			fmt.Fprintf(stderr, "ix-marins: %s; and the document could not be written: %v\n", why, err)
			return 1
		}
		fmt.Fprintf(stderr, "ix-marins: %s; the document says so (%s)\n", why, out)
		return 1
	}
	exe, err := ixExecutable(exe)
	if err != nil {
		return fail(err.Error())
	}
	doc, err := marinsOf(func(d marinsDrawing) ([]byte, error) {
		ctx, cancel := context.WithTimeout(context.Background(), ixCallSeconds*time.Second)
		defer cancel()
		return ixCall(ctx, exe, "ix_knot", ixMarinsArguments(d))
	}, now)
	if err != nil {
		return fail(err.Error())
	}
	if err := write(doc); err != nil {
		fmt.Fprintf(stderr, "ix-marins: %v\n", err)
		return 1
	}
	verified := 0
	for _, k := range doc.Knots {
		mark := "verified"
		if k.Verified {
			verified++
		} else {
			mark = "NOT verified"
		}
		fmt.Fprintf(stdout, "ix-marins: %-28s %2d crossings, writhe %3d, clearance %.3f, %s\n", k.ID, k.Crossings, k.Writhe, k.Clearance, mark)
	}
	fmt.Fprintf(stdout, "ix-marins: %d of %d verified, written to %s\n", verified, len(doc.Knots), out)
	if verified != len(doc.Knots) {
		return 1
	}
	return 0
}
