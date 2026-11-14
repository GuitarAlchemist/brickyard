package main

// A fold shown step by step (tools/crane writes it): the sheet cut by every crease any step makes, each step's flat
// folded state as a FOLD, and the motion from each step to the next. brain -ix-origami on such a manifest asks IX to
// check every step's FOLD, with the census (each stated pair of layers flipped alone, to show the rules reject it),
// and writes one document whose "steps" say what IX found at each step. The motion is Delta's: the brain checks that
// it rests, at each step, exactly where that step's FOLD places the paper (so what moves is what IX checked), and
// measures how far it bends the paper between steps, which the table shows.
//
// The manifest, format "delta-origami-steps", v 1:
//
//	{"format", "v": 1, "name", "note",
//	 "sheet": {"vertices", "edges", "assignment", "faces"} (as a document's pattern; the assignment the last step's),
//	 "steps": [{"fr", "en", "at": the frame it rests at, "flex", "layers": [a layer for each of the sheet's faces],
//	            "fold": the step's FOLD object}],
//	 "frames": [[[x, y, z]]] (the sheet's vertices in each frame)}
//
// The document gains "steps": [{"fr", "en", "ok", "max_layers", "faces", "orders", "census": [rejected, pairs],
// "vertices": the interior vertices where Kawasaki and Maekawa were checked, "isometry": the widest change of a face's
// side, "findings", "at", "flex": the widest change of a distance within a face, in the sheet's units, between the
// step before and this one, "layers"}], and "motion" the frames.

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"slices"
	"time"
)

const (
	origamiStepsFormat    = "delta-origami-steps"
	origamiMaxSteps       = 64
	origamiMaxStepFrames  = 512
	origamiMaxFlex        = 0.02 // how far a move may bend the paper, in the sheet's units (a unit square)
	origamiRestTolerance  = 1e-3
	origamiMaxStepsLayers = 255
	origamiMaxStepsInput  = 4 << 20
)

type origamiStepsManifest struct {
	Format string         `json:"format"`
	V      int            `json:"v"`
	Name   string         `json:"name"`
	Note   string         `json:"note"`
	Sheet  origamiPattern `json:"sheet"`
	Steps  []struct {
		Fr     string          `json:"fr"`
		En     string          `json:"en"`
		At     int             `json:"at"`
		Flex   float64         `json:"flex"`
		Layers []int           `json:"layers"`
		Fold   json.RawMessage `json:"fold"`
	} `json:"steps"`
	Frames [][][3]float64 `json:"frames"`
}

type origamiStep struct {
	Fr        string           `json:"fr"`
	En        string           `json:"en"`
	OK        bool             `json:"ok"`
	MaxLayers int              `json:"max_layers"`
	Faces     int              `json:"faces"`
	Orders    int              `json:"orders"`
	Census    [2]int           `json:"census"`
	Vertices  int              `json:"vertices"`
	Isometry  float64          `json:"isometry"`
	Findings  []origamiFinding `json:"findings,omitempty"`
	At        int              `json:"at"`
	Flex      float64          `json:"flex"`
	Layers    []int            `json:"layers"`
}

// isSteps: whether a file's body is a steps manifest.
func isSteps(body []byte) bool {
	var head struct {
		Format string `json:"format"`
	}
	return json.Unmarshal(body, &head) == nil && head.Format == origamiStepsFormat
}

