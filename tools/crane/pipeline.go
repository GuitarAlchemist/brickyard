package main

import (
	"encoding/json"
	"fmt"
	"math"
	"os"
)

// A step folded: its flat state, its reduced pattern, the layer order found and each crease's mountain or valley.
type folded struct {
	step     step
	flat     flat
	r        *reduced
	o        *order
	assign   []string // per reduced edge: B, M or V
	against  int
	fineSide map[[2]int]int8 // FOLD's side of fine face pairs that overlap: s of [f, g, s]
}

// fold every step in turn, each step's order keeping the relations of the faces that moved together since the last.
func (s *sheet) foldAll(root int) ([]folded, error) {
	var out []folded
	var prev *folded
	for i, st := range steps {
		fl, err := s.place(s.folds(st.Folds), root)
		if err != nil {
			return nil, fmt.Errorf("step %d (%s): %w", i, st.En, err)
		}
		r, err := s.reduce(fl)
		if err != nil {
			return nil, fmt.Errorf("step %d (%s): %w", i, st.En, err)
		}
		l := newLayering(r)
		kept := 0
		if prev != nil {
			for k, side := range prev.fineSide {
				phi, psi := k[0], k[1]
				f, g := r.Of[phi], r.Of[psi]
				if f == g {
					continue
				}
				moveF := fl.At[phi].then(inverse(prev.flat.At[phi]))
				moveG := fl.At[psi].then(inverse(prev.flat.At[psi]))
				if !moveF.near(moveG) || !l.o.overlap[f][g] {
					continue
				}
				// s of [phi, psi, s]: phi on the side psi's front looks to; the same of f and g.
				above := side
				if !l.up[g] {
					above = -side
				}
				if l.want(f, g, above, tierKept) {
					l.kept[key(f, g)] = true
					kept++
				}
			}
		}
		// The creases' mountains and valleys: the step's own word first, then a crease that was folded and whose faces
		// moved together keeps its side, then a new crease takes the final crane's.
		var before []float64
		if prev != nil {
			before = s.angles(*prev)
		}
		for _, c := range l.creases() {
			l.isCrease[key(c.f, c.g)] = true
			fe := r.Fine[c.e]
			as := func(want string, tier int) {
				v := l.valleyAbove(c.f, c.g)
				if want == "M" {
					v = -v
				}
				l.want(c.f, c.g, v, tier)
			}
			if h, ok := hint(i, s, r, c.e); ok {
				as(h, tierHint)
			}
			if prev != nil && prev.flat.Folded[fe] {
				phi, psi := s.edgeFaces[fe][0], s.edgeFaces[fe][1]
				if fl.At[phi].then(inverse(prev.flat.At[phi])).near(fl.At[psi].then(inverse(prev.flat.At[psi]))) {
					was := "M"
					if before[fe] > 0 {
						was = "V"
					}
					as(was, tierRigid)
				}
			}
			// The final crane's side, by most of the crease's pieces: a piece a later reverse fold turns says the other.
			votes := 0
			a, b := r.V[r.E[c.e][0]], r.V[r.E[c.e][1]]
			for e := range s.E {
				if !fl.Folded[e] || !s.onSegment(e, a, b) {
					continue
				}
				switch s.Final[e] {
				case "V":
					votes++
				case "M":
					votes--
				}
			}
			want := "V"
			if votes < 0 {
				want = "M"
			}
			as(want, tierFinal)
		}
		o, against, err := l.solve()
		if err != nil {
			return nil, fmt.Errorf("step %d (%s): %w", i, st.En, err)
		}
		f := folded{step: st, flat: fl, r: r, o: o, against: against, fineSide: map[[2]int]int8{}}
		for _, fs := range r.edgeFaces() {
			if len(fs) == 1 {
				f.assign = append(f.assign, "B")
				continue
			}
			if o.above[fs[0]][fs[1]] == l.valleyAbove(fs[0], fs[1]) {
				f.assign = append(f.assign, "V")
			} else {
				f.assign = append(f.assign, "M")
			}
		}
		// The fine faces' sides, for the next step.
		for phi := range s.F {
			for psi := range s.F {
				a, b := r.Of[phi], r.Of[psi]
				if phi == psi || a == b || !o.overlap[a][b] {
					continue
				}
				pa, pb := fineFolded(s, fl, phi), fineFolded(s, fl, psi)
				if x := clip(pa, pb); len(x) < 3 || polyArea(x) < 1e-10 {
					continue
				}
				side := o.above[a][b]
				if !l.up[b] {
					side = -side
				}
				f.fineSide[[2]int{phi, psi}] = side
			}
		}
		fmt.Printf("step %2d %-45s %2d faces, %4d pairs, %3d kept, %d preferences gone against %v\n", i, st.En, len(r.F), countPairs(o), kept, against, l.against)
		out = append(out, f)
		prev = &out[len(out)-1]
	}
	return out, nil
}

