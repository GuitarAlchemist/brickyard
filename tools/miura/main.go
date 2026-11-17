// miura writes a Miura-ori as a FOLD object: its crease pattern, its flat-folded state and that state's layer order,
// all from the exact rigid folding of the pattern (Schenk & Guest, PNAS 2013): parallelogram facets of sides a and b
// at an angle gamma, folded by theta from 0 (flat sheet) to pi/2 (flat-folded). Vertex (i, j), i across the straight
// creases and j across the zigzag ones, lies at
//
//	(i S, j L + [i odd] V, [j odd] H),  H = a sin(theta) sin(gamma), S = b cos(theta) tan(gamma) / k,
//	                                    L = a sqrt(1 - sin^2(theta) sin^2(gamma)), V = b / k, k = sqrt(1 + cos^2(theta) tan^2(gamma)),
//
// every facet keeping its sides and angle for every theta. The mountains and valleys are read from the fold at 45
// degrees, the flat-folded state is theta = pi/2 seen along x, and its layer order is read just before it.
//
//	go -C tools/miura run . [-m 12] [-n 8] [-gamma 65] [-ratio 1] [-frames 25] [-to 85] -out DIR/miura-12x8.fold
//
// It also writes the motion beside the fold (miura-12x8.motion.json): the sheet's vertices in 3D at -frames angles
// from flat to -to degrees, which brain -ix-origami carries to the hall's origami table. IX checks the FOLD (the flat
// fold and its layers); the motion is this closed form's, checked rigid here and again by the brain and the table.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"math"
	"os"
	"path/filepath"
	"strings"
)

type vec3 [3]float64

func sub(p, q vec3) vec3    { return vec3{p[0] - q[0], p[1] - q[1], p[2] - q[2]} }
func dot(p, q vec3) float64 { return p[0]*q[0] + p[1]*q[1] + p[2]*q[2] }
func cross(p, q vec3) vec3 {
	return vec3{p[1]*q[2] - p[2]*q[1], p[2]*q[0] - p[0]*q[2], p[0]*q[1] - p[1]*q[0]}
}
func length(p vec3) float64 { return math.Sqrt(dot(p, p)) }

type miura struct {
	m, n        int // facets across (i) and up (j)
	a, b, gamma float64
}

func (z miura) at(theta float64) []vec3 {
	st, ct, sg, tg := math.Sin(theta), math.Cos(theta), math.Sin(z.gamma), math.Tan(z.gamma)
	k := math.Sqrt(1 + ct*ct*tg*tg)
	H, S := z.a*st*sg, z.b*ct*tg/k
	L, V := z.a*math.Sqrt(1-st*st*sg*sg), z.b/k
	out := make([]vec3, (z.m+1)*(z.n+1))
	for j := 0; j <= z.n; j++ {
		for i := 0; i <= z.m; i++ {
			p := vec3{float64(i) * S, float64(j) * L, 0}
			if i%2 == 1 {
				p[1] += V
			}
			if j%2 == 1 {
				p[2] = H
			}
			out[j*(z.m+1)+i] = p
		}
	}
	return out
}

func (z miura) v(i, j int) int { return j*(z.m+1) + i }

type frame struct {
	Classes  []string    `json:"frame_classes"`
	Parent   int         `json:"frame_parent"`
	Inherit  bool        `json:"frame_inherit"`
	Vertices [][]float64 `json:"vertices_coords"`
	Orders   [][3]int    `json:"faceOrders"`
}

type fold struct {
	Spec       float64     `json:"file_spec"`
	Creator    string      `json:"file_creator"`
	Title      string      `json:"file_title"`
	Classes    []string    `json:"frame_classes"`
	Vertices   [][]float64 `json:"vertices_coords"`
	Edges      [][2]int    `json:"edges_vertices"`
	Assignment []string    `json:"edges_assignment"`
	Faces      [][4]int    `json:"faces_vertices"`
	Frames     []frame     `json:"file_frames"`
}

// clip: the convex polygon p cut by the convex, counter-clockwise polygon q (Sutherland–Hodgman).
func clip(p, q [][2]float64) [][2]float64 {
	out := p
	for k := range q {
		a, b := q[k], q[(k+1)%len(q)]
		inside := func(c [2]float64) bool { return (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]) >= 0 }
		in := out
		out = nil
		for i := range in {
			c, d := in[i], in[(i+1)%len(in)]
			ci, di := inside(c), inside(d)
			if ci {
				out = append(out, c)
			}
			if ci != di {
				// where cd crosses ab's line
				den := (b[0]-a[0])*(d[1]-c[1]) - (b[1]-a[1])*(d[0]-c[0])
				t := ((b[0]-a[0])*(a[1]-c[1]) - (b[1]-a[1])*(a[0]-c[0])) / den
				out = append(out, [2]float64{c[0] + t*(d[0]-c[0]), c[1] + t*(d[1]-c[1])})
			}
		}
		if len(out) == 0 {
			return nil
		}
	}
	return out
}