// origamiStepsDoc checks the manifest, asks IX about each step through check, and builds the document.
func origamiStepsDoc(body []byte, check func(fold json.RawMessage) ([]byte, error), now time.Time) (origamiDoc, error) {
	dec := json.NewDecoder(bytes.NewReader(body))
	dec.DisallowUnknownFields()
	var m origamiStepsManifest
	if err := dec.Decode(&m); err != nil {
		return origamiDoc{}, fmt.Errorf("the steps are not a manifest: %v", err)
	}
	if m.V != 1 {
		return origamiDoc{}, fmt.Errorf("steps manifest version %d, not 1", m.V)
	}
	for _, t := range []string{m.Name, m.Note} {
		if err := origamiTextOK("manifest's name or note", t); err != nil {
			return origamiDoc{}, err
		}
	}
	sheet, err := origamiSheetOK(m.Sheet)
	if err != nil {
		return origamiDoc{}, err
	}
	if len(m.Steps) < 2 || len(m.Steps) > origamiMaxSteps {
		return origamiDoc{}, fmt.Errorf("%d steps, not 2 to %d", len(m.Steps), origamiMaxSteps)
	}
	if len(m.Frames) < 2 || len(m.Frames) > origamiMaxStepFrames {
		return origamiDoc{}, fmt.Errorf("%d frames, not 2 to %d", len(m.Frames), origamiMaxStepFrames)
	}
	doc := origamiDoc{Format: origamiFormat, V: 1, Source: "ix_origami_check", At: now.UTC().Format(time.RFC3339), Name: m.Name, Input: "steps", Pattern: sheet}
	motion := &origamiMotion{Note: m.Note, Frames: m.Frames}
	flex, err := origamiFrames(motion, sheet)
	if err != nil {
		return origamiDoc{}, err
	}
	doc.Motion = motion
	ok := true
	for i, s := range m.Steps {
		for _, t := range []string{s.Fr, s.En} {
			if err := origamiTextOK("step's words", t); err != nil {
				return origamiDoc{}, err
			}
		}
		if (i == 0 && s.At != 0) || (i > 0 && s.At <= m.Steps[i-1].At) || s.At >= len(m.Frames) || (i == len(m.Steps)-1 && s.At != len(m.Frames)-1) {
			return origamiDoc{}, fmt.Errorf("step %d rests at frame %d: the steps must rest at the first frame, then later ones, the last at the last", i, s.At)
		}
		if len(s.Layers) != len(sheet.Faces) {
			return origamiDoc{}, fmt.Errorf("step %d gives %d layers for %d faces", i, len(s.Layers), len(sheet.Faces))
		}
		for _, l := range s.Layers {
			if l < 0 || l > origamiMaxStepsLayers {
				return origamiDoc{}, fmt.Errorf("step %d puts a face in layer %d, not 0 to %d", i, l, origamiMaxStepsLayers)
			}
		}
		if len(s.Fold) > origamiMaxInput {
			return origamiDoc{}, fmt.Errorf("step %d's FOLD is larger than %d bytes", i, origamiMaxInput)
		}
		answer, err := check(s.Fold)
		if err != nil {
			return origamiDoc{}, fmt.Errorf("step %d: %w", i, err)
		}
		one, err := origamiFromIX("fold", s.Fold, answer, m.Name, now)
		if err != nil {
			return origamiDoc{}, fmt.Errorf("step %d: %w", i, err)
		}
		if err := origamiRests(m.Frames[s.At], sheet, one); err != nil {
			return origamiDoc{}, fmt.Errorf("step %d: %w", i, err)
		}
		var r ixOrigamiReport
		_ = json.Unmarshal(answer, &r)
		step := origamiStep{Fr: s.Fr, En: s.En, OK: *one.OK, MaxLayers: one.MaxLayers, Faces: len(one.Pattern.Faces), Orders: len(one.Orders),
			Vertices: r.Local.Applicable, Isometry: round4(r.Isometry.MaxChange), Findings: one.Findings, At: s.At, Layers: s.Layers}
		if r.Census != nil {
			step.Census = [2]int{r.Census.Rejected, r.Census.Pairs}
		}
		if i > 0 {
			for k := m.Steps[i-1].At + 1; k <= s.At; k++ {
				step.Flex = math.Max(step.Flex, flex[k])
			}
			step.Flex = round4(step.Flex)
		}
		ok = ok && step.OK
		doc.MaxLayers = max(doc.MaxLayers, step.MaxLayers)
		doc.Steps = append(doc.Steps, step)
	}
	doc.OK = &ok
	return doc, nil
}

