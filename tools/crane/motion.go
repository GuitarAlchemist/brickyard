package main

import (
	"fmt"
	"math"
)

// The sheet in space: each fine face a rigid copy of its piece of the square, x -> R x + T, x = (u, v, 0).
type pose3 struct {
	R [][3][3]float64
	T [][3]float64
}

type vec3 = [3]float64

func add3(a, b vec3) vec3           { return vec3{a[0] + b[0], a[1] + b[1], a[2] + b[2]} }
func sub3(a, b vec3) vec3           { return vec3{a[0] - b[0], a[1] - b[1], a[2] - b[2]} }
func scale3(a vec3, s float64) vec3 { return vec3{a[0] * s, a[1] * s, a[2] * s} }
func dot3(a, b vec3) float64        { return a[0]*b[0] + a[1]*b[1] + a[2]*b[2] }
func cross3(a, b vec3) vec3 {
	return vec3{a[1]*b[2] - a[2]*b[1], a[2]*b[0] - a[0]*b[2], a[0]*b[1] - a[1]*b[0]}
}
func norm3(a vec3) float64 { return math.Sqrt(dot3(a, a)) }

func mulMV(m [3][3]float64, v vec3) vec3 {
	return vec3{m[0][0]*v[0] + m[0][1]*v[1] + m[0][2]*v[2], m[1][0]*v[0] + m[1][1]*v[1] + m[1][2]*v[2], m[2][0]*v[0] + m[2][1]*v[1] + m[2][2]*v[2]}
}

func mulMM(a, b [3][3]float64) [3][3]float64 {
	var m [3][3]float64
	for i := 0; i < 3; i++ {
		for j := 0; j < 3; j++ {
			m[i][j] = a[i][0]*b[0][j] + a[i][1]*b[1][j] + a[i][2]*b[2][j]
		}
	}
	return m
}

// rodrigues: the rotation by |w| about w.
func rodrigues(w vec3) [3][3]float64 {
	th := norm3(w)
	if th < 1e-15 {
		return [3][3]float64{{1, -w[2], w[1]}, {w[2], 1, -w[0]}, {-w[1], w[0], 1}}
	}
	k := scale3(w, 1/th)
	c, s := math.Cos(th), math.Sin(th)
	var m [3][3]float64
	K := [3][3]float64{{0, -k[2], k[1]}, {k[2], 0, -k[0]}, {-k[1], k[0], 0}}
	KK := mulMM(K, K)
	for i := 0; i < 3; i++ {
		for j := 0; j < 3; j++ {
			id := 0.0
			if i == j {
				id = 1
			}
			m[i][j] = id + s*K[i][j] + (1-c)*KK[i][j]
		}
	}
	return m
}

// flatPose: a flat state in space, each face's plane isometry made a rotation (a mirror turns the face over about an
// axis in the plane).
func (s *sheet) flatPose(fl flat) pose3 {
	p := pose3{R: make([][3][3]float64, len(s.F)), T: make([][3]float64, len(s.F))}
	for f, a := range fl.At {
		d := a.det()
		p.R[f] = [3][3]float64{{a.M[0][0], a.M[0][1], 0}, {a.M[1][0], a.M[1][1], 0}, {0, 0, d}}
		p.T[f] = vec3{a.T[0], a.T[1], 0}
	}
	return p
}

func (p pose3) clone() pose3 {
	return pose3{R: append([][3][3]float64(nil), p.R...), T: append([][3]float64(nil), p.T...)}
}

func (p pose3) at(f int, uv pt) vec3 { return add3(mulMV(p.R[f], vec3{uv[0], uv[1], 0}), p.T[f]) }

// foldAngle: how far the face on the right of edge e (as its left face runs it) has turned about it, positive for a
// valley (the fronts turning towards each other), in (-pi, pi].
func (s *sheet) foldAngle(p pose3, e int) float64 {
	fs := s.edgeFaces[e]
	f, g := fs[0], fs[1]
	a, b := s.E[e][0], s.E[e][1]
	// Run the edge as f runs it, counterclockwise.
	face := s.F[f]
	for k := range face {
		if face[k] == b && face[(k+1)%len(face)] == a {
			a, b = b, a
		}
	}
	d := sub3(p.at(f, s.V[b]), p.at(f, s.V[a]))
	d = scale3(d, 1/norm3(d))
	nf, ng := mulMV(p.R[f], vec3{0, 0, 1}), mulMV(p.R[g], vec3{0, 0, 1})
	return math.Atan2(-dot3(d, cross3(nf, ng)), dot3(nf, ng))
}

func wrap(x float64) float64 {
	for x > math.Pi {
		x -= 2 * math.Pi
	}
	for x <= -math.Pi {
		x += 2 * math.Pi
	}
	return x
}

