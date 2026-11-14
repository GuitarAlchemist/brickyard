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
//	 "findings": [{"rule", "where": "edge" or "vertex" or "", "ids": [...], "count", "value", "detail"}]}
//
// or {"format", "v": 1, "source", "at", "error"}. Bounds: 1024 vertices, 2048 edges, 1024 faces of at most 64
// vertices, 4096 face orders, coordinates within ±16, 32 findings of 64 ids, texts of 120 characters. "orders" came
// after the first documents; a reader of v 1 that does not know it draws the fold without its layers.

import (
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
