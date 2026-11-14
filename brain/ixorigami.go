package main

// The IX workshop's origami table: a fold that IX checked, with IX's verdict and each violation it found. brain
// -ix-origami FILE asks IX's ix_origami_check (ix-mcp, as for the knot: ixatelier.go) about a folding sequence (a
// .origami file, which IX replays into a folded state) or a FOLD object (any other file), and writes what the table
// shows as one versioned document, %LOCALAPPDATA%\delta\ix-origami.json, which the office's widget module draws: the
// crease pattern, the folded state as IX's folded frame places its vertices (flat, as IX states it: nothing moves
// between the two, and nothing is simulated), the verdict and the findings. A finding names IX's rule and, where IX
// gives them, the edges or vertices at fault; the table says it in words. When IX cannot answer, the document says
// why instead.
//
// The document, format "delta-ix-origami", v 1:
//
//	{"format", "v": 1, "source": "ix_origami_check", "at", "name", "input": "sequence" or "fold",
//	 "ok": IX's verdict on the folded state, "holds": whether a sequence's expectations held (absent for a fold),
//	 "rejected_by": [IX's rules that rejected it], "max_layers",
//	 "pattern": {"vertices": [[x, y]], "edges": [[a, b]], "assignment": ["M", "V", "B", ...], "faces": [[a, b, c, ...]]},
//	 "folded": [[x, y]] (the folded frame's vertices, by the pattern's indices; absent when there is none),
//	 "orders": [[f, g, s]] (the FOLD's faceOrders for the folded state, which IX's layer rules checked: s = 1 when
//	           face f lies on the side face g's normal points to, -1 opposite; absent without faces or a folded state),
//	 "pose": {"note", "ridge": [a, b], "parts": [{"name", "faces": [...], "to", "spread"}]} (Delta's illustrative 3D
//	         pose, from <name>.pose.json beside the fold: see origamiPose; IX checks the flat fold, not it),
//	 "motion": {"note", "frames": [[[x, y, z]]]} (the sheet folding: each frame the pattern's vertices in 3D, from
//	           <name>.motion.json beside the fold, refused unless every face keeps its shape in every frame: see
//	           origamiMotionFor; Delta's, from a closed form such as tools/miura's, not IX's),
//	 "findings": [{"rule", "where": "edge" or "vertex" or "", "ids": [...], "count", "value", "detail"}]}
//
// or {"format", "v": 1, "source", "at", "error"}. Bounds: 1024 vertices, 2048 edges, 1024 faces of at most 64
// vertices, 4096 face orders, coordinates within ±16, 32 findings of 64 ids, texts of 120 characters. "orders" came
// after the first documents; a reader of v 1 that does not know it draws the fold without its layers.

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"slices"
	"sort"
	"strings"
	"time"
)

const (
	origamiFormat      = "delta-ix-origami"
	origamiMaxVertices = 1024
	origamiMaxEdges    = 2048
	origamiMaxFaces    = 1024
	origamiMaxFaceSize = 64
	origamiMaxOrders   = 4096
	origamiMaxCoord    = 16
	origamiMaxFindings = 32
	origamiMaxIDs      = 64
	origamiMaxText     = 120
	origamiMaxInput    = 1 << 20
)

type origamiPattern struct {
	Vertices   [][2]float64 `json:"vertices"`
	Edges      [][2]int     `json:"edges"`
	Assignment []string     `json:"assignment"`
	Faces      [][]int      `json:"faces"`
}

type origamiFinding struct {
	Rule   string  `json:"rule"`
	Where  string  `json:"where,omitempty"`
	IDs    []int   `json:"ids,omitempty"`
	Count  int     `json:"count"`
	Value  float64 `json:"value,omitempty"`
	Detail string  `json:"detail,omitempty"`
}

