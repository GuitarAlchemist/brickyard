package main

import (
	"fmt"
	"math"
)

// clip: the intersection of two convex counterclockwise polygons.
func clip(subject, by []pt) []pt {
	out := subject
	for i := range by {
		a, b := by[i], by[(i+1)%len(by)]
		in := out
		out = nil
		side := func(p pt) float64 { return (b[0]-a[0])*(p[1]-a[1]) - (b[1]-a[1])*(p[0]-a[0]) }
		for j := range in {
			p, q := in[j], in[(j+1)%len(in)]
			sp, sq := side(p), side(q)
			if sp >= 0 {
				out = append(out, p)
			}
			if (sp >= 0) != (sq >= 0) {
				t := sp / (sp - sq)
				out = append(out, pt{p[0] + t*(q[0]-p[0]), p[1] + t*(q[1]-p[1])})
			}
		}
		if len(out) == 0 {
			return nil
		}
	}
	return out
}

func polyArea(p []pt) float64 {
	a := 0.0
	for i := range p {
		q, r := p[i], p[(i+1)%len(p)]
		a += q[0]*r[1] - r[0]*q[1]
	}
	return a / 2
}

// inside: the part of segment a-b strictly inside a convex polygon, as a length.
func inside(a, b pt, poly []pt) float64 {
	lo, hi := 0.0, 1.0
	for i := range poly {
		p, q := poly[i], poly[(i+1)%len(poly)]
		n := math.Hypot(q[0]-p[0], q[1]-p[1])
		side := func(x pt) float64 { return ((q[0]-p[0])*(x[1]-p[1]) - (q[1]-p[1])*(x[0]-p[0])) / n }
		sa, sb := side(a)-1e-9, side(b)-1e-9
		switch {
		case sa < 0 && sb < 0:
			return 0
		case sa < 0:
			lo = math.Max(lo, sa/(sa-sb))
		case sb < 0:
			hi = math.Min(hi, sa/(sa-sb))
		}
	}
	if hi <= lo {
		return 0
	}
	return (hi - lo) * math.Hypot(b[0]-a[0], b[1]-a[1])
}

// A layer order: above[f][g] is +1 when f lies above g (towards +z of the folded state), -1 below, 0 unknown, for
// faces that overlap.
type order struct {
	n       int
	overlap [][]bool
	above   [][]int8
}

func (o *order) clone() *order {
	c := &order{n: o.n, overlap: o.overlap, above: make([][]int8, o.n)}
	for i := range o.above {
		c.above[i] = append([]int8(nil), o.above[i]...)
	}
	return c
}

// set f above g (v = +1) or below (v = -1); false when it contradicts what is known.
func (o *order) set(f, g int, v int8) bool {
	if o.above[f][g] == -v {
		return false
	}
	o.above[f][g], o.above[g][f] = v, -v
	return true
}

// A constraint on a few faces that all overlap at one place: the orders of them it allows.
type group struct {
	faces []int
	ok    func(rank []int) bool // rank[i]: the height of faces[i], 0 the lowest
	why   string
}

// The layering problem of one step.
type layering struct {
	r        *reduced
	up       []bool // the face's front looks up (+z)
	polys    [][]pt
	o        *order
	groups   []group
	byPair   map[[2]int][]int
	prefer   map[[2]int]int8 // a preference for pairs the constraints leave open
	kept     map[[2]int]bool // pairs whose preference is the order of the step before
	isCrease map[[2]int]bool
	tier     map[[2]int]int
	against  []string
}