func countPairs(o *order) int {
	n := 0
	for a := 0; a < o.n; a++ {
		for b := a + 1; b < o.n; b++ {
			if o.overlap[a][b] {
				n++
			}
		}
	}
	return n
}

func fineFolded(s *sheet, fl flat, f int) []pt {
	var out []pt
	for _, v := range s.F[f] {
		out = append(out, fl.At[f].apply(s.V[v]))
	}
	if fl.At[f].det() < 0 {
		for i, j := 0, len(out)-1; i < j; i, j = i+1, j-1 {
			out[i], out[j] = out[j], out[i]
		}
	}
	return out
}

func inverse(a iso) iso {
	d := a.det()
	m := [2][2]float64{{a.M[1][1] / d, -a.M[0][1] / d}, {-a.M[1][0] / d, a.M[0][0] / d}}
	inv := iso{M: m}
	t := inv.apply(a.T)
	inv.T = pt{-t[0], -t[1]}
	return inv
}

// hint: the step's own word on a crease, when it names one.
func hint(i int, s *sheet, r *reduced, e int) (string, bool) {
	fe := r.Fine[e]
	for _, h := range hints[i] {
		if s.onSegment(fe, s.snap(h.A), s.snap(h.B)) {
			return h.As, true
		}
	}
	return "", false
}

type frame struct {
	Classes  []string    `json:"frame_classes"`
	Parent   int         `json:"frame_parent"`
	Inherit  bool        `json:"frame_inherit"`
	Vertices [][]float64 `json:"vertices_coords"`
	Orders   [][3]int    `json:"faceOrders"`
}

type foldOut struct {
	Spec       float64     `json:"file_spec"`
	Creator    string      `json:"file_creator"`
	Title      string      `json:"file_title"`
	Classes    []string    `json:"frame_classes"`
	Vertices   [][]float64 `json:"vertices_coords"`
	Edges      [][2]int    `json:"edges_vertices"`
	Assignment []string    `json:"edges_assignment"`
	Faces      [][]int     `json:"faces_vertices"`
	Frames     []frame     `json:"file_frames"`
}

// fold: the step as a FOLD, its crease pattern at the top and its flat folded state with the layer order in frame 1.
func (f folded) fold(title string) foldOut {
	r := f.r
	out := foldOut{Spec: 1.2, Creator: "brickyard tools/crane", Title: title, Classes: []string{"creasePattern"}, Edges: r.E, Assignment: f.assign, Faces: r.F}
	round := func(x float64) float64 { return math.Round(x*1e12) / 1e12 }
	for _, v := range r.V {
		out.Vertices = append(out.Vertices, []float64{round(v[0]), round(v[1])})
	}
	fr := frame{Classes: []string{"foldedForm"}, Parent: 0, Inherit: true}
	for _, v := range r.Folded {
		fr.Vertices = append(fr.Vertices, []float64{round(v[0]), round(v[1])})
	}
	fr.Orders = [][3]int{}
	for a := 0; a < f.o.n; a++ {
		for b := a + 1; b < f.o.n; b++ {
			if !f.o.overlap[a][b] {
				continue
			}
			// [a, b, s]: s = +1 when a lies on the side b's front looks to.
			s := int(f.o.above[a][b])
			if r.At[b].det() < 0 {
				s = -s
			}
			fr.Orders = append(fr.Orders, [3]int{a, b, s})
		}
	}
	out.Frames = []frame{fr}
	return out
}

func writeJSON(path string, v any) error {
	b, err := json.Marshal(v)
	if err != nil {
		return err
	}
	return os.WriteFile(path, b, 0o644)
}