type origamiDoc struct {
	Format     string           `json:"format"`
	V          int              `json:"v"`
	Source     string           `json:"source"`
	At         string           `json:"at"`
	Name       string           `json:"name,omitempty"`
	Input      string           `json:"input,omitempty"`
	OK         *bool            `json:"ok,omitempty"`
	Holds      *bool            `json:"holds,omitempty"`
	RejectedBy []string         `json:"rejected_by,omitempty"`
	MaxLayers  int              `json:"max_layers,omitempty"`
	Pattern    *origamiPattern  `json:"pattern,omitempty"`
	Folded     [][2]float64     `json:"folded,omitempty"`
	Orders     [][3]int         `json:"orders,omitempty"`
	Pose       *origamiPose     `json:"pose,omitempty"`
	Motion     *origamiMotion   `json:"motion,omitempty"`
	Findings   []origamiFinding `json:"findings,omitempty"`
	Error      string           `json:"error,omitempty"`
}

// A FOLD object as far as the table draws it: the crease pattern at the top level and the folded state in
// file_frames[0], as IX's replayed sequences have them, or the other way round when the top level is classed
// "foldedForm" and the frame "creasePattern", as Rabbit Ear's files have them (the frame inheriting the edges and faces).
// faceOrders, [f, g, s], are the folded state's: s = 1 when face f lies on the side g's normal points to, -1 opposite.
type foldObject struct {
	Title      string       `json:"file_title"`
	Classes    []string     `json:"frame_classes"`
	Vertices   [][]float64  `json:"vertices_coords"`
	Edges      [][]int      `json:"edges_vertices"`
	Assignment []string     `json:"edges_assignment"`
	Faces      [][]int      `json:"faces_vertices"`
	Orders     [][]float64  `json:"faceOrders"`
	Frames     []foldObject `json:"file_frames"`
}

// rabbitEarLayout: the folded form at the top level, the crease pattern in the first frame.
func (f foldObject) rabbitEarLayout() bool {
	return len(f.Frames) > 0 && slices.Contains(f.Classes, "foldedForm") && slices.Contains(f.Frames[0].Classes, "creasePattern")
}

// What ix_origami_check reports, as far as the table explains it.
type ixOrigamiReport struct {
	OK         *bool             `json:"ok"`
	RejectedBy []string          `json:"rejected_by"`
	Structure  []json.RawMessage `json:"structure"`
	Converse   []json.RawMessage `json:"converse_conflicts"`
	Isometry   struct {
		OK        bool    `json:"ok"`
		MaxChange float64 `json:"max_length_change"`
	} `json:"isometry"`
	Orientation struct {
		Violations []json.RawMessage `json:"violations"`
	} `json:"crease_orientation"`
	Layers struct {
		Adjacency struct {
			Violations []json.RawMessage `json:"violations"`
		} `json:"adjacency"`
		Cells struct {
			Cyclic    []json.RawMessage `json:"cyclic"`
			MaxLayers int               `json:"max_layers"`
		} `json:"cells"`
		TacoTaco struct {
			Violations []json.RawMessage `json:"violations"`
		} `json:"taco_taco"`
		TacoTortilla struct {
			Violations []json.RawMessage `json:"violations"`
		} `json:"taco_tortilla"`
		TortillaTortilla struct {
			Violations []json.RawMessage `json:"violations"`
		} `json:"tortilla_tortilla"`
	} `json:"layers"`
	Local struct {
		Kawasaki    []json.RawMessage `json:"kawasaki_failing"`
		KawasakiMax float64           `json:"kawasaki_max_abs_alt_sum"`
		Maekawa     []json.RawMessage `json:"maekawa_failing"`
	} `json:"local_theorems"`
	Sequence *struct {
		Name         string     `json:"name"`
		Holds        *bool      `json:"holds"`
		Fold         foldObject `json:"fold"`
		Expectations []struct {
			Expect string          `json:"expect"`
			Got    json.RawMessage `json:"got"`
			Holds  bool            `json:"holds"`
			Line   int             `json:"line"`
		} `json:"expectations"`
	} `json:"sequence"`
}

