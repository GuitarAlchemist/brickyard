package main

import (
	"fmt"
	"math"
	"sort"
)

const eps = 1e-9

type pt = [2]float64

// A sheet: the unit square cut by every crease any step makes, into fine faces. Each edge knows the crease line it
// lies on (its two ends), and the final crane's assignment when the crane folds it.
type sheet struct {
	V     []pt
	E     [][2]int
	Final []string // the final crane's assignment of the edge: M, V, B, or "" when the crane leaves it flat
	F     [][]int  // counterclockwise
	// faceEdges[f][k]: the edge from F[f][k] to F[f][k+1]; edgeFaces[e]: its one or two faces.
	faceEdges [][]int
	edgeFaces [][]int
	edgeOf    map[[2]int]int
}

type segment struct {
	A, B   pt
	Assign string
}

// cut builds the sheet from segments: the square's border, the crane's creases and the extra creases the steps make.
func cut(segs []segment) (*sheet, error) {
	s := &sheet{edgeOf: map[[2]int]int{}}
	at := func(p pt) int {
		for i, q := range s.V {
			if math.Hypot(p[0]-q[0], p[1]-q[1]) < 1e-7 {
				return i
			}
		}
		s.V = append(s.V, p)
		return len(s.V) - 1
	}
	// Every segment's points: its ends and where others cross it.
	type onSeg struct {
		t float64
		v int
	}
	points := make([][]onSeg, len(segs))
	for i, a := range segs {
		points[i] = append(points[i], onSeg{0, at(a.A)}, onSeg{1, at(a.B)})
	}
	for i := range segs {
		for j := i + 1; j < len(segs); j++ {
			for _, x := range crossings(segs[i], segs[j]) {
				v := at(x.p)
				points[i] = append(points[i], onSeg{x.ti, v})
				points[j] = append(points[j], onSeg{x.tj, v})
			}
		}
	}
	for i, a := range segs {
		ps := points[i]
		sort.Slice(ps, func(x, y int) bool { return ps[x].t < ps[y].t })
		for k := 0; k+1 < len(ps); k++ {
			u, w := ps[k].v, ps[k+1].v
			if u == w {
				continue
			}
			key := key(u, w)
			if e, ok := s.edgeOf[key]; ok {
				if a.Assign != "" && s.Final[e] == "" {
					s.Final[e] = a.Assign
				}
				continue
			}
			s.edgeOf[key] = len(s.E)
			s.E = append(s.E, [2]int{u, w})
			s.Final = append(s.Final, a.Assign)
		}
	}
	return s, s.faces()
}

type crossing struct {
	p      pt
	ti, tj float64
}

// crossings: where segment b meets segment a (a point, or the ends of a collinear overlap), with each one's parameter.
func crossings(a, b segment) []crossing {
	d1 := pt{a.B[0] - a.A[0], a.B[1] - a.A[1]}
	d2 := pt{b.B[0] - b.A[0], b.B[1] - b.A[1]}
	den := d1[0]*d2[1] - d1[1]*d2[0]
	w := pt{b.A[0] - a.A[0], b.A[1] - a.A[1]}
	if math.Abs(den) < 1e-12 {
		// Parallel: collinear overlaps give each end inside the other.
		if math.Abs(w[0]*d1[1]-w[1]*d1[0]) > 1e-9*math.Hypot(d1[0], d1[1]) {
			return nil
		}
		var out []crossing
		proj := func(p pt, o pt, d pt) float64 {
			return ((p[0]-o[0])*d[0] + (p[1]-o[1])*d[1]) / (d[0]*d[0] + d[1]*d[1])
		}
		for _, p := range []pt{b.A, b.B} {
			if t := proj(p, a.A, d1); t > eps && t < 1-eps {
				out = append(out, crossing{p, t, proj(p, b.A, d2)})
			}
		}
		for _, p := range []pt{a.A, a.B} {
			if t := proj(p, b.A, d2); t > eps && t < 1-eps {
				out = append(out, crossing{p, proj(p, a.A, d1), t})
			}
		}
		return out
	}
	t := (w[0]*d2[1] - w[1]*d2[0]) / den
	u := (w[0]*d1[1] - w[1]*d1[0]) / den
	if t < -eps || t > 1+eps || u < -eps || u > 1+eps {
		return nil
	}
	return []crossing{{pt{a.A[0] + t*d1[0], a.A[1] + t*d1[1]}, t, u}}
}