func newLayering(r *reduced) *layering {
	n := len(r.F)
	l := &layering{r: r, up: make([]bool, n), polys: make([][]pt, n), byPair: map[[2]int][]int{}, prefer: map[[2]int]int8{}, kept: map[[2]int]bool{}, isCrease: map[[2]int]bool{}, tier: map[[2]int]int{}}
	l.o = &order{n: n, overlap: make([][]bool, n), above: make([][]int8, n)}
	for f := 0; f < n; f++ {
		l.up[f] = r.At[f].det() > 0
		l.polys[f] = r.polygon(f)
		l.o.overlap[f] = make([]bool, n)
		l.o.above[f] = make([]int8, n)
	}
	for f := 0; f < n; f++ {
		for g := f + 1; g < n; g++ {
			if x := clip(l.polys[f], l.polys[g]); len(x) > 2 && polyArea(x) > 1e-10 {
				l.o.overlap[f][g], l.o.overlap[g][f] = true, true
			}
		}
	}
	// Transitivity in every place three faces overlap.
	for a := 0; a < n; a++ {
		for b := a + 1; b < n; b++ {
			if !l.o.overlap[a][b] {
				continue
			}
			ab := clip(l.polys[a], l.polys[b])
			for c := b + 1; c < n; c++ {
				if !l.o.overlap[a][c] || !l.o.overlap[b][c] {
					continue
				}
				if x := clip(ab, l.polys[c]); len(x) > 2 && polyArea(x) > 1e-10 {
					l.add(group{[]int{a, b, c}, func([]int) bool { return true }, "transitivity"})
				}
			}
		}
	}
	creases := l.creases()
	// Taco-tortilla: a face over a crease's line lies above both its faces or below both.
	for _, c := range creases {
		for h := 0; h < n; h++ {
			if h == c.f || h == c.g || inside(c.a, c.b, l.polys[h]) < 1e-9 {
				continue
			}
			l.add(group{[]int{c.f, c.g, h}, func(r []int) bool { return !between(r[2], r[0], r[1]) }, "taco-tortilla"})
		}
	}
	// Taco-taco: two creases on one line, their faces on the same side, do not interleave.
	for i, c := range creases {
		for _, d := range creases[i+1:] {
			if c.f == d.f || c.f == d.g || c.g == d.f || c.g == d.g || !collinearOverlap(c, d) || !sameSideOf(c, d, l.polys) {
				continue
			}
			l.add(group{[]int{c.f, c.g, d.f, d.g}, func(r []int) bool {
				return between(r[2], r[0], r[1]) == between(r[3], r[0], r[1])
			}, "taco-taco"})
		}
	}
	return l
}

func between(x, a, b int) bool { return (x > a && x < b) || (x > b && x < a) }

type crease struct {
	e, f, g int
	a, b    pt // folded
}

func (l *layering) creases() []crease {
	var out []crease
	ef := l.r.edgeFaces()
	for e, fs := range ef {
		if len(fs) == 2 {
			out = append(out, crease{e, fs[0], fs[1], l.r.Folded[l.r.E[e][0]], l.r.Folded[l.r.E[e][1]]})
		}
	}
	return out
}

func collinearOverlap(c, d crease) bool {
	dx, dy := c.b[0]-c.a[0], c.b[1]-c.a[1]
	n := math.Hypot(dx, dy)
	off := func(p pt) float64 { return ((p[0]-c.a[0])*dy - (p[1]-c.a[1])*dx) / n }
	if math.Abs(off(d.a)) > 1e-9 || math.Abs(off(d.b)) > 1e-9 {
		return false
	}
	t := func(p pt) float64 { return ((p[0]-c.a[0])*dx + (p[1]-c.a[1])*dy) / n }
	lo1, hi1 := 0.0, n
	lo2, hi2 := math.Min(t(d.a), t(d.b)), math.Max(t(d.a), t(d.b))
	return math.Min(hi1, hi2)-math.Max(lo1, lo2) > 1e-9
}

// sameSideOf: the four faces of two collinear creases lie on one side of the line.
func sameSideOf(c, d crease, polys [][]pt) bool {
	side := func(f int) float64 {
		var cx, cy float64
		for _, p := range polys[f] {
			cx += p[0]
			cy += p[1]
		}
		cx /= float64(len(polys[f]))
		cy /= float64(len(polys[f]))
		return (c.b[0]-c.a[0])*(cy-c.a[1]) - (c.b[1]-c.a[1])*(cx-c.a[0])
	}
	s := side(c.f)
	return s*side(d.f) > 0
}

func (l *layering) add(g group) {
	i := len(l.groups)
	l.groups = append(l.groups, g)
	for x := 0; x < len(g.faces); x++ {
		for y := x + 1; y < len(g.faces); y++ {
			k := key(g.faces[x], g.faces[y])
			l.byPair[k] = append(l.byPair[k], i)
		}
	}
}

var perms3, perms4 = permutations(3), permutations(4)

func permutations(n int) [][]int {
	if n == 1 {
		return [][]int{{0}}
	}
	var out [][]int
	for _, p := range permutations(n - 1) {
		for i := 0; i <= len(p); i++ {
			q := append(append(append([]int{}, p[:i]...), n-1), p[i:]...)
			out = append(out, q)
		}
	}
	return out
}