// origamiFromIX keeps what the table shows of IX's report: the pattern from the FOLD given (fold) or the one IX
// replayed (sequence), the folded frame, the verdict and the findings.
func origamiFromIX(input string, given []byte, answer []byte, name string, now time.Time) (origamiDoc, error) {
	var r ixOrigamiReport
	if err := json.Unmarshal(answer, &r); err != nil {
		return origamiDoc{}, fmt.Errorf("IX's answer is not an origami report: %v", err)
	}
	if r.OK == nil {
		return origamiDoc{}, errors.New("IX's report has no verdict")
	}
	var fold foldObject
	switch input {
	case "fold":
		if err := json.Unmarshal(given, &fold); err != nil {
			return origamiDoc{}, fmt.Errorf("the FOLD object given is not one: %v", err)
		}
	case "sequence":
		if r.Sequence == nil {
			return origamiDoc{}, errors.New("IX's report has no replayed sequence")
		}
		fold = r.Sequence.Fold
		if r.Sequence.Name != "" {
			name = r.Sequence.Name
		}
	default:
		return origamiDoc{}, fmt.Errorf("input %q is neither fold nor sequence", input)
	}
	if fold.Title != "" && input == "fold" {
		name = fold.Title
	}
	pattern, folded, orders, err := origamiPatternOf(fold)
	if err != nil {
		return origamiDoc{}, err
	}
	if err := origamiTextOK("name", name); err != nil {
		return origamiDoc{}, err
	}
	doc := origamiDoc{
		Format: origamiFormat, V: 1, Source: "ix_origami_check", At: now.UTC().Format(time.RFC3339), Name: name, Input: input,
		OK: r.OK, MaxLayers: r.Layers.Cells.MaxLayers, Pattern: pattern, Folded: folded, Orders: orders,
	}
	for _, rule := range r.RejectedBy {
		if origamiTextOK("rule", rule) == nil && len(doc.RejectedBy) < origamiMaxFindings {
			doc.RejectedBy = append(doc.RejectedBy, rule)
		}
	}
	add := func(f origamiFinding) {
		if len(doc.Findings) < origamiMaxFindings {
			doc.Findings = append(doc.Findings, f)
		}
	}
	for _, s := range r.Structure {
		add(origamiFinding{Rule: "structure", Count: 1, Detail: origamiShort(s)})
	}
	if !r.Isometry.OK {
		add(origamiFinding{Rule: "isometry", Count: 1, Value: round4(r.Isometry.MaxChange)})
	}
	idsOf := func(rule, where string, raw []json.RawMessage, value float64) {
		if len(raw) == 0 {
			return
		}
		f := origamiFinding{Rule: rule, Where: where, Count: len(raw), Value: round4(value)}
		for _, x := range raw {
			var id int
			if json.Unmarshal(x, &id) == nil && id >= 0 && len(f.IDs) < origamiMaxIDs {
				f.IDs = append(f.IDs, id)
			}
		}
		if len(f.IDs) == 0 {
			f.Where = "" // IX names them otherwise than by an index: counted, not placed
		}
		add(f)
	}
	idsOf("crease_orientation", "edge", r.Orientation.Violations, 0)
	idsOf("adjacency", "edge", r.Layers.Adjacency.Violations, 0)
	idsOf("taco_taco", "", r.Layers.TacoTaco.Violations, 0)
	idsOf("taco_tortilla", "", r.Layers.TacoTortilla.Violations, 0)
	idsOf("tortilla_tortilla", "", r.Layers.TortillaTortilla.Violations, 0)
	idsOf("cyclic", "", r.Layers.Cells.Cyclic, 0)
	idsOf("maekawa", "vertex", r.Local.Maekawa, 0)
	idsOf("kawasaki", "vertex", r.Local.Kawasaki, r.Local.KawasakiMax)
	idsOf("converse", "", r.Converse, 0)
	if r.Sequence != nil {
		doc.Holds = r.Sequence.Holds
		for _, e := range r.Sequence.Expectations {
			if !e.Holds {
				got := strings.Trim(string(e.Got), `"`)
				add(origamiFinding{Rule: "expectation", Count: 1, Value: float64(e.Line), Detail: origamiCut(e.Expect + " → " + got)})
			}
		}
	}
	for _, f := range doc.Findings {
		if f.Where == "edge" {
			for _, id := range f.IDs {
				if id >= len(pattern.Edges) {
					return origamiDoc{}, fmt.Errorf("IX's %s names edge %d of %d", f.Rule, id, len(pattern.Edges))
				}
			}
		}
		if f.Where == "vertex" {
			for _, id := range f.IDs {
				if id >= len(pattern.Vertices) {
					return origamiDoc{}, fmt.Errorf("IX's %s names vertex %d of %d", f.Rule, id, len(pattern.Vertices))
				}
			}
		}
	}
	return doc, nil
}

