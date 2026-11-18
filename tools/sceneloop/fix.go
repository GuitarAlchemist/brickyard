package main

// Slice 2 of the scene's improvement tree: one finding goes to a worker, and sceneloop verifies its change itself.
//
//	fix  takes finding n of a run's findings.json, makes a worktree on a new loop/… branch from the repository's HEAD,
//	     and hands the finding to a worker: `claude -p` on the user's plan, in the worktree, with the file tools only
//	     (no shell, no MCP server, no session kept). sceneloop then builds the worktree's scene and runs its selftest,
//	     renders every view before (the base scene.exe) and after (the worker's), has a second judge compare the
//	     finding's view blind (the two pictures in a random order), judges blind every other view whose picture moved,
//	     and holds a frame-rate floor. A change that passes all of it is committed on the loop branch, locally; one that
//	     fails is handed back to the worker once with the reasons, then given up, the reasons written down.
//
// Nothing is pushed, merged or put live: the loop branch waits for the user's word.

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"image"
	"image/png"
	"math/rand/v2"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

// Allowed are the folders a worker may change: the scene's code and shaders, and the gallery's Lego blocks.
var Allowed = []string{"scene/", "gallery/"}

// Forbidden are paths a worker may never change, even inside Allowed.
var Forbidden = []string{"scene/packages.lock.json", "scene/GoogleStreets.cs", "scene/GeoTiles/", "scene/scene.csproj"}

// MostLines is the largest change a worker may make, lines added and removed.
const MostLines = 400

// FPSFloor is the share of the base's frame rate a view must keep.
const FPSFloor = 0.85

// Moved is the mean difference, out of 255, past which another view's picture counts as changed and is judged.
const Moved = 4.0

// Worker is the command line of the worker in its worktree: the file tools only, the run's folder readable.
func Worker(prompt, model, runDir string) []string {
	return []string{"-p", prompt, "--output-format", "json", "--model", model,
		"--allowedTools", "Read,Edit,Write,Glob,Grep", "--permission-mode", "dontAsk", "--add-dir", runDir,
		"--strict-mcp-config", "--no-session-persistence", "--max-turns", "60"}
}

// WorkerPrompt is what the worker is asked: the finding, the rules, and the reasons of a failed first try.
func WorkerPrompt(f Finding, v View, before string, reasons []string) string {
	var b strings.Builder
	fmt.Fprintf(&b, `You improve a real-time 3D scene (C#, Vulkan; its GLSL shaders are strings in scene/Program.cs and
scene/PathTracer.cs) in this git worktree. A judge looking at the picture %s (view %q: %s) found:

- %s (severity %d): %s
- Why: %s
- Suggested fix: %s

Make the smallest change in the source that fixes it, without making anything else look worse and without slowing the
frame. Rules:
- Change only files under scene/ or gallery/; not scene/packages.lock.json, scene/scene.csproj, scene/GoogleStreets.cs
  or scene/GeoTiles/. No new project, package or downloaded asset.
- Every SelfTest check must still pass. If you change something a check measures, update the check honestly.
- You cannot build or run anything: read the code carefully first. NOTES.md explains past choices.
- Change only how the scene looks: nothing about networking, keys, Google's tiles, voice or the brain.
- At most %d lines changed.
End with two or three sentences: what you changed, where, and why.`, before, v.Name, v.What, f.Title, f.Severity, f.Where, f.Why, f.Fix, MostLines)

	if len(reasons) > 0 {
		b.WriteString("\n\nA first try was refused, and the worktree is back at its base. The reasons:\n")
		for _, r := range reasons {
			fmt.Fprintf(&b, "- %s\n", r)
		}
	}

	return b.String()
}

// Change is what a worker changed: each path with its lines added and removed.
type Change struct {
	Path  string `json:"path"`
	Lines int    `json:"lines"`
}

var numstat = regexp.MustCompile(`^(\d+|-)\t(\d+|-)\t(.+)$`)

