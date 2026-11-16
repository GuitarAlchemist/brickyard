package main

// The knot board ("tableau de nœuds") of the knots' floor: knots tied in one rope, as a sailor's board shows them, each
// drawn loose with its two ends free. brain -ix-cordage sends each drawing to IX's ix_knot, which closes the rope by an
// arc over everything and reads the closure: its crossings, writhe and Jones polynomial. An exhibit is verified when
// that polynomial is the tables' for the knot its closure must be (or its mirror's): the overhand closes into the
// trefoil 3_1, the figure-eight into 4_1, the double overhand into 5_2, the stevedore into 6_1, and the granny and reef
// knots, two overhands in a row in one rope (as tied round a parcel), into the trefoil's sum with itself or with its
// mirror. The shapes are loose drawings of what the knot is, not a rope pulled tight: no physics.
//
// The document is the knots' floor's, format "delta-ix-knots" v 1, its ropes open ("closed": false), by default
// %LOCALAPPDATA%\delta\ix-cordage.json.

import (
	"io"
	"math"
	"os"
	"path/filepath"
)

// longKnot: the closed curve f sampled at n points and cut open at its highest point, where no strand passes above
// it, its two ends then drawn up and away. Closing it again by an arc over everything gives back f's knot.
func longKnot(f func(t float64) [3]float64, n int) [][3]float64 {
	pts := make([][3]float64, n)
	top := 0
	for i := range pts {
		pts[i] = f(2 * math.Pi * (float64(i) + 0.37) / float64(n))
		if pts[i][2] > pts[top][2] {
			top = i
		}
	}
	// The cut leaves out the few points round the top, so that the two ends start apart.
	const gap = 3
	body := make([][3]float64, 0, n)
	for i := gap; i <= n-gap; i++ {
		body = append(body, pts[(top+i)%n])
	}
	return withEnds(body)
}

