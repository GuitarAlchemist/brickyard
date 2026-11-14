package main

import (
	"fmt"
	"math"
	"sort"
)

// An isometry of the plane: x -> M x + T.
type iso struct {
	M [2][2]float64
	T pt
}

var identity = iso{M: [2][2]float64{{1, 0}, {0, 1}}}

func (a iso) apply(p pt) pt {
	return pt{a.M[0][0]*p[0] + a.M[0][1]*p[1] + a.T[0], a.M[1][0]*p[0] + a.M[1][1]*p[1] + a.T[1]}
}

// then: a after b, x -> a(b(x)).
func (a iso) then(b iso) iso {
	var m [2][2]float64
	for i := 0; i < 2; i++ {
		for j := 0; j < 2; j++ {
			m[i][j] = a.M[i][0]*b.M[0][j] + a.M[i][1]*b.M[1][j]
		}
	}
	return iso{m, a.apply(b.T)}
}

func (a iso) det() float64 { return a.M[0][0]*a.M[1][1] - a.M[0][1]*a.M[1][0] }

func (a iso) near(b iso) bool {
	d := math.Abs(a.T[0]-b.T[0]) + math.Abs(a.T[1]-b.T[1])
	for i := 0; i < 2; i++ {
		for j := 0; j < 2; j++ {
			d += math.Abs(a.M[i][j] - b.M[i][j])
		}
	}
	return d < 1e-7
}

// reflection about the line through p and q.
func reflection(p, q pt) iso {
	th := math.Atan2(q[1]-p[1], q[0]-p[0])
	c, s := math.Cos(2*th), math.Sin(2*th)
	m := [2][2]float64{{c, s}, {s, -c}}
	r := iso{M: m}
	mp := r.apply(p)
	r.T = pt{p[0] - mp[0], p[1] - mp[1]}
	return r
}

// A flat folded state of the sheet: which edges are folded, and where each fine face lies.
type flat struct {
	Folded []bool
	At     []iso
}

// place folds the sheet flat by its folded edges, from the root face kept where it is; it fails where the faces met
// round a vertex disagree, which is where the folded edges break Kawasaki's theorem.
func (s *sheet) place(folded []bool, root int) (flat, error) {
	at := make([]iso, len(s.F))
	done := make([]bool, len(s.F))
	at[root], done[root] = identity, true
	queue := []int{root}
	var bad []string
	for len(queue) > 0 {
		f := queue[0]
		queue = queue[1:]
		for _, e := range s.faceEdges[f] {
			for _, g := range s.edgeFaces[e] {
				if g == f {
					continue
				}
				want := at[f]
				if folded[e] {
					want = at[f].then(reflection(s.V[s.E[e][0]], s.V[s.E[e][1]]))
				}
				if !done[g] {
					at[g], done[g] = want, true
					queue = append(queue, g)
				} else if !at[g].near(want) {
					bad = append(bad, fmt.Sprintf("e%d (%.4f,%.4f)-(%.4f,%.4f)", e, s.V[s.E[e][0]][0], s.V[s.E[e][0]][1], s.V[s.E[e][1]][0], s.V[s.E[e][1]][1]))
				}
			}
		}
	}
	if len(bad) > 0 {
		sort.Strings(bad)
		return flat{}, fmt.Errorf("the folded edges do not close round their vertices: %d edges disagree, first %v", len(bad), bad[:min(len(bad), 6)])
	}
	return flat{folded, at}, nil
}

// A step's reduced pattern: the fine faces joined across the edges it leaves flat, into its own faces.
type reduced struct {
	V      []pt // the pattern's vertices kept, in the square
	Folded []pt // where each lies folded
	E      [][2]int
	Fine   []int // for each reduced edge, one fine edge it covers
	F      [][]int
	Of     []int   // the reduced face of each fine face
	Faces  [][]int // the fine faces of each reduced face
	At     []iso   // each reduced face's place
}