// faces walks the planar graph: each half-edge's next is the one just clockwise of its twin at the far vertex.
func (s *sheet) faces() error {
	out := make([][]int, len(s.V))
	for _, e := range s.E {
		out[e[0]] = append(out[e[0]], e[1])
		out[e[1]] = append(out[e[1]], e[0])
	}
	angle := func(u, w int) float64 { return math.Atan2(s.V[w][1]-s.V[u][1], s.V[w][0]-s.V[u][0]) }
	for u := range out {
		sort.Slice(out[u], func(i, j int) bool { return angle(u, out[u][i]) < angle(u, out[u][j]) })
	}
	seen := map[[2]int]bool{}
	for u := range out {
		for _, w := range out[u] {
			if seen[[2]int{u, w}] {
				continue
			}
			var face []int
			a, b := u, w
			for !seen[[2]int{a, b}] {
				seen[[2]int{a, b}] = true
				face = append(face, a)
				// At b, the neighbours sorted counterclockwise; take the one just before a (clockwise of the twin).
				ns := out[b]
				k := 0
				for i, n := range ns {
					if n == a {
						k = i
					}
				}
				a, b = b, ns[(k-1+len(ns))%len(ns)]
			}
			if area(s.V, face) > 1e-12 {
				s.F = append(s.F, face)
			}
		}
	}
	s.faceEdges = make([][]int, len(s.F))
	s.edgeFaces = make([][]int, len(s.E))
	for f, face := range s.F {
		for k := range face {
			e := s.edgeOf[key(face[k], face[(k+1)%len(face)])]
			s.faceEdges[f] = append(s.faceEdges[f], e)
			s.edgeFaces[e] = append(s.edgeFaces[e], f)
		}
	}
	total := 0.0
	for _, f := range s.F {
		total += area(s.V, f)
	}
	if math.Abs(total-1) > 1e-9 {
		return fmt.Errorf("the faces cover %.9f of the square, not 1", total)
	}
	for e, fs := range s.edgeFaces {
		border := onBorder(s.V[s.E[e][0]]) && onBorder(s.V[s.E[e][1]]) && sameSide(s.V[s.E[e][0]], s.V[s.E[e][1]])
		if (len(fs) == 1) != border {
			return fmt.Errorf("edge %d borders %d faces", e, len(fs))
		}
	}
	return nil
}

func onBorder(p pt) bool {
	return p[0] < 1e-9 || p[0] > 1-1e-9 || p[1] < 1e-9 || p[1] > 1-1e-9
}

func sameSide(p, q pt) bool {
	for k := 0; k < 2; k++ {
		for _, c := range []float64{0, 1} {
			if math.Abs(p[k]-c) < 1e-9 && math.Abs(q[k]-c) < 1e-9 {
				return true
			}
		}
	}
	return false
}

func (s *sheet) border(e int) bool { return len(s.edgeFaces[e]) == 1 }

// onSegment: whether edge e lies on the segment from a to b.
func (s *sheet) onSegment(e int, a, b pt) bool {
	for _, v := range s.E[e] {
		p := s.V[v]
		d := pt{b[0] - a[0], b[1] - a[1]}
		l2 := d[0]*d[0] + d[1]*d[1]
		t := ((p[0]-a[0])*d[0] + (p[1]-a[1])*d[1]) / l2
		if t < -1e-9 || t > 1+1e-9 {
			return false
		}
		if math.Abs((p[0]-a[0])*d[1]-(p[1]-a[1])*d[0])/math.Sqrt(l2) > 1e-7 {
			return false
		}
	}
	return true
}

// snap: the sheet's vertex within 1e-3 of a point given to a few places, or the point itself.
func (s *sheet) snap(p pt) pt {
	best, d := p, 1e-3
	for _, v := range s.V {
		if h := math.Hypot(v[0]-p[0], v[1]-p[1]); h < d {
			best, d = v, h
		}
	}
	return best
}

// The face holding a point of the square.
func (s *sheet) faceAt(p pt) int {
	for f, face := range s.F {
		inside := true
		for k := range face {
			a, b := s.V[face[k]], s.V[face[(k+1)%len(face)]]
			if (b[0]-a[0])*(p[1]-a[1])-(b[1]-a[1])*(p[0]-a[0]) < -1e-12 {
				inside = false
				break
			}
		}
		if inside {
			return f
		}
	}
	return -1
}