// propagate: deduce from every group touched until nothing changes; false on a contradiction.
func (l *layering) propagate(o *order, work []int) bool {
	queued := make([]bool, len(l.groups))
	for _, w := range work {
		queued[w] = true
	}
	for len(work) > 0 {
		gi := work[len(work)-1]
		work = work[:len(work)-1]
		queued[gi] = false
		g := l.groups[gi]
		perms := perms3
		if len(g.faces) == 4 {
			perms = perms4
		}
		var allowed [][]int
		for _, rank := range perms {
			if !g.ok(rank) {
				continue
			}
			fits := true
			for x := 0; x < len(rank) && fits; x++ {
				for y := 0; y < len(rank); y++ {
					if x == y {
						continue
					}
					a := o.above[g.faces[x]][g.faces[y]]
					if (a == 1 && rank[x] < rank[y]) || (a == -1 && rank[x] > rank[y]) {
						fits = false
						break
					}
				}
			}
			if fits {
				allowed = append(allowed, rank)
			}
		}
		if len(allowed) == 0 {
			return false
		}
		for x := 0; x < len(g.faces); x++ {
			for y := x + 1; y < len(g.faces); y++ {
				f, h := g.faces[x], g.faces[y]
				if o.above[f][h] != 0 || !o.overlap[f][h] {
					continue
				}
				v := int8(0)
				for i, rank := range allowed {
					w := int8(1)
					if rank[x] < rank[y] {
						w = -1
					}
					if i == 0 {
						v = w
					} else if v != w {
						v = 0
						break
					}
				}
				if v != 0 {
					o.set(f, h, v)
					for _, gj := range l.byPair[key(f, h)] {
						if !queued[gj] {
							queued[gj] = true
							work = append(work, gj)
						}
					}
				}
			}
		}
	}
	return true
}

// solve: every overlapping pair decided, the constraints kept, the preferences followed where they can be; how many
// preferences it had to go against.
func (l *layering) solve() (*order, int, error) {
	o := l.o.clone()
	all := make([]int, len(l.groups))
	for i := range all {
		all[i] = i
	}
	if !l.propagate(o, all) {
		return nil, 0, fmt.Errorf("the fixed relations already contradict the constraints")
	}
	against := 0
	var dfs func(o *order, depth int) *order
	nodes := 0
	dfs = func(o *order, depth int) *order {
		nodes++
		if nodes > 200000 {
			return nil
		}
		// Decide the creases first, then the orders kept from the step before, then the rest.
		f, g, best := -1, -1, -1
		for a := 0; a < o.n; a++ {
			for b := a + 1; b < o.n; b++ {
				if !o.overlap[a][b] || o.above[a][b] != 0 {
					continue
				}
				p := l.tier[key(a, b)]
				if p > best {
					f, g, best = a, b, p
				}
			}
		}
		if f < 0 {
			return o
		}
		first := l.prefer[[2]int{f, g}]
		if first == 0 {
			first = 1
		}
		for _, v := range []int8{first, -first} {
			c := o.clone()
			c.set(f, g, v)
			if l.propagate(c, append([]int(nil), l.byPair[key(f, g)]...)) {
				if r := dfs(c, depth+1); r != nil {
					if v != first && l.prefer[[2]int{f, g}] != 0 {
						against++
						l.against = append(l.against, fmt.Sprintf("%d/%d tier %d", f, g, l.tier[key(f, g)]))
					}
					return r
				}
			}
		}
		return nil
	}
	r := dfs(o, 0)
	if r == nil {
		return nil, 0, fmt.Errorf("no layer order keeps every constraint (%d tries)", nodes)
	}
	return r, against, nil
}

// The preferences, strongest first: a step's own mountain or valley, a folded crease whose faces moved together,
// the final crane's side for a new crease, the order of two faces kept from the step before.
const (
	tierKept = iota + 1
	tierFinal
	tierRigid
	tierHint
)

// want: prefer f above g (+1) or below, unless a stronger preference has the pair; whether it took.
func (l *layering) want(f, g int, above int8, tier int) bool {
	k := key(f, g)
	if l.tier[k] >= tier {
		return false
	}
	l.tier[k] = tier
	l.prefer[[2]int{f, g}] = above
	l.prefer[[2]int{g, f}] = -above
	return true
}

// fix: a relation known before solving, as a crease's mountain or valley or an order kept from the step before.
func (l *layering) fix(f, g int, above int8) bool {
	if !l.o.overlap[f][g] {
		return true
	}
	return l.o.set(f, g, above)
}

// valleyAbove: across a valley, f lies on the side g's front looks to.
func (l *layering) valleyAbove(f, g int) int8 {
	if l.up[g] {
		return 1
	}
	return -1
}
