// Command sceneloop is the first slice of the scene's improvement tree (the user, 2026-11-18: « peut on rajouter un
// systeme d'arbre de boucles agentiques (Decomposition recursive) qui regarde la scene et fait des ameliorations toute
// seule »): it looks at the scene and says what is wrong with it, and changes nothing.
//
//	observe   renders each reference view in a hidden window (port 8899, never the live 8805; --osm, so that no
//	          request goes to Google) and keeps its last picture and its frame rate.
//	critique  hands each picture to a judge, `claude -p` on the user's plan (ANTHROPIC_API_KEY is taken out of its
//	          environment, so that no API key pays), and gathers its findings, ranked, into findings.json and
//	          report.md.
//	run       both, into a new folder.
//
// The judge may only read the picture: no tool but Read, no MCP server, no session kept. Later slices delegate a
// finding to an agent in a worktree and verify its change; they read findings.json.
package main

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

// View is a reference picture of the scene: its name and the scene's arguments that frame it.
type View struct {
	Name string   `json:"name"`
	Args []string `json:"args"`
	What string   `json:"what"` // what the picture should show, for the judge
}

// Views are the pictures every pass looks at.
var Views = []View{
	{"hall", []string{"--view", "4"}, "the whole hall: brick walls, concrete, the robot Delta, the agents' office tower, clocks"},
	{"office", []string{"--view", "2"}, "the agents' office: small robots in cubicles round a tower"},
	{"gallery", []string{"--view", "8"}, "the gallery on the left wall: water globes and paperweights on shelves, a projection screen"},
	{"knots", []string{"--knots", "near"}, "sailors' knots in three-strand hemp rope on a planked table, with cards"},
	{"rope", []string{"--knots", "close"}, "a hand's length from the rope: strands, yarns, a whipped end"},
	{"gym", []string{"--gym", "court"}, "a school gymnasium: painted block walls, maple floor, basketball court, bleachers, high-bay lamps"},
	{"gym-door", []string{"--gym", "door"}, "the gymnasium seen through its door from the hall"},
	{"street", []string{"--door", "street"}, "the streets of Ottawa outside the hall's roll-up door (OpenStreetMap buildings)"},
	{"wing", []string{"--door", "wing"}, "the building and its gymnasium wing from the air, in daylight"},
}

// Shot is what observe kept of a view.
type Shot struct {
	View    View    `json:"view"`
	Picture string  `json:"picture"` // relative to the run's folder
	FPS     float64 `json:"fps"`
	Error   string  `json:"error,omitempty"`
}

// Finding is one defect the judge saw.
type Finding struct {
	View       string `json:"view"`
	Title      string `json:"title"`
	Where      string `json:"where"`
	Why        string `json:"why"`
	Severity   int    `json:"severity"` // 1 a detail to 5 breaks the picture
	Fix        string `json:"fix"`
	Confidence int    `json:"confidence"` // 1 to 5
}

func main() {
	if len(os.Args) < 2 {
		fmt.Fprintln(os.Stderr, "usage: sceneloop observe|critique|run [flags]")
		os.Exit(2)
	}

	fs := flag.NewFlagSet(os.Args[1], flag.ExitOnError)
	exe := fs.String("exe", "", "the scene.exe to render with (a build outside the live checkout)")
	out := fs.String("out", "", "the run's folder (run: a new folder under it)")
	only := fs.String("views", "", "comma-separated view names (default: all)")
	model := fs.String("model", "sonnet", "the judge's model")
	parallel := fs.Int("parallel", 3, "judges at once")
	seconds := fs.Int("seconds", 24, "seconds each hidden window runs")
	_ = fs.Parse(os.Args[2:])

	views, err := pick(*only)
	if err != nil {
		fail(err)
	}

	switch os.Args[1] {
	case "observe":
		need(*exe != "" && *out != "", "observe needs -exe and -out")
		_, err = observe(*exe, *out, views, *seconds)
	case "critique":
		need(*out != "", "critique needs -out")
		_, err = critique(*out, *model, *parallel)
	case "run":
		need(*exe != "" && *out != "", "run needs -exe and -out")
		dir := filepath.Join(*out, time.Now().Format("2006-01-02_150405"))
		if _, err = observe(*exe, dir, views, *seconds); err == nil {
			_, err = critique(dir, *model, *parallel)
		}
	default:
		err = fmt.Errorf("unknown command %q", os.Args[1])
	}

	if err != nil {
		fail(err)
	}
}