// origamiSheetOK: the sheet as a pattern, within the document's bounds, its faces few enough to move.
func origamiSheetOK(p origamiPattern) (*origamiPattern, error) {
	raw := foldObject{Edges: make([][]int, len(p.Edges)), Assignment: p.Assignment, Faces: p.Faces}
	for i, e := range p.Edges {
		raw.Edges[i] = []int{e[0], e[1]}
	}
	var coords [][]float64
	for _, v := range p.Vertices {
		coords = append(coords, []float64{v[0], v[1]})
	}
	sheet, _, err := origamiShapes(raw, coords, nil)
	if err != nil {
		return nil, fmt.Errorf("the sheet: %w", err)
	}
	if len(sheet.Faces) == 0 || len(sheet.Faces) > origamiMaxMotionFaces {
		return nil, fmt.Errorf("the sheet has %d faces, not 1 to %d", len(sheet.Faces), origamiMaxMotionFaces)
	}
	return sheet, nil
}

// origamiFrames checks every frame against the sheet and rounds it as the document keeps it; it returns how far each
// frame bends the paper: the widest change of a distance between two vertices of a face, refused beyond origamiMaxFlex.
func origamiFrames(m *origamiMotion, sheet *origamiPattern) ([]float64, error) {
	v := sheet.Vertices
	flex := make([]float64, len(m.Frames))
	for k, frame := range m.Frames {
		if len(frame) != len(v) {
			return nil, fmt.Errorf("frame %d has %d vertices, the sheet %d", k, len(frame), len(v))
		}
		for i := range frame {
			for c := range 3 {
				x := frame[i][c]
				if math.IsNaN(x) || math.IsInf(x, 0) || math.Abs(x) > origamiMaxCoord {
					return nil, fmt.Errorf("frame %d: vertex %d lies beyond ±%d", k, i, origamiMaxCoord)
				}
				frame[i][c] = round4(x)
			}
		}
		for f, face := range sheet.Faces {
			for a := 0; a < len(face); a++ {
				for b := a + 1; b < len(face); b++ {
					p, q := frame[face[a]], frame[face[b]]
					got := math.Sqrt((p[0]-q[0])*(p[0]-q[0]) + (p[1]-q[1])*(p[1]-q[1]) + (p[2]-q[2])*(p[2]-q[2]))
					want := math.Hypot(v[face[a]][0]-v[face[b]][0], v[face[a]][1]-v[face[b]][1])
					flex[k] = math.Max(flex[k], math.Abs(got-want))
					if math.Abs(got-want) > origamiMaxFlex {
						return nil, fmt.Errorf("frame %d: face %d's vertices %d and %d are %.4f apart, %.4f in the sheet: more than the paper bends", k, f, face[a], face[b], got, want)
					}
				}
			}
		}
	}
	return flex, nil
}