// Changes reads `git diff --numstat` and `git ls-files --others --exclude-standard` output.
func Changes(numstatOut, untracked string) []Change {
	var c []Change
	for _, line := range strings.Split(strings.ReplaceAll(numstatOut, "\r", ""), "\n") {
		m := numstat.FindStringSubmatch(line)
		if m == nil {
			continue
		}

		var a, d int
		fmt.Sscanf(m[1], "%d", &a)
		fmt.Sscanf(m[2], "%d", &d)
		c = append(c, Change{Path: m[3], Lines: a + d})
	}

	for _, line := range strings.Split(strings.ReplaceAll(untracked, "\r", ""), "\n") {
		if line = strings.TrimSpace(line); line != "" {
			c = append(c, Change{Path: line, Lines: -1}) // counted when it is read
		}
	}

	return c
}

// Scope checks a change against the rules: something changed, only in Allowed, nothing Forbidden, at most MostLines.
func Scope(changes []Change) []string {
	if len(changes) == 0 {
		return []string{"the worker changed nothing"}
	}

	var reasons []string
	total := 0
	for _, c := range changes {
		inside := false
		for _, a := range Allowed {
			inside = inside || strings.HasPrefix(c.Path, a)
		}

		for _, f := range Forbidden {
			if strings.HasPrefix(c.Path, f) {
				inside = false
			}
		}

		if !inside {
			reasons = append(reasons, fmt.Sprintf("%s may not be changed", c.Path))
		}

		total += max(c.Lines, 0)
	}

	if total > MostLines {
		reasons = append(reasons, fmt.Sprintf("%d lines changed, more than %d", total, MostLines))
	}

	return reasons
}

// Blind is the second judge's verdict on two pictures, a and b, in an order it does not know.
type Blind struct {
	Better string `json:"better"` // "a", "b" or "same"
	Fixed  bool   `json:"fixed"`  // the better picture no longer shows the finding
	Worse  string `json:"worse"`  // anything new that looks wrong in the better picture, or ""
	Why    string `json:"why"`
}

// BlindSchema is the second judge's answer.
const BlindSchema = `{"type":"object","properties":{"better":{"type":"string","enum":["a","b","same"]},"fixed":{"type":"boolean"},
"worse":{"type":"string"},"why":{"type":"string"}},"required":["better","fixed","worse","why"]}`

// BlindPrompt asks which of two pictures is better, about a finding or, with no finding, as a whole.
func BlindPrompt(v View, f *Finding) string {
	about := "Which one looks more correct and more real as a whole? fixed is true when the better one has no new defect."
	if f != nil {
		about = fmt.Sprintf("A judge found this defect in one version: %q (%s; %s). Which picture looks better, and in the better one, is that defect gone (fixed)?", f.Title, f.Where, f.Why)
	}

	return fmt.Sprintf(`You compare two pictures of the same real-time 3D scene, a.png and b.png in this folder; read both with the
Read tool. They should show: %s. They were rendered a few seconds apart, so small motions (robots, videos, clocks) are
not differences that count. %s Say "same" when neither is clearly better. In worse, name anything that looks wrong in
the better picture and not in the other, or leave it empty. The window's buttons at the top right and a hint bar at the
bottom are intended.`, v.What, about)
}

// BlindArgs is the second judge's command line.
func BlindArgs(prompt, model string) []string {
	return []string{"-p", prompt, "--output-format", "json", "--json-schema", BlindSchema, "--model", model,
		"--allowedTools", "Read", "--permission-mode", "dontAsk", "--strict-mcp-config", "--no-session-persistence"}
}

// BlindAnswer reads the second judge's verdict from its envelope.
func BlindAnswer(envelope []byte) (Blind, error) {
	var e struct {
		IsError    bool            `json:"is_error"`
		Result     string          `json:"result"`
		Structured json.RawMessage `json:"structured_output"`
	}

	if err := json.Unmarshal(envelope, &e); err != nil {
		return Blind{}, fmt.Errorf("not a JSON envelope: %w", err)
	}

	if e.IsError {
		return Blind{}, fmt.Errorf("the judge failed: %.200s", e.Result)
	}

	body := []byte(e.Structured)
	if len(body) == 0 || string(body) == "null" {
		body = []byte(strings.TrimSpace(strings.TrimSuffix(strings.TrimPrefix(strings.TrimSpace(e.Result), "```json"), "```")))
	}

	var b Blind
	if err := json.Unmarshal(body, &b); err != nil {
		return Blind{}, fmt.Errorf("no verdict in the answer: %w", err)
	}

	return b, nil
}