func pick(only string) ([]View, error) {
	if only == "" {
		return Views, nil
	}

	var views []View
	for _, name := range strings.Split(only, ",") {
		found := false
		for _, v := range Views {
			if v.Name == strings.TrimSpace(name) {
				views = append(views, v)
				found = true
			}
		}

		if !found {
			return nil, fmt.Errorf("no view %q", name)
		}
	}

	return views, nil
}

// Hidden is the scene's arguments for a hidden picture of a view into dir: never the live window's port, no tour, no
// Google request.
func Hidden(v View, dir string, seconds int) []string {
	args := []string{"--hidden", "--listen", "8899", "--seconds", strconv.Itoa(seconds), "--tour-after", "9999", "--osm", "--size", "1600x900", "--peek", dir}
	return append(args, v.Args...)
}

var framesLine = regexp.MustCompile(`SCENE frames \d+ in [\d.]+ s \(([\d.]+) fps\)`)

// FPS reads the frame rate from a hidden window's output, or 0.
func FPS(output string) float64 {
	if m := framesLine.FindStringSubmatch(output); m != nil {
		f, _ := strconv.ParseFloat(m[1], 64)
		return f
	}

	return 0
}

func observe(exe, dir string, views []View, seconds int) ([]Shot, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}

	var shots []Shot
	for _, v := range views {
		shot := Shot{View: v}
		peek := filepath.Join(dir, v.Name)
		if err := os.MkdirAll(peek, 0o755); err != nil {
			return nil, err
		}

		ctx, cancel := context.WithTimeout(context.Background(), time.Duration(seconds+50)*time.Second)
		cmd := exec.CommandContext(ctx, exe, Hidden(v, peek, seconds)...)
		output, err := cmd.CombinedOutput()
		cancel()
		_ = os.WriteFile(filepath.Join(peek, "scene.txt"), output, 0o644)
		shot.FPS = FPS(string(output))
		pictures, _ := filepath.Glob(filepath.Join(peek, "peek-*.png"))
		sort.Strings(pictures)
		switch {
		case len(pictures) > 0:
			shot.Picture, _ = filepath.Rel(dir, pictures[len(pictures)-1])
		case err != nil:
			shot.Error = err.Error()
		default:
			shot.Error = "no picture"
		}

		fmt.Printf("SCENELOOP %-9s %5.1f fps %s%s\n", v.Name, shot.FPS, shot.Picture, shot.Error)
		shots = append(shots, shot)
	}

	return shots, write(filepath.Join(dir, "shots.json"), shots)
}

// Schema is the judge's answer.
const Schema = `{"type":"object","properties":{"findings":{"type":"array","items":{"type":"object","properties":{
"title":{"type":"string"},"where":{"type":"string"},"why":{"type":"string"},"severity":{"type":"integer","minimum":1,"maximum":5},
"fix":{"type":"string"},"confidence":{"type":"integer","minimum":1,"maximum":5}},
"required":["title","where","why","severity","fix","confidence"]}}},"required":["findings"]}`

// Prompt is what the judge is asked about one picture.
func Prompt(s Shot) string {
	return fmt.Sprintf(`You judge one picture of a real-time 3D scene (Vulkan, C#), to find what makes it look wrong or unreal.
Read the picture %s with the Read tool. It should show: %s. It ran at %.0f fps hidden at 1600x900.
List the defects a viewer would notice, most visible first: rendering artefacts (moiré, shimmering, z-fighting, light
leaks, black or blown-out areas, banding, stretched or repeating textures), wrong scale or proportions, floating or
intersecting objects, flat or implausible lighting and materials, empty or unfinished areas. Name where in the picture
each one is (left, centre, top right...), why it looks wrong, and the concrete change in the scene that would fix it
(geometry, a material, the lighting). Severity 1 is a detail, 5 breaks the picture. Confidence 1 is a guess, 5 is
certain. Say only what the picture shows; the window's buttons at the top right and a hint bar at the bottom are
intended. At most 6 findings.`, s.Picture, s.View.What, s.FPS)
}

// Judge is the command line of one judge, run in the run's folder.
func Judge(s Shot, model string) []string {
	return []string{"-p", Prompt(s), "--output-format", "json", "--json-schema", Schema, "--model", model,
		"--allowedTools", "Read", "--permission-mode", "dontAsk", "--strict-mcp-config", "--no-session-persistence"}
}

// PlanOnly is the judge's environment: the user's own, without an API key, so that the plan's tokens pay.
func PlanOnly(env []string) []string {
	var kept []string
	for _, e := range env {
		if strings.HasPrefix(strings.ToUpper(e), "ANTHROPIC_API_KEY=") || strings.HasPrefix(strings.ToUpper(e), "ANTHROPIC_AUTH_TOKEN=") {
			continue
		}

		kept = append(kept, e)
	}

	return kept
}

