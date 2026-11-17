// Command crane folds Rabbit Ear's traditional crane step by step, from the flat square to the bird, and writes each
// step's flat folded state as a FOLD for IX to check, with the rigid motion from each step to the next.
package main

import (
	"flag"
	"fmt"
	"math"
	"os"
	"path/filepath"
)

var perStep *int

func main() {
	in := flag.String("crane", "../../brain/testdata/origami/crane.fold", "Rabbit Ear's crane, the pattern every step folds part of")
	out := flag.String("out", "", "the folder to write each step's FOLD into (step-00.fold ...)")
	describe := flag.Bool("describe", false, "print the pattern's lines, edges and faces, and stop")
	perStep = flag.Int("frames", 10, "frames from each step to the next")
	flag.Parse()
	if err := run(*in, *out, *describe); err != nil {
		fmt.Fprintln(os.Stderr, "crane:", err)
		os.Exit(1)
	}
}

func run(in, out string, describe bool) error {
	p, err := readCrane(in)
	if err != nil {
		return err
	}
	if describe {
		fmt.Printf("%d vertices, %d edges, %d faces\n", len(p.V), len(p.E), len(p.F))
		for _, l := range p.lines() {
			fmt.Printf("line %.2f° offset %.4f:", l.Deg, l.Offset)
			for _, e := range l.Edges {
				fmt.Printf(" e%d %s(v%d-v%d)", e, p.Assign[e], p.E[e][0], p.E[e][1])
			}
			fmt.Println()
		}
		return nil
	}
	sh, err := build(p)
	if err != nil {
		return err
	}
	fmt.Printf("sheet: %d vertices, %d edges, %d fine faces\n", len(sh.V), len(sh.E), len(sh.F))
	all, err := sh.foldAll(sh.faceAt(pt{0.45, 0.33}))
	if err != nil {
		return err
	}
	if out == "" {
		return nil
	}
	if err := os.MkdirAll(out, 0o755); err != nil {
		return err
	}
	for i, f := range all {
		if err := writeJSON(filepath.Join(out, fmt.Sprintf("step-%02d.fold", i)), f.fold(fmt.Sprintf("crane, step %d: %s", i, f.step.En))); err != nil {
			return err
		}
	}
	root := sh.faceAt(pt{0.45, 0.33})
	frames, rests, flexes, err := sh.moveAll(all, root, *perStep)
	if err != nil {
		return err
	}
	return writeJSON(filepath.Join(out, "crane.steps.json"), sh.manifest(all, frames, rests, flexes))
}

// The steps' manifest, which brain -ix-origami reads: the sheet, each step's FOLD and layers, and the motion.
type manifest struct {
	Format string         `json:"format"`
	V      int            `json:"v"`
	Name   string         `json:"name"`
	Note   string         `json:"note"`
	Sheet  sheetOut       `json:"sheet"`
	Steps  []stepOut      `json:"steps"`
	Frames [][][3]float64 `json:"frames"`
}

type sheetOut struct {
	Vertices   [][2]float64 `json:"vertices"`
	Edges      [][2]int     `json:"edges"`
	Assignment []string     `json:"assignment"`
	Faces      [][]int      `json:"faces"`
}

type stepOut struct {
	Fr     string  `json:"fr"`
	En     string  `json:"en"`
	At     int     `json:"at"`
	Flex   float64 `json:"flex"`
	Layers []int   `json:"layers"`
	Fold   foldOut `json:"fold"`
}

func (s *sheet) manifest(all []folded, frames []pose3, rests []int, flexes []float64) manifest {
	r4 := func(x float64) float64 { return math.Round(x*1e4) / 1e4 }
	m := manifest{Format: "delta-origami-steps", V: 1, Name: "crane",
		Note: "the crane step by step: IX checks each flat state; the motion between is Delta's, and bends the paper where flex says"}
	last := all[len(all)-1]
	for _, v := range s.V {
		m.Sheet.Vertices = append(m.Sheet.Vertices, [2]float64{r4(v[0]), r4(v[1])})
	}
	for e, uv := range s.E {
		m.Sheet.Edges = append(m.Sheet.Edges, uv)
		a := "F"
		switch {
		case s.border(e):
			a = "B"
		case last.flat.Folded[e]:
			a = "M"
			if s.angles(last)[e] > 0 {
				a = "V"
			}
		}
		m.Sheet.Assignment = append(m.Sheet.Assignment, a)
	}
	m.Sheet.Faces = s.F
	for i, f := range all {
		layer := layersOf(f.o)
		st := stepOut{Fr: f.step.Fr, En: f.step.En, At: rests[i], Flex: r4(flexes[i]), Fold: f.fold(fmt.Sprintf("crane, step %d: %s", i, f.step.En))}
		for fine := range s.F {
			st.Layers = append(st.Layers, layer[f.r.Of[fine]])
		}
		m.Steps = append(m.Steps, st)
	}
	for _, p := range frames {
		sum := make([][3]float64, len(s.V))
		count := make([]float64, len(s.V))
		for f, face := range s.F {
			for _, v := range face {
				x := p.at(f, s.V[v])
				sum[v] = add3(sum[v], x)
				count[v]++
			}
		}
		var fr [][3]float64
		for v := range sum {
			fr = append(fr, [3]float64{r4(sum[v][0] / count[v]), r4(sum[v][1] / count[v]), r4(sum[v][2] / count[v])})
		}
		m.Frames = append(m.Frames, fr)
	}
	return m
}

// layersOf: each face's layer, 0 the lowest: the longest chain of faces beneath it.
func layersOf(o *order) []int {
	out := make([]int, o.n)
	done := make([]bool, o.n)
	var height func(f int) int
	height = func(f int) int {
		if done[f] {
			return out[f]
		}
		h := 0
		for g := 0; g < o.n; g++ {
			if o.overlap[f][g] && o.above[f][g] == 1 {
				h = max(h, height(g)+1)
			}
		}
		out[f], done[f] = h, true
		return h
	}
	for f := range out {
		height(f)
	}
	return out
}

// build: the crane's creases, the square's border and the steps' extra creases, cut into one sheet.
func build(p *pattern) (*sheet, error) {
	var segs []segment
	for e, uv := range p.E {
		segs = append(segs, segment{p.V[uv[0]], p.V[uv[1]], p.Assign[e]})
	}
	segs = append(segs, extra...)
	return cut(segs)
}

// folds: the sheet's edges on any of the creases.
func (s *sheet) folds(set []seg) []bool {
	out := make([]bool, len(s.E))
	for e := range s.E {
		if s.border(e) {
			continue
		}
		for _, c := range set {
			if s.onSegment(e, s.snap(c.A), s.snap(c.B)) {
				out[e] = true
				break
			}
		}
	}
	return out
}
