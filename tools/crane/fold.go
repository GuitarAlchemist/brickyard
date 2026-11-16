package main

import (
	"encoding/json"
	"fmt"
	"math"
	"os"
	"sort"
)

// A crease pattern: the square's vertices, its edges with their final assignment, and its faces, counterclockwise.
type pattern struct {
	V      [][2]float64
	E      [][2]int
	Assign []string
	F      [][]int
	// edgeOf[[u, w]] with u < w is the edge's index; faceEdges[f][k] is the edge from F[f][k] to F[f][k+1].
	edgeOf    map[[2]int]int
	faceEdges [][]int
	// edgeFaces[e] are the one or two faces on the edge.
	edgeFaces [][]int
}

type foldFile struct {
	Edges       [][2]int         `json:"edges_vertices"`
	Assignments []string         `json:"edges_assignment"`
	Faces       [][]int          `json:"faces_vertices"`
	Frames      []map[string]any `json:"file_frames"`
}

// readCrane reads Rabbit Ear's crane, whose top level is the folded form and whose file_frames[0] is the crease
// pattern's vertices.
func readCrane(path string) (*pattern, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var f foldFile
	if err := json.Unmarshal(raw, &f); err != nil {
		return nil, err
	}
	if len(f.Frames) == 0 {
		return nil, fmt.Errorf("%s has no crease pattern frame", path)
	}
	cpRaw, ok := f.Frames[0]["vertices_coords"].([]any)
	if !ok {
		return nil, fmt.Errorf("%s: the crease pattern frame has no vertices", path)
	}
	p := &pattern{E: f.Edges, Assign: f.Assignments, F: f.Faces}
	for _, v := range cpRaw {
		xy := v.([]any)
		p.V = append(p.V, [2]float64{xy[0].(float64), xy[1].(float64)})
	}
	// Faces counterclockwise in the crease pattern.
	for i, face := range p.F {
		if area(p.V, face) < 0 {
			r := make([]int, len(face))
			for k := range face {
				r[k] = face[len(face)-1-k]
			}
			p.F[i] = r
		}
	}
	return p, p.index()
}

func (p *pattern) index() error {
	p.edgeOf = map[[2]int]int{}
	for e, uv := range p.E {
		p.edgeOf[key(uv[0], uv[1])] = e
	}
	p.edgeFaces = make([][]int, len(p.E))
	p.faceEdges = make([][]int, len(p.F))
	for f, face := range p.F {
		for k := range face {
			e, ok := p.edgeOf[key(face[k], face[(k+1)%len(face)])]
			if !ok {
				return fmt.Errorf("face %d's side %d-%d is no edge", f, face[k], face[(k+1)%len(face)])
			}
			p.faceEdges[f] = append(p.faceEdges[f], e)
			p.edgeFaces[e] = append(p.edgeFaces[e], f)
		}
	}
	for e, fs := range p.edgeFaces {
		if (len(fs) == 1) != (p.Assign[e] == "B") || len(fs) > 2 {
			return fmt.Errorf("edge %d (%s) borders %d faces", e, p.Assign[e], len(fs))
		}
	}
	return nil
}

func key(u, w int) [2]int {
	if u > w {
		u, w = w, u
	}
	return [2]int{u, w}
}

func area(v [][2]float64, face []int) float64 {
	a := 0.0
	for k := range face {
		p, q := v[face[k]], v[face[(k+1)%len(face)]]
		a += p[0]*q[1] - q[0]*p[1]
	}
	return a / 2
}

func centroid(v [][2]float64, face []int) [2]float64 {
	var c [2]float64
	for _, i := range face {
		c[0] += v[i][0]
		c[1] += v[i][1]
	}
	return [2]float64{c[0] / float64(len(face)), c[1] / float64(len(face))}
}

// The lines the pattern's creases lie on: each a direction (degrees, 0 to 180) and an offset, with its edges.
type line struct {
	Deg, Offset float64
	Edges       []int
}

func (p *pattern) lines() []line {
	var out []line
	for e, uv := range p.E {
		if p.Assign[e] == "B" {
			continue
		}
		a, b := p.V[uv[0]], p.V[uv[1]]
		deg := math.Mod(math.Atan2(b[1]-a[1], b[0]-a[0])*180/math.Pi+360, 180)
		if deg > 179.999 {
			deg = 0
		}
		r := deg * math.Pi / 180
		off := -math.Sin(r)*a[0] + math.Cos(r)*a[1]
		found := false
		for i := range out {
			if math.Abs(out[i].Deg-deg) < 1e-6 && math.Abs(out[i].Offset-off) < 1e-6 {
				out[i].Edges = append(out[i].Edges, e)
				found = true
				break
			}
		}
		if !found {
			out = append(out, line{deg, off, []int{e}})
		}
	}
	sort.Slice(out, func(i, j int) bool {
		if math.Abs(out[i].Deg-out[j].Deg) > 1e-6 {
			return out[i].Deg < out[j].Deg
		}
		return out[i].Offset < out[j].Offset
	})
	return out
}