// Prefers says whether a blind verdict chose the candidate, which was shown as a when afterIsA.
func Prefers(b Blind, afterIsA bool) bool {
	return (b.Better == "a") == afterIsA && b.Better != "same"
}

// Difference is the mean absolute difference of two pictures' channels, out of 255, every fourth pixel; 255 when
// their sizes differ or one cannot be read.
func Difference(a, b string) float64 {
	ia, ea := load(a)
	ib, eb := load(b)
	if ea != nil || eb != nil || ia.Bounds() != ib.Bounds() {
		return 255
	}

	r := ia.Bounds()
	var sum float64
	n := 0
	for y := r.Min.Y; y < r.Max.Y; y += 4 {
		for x := r.Min.X; x < r.Max.X; x += 4 {
			r1, g1, b1, _ := ia.At(x, y).RGBA()
			r2, g2, b2, _ := ib.At(x, y).RGBA()
			sum += abs(int(r1>>8)-int(r2>>8)) + abs(int(g1>>8)-int(g2>>8)) + abs(int(b1>>8)-int(b2>>8))
			n += 3
		}
	}

	return sum / float64(max(n, 1))
}

func abs(v int) float64 {
	if v < 0 {
		return float64(-v)
	}

	return float64(v)
}

func load(path string) (image.Image, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}

	defer f.Close()
	return png.Decode(f)
}

// Slower names the views whose frame rate fell under the floor.
func Slower(before, after []Shot) []string {
	var slow []string
	for _, a := range after {
		for _, b := range before {
			if b.View.Name == a.View.Name && b.FPS > 0 && a.FPS < b.FPS*FPSFloor {
				slow = append(slow, fmt.Sprintf("%s ran at %.0f fps, under %.0f%% of %.0f", a.View.Name, a.FPS, FPSFloor*100, b.FPS))
			}
		}
	}

	return slow
}

// Slug is a branch-safe short name for a finding.
func Slug(f Finding) string {
	s := strings.ToLower(f.View + "-" + f.Title)
	s = regexp.MustCompile(`[^a-z0-9]+`).ReplaceAllString(s, "-")
	s = strings.Trim(s, "-")
	if len(s) > 40 {
		s = strings.TrimRight(s[:40], "-")
	}

	return s
}

// Outcome is what a fix pass did, written to outcome.json.
type Outcome struct {
	Finding  Finding  `json:"finding"`
	Branch   string   `json:"branch"`
	Worktree string   `json:"worktree"`
	Tries    int      `json:"tries"`
	Commit   string   `json:"commit,omitempty"`
	Reasons  []string `json:"reasons,omitempty"`
	Worker   string   `json:"worker,omitempty"` // the worker's own account of its change
	Verdicts []string `json:"verdicts,omitempty"`
}

type fixer struct {
	repo, out, baseExe, workerModel, judgeModel string
	seconds                                     int
	views                                       []View
	log                                         func(string, ...any)
}