// Answer reads the judge's findings from its JSON envelope: the structured output, else the result as JSON.
func Answer(envelope []byte) ([]Finding, error) {
	var e struct {
		IsError    bool            `json:"is_error"`
		Result     string          `json:"result"`
		Structured json.RawMessage `json:"structured_output"`
	}

	if err := json.Unmarshal(envelope, &e); err != nil {
		return nil, fmt.Errorf("not a JSON envelope: %w", err)
	}

	if e.IsError {
		return nil, fmt.Errorf("the judge failed: %.200s", e.Result)
	}

	body := []byte(e.Structured)
	if len(body) == 0 || string(body) == "null" {
		body = []byte(strings.TrimSpace(strings.TrimSuffix(strings.TrimPrefix(strings.TrimSpace(e.Result), "```json"), "```")))
	}

	var a struct {
		Findings []Finding `json:"findings"`
	}

	if err := json.Unmarshal(body, &a); err != nil {
		return nil, fmt.Errorf("no findings in the answer: %w", err)
	}

	return a.Findings, nil
}

func critique(dir, model string, parallel int) ([]Finding, error) {
	raw, err := os.ReadFile(filepath.Join(dir, "shots.json"))
	if err != nil {
		return nil, err
	}

	var shots []Shot
	if err := json.Unmarshal(raw, &shots); err != nil {
		return nil, err
	}

	var (
		mu       sync.Mutex
		findings []Finding
		failures []string
		wg       sync.WaitGroup
		slots    = make(chan struct{}, max(1, parallel))
	)

	for _, s := range shots {
		if s.Picture == "" {
			continue
		}

		wg.Add(1)
		go func(s Shot) {
			defer wg.Done()
			slots <- struct{}{}
			defer func() { <-slots }()
			ctx, cancel := context.WithTimeout(context.Background(), 6*time.Minute)
			defer cancel()
			cmd := exec.CommandContext(ctx, "claude", Judge(s, model)...)
			cmd.Dir = dir
			cmd.Env = PlanOnly(os.Environ())
			cmd.Stdin = strings.NewReader("")
			output, err := cmd.Output()
			_ = os.WriteFile(filepath.Join(dir, s.View.Name, "judge.json"), output, 0o644)
			found, aerr := Answer(output)
			mu.Lock()
			defer mu.Unlock()
			if aerr != nil {
				failures = append(failures, fmt.Sprintf("%s: %v %v", s.View.Name, err, aerr))
				return
			}

			for i := range found {
				found[i].View = s.View.Name
			}

			findings = append(findings, found...)
			fmt.Printf("SCENELOOP judged %-9s %d findings\n", s.View.Name, len(found))
		}(s)
	}

	wg.Wait()
	sort.SliceStable(findings, func(i, j int) bool {
		a, b := findings[i], findings[j]
		return a.Severity*a.Confidence > b.Severity*b.Confidence
	})

	if err := write(filepath.Join(dir, "findings.json"), findings); err != nil {
		return nil, err
	}

	if err := os.WriteFile(filepath.Join(dir, "report.md"), []byte(Report(shots, findings, failures)), 0o644); err != nil {
		return nil, err
	}

	if len(failures) > 0 {
		return findings, errors.New(strings.Join(failures, "; "))
	}

	return findings, nil
}

// Report is the run's findings for a reader: ranked by severity times confidence, each with its view's picture.
func Report(shots []Shot, findings []Finding, failures []string) string {
	var b strings.Builder
	b.WriteString("# Scene loop: what the judge saw\n\n| View | fps | Picture |\n|---|---|---|\n")
	for _, s := range shots {
		fmt.Fprintf(&b, "| %s | %.0f | %s%s |\n", s.View.Name, s.FPS, filepath.ToSlash(s.Picture), s.Error)
	}

	b.WriteString("\n## Findings, most severe first\n\n")
	for i, f := range findings {
		fmt.Fprintf(&b, "%d. **%s** (%s; severity %d, confidence %d): %s\n   - Why: %s\n   - Fix: %s\n", i+1, f.Title, f.View, f.Severity, f.Confidence, f.Where, f.Why, f.Fix)
	}

	for _, f := range failures {
		fmt.Fprintf(&b, "\nNot judged: %s\n", f)
	}

	return b.String()
}

func write(path string, v any) error {
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}

	return os.WriteFile(path, data, 0o644)
}

func need(ok bool, why string) {
	if !ok {
		fail(errors.New(why))
	}
}

func fail(err error) {
	w := bufio.NewWriter(os.Stderr)
	fmt.Fprintln(w, "sceneloop:", err)
	w.Flush()
	os.Exit(1)
}
