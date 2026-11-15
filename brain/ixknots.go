package main

// The knots' floor in Delta's hall: a gallery of mathematical knots and links, each one a curve in space that IX reads
// and names. brain -ix-knots draws each exhibit's curve (below: torus knots, Lissajous knots, the Borromean rings),
// sends it to IX's ix_knot as a drawing whose crossings the curve's own heights decide ("over": "height"), and keeps
// IX's answer: its components, crossings, writhe and Jones polynomial. An exhibit is verified when IX's Jones polynomial
// is the one the knot tables give for it (the Knot Atlas's), or its mirror's: the curve the scene draws is then that
// knot. The scene draws the same points as a tube; IX does the mathematics, Delta only draws the curve IX read.
//
// The document, format "delta-ix-knots", v 1, by default %LOCALAPPDATA%\delta\ix-knots.json:
//
//	{"format", "v": 1, "source": "ix_knot", "at": RFC 3339,
//	 "exhibits": [{"id", "rolfsen", "en", "fr", "expected", "jones", "mirror", "verified", "components", "crossings",
//	               "writhe", "radius", "ropes": [{"closed": true, "points": [[x, y, z], ...]}]}]}
//
// or {"format", "v": 1, "source", "at", "error": why}. Each exhibit's curve fits a unit cube centred on 0, its tube's
// radius keeps it clear of itself. Bounds the scene checks again: at most 16 exhibits, 8 ropes and 256 points each,
// coordinates within ±1, the radius in (0, 0.1], texts of at most 96 characters.

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

const (
	knotsFormat    = "delta-ix-knots"
	knotsVersion   = 1
	knotsMaxPoints = 256 // IX reads at most this many control points a drawing
)

type knotRope struct {
	Closed bool         `json:"closed"`
	Points [][3]float64 `json:"points"`
}

type knotExhibit struct {
	ID         string     `json:"id"`
	Rolfsen    string     `json:"rolfsen"`
	En         string     `json:"en"`
	Fr         string     `json:"fr"`
	Expected   string     `json:"expected"`
	Jones      string     `json:"jones"`
	Mirror     bool       `json:"mirror"`
	Verified   bool       `json:"verified"`
	Components int        `json:"components"`
	Crossings  int        `json:"crossings"`
	Writhe     int        `json:"writhe"`
	Radius     float64    `json:"radius"`
	Ropes      []knotRope `json:"ropes"`
}

type knotsDoc struct {
	Format   string        `json:"format"`
	V        int           `json:"v"`
	Source   string        `json:"source"`
	At       string        `json:"at"`
	Exhibits []knotExhibit `json:"exhibits,omitempty"`
	Error    string        `json:"error,omitempty"`
}

// A knot of the gallery: its names, its Jones polynomial in the tables (exponent: coefficient), and its curve.
type knotSpec struct {
	ID, Rolfsen, En, Fr string
	Jones               map[float64]int
	Curve               func(t float64) [3]float64 // one rope, t in [0, 2π)
	Ropes               []func(t float64) [3]float64
	Points              int // a rope's
}

func torusKnot(p, q float64) func(t float64) [3]float64 {
	return func(t float64) [3]float64 {
		r := 2 + 0.9*math.Cos(q*t)
		return [3]float64{r * math.Cos(p*t), r * math.Sin(p*t), 0.9 * math.Sin(q*t)}
	}
}

func lissajous(nx, ny, nz, px, py float64) func(t float64) [3]float64 {
	return func(t float64) [3]float64 {
		return [3]float64{math.Cos(nx*t + px), math.Cos(ny*t + py), math.Cos(nz * t)}
	}
}

// turned: the curve seen from a generic direction, so that no part of it is edge-on to the drawing's plane.
func turned(f func(t float64) [3]float64) func(t float64) [3]float64 { return turnedBy(f, 0.5, 0.7) }

// turnedBy: the curve turned by a about y, then b about x.
func turnedBy(f func(t float64) [3]float64, a, b float64) func(t float64) [3]float64 {
	return func(t float64) [3]float64 {
		p := f(t)
		x, z := p[0]*math.Cos(a)+p[2]*math.Sin(a), -p[0]*math.Sin(a)+p[2]*math.Cos(a)
		y, z2 := p[1]*math.Cos(b)-z*math.Sin(b), p[1]*math.Sin(b)+z*math.Cos(b)
		return [3]float64{x, y, z2}
	}
}