func origamiPatternOf(fold foldObject) (*origamiPattern, [][2]float64, [][3]int, error) {
	creases, flat := fold.Vertices, [][]float64(nil) // the crease pattern's vertices, the folded state's
	rawOrders := fold.Orders
	if len(fold.Frames) > 0 {
		flat, rawOrders = fold.Frames[0].Vertices, fold.Frames[0].Orders
		if len(rawOrders) == 0 {
			rawOrders = fold.Orders
		}
	}
	if fold.rabbitEarLayout() {
		creases, flat, rawOrders = fold.Frames[0].Vertices, fold.Vertices, fold.Orders
	}
	pattern, folded, err := origamiShapes(fold, creases, flat)
	if err != nil {
		return nil, nil, nil, err
	}
	if folded == nil || len(pattern.Faces) == 0 {
		return pattern, folded, nil, nil
	}
	if len(rawOrders) > origamiMaxOrders {
		return nil, nil, nil, fmt.Errorf("%d face orders, more than %d", len(rawOrders), origamiMaxOrders)
	}
	var orders [][3]int
	for i, o := range rawOrders {
		whole := func(x float64) bool { return x == math.Trunc(x) && !math.IsInf(x, 0) }
		if len(o) != 3 || !whole(o[0]) || !whole(o[1]) || !whole(o[2]) {
			return nil, nil, nil, fmt.Errorf("face order %d is not [f, g, s] in whole numbers", i)
		}
		f, g, s := int(o[0]), int(o[1]), int(o[2])
		if f < 0 || g < 0 || f >= len(pattern.Faces) || g >= len(pattern.Faces) || f == g || s < -1 || s > 1 {
			return nil, nil, nil, fmt.Errorf("face order %d is [%d, %d, %d], not two of the %d faces and -1, 0 or 1", i, f, g, s, len(pattern.Faces))
		}
		if s != 0 { // 0: the file does not know
			orders = append(orders, [3]int{f, g, s})
		}
	}
	return pattern, folded, orders, nil
}

// origamiShapes: the crease pattern's vertices, edges and faces, and the folded state's vertices, checked.
func origamiShapes(fold foldObject, creases, flat [][]float64) (*origamiPattern, [][2]float64, error) {
	coords := func(what string, raw [][]float64) ([][2]float64, error) {
		if len(raw) == 0 || len(raw) > origamiMaxVertices {
			return nil, fmt.Errorf("%s: %d vertices, not 1 to %d", what, len(raw), origamiMaxVertices)
		}
		out := make([][2]float64, len(raw))
		for i, p := range raw {
			if len(p) != 2 {
				return nil, fmt.Errorf("%s: vertex %d has %d coordinates, not 2", what, i, len(p))
			}
			for k, v := range p {
				if math.IsNaN(v) || math.IsInf(v, 0) || math.Abs(v) > origamiMaxCoord {
					return nil, fmt.Errorf("%s: vertex %d lies beyond ±%d", what, i, origamiMaxCoord)
				}
				out[i][k] = round4(v)
			}
		}
		return out, nil
	}
	vertices, err := coords("the crease pattern", creases)
	if err != nil {
		return nil, nil, err
	}
	n := len(vertices)
	if len(fold.Edges) == 0 || len(fold.Edges) > origamiMaxEdges || len(fold.Assignment) != len(fold.Edges) {
		return nil, nil, fmt.Errorf("%d edges with %d assignments, not 1 to %d of each", len(fold.Edges), len(fold.Assignment), origamiMaxEdges)
	}
	p := &origamiPattern{Vertices: vertices, Edges: make([][2]int, len(fold.Edges)), Assignment: make([]string, len(fold.Edges))}
	for i, e := range fold.Edges {
		if len(e) != 2 || e[0] < 0 || e[1] < 0 || e[0] >= n || e[1] >= n {
			return nil, nil, fmt.Errorf("edge %d joins no two of the %d vertices", i, n)
		}
		p.Edges[i] = [2]int{e[0], e[1]}
		switch a := fold.Assignment[i]; a {
		case "M", "V", "B", "F", "U", "C", "J":
			p.Assignment[i] = a
		default:
			return nil, nil, fmt.Errorf("edge %d is assigned %q, not M, V, B, F, U, C or J", i, a)
		}
	}
	if len(fold.Faces) > origamiMaxFaces {
		return nil, nil, fmt.Errorf("%d faces, more than %d", len(fold.Faces), origamiMaxFaces)
	}
	for i, f := range fold.Faces {
		if len(f) < 3 || len(f) > origamiMaxFaceSize {
			return nil, nil, fmt.Errorf("face %d has %d vertices, not 3 to %d", i, len(f), origamiMaxFaceSize)
		}
		for _, v := range f {
			if v < 0 || v >= n {
				return nil, nil, fmt.Errorf("face %d names vertex %d of %d", i, v, n)
			}
		}
		p.Faces = append(p.Faces, append([]int(nil), f...))
	}
	var folded [][2]float64
	if len(flat) > 0 {
		if folded, err = coords("the folded state", flat); err != nil {
			return nil, nil, err
		}
		if len(folded) != n {
			return nil, nil, fmt.Errorf("the folded state has %d vertices, the crease pattern %d", len(folded), n)
		}
	}
	return p, folded, nil
}