// withEnds: the rope's two ends led up and away from the knot's middle, above all the rest.
func withEnds(body [][3]float64) [][3]float64 {
	lo, hi := extent(body)
	size := math.Max(hi[0]-lo[0], hi[1]-lo[1])
	lift := hi[2] - lo[2]
	mid := [3]float64{(lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2}
	tail := func(from [3]float64) [][3]float64 {
		dx, dy := from[0]-mid[0], from[1]-mid[1]
		l := math.Hypot(dx, dy)
		if l == 0 {
			dx, dy, l = 1, 0, 1
		}
		var out [][3]float64
		for k := 1; k <= 3; k++ {
			s := float64(k) / 3
			out = append(out, [3]float64{from[0] + 0.25*size*s*dx/l, from[1] + 0.25*size*s*dy/l, from[2] + 0.6*lift*s})
		}
		return out
	}
	start := tail(body[0])
	end := tail(body[len(body)-1])
	rope := make([][3]float64, 0, len(body)+6)
	for i := len(start) - 1; i >= 0; i-- {
		rope = append(rope, start[i])
	}
	rope = append(rope, body...)
	return append(rope, end...)
}

// inRow: two long knots in one rope, b beside a, a's end joined to b's start by a bridge above both knots and below
// the rope's two ends: their sum.
func inRow(a, b [][3]float64) [][3]float64 {
	a, b = a[:len(a)-3], b[3:] // the ends the bridge replaces
	alo, ahi := extent(a)
	blo, bhi := extent(b)
	shift := ahi[0] - blo[0] + 0.25*(ahi[0]-alo[0])
	moved := make([][3]float64, len(b))
	for i, p := range b {
		moved[i] = [3]float64{p[0] + shift, p[1], p[2]}
	}
	_, atop := extent(a[3:])
	_, btop := extent(b[:len(b)-3])
	knots := math.Max(atop[2], btop[2])
	z := knots + 0.3*(math.Min(ahi[2], bhi[2])-knots)
	last, first := a[len(a)-1], moved[0]
	rope := append([][3]float64{}, a...)
	rope = append(rope, [3]float64{(2*last[0] + first[0]) / 3, (last[1] + first[1]) / 2, z})
	rope = append(rope, [3]float64{(last[0] + 2*first[0]) / 3, (last[1] + first[1]) / 2, z})
	return append(rope, moved...)
}

// mirrored: the curve seen in a mirror across x = 0, its heights kept.
func mirrored(f func(t float64) [3]float64) func(t float64) [3]float64 {
	return func(t float64) [3]float64 {
		p := f(t)
		return [3]float64{-p[0], p[1], p[2]}
	}
}

func extent(pts [][3]float64) (lo, hi [3]float64) {
	lo, hi = [3]float64{math.Inf(1), math.Inf(1), math.Inf(1)}, [3]float64{math.Inf(-1), math.Inf(-1), math.Inf(-1)}
	for _, p := range pts {
		for a := 0; a < 3; a++ {
			lo[a], hi[a] = math.Min(lo[a], p[a]), math.Max(hi[a], p[a])
		}
	}
	return lo, hi
}

// jonesProduct: a sum of knots has the product of their Jones polynomials.
func jonesProduct(a, b map[float64]int) map[float64]int {
	out := map[float64]int{}
	for ea, ca := range a {
		for eb, cb := range b {
			out[ea+eb] += ca * cb
		}
	}
	for e, c := range out {
		if c == 0 {
			delete(out, e)
		}
	}
	return out
}

func jonesMirror(a map[float64]int) map[float64]int {
	out := map[float64]int{}
	for e, c := range a {
		out[-e] = c
	}
	return out
}

// The board's knots, in the order they hang. Their curves and polynomials are the knots' floor's (ixknots.go).
var cordageGallery = func() []knotSpec {
	byID := map[string]knotSpec{}
	for _, k := range knotGallery {
		byID[k.ID] = k
	}
	trefoil, eight, three, stevedore := byID["trefoil"], byID["figure-eight"], byID["three-twist"], byID["stevedore"]
	tilt := func(f func(t float64) [3]float64) func(t float64) [3]float64 { return turnedBy(f, 0.11, 0.07) }
	return []knotSpec{
		{ID: "overhand", Rolfsen: "3_1", En: "Overhand knot", Fr: "Nœud simple", Jones: trefoil.Jones,
			Open: func() [][3]float64 { return longKnot(tilt(trefoil.Curve), 160) }},
		{ID: "figure-eight-knot", Rolfsen: "4_1", En: "Figure-eight knot", Fr: "Nœud en huit", Jones: eight.Jones,
			Open: func() [][3]float64 { return longKnot(tilt(eight.Curve), 180) }},
		{ID: "double-overhand", Rolfsen: "5_2", En: "Double overhand knot", Fr: "Nœud double", Jones: three.Jones,
			Open: func() [][3]float64 { return longKnot(tilt(three.Curve), 240) }},
		{ID: "stevedore-knot", Rolfsen: "6_1", En: "Stevedore knot", Fr: "Nœud de docker", Jones: stevedore.Jones,
			Open: func() [][3]float64 { return longKnot(tilt(stevedore.Curve), 240) }},
		{ID: "granny", Rolfsen: "3_1#3_1", En: "Granny knot", Fr: "Nœud de vache", Jones: jonesProduct(trefoil.Jones, trefoil.Jones),
			Open: func() [][3]float64 {
				return inRow(longKnot(tilt(trefoil.Curve), 118), longKnot(tilt(trefoil.Curve), 118))
			}},
		{ID: "reef", Rolfsen: "3_1#3_1*", En: "Reef knot", Fr: "Nœud plat", Jones: jonesProduct(trefoil.Jones, jonesMirror(trefoil.Jones)),
			Open: func() [][3]float64 {
				return inRow(longKnot(tilt(trefoil.Curve), 118), longKnot(tilt(mirrored(trefoil.Curve)), 118))
			}},
	}
}()

// defaultCordagePath is where the scene looks: %LOCALAPPDATA%\delta\ix-cordage.json on Windows.
func defaultCordagePath() string {
	dir, err := os.UserCacheDir()
	if err != nil {
		dir = os.TempDir()
	}
	return filepath.Join(dir, "delta", "ix-cordage.json")
}

// runIXCordage is brain -ix-cordage: the knot board as IX reads it, or why there is none. 0 when every knot is verified.
func runIXCordage(exe, out string, stdout, stderr io.Writer) int {
	if out == "" {
		out = defaultCordagePath()
	}
	return runGallery("ix-cordage", cordageGallery, exe, out, stdout, stderr)
}