func fixCommand(repo, findingsPath string, n int, baseExe, out, workerModel, judgeModel string, seconds int, views []View) error {
	raw, err := os.ReadFile(findingsPath)
	if err != nil {
		return err
	}

	var findings []Finding
	if err := json.Unmarshal(raw, &findings); err != nil {
		return err
	}

	if n < 1 || n > len(findings) {
		return fmt.Errorf("finding %d of %d", n, len(findings))
	}

	f := findings[n-1]
	target, err := pick(f.View)
	if err != nil {
		return err
	}

	// The finding's view first, then the others.
	ordered := append([]View{}, target...)
	for _, v := range views {
		if v.Name != f.View {
			ordered = append(ordered, v)
		}
	}

	dir := filepath.Join(out, time.Now().Format("2006-01-02_150405")+"-"+Slug(f))
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}

	x := fixer{repo: repo, out: dir, baseExe: baseExe, workerModel: workerModel, judgeModel: judgeModel, seconds: seconds, views: ordered,
		log: func(format string, a ...any) { fmt.Printf("SCENELOOP "+format+"\n", a...) }}
	o := x.fix(f)
	if err := write(filepath.Join(dir, "outcome.json"), o); err != nil {
		return err
	}

	if o.Commit == "" {
		return fmt.Errorf("given up after %d tries: %s", o.Tries, strings.Join(o.Reasons, "; "))
	}

	x.log("committed %s on %s (worktree %s)", o.Commit, o.Branch, o.Worktree)
	return nil
}

func (x fixer) fix(f Finding) Outcome {
	o := Outcome{Finding: f, Branch: "loop/" + time.Now().Format("20060102-150405") + "-" + Slug(f)}
	o.Worktree = filepath.Join(filepath.Dir(x.repo), "brickyard-loop-"+filepath.Base(x.out))
	if _, err := x.git(x.repo, "worktree", "add", "-b", o.Branch, o.Worktree, "HEAD"); err != nil {
		o.Reasons = []string{"no worktree: " + err.Error()}
		return o
	}

	x.log("worktree %s on %s", o.Worktree, o.Branch)
	before, err := observe(x.baseExe, filepath.Join(x.out, "before"), x.views, x.seconds)
	if err != nil || len(before) == 0 || before[0].Picture == "" {
		o.Reasons = []string{fmt.Sprintf("no picture before: %v", err)}
		return o
	}

	var reasons []string
	for try := 1; try <= 2; try++ {
		o.Tries = try
		if try > 1 {
			if _, err := x.git(o.Worktree, "checkout", "--", "."); err == nil {
				_, _ = x.git(o.Worktree, "clean", "-fd", "--", "scene", "gallery")
			}
		}

		picture := filepath.Join(x.out, "before", before[0].Picture)
		said, err := x.work(o.Worktree, WorkerPrompt(f, before[0].View, picture, reasons), try)
		o.Worker = said
		if err != nil {
			reasons = []string{"the worker failed: " + err.Error()}
			o.Reasons = reasons
			continue
		}

		var verdicts []string
		reasons, verdicts = x.verify(o.Worktree, f, before, try)
		o.Verdicts = append(o.Verdicts, verdicts...)
		o.Reasons = reasons
		if len(reasons) == 0 {
			o.Commit, err = x.commit(o.Worktree, f, said, verdicts)
			if err != nil {
				o.Reasons = []string{"the commit failed: " + err.Error()}
			}

			return o
		}

		x.log("try %d refused: %s", try, strings.Join(reasons, "; "))
	}

	return o
}

// work runs the worker in the worktree and returns its account of the change.
func (x fixer) work(wt, prompt string, try int) (string, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Minute)
	defer cancel()
	cmd := exec.CommandContext(ctx, "claude", Worker(prompt, x.workerModel, x.out)...)
	cmd.Dir = wt
	cmd.Env = PlanOnly(os.Environ())
	cmd.Stdin = strings.NewReader("")
	output, err := cmd.Output()
	_ = os.WriteFile(filepath.Join(x.out, fmt.Sprintf("worker-%d.json", try)), output, 0o644)
	var e struct {
		IsError bool   `json:"is_error"`
		Result  string `json:"result"`
	}

	if jerr := json.Unmarshal(output, &e); jerr != nil {
		return "", errors.Join(err, jerr)
	}

	if e.IsError {
		return e.Result, fmt.Errorf("%.200s", e.Result)
	}

	return e.Result, nil
}