// The rigid-face fold between two states: Gauss-Newton on each face's turn and shift (the root's fixed), the edges'
// ends held together (weight 100) and each edge turned to its angle along the way (weight 1).
type mover struct {
	s        *sheet
	root     int
	interior []int
	// cur: each edge's angle as the motion has carried it, not wrapped: a face goes on round, never jumps to the
	// other side of ±180°.
	cur []float64
	// weight: each edge's angle weight; an edge flat at both ends of the move is the sheet itself, as stiff as a joint.
	weight []float64
}

// carry: the angle of edge e now, taken on from where the motion had it.
func (m *mover) carry(p pose3, e int) float64 {
	return m.cur[e] + wrap(m.s.foldAngle(p, e)-m.cur[e])
}

func (m *mover) settle(p pose3) {
	for _, e := range m.interior {
		m.cur[e] = m.carry(p, e)
	}
}

func (s *sheet) mover(root int) *mover {
	m := &mover{s: s, root: root, cur: make([]float64, len(s.E)), weight: make([]float64, len(s.E))}
	for e := range s.E {
		if len(s.edgeFaces[e]) == 2 {
			m.interior = append(m.interior, e)
		}
	}
	return m
}

const jointWeight, angleWeight, flatWeight, flipWeight, driveWeight = 100.0, 1.0, 100.0, 0.02, 10.0

// residuals of the edges touching the faces in touch (all when nil).
func (m *mover) residuals(p pose3, target []float64, out []float64) {
	k := 0
	for _, e := range m.interior {
		f, g := m.s.edgeFaces[e][0], m.s.edgeFaces[e][1]
		for _, v := range m.s.E[e] {
			d := sub3(p.at(f, m.s.V[v]), p.at(g, m.s.V[v]))
			out[k], out[k+1], out[k+2] = jointWeight*d[0], jointWeight*d[1], jointWeight*d[2]
			k += 3
		}
		out[k] = m.weight[e] * (m.carry(p, e) - target[e])
		k++
	}
}

func (m *mover) nres() int { return 7 * len(m.interior) }

// solve moves p towards the edges' target angles; it returns the widest gap left at an edge's end.
func (m *mover) solve(p pose3, target []float64, iters int) float64 {
	n := len(m.s.F)
	nr := m.nres()
	r := make([]float64, nr)
	rp := make([]float64, nr)
	// The residuals each face moves: those of its edges.
	rowsOf := make([][]int, n)
	for i, e := range m.interior {
		for _, f := range m.s.edgeFaces[e] {
			for k := 0; k < 7; k++ {
				rowsOf[f] = append(rowsOf[f], 7*i+k)
			}
		}
	}
	np := 6 * n
	lambda := 1e-3
	for it := 0; it < iters; it++ {
		m.residuals(p, target, r)
		cost := 0.0
		for _, x := range r {
			cost += x * x
		}
		// The Jacobian, a column per parameter, sparse by face.
		type col struct {
			rows []int
			vals []float64
		}
		cols := make([]col, np)
		const h = 1e-7
		for f := 0; f < n; f++ {
			if f == m.root {
				continue
			}
			for j := 0; j < 6; j++ {
				q := p
				q.R = append([][3][3]float64(nil), p.R...)
				q.T = append([][3]float64(nil), p.T...)
				if j < 3 {
					var w vec3
					w[j] = h
					q.R[f] = mulMM(rodrigues(w), p.R[f])
				} else {
					q.T[f][j-3] += h
				}
				m.residuals(q, target, rp)
				c := col{}
				for _, row := range rowsOf[f] {
					d := (rp[row] - r[row]) / h
					if d != 0 {
						c.rows = append(c.rows, row)
						c.vals = append(c.vals, d)
					}
				}
				cols[6*f+j] = c
			}
		}
		// Normal equations.
		A := make([][]float64, np)
		for i := range A {
			A[i] = make([]float64, np)
		}
		g := make([]float64, np)
		dense := make([][]float64, np)
		for i, c := range cols {
			if len(c.rows) == 0 {
				continue
			}
			dense[i] = make([]float64, nr)
			for k, row := range c.rows {
				dense[i][row] = c.vals[k]
			}
		}
		for i := 0; i < np; i++ {
			if dense[i] == nil {
				A[i][i] = 1
				continue
			}
			for k, row := range cols[i].rows {
				g[i] += cols[i].vals[k] * r[row]
			}
			for j := i; j < np; j++ {
				if dense[j] == nil {
					continue
				}
				sum := 0.0
				for k, row := range cols[i].rows {
					sum += cols[i].vals[k] * dense[j][row]
				}
				A[i][j], A[j][i] = sum, sum
			}
		}
		for {
			M := make([][]float64, np)
			for i := range M {
				M[i] = append([]float64(nil), A[i]...)
				M[i][i] += lambda * (1 + A[i][i])
			}
			step, ok := cholSolve(M, g)
			if !ok {
				lambda *= 10
				continue
			}
			q := p.clone()
			for f := 0; f < n; f++ {
				if f == m.root {
					continue
				}
				w := vec3{-step[6*f], -step[6*f+1], -step[6*f+2]}
				q.R[f] = mulMM(rodrigues(w), p.R[f])
				q.T[f] = add3(p.T[f], vec3{-step[6*f+3], -step[6*f+4], -step[6*f+5]})
			}
			m.residuals(q, target, rp)
			c2 := 0.0
			for _, x := range rp {
				c2 += x * x
			}
			if c2 < cost {
				copy(p.R, q.R)
				copy(p.T, q.T)
				lambda = math.Max(lambda/3, 1e-9)
				break
			}
			lambda *= 10
			if lambda > 1e6 {
				break
			}
		}
		if lambda > 1e6 {
			break
		}
	}
	return m.gap(p)
}