// The gallery, in the order of its plinths. The Jones polynomials are the Knot Atlas's (q for t).
var knotGallery = []knotSpec{
	{ID: "trefoil", Rolfsen: "3_1", En: "Trefoil", Fr: "Nœud de trèfle", Points: 200,
		Jones: map[float64]int{-4: -1, -3: 1, -1: 1},
		Curve: func(t float64) [3]float64 {
			return [3]float64{math.Sin(t) + 2*math.Sin(2*t), math.Cos(t) - 2*math.Cos(2*t), -math.Sin(3 * t)}
		}},
	{ID: "figure-eight", Rolfsen: "4_1", En: "Figure-eight knot", Fr: "Nœud en huit", Points: 220,
		Jones: map[float64]int{-2: 1, -1: -1, 0: 1, 1: -1, 2: 1},
		Curve: func(t float64) [3]float64 {
			r := 2 + math.Cos(2*t)
			return [3]float64{r * math.Cos(3*t), r * math.Sin(3*t), math.Sin(4 * t)}
		}},
	{ID: "cinquefoil", Rolfsen: "5_1", En: "Cinquefoil", Fr: "Nœud à cinq feuilles", Points: 240,
		Jones: map[float64]int{-7: -1, -6: 1, -5: -1, -4: 1, -2: 1},
		Curve: torusKnot(2, 5)},
	{ID: "three-twist", Rolfsen: "5_2", En: "Three-twist knot", Fr: "Nœud à trois torsions", Points: 256,
		Jones: map[float64]int{-6: -1, -5: 1, -4: -1, -3: 2, -2: -1, -1: 1},
		Curve: lissajous(3, 2, 7, 0.7, 0.2)},
	{ID: "stevedore", Rolfsen: "6_1", En: "Stevedore knot", Fr: "Nœud de docker", Points: 256,
		Jones: map[float64]int{-4: 1, -3: -1, -2: 1, -1: -2, 0: 2, 1: -1, 2: 1},
		Curve: lissajous(3, 2, 5, 1.5, 0.2)},
	{ID: "septafoil", Rolfsen: "7_1", En: "Septafoil", Fr: "Nœud à sept feuilles", Points: 256,
		Jones: map[float64]int{-10: -1, -9: 1, -8: -1, -7: 1, -6: -1, -5: 1, -3: 1},
		Curve: torusKnot(2, 7)},
	{ID: "torus-3-4", Rolfsen: "8_19", En: "Torus knot (3, 4)", Fr: "Nœud torique (3, 4)", Points: 256,
		Jones: map[float64]int{3: 1, 5: 1, 8: -1},
		Curve: torusKnot(3, 4)},
	{ID: "borromean", Rolfsen: "L6a4", En: "Borromean rings", Fr: "Anneaux borroméens", Points: 84,
		Jones: map[float64]int{-3: -1, -2: 3, -1: -2, 0: 4, 1: -2, 2: 3, 3: -1},
		Ropes: []func(t float64) [3]float64{
			turned(func(t float64) [3]float64 { return [3]float64{2 * math.Cos(t), math.Sin(t), 0} }),
			turned(func(t float64) [3]float64 { return [3]float64{0, 2 * math.Cos(t), math.Sin(t)} }),
			turned(func(t float64) [3]float64 { return [3]float64{math.Sin(t), 0, 2 * math.Cos(t)} }),
		}},
}

// ropes: the exhibit's curves sampled, centred on 0 and scaled so that their largest extent is 1.
func (k knotSpec) ropes() []knotRope {
	curves := k.Ropes
	if k.Curve != nil {
		// A knot's classic view, tilted a little: seen straight along a symmetry axis, two passages may meet exactly
		// at a point, which no drawing can read.
		curves = []func(t float64) [3]float64{turnedBy(k.Curve, 0.11, 0.07)}
	}
	var out []knotRope
	lo, hi := [3]float64{math.Inf(1), math.Inf(1), math.Inf(1)}, [3]float64{math.Inf(-1), math.Inf(-1), math.Inf(-1)}
	for _, f := range curves {
		r := knotRope{Closed: true}
		for i := 0; i < k.Points; i++ {
			p := f(2 * math.Pi * (float64(i) + 0.37) / float64(k.Points)) // off the curve's symmetric points
			for a := 0; a < 3; a++ {
				lo[a], hi[a] = math.Min(lo[a], p[a]), math.Max(hi[a], p[a])
			}
			r.Points = append(r.Points, p)
		}
		out = append(out, r)
	}
	size := math.Max(hi[0]-lo[0], math.Max(hi[1]-lo[1], hi[2]-lo[2]))
	for _, r := range out {
		for i, p := range r.Points {
			for a := 0; a < 3; a++ {
				p[a] = round4((p[a] - (lo[a]+hi[a])/2) / size)
			}
			r.Points[i] = p
		}
	}
	return out
}