func (s *sheet) reduce(st flat) (*reduced, error) {
	parent := make([]int, len(s.F))
	for i := range parent {
		parent[i] = i
	}
	var find func(int) int
	find = func(i int) int {
		for parent[i] != i {
			parent[i] = parent[parent[i]]
			i = parent[i]
		}
		return i
	}
	for e, fs := range s.edgeFaces {
		if len(fs) == 2 && !st.Folded[e] {
			parent[find(fs[0])] = find(fs[1])
		}
	}
	r := &reduced{Of: make([]int, len(s.F))}
	group := map[int]int{}
	for f := range s.F {
		g := find(f)
		if _, ok := group[g]; !ok {
			group[g] = len(r.Faces)
			r.Faces = append(r.Faces, nil)
			r.At = append(r.At, st.At[f])
		}
		r.Of[f] = group[g]
		r.Faces[group[g]] = append(r.Faces[group[g]], f)
	}
	// Each reduced face's boundary: the fine half-edges whose other side is another reduced face or nothing.
	vertexOf := map[int]int{}
	keep := func(v int) int {
		if i, ok := vertexOf[v]; ok {
			return i
		}
		vertexOf[v] = len(r.V)
		r.V = append(r.V, s.V[v])
		return len(r.V) - 1
	}
	type half struct{ a, b, e int }
	for rf, fines := range r.Faces {
		next := map[int]half{}
		for _, f := range fines {
			face := s.F[f]
			for k := range face {
				e := s.faceEdges[f][k]
				other := -1
				for _, g := range s.edgeFaces[e] {
					if g != f {
						other = g
					}
				}
				if other >= 0 && r.Of[other] == rf {
					continue
				}
				a, b := face[k], face[(k+1)%len(face)]
				if _, dup := next[a]; dup {
					return nil, fmt.Errorf("reduced face %d passes vertex %d twice", rf, a)
				}
				next[a] = half{a, b, e}
			}
		}
		// Walk the boundary once round.
		var start int
		for a := range next {
			start = a
			break
		}
		var loop []half
		for a := start; ; {
			h, ok := next[a]
			if !ok {
				return nil, fmt.Errorf("reduced face %d's boundary breaks at vertex %d", rf, a)
			}
			loop = append(loop, h)
			a = h.b
			if a == start {
				break
			}
			if len(loop) > len(next) {
				return nil, fmt.Errorf("reduced face %d's boundary does not close", rf)
			}
		}
		if len(loop) != len(next) {
			return nil, fmt.Errorf("reduced face %d has a hole or two boundaries", rf)
		}
		// Keep the corners: drop vertices where the boundary runs straight on.
		var corners []int
		var fineEdge []int
		for k, h := range loop {
			prev := loop[(k-1+len(loop))%len(loop)]
			p, q, w := s.V[prev.a], s.V[h.a], s.V[h.b]
			cross := (q[0]-p[0])*(w[1]-q[1]) - (q[1]-p[1])*(w[0]-q[0])
			if math.Abs(cross) > 1e-10 {
				if cross < 0 {
					return nil, fmt.Errorf("reduced face %d is not convex at (%.4f, %.4f)", rf, q[0], q[1])
				}
				corners = append(corners, keep(h.a))
				fineEdge = append(fineEdge, h.e)
			}
		}
		r.F = append(r.F, corners)
		for k := range corners {
			a, b := corners[k], corners[(k+1)%len(corners)]
			if _, ok := r.edgeOf(a, b); !ok {
				r.E = append(r.E, [2]int{a, b})
				r.Fine = append(r.Fine, fineEdge[k])
			}
		}
	}
	r.Folded = make([]pt, len(r.V))
	for rf, face := range r.F {
		for _, v := range face {
			r.Folded[v] = r.At[rf].apply(r.V[v])
		}
	}
	return r, nil
}

func (r *reduced) edgeOf(a, b int) (int, bool) {
	for e, uv := range r.E {
		if key(uv[0], uv[1]) == key(a, b) {
			return e, true
		}
	}
	return 0, false
}

// The reduced edges' faces, and the faces' folded polygons.
func (r *reduced) edgeFaces() [][]int {
	out := make([][]int, len(r.E))
	for f, face := range r.F {
		for k := range face {
			e, _ := r.edgeOf(face[k], face[(k+1)%len(face)])
			out[e] = append(out[e], f)
		}
	}
	return out
}

func (r *reduced) polygon(f int) []pt {
	var out []pt
	for _, v := range r.F[f] {
		out = append(out, r.Folded[v])
	}
	if r.At[f].det() < 0 {
		for i, j := 0, len(out)-1; i < j; i, j = i+1, j-1 {
			out[i], out[j] = out[j], out[i]
		}
	}
	return out
}