func (m *mover) gap(p pose3) float64 {
	worst := 0.0
	for _, e := range m.interior {
		f, g := m.s.edgeFaces[e][0], m.s.edgeFaces[e][1]
		for _, v := range m.s.E[e] {
			worst = math.Max(worst, norm3(sub3(p.at(f, m.s.V[v]), p.at(g, m.s.V[v]))))
		}
	}
	return worst
}

func cholSolve(A [][]float64, b []float64) ([]float64, bool) {
	n := len(A)
	L := make([][]float64, n)
	for i := range L {
		L[i] = make([]float64, n)
	}
	for i := 0; i < n; i++ {
		for j := 0; j <= i; j++ {
			sum := A[i][j]
			for k := 0; k < j; k++ {
				sum -= L[i][k] * L[j][k]
			}
			if i == j {
				if sum <= 0 {
					return nil, false
				}
				L[i][i] = math.Sqrt(sum)
			} else {
				L[i][j] = sum / L[j][j]
			}
		}
	}
	y := make([]float64, n)
	for i := 0; i < n; i++ {
		sum := b[i]
		for k := 0; k < i; k++ {
			sum -= L[i][k] * y[k]
		}
		y[i] = sum / L[i][i]
	}
	x := make([]float64, n)
	for i := n - 1; i >= 0; i-- {
		sum := y[i]
		for k := i + 1; k < n; k++ {
			sum -= L[k][i] * x[k]
		}
		x[i] = sum / L[i][i]
	}
	return x, true
}

// angles: each fine edge's fold angle in a flat state, from its mountain or valley (0 when flat).
func (s *sheet) angles(f folded) []float64 {
	out := make([]float64, len(s.E))
	for e, fs := range s.edgeFaces {
		if len(fs) != 2 || !f.flat.Folded[e] {
			continue
		}
		a, b := f.r.Of[fs[0]], f.r.Of[fs[1]]
		up := f.flat.At[fs[1]].det() > 0
		valleyAbove := int8(-1)
		if f.r.At[b].det() > 0 {
			valleyAbove = 1
		}
		_ = up
		if f.o.above[a][b] == valleyAbove {
			out[e] = math.Pi
		} else {
			out[e] = -math.Pi
		}
		// foldAngle measures the right face against the left; a valley is a valley either way.
	}
	return out
}