// clearance: the least distance between two of the ropes' segments that do not touch, the tube's room.
func clearance(ropes []knotRope) float64 {
	type seg struct {
		a, b   [3]float64
		r, i   int
		closed bool
	}
	var segs []seg
	for ri, r := range ropes {
		n := len(r.Points)
		for i := 0; i < n; i++ {
			segs = append(segs, seg{r.Points[i], r.Points[(i+1)%n], ri, i, r.Closed})
		}
	}
	best := math.Inf(1)
	for x := 0; x < len(segs); x++ {
		for y := x + 1; y < len(segs); y++ {
			s, u := segs[x], segs[y]
			if s.r == u.r {
				n := len(ropes[s.r].Points)
				d := (u.i - s.i + n) % n
				if d <= 3 || d >= n-3 { // neighbours along the rope: their nearness is the rope bending, not touching
					continue
				}
			}
			best = math.Min(best, segmentDistance(s.a, s.b, u.a, u.b))
		}
	}
	return best
}

func segmentDistance(p1, q1, p2, q2 [3]float64) float64 {
	sub := func(a, b [3]float64) [3]float64 { return [3]float64{a[0] - b[0], a[1] - b[1], a[2] - b[2]} }
	dot := func(a, b [3]float64) float64 { return a[0]*b[0] + a[1]*b[1] + a[2]*b[2] }
	d1, d2, r := sub(q1, p1), sub(q2, p2), sub(p1, p2)
	a, e, f := dot(d1, d1), dot(d2, d2), dot(d2, r)
	c, b := dot(d1, r), dot(d1, d2)
	den := a*e - b*b
	s := 0.0
	if den > 1e-12 {
		s = math.Max(0, math.Min(1, (b*f-c*e)/den))
	}
	t := (b*s + f) / e
	if t < 0 {
		t, s = 0, math.Max(0, math.Min(1, -c/a))
	} else if t > 1 {
		t, s = 1, math.Max(0, math.Min(1, (b-c)/a))
	}
	var g [3]float64
	for i := 0; i < 3; i++ {
		g[i] = p1[i] + d1[i]*s - p2[i] - d2[i]*t
	}
	return math.Sqrt(dot(g, g))
}

// jonesText: a polynomial as IX writes it, highest power last: "t^-2 - t^-1 + 1 - t + t^2".
func jonesText(p map[float64]int) string {
	var exps []float64
	for e, c := range p {
		if c != 0 {
			exps = append(exps, e)
		}
	}
	sort.Float64s(exps)
	var b strings.Builder
	for i, e := range exps {
		c := p[e]
		switch {
		case i == 0 && c < 0:
			b.WriteString("-")
		case i > 0 && c < 0:
			b.WriteString(" - ")
		case i > 0:
			b.WriteString(" + ")
		}
		a := c
		if a < 0 {
			a = -a
		}
		term := ""
		switch e {
		case 0:
			term = ""
		case 1:
			term = "t"
		default:
			term = "t^" + strings.TrimSuffix(fmt.Sprintf("%g", e), ".0")
		}
		switch {
		case term == "":
			fmt.Fprintf(&b, "%d", a)
		case a == 1:
			b.WriteString(term)
		default:
			fmt.Fprintf(&b, "%d%s", a, term)
		}
	}
	return b.String()
}

func samePolynomial(a, b map[float64]int) bool {
	for e, c := range a {
		if b[e] != c {
			return false
		}
	}
	for e, c := range b {
		if a[e] != c {
			return false
		}
	}
	return true
}

// ixKnotDrawing asks IX to read one exhibit's ropes, the higher point of each crossing in front.
func ixKnotDrawing(ctx context.Context, exe string, ropes []knotRope) ([]byte, error) {
	var drawn []map[string]any
	for _, r := range ropes {
		drawn = append(drawn, map[string]any{"closed": r.Closed, "points": r.Points})
	}
	return ixCall(ctx, exe, "ix_knot", map[string]any{"ropes": drawn, "over": "height"})
}