// An illustrative 3D pose of a flat-folded model, Delta's and not IX's: parts of its faces turned about a ridge (a line
// through two vertices of the folded state), each to To degrees from the side of the ridge it stands on, towards the
// side of the stack its layers are on; a Spread part has its two halves of layers turned apart. It comes from a file
// beside the fold, <name>.pose.json, and the table says it is illustrative.
type origamiPose struct {
	Note  string        `json:"note,omitempty"`
	Ridge [2]int        `json:"ridge"`
	Parts []origamiPart `json:"parts"`
}

type origamiPart struct {
	Name   string  `json:"name,omitempty"`
	Faces  []int   `json:"faces"`
	To     float64 `json:"to"`
	Spread bool    `json:"spread,omitempty"`
}

const origamiMaxParts = 8

// origamiPoseFor reads the pose beside file, checked against the document: nil when there is none.
func origamiPoseFor(file string, doc origamiDoc) (*origamiPose, error) {
	body, err := os.ReadFile(strings.TrimSuffix(file, filepath.Ext(file)) + ".pose.json")
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	if len(body) > 64<<10 {
		return nil, fmt.Errorf("the pose is larger than %d bytes", 64<<10)
	}
	dec := json.NewDecoder(bytes.NewReader(body))
	dec.DisallowUnknownFields()
	var pose origamiPose
	if err := dec.Decode(&pose); err != nil {
		return nil, fmt.Errorf("the pose is not one: %v", err)
	}
	if doc.Pattern == nil || doc.Folded == nil || len(doc.Orders) == 0 {
		return nil, errors.New("a pose needs the folded state's faces and orders")
	}
	if err := origamiTextOK("pose's note", pose.Note); err != nil {
		return nil, err
	}
	n, faces := len(doc.Folded), len(doc.Pattern.Faces)
	a, b := pose.Ridge[0], pose.Ridge[1]
	if a < 0 || b < 0 || a >= n || b >= n || math.Hypot(doc.Folded[a][0]-doc.Folded[b][0], doc.Folded[a][1]-doc.Folded[b][1]) < 1e-6 {
		return nil, fmt.Errorf("the ridge [%d, %d] is not two apart of the %d vertices", a, b, n)
	}
	if len(pose.Parts) == 0 || len(pose.Parts) > origamiMaxParts {
		return nil, fmt.Errorf("%d parts, not 1 to %d", len(pose.Parts), origamiMaxParts)
	}
	taken := map[int]bool{}
	for i, p := range pose.Parts {
		if err := origamiTextOK("part's name", p.Name); err != nil {
			return nil, err
		}
		if len(p.Faces) == 0 || math.IsNaN(p.To) || p.To < 0 || p.To > 180 {
			return nil, fmt.Errorf("part %d has no faces, or turns to %v, not 0 to 180 degrees", i, p.To)
		}
		for _, f := range p.Faces {
			if f < 0 || f >= faces || taken[f] {
				return nil, fmt.Errorf("part %d names face %d, not one of the %d or already in a part", i, f, faces)
			}
			taken[f] = true
		}
	}
	return &pose, nil
}