// origamiRests: the frame a step rests at puts every vertex of the sheet where the step's FOLD folds it, flat. The
// FOLD's vertices are among the sheet's; a sheet vertex inside one of the FOLD's faces goes where that face's own
// isometry, from its crease pattern to its folded state, takes it.
func origamiRests(frame [][3]float64, sheet *origamiPattern, step origamiDoc) error {
	if step.Folded == nil {
		return errors.New("its FOLD has no folded state")
	}
	p, folded := step.Pattern, step.Folded
	for i, v := range p.Vertices {
		if !slices.ContainsFunc(sheet.Vertices, func(q [2]float64) bool { return math.Abs(v[0]-q[0]) < 2e-4 && math.Abs(v[1]-q[1]) < 2e-4 }) {
			return fmt.Errorf("its vertex %d (%.4f, %.4f) is none of the sheet's", i, v[0], v[1])
		}
	}
	// Each face's map x -> M x + b, from three of its corners that are not in line.
	type affine struct {
		m [2][2]float64
		b [2]float64
	}
	maps := make([]affine, len(p.Faces))
	for f, face := range p.Faces {
		c0, f0 := p.Vertices[face[0]], folded[face[0]]
		found := false
		for k := 1; k+1 < len(face) && !found; k++ {
			c1, c2 := p.Vertices[face[k]], p.Vertices[face[k+1]]
			u, w := [2]float64{c1[0] - c0[0], c1[1] - c0[1]}, [2]float64{c2[0] - c0[0], c2[1] - c0[1]}
			det := u[0]*w[1] - u[1]*w[0]
			if math.Abs(det) < 1e-6 {
				continue
			}
			fu := [2]float64{folded[face[k]][0] - f0[0], folded[face[k]][1] - f0[1]}
			fw := [2]float64{folded[face[k+1]][0] - f0[0], folded[face[k+1]][1] - f0[1]}
			var a affine
			for r := range 2 { // M = [fu fw] [u w]^-1
				a.m[r] = [2]float64{(fu[r]*w[1] - fw[r]*u[1]) / det, (fw[r]*u[0] - fu[r]*w[0]) / det}
				a.b[r] = f0[r] - a.m[r][0]*c0[0] - a.m[r][1]*c0[1]
			}
			maps[f], found = a, true
		}
		if !found {
			return fmt.Errorf("its face %d has no three corners out of line", f)
		}
	}
	inside := func(face []int, q [2]float64) bool {
		sign := 0.0
		for k := range face {
			a, b := p.Vertices[face[k]], p.Vertices[face[(k+1)%len(face)]]
			cross := (b[0]-a[0])*(q[1]-a[1]) - (b[1]-a[1])*(q[0]-a[0])
			if math.Abs(cross) < 1e-6 {
				continue
			}
			if sign == 0 {
				sign = cross
			} else if (cross > 0) != (sign > 0) {
				return false
			}
		}
		return true
	}
	for i, q := range sheet.Vertices {
		f := slices.IndexFunc(p.Faces, func(face []int) bool { return inside(face, q) })
		if f < 0 {
			return fmt.Errorf("the sheet's vertex %d (%.4f, %.4f) lies in none of its faces", i, q[0], q[1])
		}
		a, x := maps[f], frame[i]
		want := [2]float64{a.m[0][0]*q[0] + a.m[0][1]*q[1] + a.b[0], a.m[1][0]*q[0] + a.m[1][1]*q[1] + a.b[1]}
		if math.Abs(want[0]-x[0]) > origamiRestTolerance || math.Abs(want[1]-x[1]) > origamiRestTolerance || math.Abs(x[2]) > origamiRestTolerance {
			return fmt.Errorf("the motion rests with vertex %d at (%.4f, %.4f, %.4f), its FOLD folds it to (%.4f, %.4f)", i, x[0], x[1], x[2], want[0], want[1])
		}
	}
	return nil
}

// runIXOrigamiSteps is brain -ix-origami on a steps manifest.
func runIXOrigamiSteps(body []byte, exe string, write func(origamiDoc) error, fail func(string) int, stdout, stderr io.Writer, out string, now time.Time) int {
	exe, err := ixExecutable(exe)
	if err != nil {
		return fail(err.Error())
	}
	check := func(fold json.RawMessage) ([]byte, error) {
		var obj map[string]any
		if err := json.Unmarshal(fold, &obj); err != nil {
			return nil, fmt.Errorf("its FOLD is not a JSON object: %v", err)
		}
		ctx, cancel := context.WithTimeout(context.Background(), ixCallSeconds*time.Second)
		defer cancel()
		return ixCall(ctx, exe, "ix_origami_check", map[string]any{"fold": obj, "census": true})
	}
	doc, err := origamiStepsDoc(body, check, now)
	if err != nil {
		return fail("the steps refused: " + err.Error())
	}
	if err := write(doc); err != nil {
		fmt.Fprintf(stderr, "ix-origami: %v\n", err)
		return 1
	}
	verdict := "every step ok"
	if !*doc.OK {
		verdict = "a step rejected"
	}
	fmt.Fprintf(stdout, "ix-origami: %s, %d steps (%s), %d frames, written to %s\n", doc.Name, len(doc.Steps), verdict, len(doc.Motion.Frames), out)
	for i, s := range doc.Steps {
		fmt.Fprintf(stdout, "  %2d %-48s ok=%v layers=%d faces=%d orders=%d census=%d/%d vertices=%d flex=%.4f\n", i, s.En, s.OK, s.MaxLayers, s.Faces, s.Orders, s.Census[0], s.Census[1], s.Vertices, s.Flex)
	}
	return 0
}