// exhibitFromIX: an exhibit with IX's reading of its ropes, verified when IX's Jones polynomial is the tables'.
func exhibitFromIX(k knotSpec, ropes []knotRope, answer []byte) (knotExhibit, error) {
	var a struct {
		Components *int `json:"components"`
		Crossings  *int `json:"crossings"`
		Writhe     *int `json:"writhe"`
		Jones      *struct {
			Terms [][2]float64 `json:"terms"`
			Text  string       `json:"text"`
		} `json:"jones"`
	}
	if err := json.Unmarshal(answer, &a); err != nil {
		return knotExhibit{}, fmt.Errorf("not IX's reading of a drawing: %v", err)
	}
	if a.Components == nil || a.Crossings == nil || a.Writhe == nil || a.Jones == nil {
		return knotExhibit{}, errors.New("IX's reading lacks its components, crossings, writhe or Jones polynomial")
	}
	if *a.Crossings < 0 || *a.Crossings > 64 || *a.Writhe < -64 || *a.Writhe > 64 || *a.Components < 1 || *a.Components > 8 {
		return knotExhibit{}, errors.New("IX's crossings, writhe or components out of bounds")
	}
	if err := atelierText("IX's Jones polynomial", a.Jones.Text); err != nil {
		return knotExhibit{}, err
	}
	got := map[float64]int{}
	for _, t := range a.Jones.Terms {
		got[t[0]] += int(math.Round(t[1]))
	}
	mirror := map[float64]int{}
	for e, c := range k.Jones {
		mirror[-e] = c
	}
	room := clearance(ropes)
	return knotExhibit{
		ID: k.ID, Rolfsen: k.Rolfsen, En: k.En, Fr: k.Fr, Expected: jonesText(k.Jones), Jones: a.Jones.Text,
		Mirror:     samePolynomial(got, mirror) && !samePolynomial(got, k.Jones),
		Verified:   (samePolynomial(got, k.Jones) || samePolynomial(got, mirror)) && *a.Components == len(ropes),
		Components: *a.Components, Crossings: *a.Crossings, Writhe: *a.Writhe,
		Radius: round4(math.Min(0.035, 0.45*room)), Ropes: ropes,
	}, nil
}

// knotsGallery: every exhibit as IX reads it, ask being one call to IX for one exhibit's ropes.
func knotsGallery(ask func(k knotSpec, ropes []knotRope) ([]byte, error), now time.Time) (knotsDoc, error) {
	doc := knotsDoc{Format: knotsFormat, V: knotsVersion, Source: "ix_knot", At: now.UTC().Format(time.RFC3339)}
	for _, k := range knotGallery {
		ropes := k.ropes()
		total := 0
		for _, r := range ropes {
			total += len(r.Points)
		}
		if total > knotsMaxPoints {
			return doc, fmt.Errorf("%s: %d points, more than IX reads", k.ID, total)
		}
		answer, err := ask(k, ropes)
		if err != nil {
			return doc, fmt.Errorf("%s: %v", k.ID, err)
		}
		e, err := exhibitFromIX(k, ropes, answer)
		if err != nil {
			return doc, fmt.Errorf("%s: %v", k.ID, err)
		}
		doc.Exhibits = append(doc.Exhibits, e)
	}
	return doc, nil
}

// defaultKnotsPath is where the scene looks: %LOCALAPPDATA%\delta\ix-knots.json on Windows.
func defaultKnotsPath() string {
	dir, err := os.UserCacheDir()
	if err != nil {
		dir = os.TempDir()
	}
	return filepath.Join(dir, "delta", "ix-knots.json")
}

// runIXKnots is brain -ix-knots: the gallery as IX reads it, or why there is none. 0 when every exhibit is verified.
func runIXKnots(exe, out string, stdout, stderr io.Writer) int {
	now := time.Now()
	if out == "" {
		out = defaultKnotsPath()
	}
	write := func(doc knotsDoc) error {
		body, err := json.Marshal(doc)
		if err != nil {
			return err
		}
		return writeDocument(out, body)
	}
	fail := func(why string) int {
		if err := write(knotsDoc{Format: knotsFormat, V: knotsVersion, Source: "ix_knot", At: now.UTC().Format(time.RFC3339), Error: why}); err != nil {
			fmt.Fprintf(stderr, "ix-knots: %s; and the floor's document could not be written: %v\n", why, err)
			return 1
		}
		fmt.Fprintf(stderr, "ix-knots: %s; the floor says so (%s)\n", why, out)
		return 1
	}
	exe, err := ixExecutable(exe)
	if err != nil {
		return fail(err.Error())
	}
	doc, err := knotsGallery(func(_ knotSpec, ropes []knotRope) ([]byte, error) {
		ctx, cancel := context.WithTimeout(context.Background(), ixCallSeconds*time.Second)
		defer cancel()
		return ixKnotDrawing(ctx, exe, ropes)
	}, now)
	if err != nil {
		return fail(err.Error())
	}
	if err := write(doc); err != nil {
		fmt.Fprintf(stderr, "ix-knots: %v\n", err)
		return 1
	}
	code := 0
	for _, e := range doc.Exhibits {
		verdict := "verified"
		if !e.Verified {
			verdict, code = "NOT the tables' knot", 1
		} else if e.Mirror {
			verdict = "verified (its mirror)"
		}
		fmt.Fprintf(stdout, "ix-knots: %-12s %-5s %d crossing(s) drawn, writhe %3d, Jones %s: %s; tube %.4f\n",
			e.ID, e.Rolfsen, e.Crossings, e.Writhe, e.Jones, verdict, e.Radius)
	}
	fmt.Fprintf(stdout, "ix-knots: %d exhibits written to %s\n", len(doc.Exhibits), out)
	return code
}