// The sheet folding, Delta's and not IX's: frames of the pattern's vertices in 3D, in the pattern's units, from a
// closed form (tools/miura). Every face keeps its shape in every frame (its vertices as far apart as in the pattern,
// within origamiRigid), so the sheet only folds: nothing stretches, nothing is cut or glued. Between frames the table
// turns each face rigidly. Whether faces pass through one another mid-fold is not checked.
type origamiMotion struct {
	Note   string         `json:"note,omitempty"`
	Frames [][][3]float64 `json:"frames"`
}

const (
	origamiMaxFrames      = 64
	origamiMaxMotionFaces = 256
	origamiRigid          = 1e-3 // how far a face's vertices may come apart or together, in the pattern's units
)

// origamiMotionFor reads the motion beside file, checked against the document: nil when there is none.
func origamiMotionFor(file string, doc origamiDoc) (*origamiMotion, error) {
	body, err := os.ReadFile(strings.TrimSuffix(file, filepath.Ext(file)) + ".motion.json")
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	if len(body) > origamiMaxInput {
		return nil, fmt.Errorf("the motion is larger than %d bytes", origamiMaxInput)
	}
	dec := json.NewDecoder(bytes.NewReader(body))
	dec.DisallowUnknownFields()
	var m origamiMotion
	if err := dec.Decode(&m); err != nil {
		return nil, fmt.Errorf("the motion is not one: %v", err)
	}
	if err := origamiMotionOK(&m, doc); err != nil {
		return nil, err
	}
	return &m, nil
}

// origamiMotionOK checks a motion against the pattern and rounds its coordinates as the document keeps them.
func origamiMotionOK(m *origamiMotion, doc origamiDoc) error {
	if err := origamiTextOK("motion's note", m.Note); err != nil {
		return err
	}
	if doc.Pattern == nil || len(doc.Pattern.Faces) == 0 || len(doc.Pattern.Faces) > origamiMaxMotionFaces {
		return fmt.Errorf("a motion needs the pattern's faces, 1 to %d of them", origamiMaxMotionFaces)
	}
	if len(m.Frames) < 2 || len(m.Frames) > origamiMaxFrames {
		return fmt.Errorf("%d frames, not 2 to %d", len(m.Frames), origamiMaxFrames)
	}
	v := doc.Pattern.Vertices
	for k, frame := range m.Frames {
		if len(frame) != len(v) {
			return fmt.Errorf("frame %d has %d vertices, the pattern %d", k, len(frame), len(v))
		}
		for i := range frame {
			for c := range 3 {
				x := frame[i][c]
				if math.IsNaN(x) || math.IsInf(x, 0) || math.Abs(x) > origamiMaxCoord {
					return fmt.Errorf("frame %d: vertex %d lies beyond ±%d", k, i, origamiMaxCoord)
				}
				frame[i][c] = round4(x)
			}
		}
		for f, face := range doc.Pattern.Faces {
			for a := 0; a < len(face); a++ {
				for b := a + 1; b < len(face); b++ {
					p, q := frame[face[a]], frame[face[b]]
					got := math.Sqrt((p[0]-q[0])*(p[0]-q[0]) + (p[1]-q[1])*(p[1]-q[1]) + (p[2]-q[2])*(p[2]-q[2]))
					want := math.Hypot(v[face[a]][0]-v[face[b]][0], v[face[a]][1]-v[face[b]][1])
					if math.Abs(got-want) > origamiRigid {
						return fmt.Errorf("frame %d: face %d's vertices %d and %d are %.4f apart, %.4f in the pattern: the face does not keep its shape",
							k, f, face[a], face[b], got, want)
					}
				}
			}
		}
	}
	return nil
}

func origamiTextOK(what, s string) error {
	if len([]rune(s)) > origamiMaxText || strings.ContainsFunc(s, func(r rune) bool { return r < 0x20 || r == 0x7f }) {
		return fmt.Errorf("the %s is longer than %d characters or holds a control character", what, origamiMaxText)
	}
	return nil
}

func origamiCut(s string) string {
	s = strings.Map(func(r rune) rune {
		if r < 0x20 || r == 0x7f {
			return ' '
		}
		return r
	}, s)
	if r := []rune(s); len(r) > origamiMaxText {
		s = string(r[:origamiMaxText-1]) + "…"
	}
	return s
}