// verify checks the worktree's change: its scope, the build, the selftest, the pictures and the frame rates.
func (x fixer) verify(wt string, f Finding, before []Shot, try int) (reasons, verdicts []string) {
	stat, _ := x.git(wt, "diff", "--numstat", "HEAD")
	untracked, _ := x.git(wt, "ls-files", "--others", "--exclude-standard")
	changes := Changes(stat, untracked)
	for i, c := range changes {
		if c.Lines < 0 {
			data, _ := os.ReadFile(filepath.Join(wt, c.Path))
			changes[i].Lines = bytes.Count(data, []byte("\n")) + 1
		}
	}

	if r := Scope(changes); len(r) > 0 {
		return r, nil
	}

	exe := filepath.Join(x.out, fmt.Sprintf("build-%d", try), "scene.exe")
	build := exec.Command("dotnet", "build", "-c", "Debug", "-r", "win-x64", "-o", filepath.Dir(exe))
	build.Dir = filepath.Join(wt, "scene")
	output, err := build.CombinedOutput()
	_, _ = x.git(wt, "checkout", "--", "scene/packages.lock.json")
	_ = os.WriteFile(filepath.Join(x.out, fmt.Sprintf("build-%d.txt", try)), output, 0o644)
	if err != nil {
		return []string{"the build failed: " + Errors(string(output))}, nil
	}

	ctx, cancel := context.WithTimeout(context.Background(), 6*time.Minute)
	output, _ = exec.CommandContext(ctx, exe, "--selftest").CombinedOutput()
	cancel()
	_ = os.WriteFile(filepath.Join(x.out, fmt.Sprintf("selftest-%d.txt", try)), output, 0o644)
	if !strings.Contains(string(output), "SELFTEST ok") {
		return []string{"the selftest failed: " + Failed(string(output))}, nil
	}

	after, err := observe(exe, filepath.Join(x.out, fmt.Sprintf("after-%d", try)), x.views, x.seconds)
	if err != nil {
		return []string{"no pictures after: " + err.Error()}, nil
	}

	reasons = append(reasons, Slower(before, after)...)
	for i, a := range after {
		if a.Picture == "" {
			reasons = append(reasons, fmt.Sprintf("%s rendered no picture (%s)", a.View.Name, a.Error))
			continue
		}

		b := before[i]
		pa := filepath.Join(x.out, fmt.Sprintf("after-%d", try), a.Picture)
		pb := filepath.Join(x.out, "before", b.Picture)
		moved := Difference(pb, pa)
		var finding *Finding
		if i == 0 {
			finding = &f
		} else if moved < Moved {
			continue
		}

		verdict, afterIsA, err := x.blind(a.View, finding, pb, pa, fmt.Sprintf("ab-%d-%s", try, a.View.Name))
		if err != nil {
			reasons = append(reasons, fmt.Sprintf("%s: no blind verdict: %v", a.View.Name, err))
			continue
		}

		chose := map[bool]string{true: "the change", false: "the base"}[Prefers(verdict, afterIsA)]
		if verdict.Better == "same" {
			chose = "neither"
		}

		verdicts = append(verdicts, fmt.Sprintf("%s (moved %.1f): %s; fixed %v; %s", a.View.Name, moved, chose, verdict.Fixed, verdict.Why))
		switch {
		case i == 0 && !(Prefers(verdict, afterIsA) && verdict.Fixed):
			reasons = append(reasons, fmt.Sprintf("the blind judge did not see the finding fixed in %s: %s", a.View.Name, verdict.Why))
		case i == 0 && verdict.Worse != "":
			reasons = append(reasons, fmt.Sprintf("the blind judge saw something new wrong in %s: %s", a.View.Name, verdict.Worse))
		case i > 0 && verdict.Better != "same" && !Prefers(verdict, afterIsA):
			reasons = append(reasons, fmt.Sprintf("%s looks worse after the change: %s", a.View.Name, verdict.Why))
		}
	}

	return reasons, verdicts
}