func area(p [][2]float64) float64 {
	s := 0.0
	for i := range p {
		a, b := p[i], p[(i+1)%len(p)]
		s += a[0]*b[1] - b[0]*a[1]
	}
	return s / 2
}

func centroid(p [][2]float64) [2]float64 {
	var cx, cy, s float64
	for i := range p {
		a, b := p[i], p[(i+1)%len(p)]
		w := a[0]*b[1] - b[0]*a[1]
		cx += (a[0] + b[0]) * w
		cy += (a[1] + b[1]) * w
		s += w
	}
	return [2]float64{cx / (3 * s), cy / (3 * s)}
}

// depth: x on the face (a parallelogram, nearly in the yz-plane) at its projected point q, from the face's plane.
func depth(face []vec3, q [2]float64) float64 {
	n := cross(sub(face[1], face[0]), sub(face[3], face[0]))
	// n·(p - face[0]) = 0 with p = (x, q0, q1)
	return face[0][0] - (n[1]*(q[0]-face[0][1])+n[2]*(q[1]-face[0][2]))/n[0]
}

func main() {
	m := flag.Int("m", 12, "facets across the straight creases")
	n := flag.Int("n", 8, "facets across the zigzags")
	gammaDeg := flag.Float64("gamma", 65, "the facets' acute angle, degrees")
	ratio := flag.Float64("ratio", 1.0, "b / a")
	out := flag.String("out", "miura.fold", "the FOLD file; the motion goes beside it, as .motion.json")
	frames := flag.Int("frames", 25, "the motion's frames, flat first")
	to := flag.Float64("to", 85, "the motion's last fold angle, degrees (90 is flat-folded, its layers touching)")
	flag.Parse()
	z := miura{m: *m, n: *n, a: 1, b: *ratio, gamma: *gammaDeg * math.Pi / 180}

	flat := z.at(0)
	// The sheet's size, so that its larger side is 1.
	var w, h float64
	for _, p := range flat {
		w, h = math.Max(w, p[0]), math.Max(h, p[1])
	}
	scale := 1 / math.Max(w, h)

	var f fold
	f.Spec, f.Creator, f.Title = 1.2, "Delta (miura, exact rigid folding)", fmt.Sprintf("miura-ori %dx%d", z.m, z.n)
	f.Classes = []string{"creasePattern"}
	for _, p := range flat {
		f.Vertices = append(f.Vertices, []float64{p[0] * scale, p[1] * scale})
	}
	var faces [][4]int
	for j := 0; j < z.n; j++ {
		for i := 0; i < z.m; i++ {
			faces = append(faces, [4]int{z.v(i, j), z.v(i+1, j), z.v(i+1, j+1), z.v(i, j+1)})
		}
	}
	f.Faces = faces

	// Edges, each with the faces either side.
	type edge struct{ a, b, left, right int }
	index := map[[2]int]int{}
	var edges []edge
	for fi, fc := range faces {
		for k := 0; k < 4; k++ {
			a, b := fc[k], fc[(k+1)%4]
			key := [2]int{min(a, b), max(a, b)}
			if e, ok := index[key]; ok {
				edges[e].right = fi
				continue
			}
			index[key] = len(edges)
			edges = append(edges, edge{a, b, fi, -1})
		}
	}
	mid := z.at(math.Pi / 4)
	normal := func(pts []vec3, fc [4]int) vec3 {
		return cross(sub(pts[fc[1]], pts[fc[0]]), sub(pts[fc[3]], pts[fc[0]]))
	}
	centre := func(pts []vec3, fc [4]int) vec3 {
		var c vec3
		for _, v := range fc {
			for k := range 3 {
				c[k] += pts[v][k] / 4
			}
		}
		return c
	}
	mountains, valleys := 0, 0
	for _, e := range edges {
		f.Edges = append(f.Edges, [2]int{e.a, e.b})
		if e.right < 0 {
			f.Assignment = append(f.Assignment, "B")
			continue
		}
		// Valley: the other face turns towards this face's front (+z in the pattern).
		d := dot(sub(centre(mid, faces[e.right]), mid[e.a]), normal(mid, faces[e.left]))
		d2 := dot(sub(centre(mid, faces[e.left]), mid[e.a]), normal(mid, faces[e.right]))
		if d*d2 <= 0 || math.Abs(d) < 1e-9 {
			log.Fatalf("edge %d-%d bends neither way (%g, %g)", e.a, e.b, d, d2)
		}
		if d > 0 {
			f.Assignment = append(f.Assignment, "V")
			valleys++
		} else {
			f.Assignment = append(f.Assignment, "M")
			mountains++
		}
	}

	// Every facet keeps its sides through the fold: checked at nine angles.
	worst := 0.0
	for s := 0; s <= 8; s++ {
		pts := z.at(float64(s) / 8 * math.Pi / 2)
		for _, e := range edges {
			worst = math.Max(worst, math.Abs(length(sub(pts[e.a], pts[e.b]))-length(sub(flat[e.a], flat[e.b]))))
		}
		for _, fc := range faces { // and stays flat: its fourth corner where the other three put it
			p := pts[fc[0]]
			q := sub(pts[fc[1]], p)
			r := sub(pts[fc[3]], p)
			want := vec3{p[0] + q[0] + r[0], p[1] + q[1] + r[1], p[2] + q[2] + r[2]}
			worst = math.Max(worst, length(sub(pts[fc[2]], want)))
		}
	}

	// The flat-folded state, seen along x: (y, z).
	folded := z.at(math.Pi / 2)
	var fr frame
	fr.Classes, fr.Parent, fr.Inherit = []string{"foldedForm"}, 0, true
	for _, p := range folded {
		fr.Vertices = append(fr.Vertices, []float64{p[1] * scale, p[2] * scale})
	}
	// The layer order, read just before flat: where two faces overlap, the one further along x is on that side.
	const eps = 1e-4
	near := z.at(math.Pi/2 - eps)
	poly := func(pts []vec3, fc [4]int) [][2]float64 {
		p := make([][2]float64, 4)
		for k, v := range fc {
			p[k] = [2]float64{pts[v][1], pts[v][2]}
		}
		return p
	}
	ccw := func(p [][2]float64) [][2]float64 {
		if area(p) < 0 {
			return [][2]float64{p[3], p[2], p[1], p[0]}
		}
		return p
	}
	faceArea := math.Abs(area(poly(near, faces[0])))
	smallest := math.Inf(1)
	for fi := range faces {
		for gi := fi + 1; gi < len(faces); gi++ {
			pf, pg := ccw(poly(near, faces[fi])), ccw(poly(near, faces[gi]))
			in := clip(pf, pg)
			if len(in) < 3 || math.Abs(area(in)) < 1e-3*faceArea {
				continue
			}
			q := centroid(in)
			pts := func(fc [4]int) []vec3 { return []vec3{near[fc[0]], near[fc[1]], near[fc[2]], near[fc[3]]} }
			dx := depth(pts(faces[fi]), q) - depth(pts(faces[gi]), q)
			smallest = math.Min(smallest, math.Abs(dx))
			if math.Abs(dx) < 1e-12 {
				log.Fatalf("faces %d and %d overlap at the same depth", fi, gi)
			}
			// g's normal in the folded frame: +w (w = x) when g is counter-clockwise in (y, z).
			gUp := area(poly(folded, faces[gi])) > 0
			s := 1
			if (dx > 0) != gUp {
				s = -1
			}
			fr.Orders = append(fr.Orders, [3]int{fi, gi, s})
		}
	}
	f.Frames = []frame{fr}
	body, _ := json.Marshal(f)
	if err := os.WriteFile(*out, body, 0o644); err != nil {
		log.Fatal(err)
	}

	// The motion: the sheet in 3D, from flat to the last angle, in the pattern's units (its x and y, z off the sheet's
	// front). Rounded to 1e-5, as the brain keeps them to 1e-4.
	if *frames < 2 || *to <= 0 || *to > 90 {
		log.Fatalf("-frames %d, -to %g: at least 2 frames, to an angle above 0 and at most 90", *frames, *to)
	}
	motion := struct {
		Note   string         `json:"note"`
		Frames [][][3]float64 `json:"frames"`
	}{Note: fmt.Sprintf("Miura-ori folded rigidly from flat to %g degrees, Schenk & Guest's closed form: every facet keeps its shape", *to)}
	r := func(x float64) float64 { return math.Round(x*1e5) / 1e5 }
	for k := 0; k < *frames; k++ {
		pts := z.at(float64(k) / float64(*frames-1) * *to * math.Pi / 180)
		var one [][3]float64
		for _, p := range pts {
			one = append(one, [3]float64{r(p[0] * scale), r(p[1] * scale), r(p[2] * scale)})
		}
		motion.Frames = append(motion.Frames, one)
	}
	mbody, _ := json.Marshal(motion)
	if err := os.WriteFile(strings.TrimSuffix(*out, filepath.Ext(*out))+".motion.json", mbody, 0o644); err != nil {
		log.Fatal(err)
	}
	fmt.Printf("miura %dx%d, gamma %.0f°, b/a %.2f: %d vertices, %d edges (%d M, %d V), %d faces, %d orders; facets kept to %.1e; depths apart by %.1e at least; %d frames to %g°\n",
		z.m, z.n, *gammaDeg, *ratio, len(f.Vertices), len(f.Edges), mountains, valleys, len(faces), len(fr.Orders), worst, smallest, *frames, *to)
}