// origamiShort is a structure problem as IX words it, in one line: its text, or its JSON.
func origamiShort(raw json.RawMessage) string {
	var s string
	if json.Unmarshal(raw, &s) == nil {
		return origamiCut(s)
	}
	var m map[string]any
	if json.Unmarshal(raw, &m) == nil {
		for _, k := range []string{"message", "problem", "kind"} {
			if v, ok := m[k].(string); ok {
				return origamiCut(v)
			}
		}
		keys := make([]string, 0, len(m))
		for k := range m {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		return origamiCut(strings.Join(keys, ", "))
	}
	return origamiCut(string(raw))
}

func origamiError(why string, now time.Time) origamiDoc {
	return origamiDoc{Format: origamiFormat, V: 1, Source: "ix_origami_check", At: now.UTC().Format(time.RFC3339), Error: origamiCut(why)}
}

func defaultOrigamiPath() string {
	return filepath.Join(filepath.Dir(defaultAtelierPath()), "ix-origami.json")
}

// runIXOrigami is brain -ix-origami: IX's verdict on the fold in file, or why there is none, written for the table.
// 0 when IX answered, whatever its verdict.
func runIXOrigami(file, exe, out string, stdout, stderr io.Writer) int {
	now := time.Now()
	if out == "" {
		out = defaultOrigamiPath()
	}
	write := func(doc origamiDoc) error {
		body, err := json.Marshal(doc)
		if err != nil {
			return err
		}
		return writeDocument(out, body)
	}
	fail := func(why string) int {
		if err := write(origamiError(why, now)); err != nil {
			fmt.Fprintf(stderr, "ix-origami: %s; and the table's document could not be written: %v\n", why, err)
			return 1
		}
		fmt.Fprintf(stderr, "ix-origami: %s; the table says so (%s)\n", why, out)
		return 1
	}
	given, err := os.ReadFile(file)
	if err != nil {
		fmt.Fprintf(stderr, "ix-origami: %v\n", err)
		return 2
	}
	if len(given) > origamiMaxInput {
		fmt.Fprintf(stderr, "ix-origami: %s is larger than %d bytes\n", file, origamiMaxInput)
		return 2
	}
	input, arguments := "fold", map[string]any{}
	if strings.EqualFold(filepath.Ext(file), ".origami") {
		input, arguments["sequence"] = "sequence", string(given)
	} else {
		var fold map[string]any
		if err := json.Unmarshal(given, &fold); err != nil {
			fmt.Fprintf(stderr, "ix-origami: %s is neither a .origami sequence nor a FOLD object: %v\n", file, err)
			return 2
		}
		arguments["fold"] = fold
	}
	if exe, err = ixExecutable(exe); err != nil {
		return fail(err.Error())
	}
	ctx, cancel := context.WithTimeout(context.Background(), ixCallSeconds*time.Second)
	defer cancel()
	answer, err := ixCall(ctx, exe, "ix_origami_check", arguments)
	if err != nil {
		return fail(err.Error())
	}
	name := strings.TrimSuffix(filepath.Base(file), filepath.Ext(file))
	doc, err := origamiFromIX(input, given, answer, name, now)
	if err != nil {
		return fail("IX's report refused: " + err.Error())
	}
	if doc.Pose, err = origamiPoseFor(file, doc); err != nil {
		fmt.Fprintf(stderr, "ix-origami: the pose beside %s is left out: %v\n", file, err) // IX's verdict stands without it
	}
	if doc.Motion, err = origamiMotionFor(file, doc); err != nil {
		fmt.Fprintf(stderr, "ix-origami: the motion beside %s is left out: %v\n", file, err)
	}
	if err := write(doc); err != nil {
		fmt.Fprintf(stderr, "ix-origami: %v\n", err)
		return 1
	}
	verdict := "ok"
	if !*doc.OK {
		verdict = "rejected by " + strings.Join(doc.RejectedBy, ", ")
	}
	if doc.Holds != nil && !*doc.Holds {
		verdict += ", expectations not held"
	}
	fmt.Fprintf(stdout, "ix-origami: %s (%s), %s, %d finding(s), %d edges, written to %s\n", doc.Name, input, verdict, len(doc.Findings), len(doc.Pattern.Edges), out)
	return 0
}