// moveAll: the frames from each step to the next, n per step, where each step rests, and how far each move bends
// the paper (the widest change of a distance within a face, once each vertex is placed where its faces put it).
func (s *sheet) moveAll(all []folded, root, per int) ([]pose3, []int, []float64, error) {
	m := s.mover(root)
	p := s.flatPose(all[0].flat)
	frames := []pose3{p.clone()}
	rests := []int{0}
	flexes := []float64{0}
	for k := 1; k < len(all); k++ {
		from, to := s.angles(all[k-1]), s.angles(all[k])
		flipping := false
		for e := range from {
			if from[e]*to[e] < 0 {
				flipping = true
			}
		}
		copy(m.cur, from)
		for e := range m.weight {
			m.weight[e] = angleWeight
			if from[e] == 0 && to[e] == 0 {
				m.weight[e] = flatWeight
			}
			if from[e]*to[e] < 0 {
				m.weight[e] = flipWeight // a spine a reverse fold turns inside out: it follows the faces round
			}
			if flipping && (from[e] == 0) != (to[e] == 0) {
				m.weight[e] = driveWeight // the reverse fold's new creases drive it
			}
		}
		// A reverse fold, as hands make it: open the flap (its spine unfolds part way), turn the tip inside out (the
		// hinges fold, the spine beyond them goes over to the other side), close the flap again.
		spine := make([]bool, len(s.E))
		if flipping {
			for e := range from {
				if from[e]*to[e] >= 0 && from[e] != 0 && from[e] == to[e] {
					for f := range from {
						if from[f]*to[f] < 0 && collinear(s, e, f) {
							spine[e] = true
						}
					}
				}
			}
		}
		target := func(t float64) []float64 {
			out := make([]float64, len(s.E))
			for e := range out {
				out[e] = from[e] + (to[e]-from[e])*t
			}
			if !flipping {
				return out
			}
			const open = 0.35
			phase := func(a, b float64) float64 { return math.Max(0, math.Min(1, (t-a)/(b-a))) }
			o, turn, c := phase(0, 1.0/3), phase(1.0/3, 2.0/3), phase(2.0/3, 1)
			for e := range out {
				switch {
				case spine[e]: // the spine inside: opens, then closes
					sgn := math.Copysign(1, from[e])
					out[e] = sgn * math.Pi * (1 - (1-open)*o + (1-open)*c)
				case from[e]*to[e] < 0: // the spine beyond: opens, crosses, closes on the other side
					sgn := math.Copysign(1, from[e])
					mag := math.Pi * (1 - (1-open)*o)
					out[e] = sgn * (mag - 2*open*math.Pi*turn)
					if c > 0 {
						out[e] = -sgn * math.Pi * (open + (1-open)*c)
					}
				case from[e] != to[e]: // the hinges: fold while the tip turns
					out[e] = from[e] + (to[e]-from[e])*turn
				}
			}
			return out
		}
		var moving []pose3
		n := per
		if flipping {
			n = 3 * per
		}
		for i := 1; i < n; i++ {
			m.solve(p, target(float64(i)/float64(n)), 30)
			m.settle(p)
			moving = append(moving, p.clone())
		}
		end := s.flatPose(all[k].flat)
		for e := range m.weight {
			if from[e] != 0 && from[e] == to[e] {
				m.weight[e] = angleWeight
			}
		}
		m.solve(p, to, 30)
		off := 0.0
		for f := range s.F {
			for _, v := range s.F[f] {
				off = math.Max(off, norm3(sub3(p.at(f, s.V[v]), end.at(f, s.V[v]))))
			}
		}
		// Where the faces do not come to the flat state together (a reverse fold, which rigid faces make only by
		// opening the flap flat), each face is placed from the root along a tree of edges at the angles interpolated:
		// every face rigid, the tree's joints closed, the other joints open as far as the gap below says.

		flex, gap := 0.0, 0.0
		for _, q := range moving {
			flex = math.Max(flex, s.flex(q))
			gap = math.Max(gap, m.gap(q))
		}
		frames = append(frames, moving...)
		p = end.clone()
		frames = append(frames, p.clone())
		rests = append(rests, len(frames)-1)
		flexes = append(flexes, flex)
		fmt.Printf("move %2d -> %2d: widest gap at a joint %.2e, the paper bends %.2e, the faces came %.2e from the flat state by themselves\n", k-1, k, gap, flex, off)
	}
	return frames, rests, flexes, nil
}

// flex: with each vertex where its faces put it on average, the widest change of a distance within a face.
func (s *sheet) flex(p pose3) float64 {
	at := s.vertices(p)
	worst := 0.0
	for _, face := range s.F {
		for a := 0; a < len(face); a++ {
			for b := a + 1; b < len(face); b++ {
				got := norm3(sub3(at[face[a]], at[face[b]]))
				want := math.Hypot(s.V[face[a]][0]-s.V[face[b]][0], s.V[face[a]][1]-s.V[face[b]][1])
				worst = math.Max(worst, math.Abs(got-want))
			}
		}
	}
	return worst
}

func (s *sheet) vertices(p pose3) []vec3 {
	sum := make([]vec3, len(s.V))
	count := make([]float64, len(s.V))
	for f, face := range s.F {
		for _, v := range face {
			sum[v] = add3(sum[v], p.at(f, s.V[v]))
			count[v]++
		}
	}
	for v := range sum {
		sum[v] = scale3(sum[v], 1/count[v])
	}
	return sum
}

// collinear: two edges on one line.
func collinear(s *sheet, e, f int) bool {
	a, b := s.V[s.E[e][0]], s.V[s.E[e][1]]
	for _, v := range s.E[f] {
		p := s.V[v]
		if math.Abs((b[0]-a[0])*(p[1]-a[1])-(b[1]-a[1])*(p[0]-a[0])) > 1e-9 {
			return false
		}
	}
	return true
}