// blind copies the two pictures as a.png and b.png in a random order and asks a second judge.
func (x fixer) blind(v View, f *Finding, before, after, name string) (Blind, bool, error) {
	dir := filepath.Join(x.out, name)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return Blind{}, false, err
	}

	afterIsA := rand.IntN(2) == 0
	first, second := before, after
	if afterIsA {
		first, second = after, before
	}

	for src, dst := range map[string]string{first: "a.png", second: "b.png"} {
		data, err := os.ReadFile(src)
		if err != nil {
			return Blind{}, false, err
		}

		if err := os.WriteFile(filepath.Join(dir, dst), data, 0o644); err != nil {
			return Blind{}, false, err
		}
	}

	ctx, cancel := context.WithTimeout(context.Background(), 6*time.Minute)
	defer cancel()
	cmd := exec.CommandContext(ctx, "claude", BlindArgs(BlindPrompt(v, f), x.judgeModel)...)
	cmd.Dir = dir
	cmd.Env = PlanOnly(os.Environ())
	cmd.Stdin = strings.NewReader("")
	output, _ := cmd.Output()
	_ = os.WriteFile(filepath.Join(dir, "judge.json"), output, 0o644)
	_ = os.WriteFile(filepath.Join(dir, "order.txt"), []byte(map[bool]string{true: "a is the change\n", false: "b is the change\n"}[afterIsA]), 0o644)
	verdict, err := BlindAnswer(output)
	return verdict, afterIsA, err
}

// commit stages the worker's paths by name and commits them on the loop branch.
func (x fixer) commit(wt string, f Finding, said string, verdicts []string) (string, error) {
	stat, _ := x.git(wt, "diff", "--numstat", "HEAD")
	untracked, _ := x.git(wt, "ls-files", "--others", "--exclude-standard")
	args := []string{"add", "--"}
	for _, c := range Changes(stat, untracked) {
		args = append(args, c.Path)
	}

	if _, err := x.git(wt, args...); err != nil {
		return "", err
	}

	msg := filepath.Join(x.out, "commit.txt")
	if err := os.WriteFile(msg, []byte(CommitMessage(f, said, verdicts)), 0o644); err != nil {
		return "", err
	}

	if _, err := x.git(wt, "commit", "-F", msg); err != nil {
		return "", err
	}

	sha, err := x.git(wt, "rev-parse", "--short", "HEAD")
	return strings.TrimSpace(sha), err
}

// CommitMessage is a loop commit's message: the finding, the worker's account, the verdicts.
func CommitMessage(f Finding, said string, verdicts []string) string {
	var b strings.Builder
	title := fmt.Sprintf("sceneloop: %s (%s)", f.Title, f.View)
	if len(title) > 72 {
		title = title[:69] + "..."
	}

	fmt.Fprintf(&b, "%s\n\nThe judge's finding (severity %d, confidence %d): %s\nWhy: %s\n\nThe worker: %s\n\nVerified by sceneloop: scope, build, selftest, frame rates, blind judges:\n",
		title, f.Severity, f.Confidence, f.Where, f.Why, strings.TrimSpace(said))
	for _, v := range verdicts {
		fmt.Fprintf(&b, "- %s\n", v)
	}

	b.WriteString("\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>\nClaude-Session: https://claude.ai/code/session_01HJoXRPZvYWEzETZFkL9238\n")
	return b.String()
}

var errorLine = regexp.MustCompile(`(?m)^.*: error [A-Z]+\d+:.*$`)

// Errors is a build's first error lines.
func Errors(output string) string {
	lines := errorLine.FindAllString(output, 4)
	if len(lines) == 0 {
		return "no error line"
	}

	return strings.Join(lines, " | ")
}

var failedLine = regexp.MustCompile(`(?m)^.*(FAIL|failed).*$`)

// Failed is a selftest's first failed checks.
func Failed(output string) string {
	lines := failedLine.FindAllString(output, 4)
	if len(lines) == 0 {
		return "no SELFTEST ok line"
	}

	return strings.Join(lines, " | ")
}

func (x fixer) git(dir string, args ...string) (string, error) {
	cmd := exec.Command("git", append([]string{"-C", dir}, args...)...)
	var out, errb bytes.Buffer
	cmd.Stdout, cmd.Stderr = &out, &errb
	if err := cmd.Run(); err != nil {
		return out.String(), fmt.Errorf("git %s: %w: %s", strings.Join(args, " "), err, strings.TrimSpace(errb.String()))
	}

	return out.String(), nil
}
