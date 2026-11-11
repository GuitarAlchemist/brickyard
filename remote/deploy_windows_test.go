package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"reflect"
	"strings"
	"syscall"
	"testing"
	"time"
)

// deploy.ps1 is run against fake gateways: this test binary, copied under delta-remote-*.exe names into a folder of its
// own, which then only listens on loopback ports. The script touches only the gateway started from the folder it is
// given, so the real one, started elsewhere, is never touched.
const fakeGatewayEnv = "DELTA_REMOTE_FAKE_GATEWAY"

// fakeMark is set for the fakes and the script only, so that an argument naming it shows whether something expanded it.
const fakeMark = "DELTA_REMOTE_FAKE_MARK"

// slowMark, a file in a fake folder, makes the builds started from it slow to listen.
const slowMark = "slow-start"

func fakeEnv() []string { return append(os.Environ(), fakeGatewayEnv+"=1", fakeMark+"=EXPANDED") }

func TestMain(m *testing.M) {
	if os.Getenv(fakeGatewayEnv) != "" {
		fakeGateway()
		return
	}
	if folder := os.Getenv(fakeLauncherEnv); folder != "" {
		fakeLauncher(folder)
		return
	}
	os.Exit(m.Run())
}

type fakeStart struct {
	PID  int
	Exe  string
	Args []string
}

// fakeGateway listens on the -port and -local-port it is given, then writes what it was started with beside its
// executable. A build named *broken* exits at once, as a gateway that cannot start would; with slowMark beside it, a
// build waits 25 s before it listens, longer than deploy.ps1 waits.
func fakeGateway() {
	exe, _ := os.Executable()
	if strings.Contains(filepath.Base(exe), "broken") {
		os.Exit(3)
	}
	args := os.Args[1:]
	started := filepath.Join(filepath.Dir(exe), fmt.Sprintf("started-%d.json", os.Getpid()))
	if _, err := os.Stat(filepath.Join(filepath.Dir(exe), slowMark)); err == nil {
		data, _ := json.Marshal(fakeStart{PID: os.Getpid(), Exe: exe, Args: args})
		os.WriteFile(started, data, 0o644)
		time.Sleep(25 * time.Second)
	}
	for i := 0; i+1 < len(args); i++ {
		if args[i] != "-port" && args[i] != "-local-port" {
			continue
		}
		l, err := net.Listen("tcp", "127.0.0.1:"+args[i+1])
		if err != nil {
			os.Exit(4)
		}
		go func() {
			for {
				c, err := l.Accept()
				if err != nil {
					return
				}
				go func() { io.Copy(io.Discard, c); c.Close() }()
			}
		}()
	}
	data, _ := json.Marshal(fakeStart{PID: os.Getpid(), Exe: exe, Args: args})
	os.WriteFile(started, data, 0o644)
	select {}
}

type fakeFolder struct {
	t           *testing.T
	dir         string
	old, newer  string
	broken      string
	args        []string
	first       *exec.Cmd
	firstExited chan struct{}
}

// newFakeFolder starts the old build with the arguments the real gateway's shape has, a quoted name included.
func newFakeFolder(t *testing.T) *fakeFolder { return newFakeFolderIn(t, "", nil) }

// newFakeFolderIn puts the builds in the folder sub of a temporary one, and starts the old one with extra arguments
// after the usual ones.
func newFakeFolderIn(t *testing.T, sub string, extra []string) *fakeFolder {
	self, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	f := &fakeFolder{t: t, dir: filepath.Join(t.TempDir(), sub)}
	if err := os.MkdirAll(f.dir, 0o755); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(f.stopAll) // before t.TempDir's removal, which a running executable would fail
	body, err := os.ReadFile(self)
	if err != nil {
		t.Fatal(err)
	}
	// Bytes after the image leave it runnable and make each build's hash its own.
	for name, tail := range map[string]string{"old": "", "new": "new", "broken": "broken"} {
		path := filepath.Join(f.dir, "delta-remote-"+name+".exe")
		if err := os.WriteFile(path, append(append([]byte{}, body...), tail...), 0o755); err != nil {
			t.Fatal(err)
		}
	}
	f.old = filepath.Join(f.dir, "delta-remote-old.exe")
	f.newer = filepath.Join(f.dir, "delta-remote-new.exe")
	f.broken = filepath.Join(f.dir, "delta-remote-broken.exe")
	f.args = []string{"-port", freePort(t), "-local-port", freePort(t), "-mic", "Headset Microphone (2- Jabra EVOLVE 20 MS)"}
	f.args = append(f.args, extra...)

	f.first = exec.Command(f.old, f.args...)
	f.first.Env = fakeEnv()
	if err := f.first.Start(); err != nil {
		t.Fatal(err)
	}
	f.firstExited = make(chan struct{})
	go func() { f.first.Wait(); close(f.firstExited) }()
	f.await(f.old)
	return f
}

func freePort(t *testing.T) string {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	return fmt.Sprint(l.Addr().(*net.TCPAddr).Port)
}

func (f *fakeFolder) starts() []fakeStart {
	var all []fakeStart
	entries, _ := os.ReadDir(f.dir) // not filepath.Glob, which reads brackets in the folder's name
	for _, e := range entries {
		if !strings.HasPrefix(e.Name(), "started-") || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		var s fakeStart
		if data, err := os.ReadFile(filepath.Join(f.dir, e.Name())); err == nil && json.Unmarshal(data, &s) == nil {
			all = append(all, s)
		}
	}
	return all
}

// await waits for a start of exe other than the PIDs in not, and returns it.
func (f *fakeFolder) await(exe string, not ...int) fakeStart {
	f.t.Helper()
	for until := time.Now().Add(15 * time.Second); time.Now().Before(until); time.Sleep(100 * time.Millisecond) {
		for _, s := range f.starts() {
			if s.Exe == exe && !contains(not, s.PID) {
				return s
			}
		}
	}
	f.t.Fatalf("%s did not start; started: %+v", exe, f.starts())
	return fakeStart{}
}

func contains(pids []int, pid int) bool {
	for _, p := range pids {
		if p == pid {
			return true
		}
	}
	return false
}

func alive(pid int) bool {
	out, _ := exec.Command("tasklist", "/FI", fmt.Sprintf("PID eq %d", pid), "/NH").Output()
	return bytes.Contains(out, []byte(fmt.Sprint(pid)))
}

// stopAll ends every fake this folder started, and only those.
func (f *fakeFolder) stopAll() {
	for _, s := range f.starts() {
		if filepath.Dir(s.Exe) != f.dir {
			continue
		}
		if p, err := os.FindProcess(s.PID); err == nil {
			p.Kill()
			p.Wait()
		}
	}
	if f.first != nil && f.first.Process != nil {
		f.first.Process.Kill()
		select {
		case <-f.firstExited:
		case <-time.After(10 * time.Second):
			f.t.Errorf("the old build, PID %d, did not end", f.first.Process.Pid)
		}
	}
}

func (f *fakeFolder) deploy(args ...string) (string, error) {
	f.t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
	defer cancel()
	script, err := filepath.Abs("deploy.ps1")
	if err != nil {
		f.t.Fatal(err)
	}
	cmd := exec.CommandContext(ctx, "powershell", append([]string{"-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Dir", f.dir}, args...)...)
	cmd.Env = fakeEnv()
	// A file, not a pipe, so that these tests do not hang if the gateway kept the caller's pipe; deployPiped checks
	// that it does not.
	log, err := os.CreateTemp(f.dir, "deploy-*.log")
	if err != nil {
		f.t.Fatal(err)
	}
	defer log.Close()
	cmd.Stdout, cmd.Stderr = log, log
	err = cmd.Run()
	out, _ := os.ReadFile(log.Name())
	f.t.Logf("deploy.ps1 %s:\n%s", strings.Join(args, " "), out)
	return string(out), err
}

// deployPiped runs the script as a shell or a tool does: it reads the script's output through a pipe until the pipe
// closes. held says the pipe stayed open 5 s after the script ended, which a gateway that inherited it would do.
func (f *fakeFolder) deployPiped(args ...string) (out string, code int, held bool) {
	f.t.Helper()
	script, err := filepath.Abs("deploy.ps1")
	if err != nil {
		f.t.Fatal(err)
	}
	r, w, err := os.Pipe()
	if err != nil {
		f.t.Fatal(err)
	}
	defer r.Close()
	cmd := exec.Command("powershell", append([]string{"-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Dir", f.dir}, args...)...)
	cmd.Env = fakeEnv()
	cmd.Stdout, cmd.Stderr = w, w
	if err := cmd.Start(); err != nil {
		f.t.Fatal(err)
	}
	w.Close()
	var b bytes.Buffer
	closed := make(chan struct{})
	go func() { io.Copy(&b, r); close(closed) }()
	state, err := cmd.Process.Wait()
	if err != nil {
		f.t.Fatal(err)
	}
	select {
	case <-closed:
	case <-time.After(5 * time.Second):
		held = true
		r.Close() // the reader stops; what it read so far is kept
		<-closed
	}
	f.t.Logf("deploy.ps1 %s, through a pipe, exit %d, held %v:\n%s", strings.Join(args, " "), state.ExitCode(), held, b.String())
	return b.String(), state.ExitCode(), held
}

func TestDeployReturnsToACallerReadingItsOutputWhileTheGatewayRuns(t *testing.T) {
	t.Parallel()
	f := newFakeFolder(t)

	out, code, held := f.deployPiped("-Exe", f.newer)
	if held {
		t.Error("the caller's pipe stayed open after the script ended: the gateway holds it")
	}
	if code != 0 || !strings.Contains(out, "runs, PID") {
		t.Errorf("a deploy: exit %d", code)
	}
	newer := f.await(f.newer)
	if !reflect.DeepEqual(newer.Args, f.args) {
		t.Errorf("the new build got %q, not %q", newer.Args, f.args)
	}
	c, err := net.DialTimeout("tcp", "127.0.0.1:"+f.args[1], 2*time.Second)
	if err != nil {
		t.Fatalf("the gateway does not answer after the caller returned: %v", err)
	}
	c.Close()

	// The old build put back after a failure goes through the same start, and the failure's exit status must reach
	// the caller.
	out, code, held = f.deployPiped("-Exe", f.broken)
	if held {
		t.Error("after a failed deploy the caller's pipe stayed open: the gateway put back holds it")
	}
	if code != 1 || !strings.Contains(out, "runs again") {
		t.Errorf("a failed deploy: exit %d", code)
	}
	f.await(f.newer, newer.PID)
}

// A folder and arguments that a shell would read: a space, &, %, quotes, and brackets PowerShell would read. Windows allows them all in names.
func TestDeployKeepsArgumentsAShellWouldReadExactly(t *testing.T) {
	t.Parallel()
	f := newFakeFolderIn(t, `gate [1] way & 100%`+fakeMark+`%`, []string{
		"-title", "a & b", "-name", `say "hi" & 50% off`, "-brain", "%" + fakeMark + "%", "-exe", `C:\x y\^z|<w>.exe`,
	})

	out, code, held := f.deployPiped("-Exe", f.newer)
	if held || code != 0 || !strings.Contains(out, "runs, PID") {
		t.Errorf("a deploy: exit %d, pipe held %v", code, held)
	}
	newer := f.await(f.newer)
	if !reflect.DeepEqual(newer.Args, f.args) {
		t.Errorf("the new build got %q, not %q", newer.Args, f.args)
	}

	out, code, held = f.deployPiped("-Exe", f.broken)
	if held || code != 1 || !strings.Contains(out, "runs again") {
		t.Errorf("a failed deploy: exit %d, pipe held %v", code, held)
	}
	if again := f.await(f.newer, newer.PID); !reflect.DeepEqual(again.Args, f.args) {
		t.Errorf("the build put back got %q, not %q", again.Args, f.args)
	}
}

// A gateway started through an unquoted path with a space: its command line's first word is not its executable, so
// where its arguments start cannot be told. Nothing may change: no state, no file, no process.
func TestDeployRefusesACommandLineItCannotSplit(t *testing.T) {
	t.Parallel()
	f := newFakeFolderIn(t, "gate way", nil)
	f.first.Process.Kill()
	<-f.firstExited
	os.Remove(filepath.Join(f.dir, fmt.Sprintf("started-%d.json", f.first.Process.Pid)))

	f.first = exec.Command(f.old)
	f.first.SysProcAttr = &syscall.SysProcAttr{CmdLine: f.old + " " + strings.Join(f.args[:4], " ")}
	f.first.Env = fakeEnv()
	if err := f.first.Start(); err != nil {
		t.Fatal(err)
	}
	f.firstExited = make(chan struct{})
	go func() { f.first.Wait(); close(f.firstExited) }()
	f.await(f.old)
	before := map[string][]byte{}
	for _, exe := range []string{f.old, f.newer} {
		before[exe], _ = os.ReadFile(exe)
	}

	out, code, held := f.deployPiped("-Exe", f.newer)
	if held || code != 1 || !strings.Contains(out, "cannot be told from its command line: nothing replaced") {
		t.Errorf("an unsplittable command line: exit %d, pipe held %v", code, held)
	}
	if !alive(f.first.Process.Pid) || len(f.starts()) != 1 {
		t.Error("a gateway was stopped or started")
	}
	if _, err := os.Stat(filepath.Join(f.dir, "deploy.json")); !os.IsNotExist(err) {
		t.Error("deploy.json was written")
	}
	for exe, b := range before {
		if now, _ := os.ReadFile(exe); !bytes.Equal(now, b) {
			t.Errorf("%s changed", filepath.Base(exe))
		}
	}
}

// invalidBuild is a file Windows cannot start at all, unlike the broken build, which starts and exits.
func (f *fakeFolder) invalidBuild() string {
	path := filepath.Join(f.dir, "delta-remote-invalid.exe")
	if err := os.WriteFile(path, []byte("not an executable\n"), 0o755); err != nil {
		f.t.Fatal(err)
	}
	return path
}

func TestDeployPutsTheOldBuildBackWhenTheNewOneCannotBeStarted(t *testing.T) {
	t.Parallel()
	f := newFakeFolder(t)

	out, code, held := f.deployPiped("-Exe", f.invalidBuild())
	if held || code != 1 || !strings.Contains(out, "could not start") || !strings.Contains(out, "runs again") {
		t.Errorf("a build that cannot start: exit %d, pipe held %v", code, held)
	}
	again := f.await(f.old, f.first.Process.Pid)
	if !reflect.DeepEqual(again.Args, f.args) {
		t.Errorf("the old build came back with %q, not %q", again.Args, f.args)
	}
	data, _ := os.ReadFile(filepath.Join(f.dir, "deploy.json"))
	var state struct{ Previous *string }
	if json.Unmarshal(data, &state) != nil || state.Previous != nil {
		t.Errorf("deploy.json after the rollback names a replaced build: %s", data)
	}
}

// The old build put back cannot start either: its file was moved away while it ran, and something else took its name.
func TestDeploySaysNoGatewayRunsWhenNeitherBuildCanStart(t *testing.T) {
	t.Parallel()
	f := newFakeFolder(t)
	if err := os.Rename(f.old, f.old+".moved"); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(f.old, []byte("not an executable either\n"), 0o755); err != nil {
		t.Fatal(err)
	}

	out, code, held := f.deployPiped("-Exe", f.invalidBuild())
	if held || code != 1 || !strings.Contains(out, "no gateway runs") || strings.Contains(out, "runs again") {
		t.Errorf("neither build starts: exit %d, pipe held %v", code, held)
	}
}

// The old build put back is still starting when deploy.ps1 stops waiting: it is not said to be gone.
func TestDeploySaysTheOldBuildStillStartsWhenItIsSlowToListenAgain(t *testing.T) {
	t.Parallel()
	f := newFakeFolder(t)
	if err := os.WriteFile(filepath.Join(f.dir, slowMark), nil, 0o644); err != nil {
		t.Fatal(err)
	}

	out, err := f.deploy("-Exe", f.broken)
	again := f.await(f.old, f.first.Process.Pid)
	if err == nil || strings.Contains(out, "no gateway runs") || !strings.Contains(out, fmt.Sprintf("PID %d", again.PID)) {
		t.Errorf("the old build, PID %d, still starting, was reported as: %v", again.PID, err)
	}
	if !alive(again.PID) {
		t.Errorf("the old build, PID %d, was stopped", again.PID)
	}
}

// Built from the checkout, the new build goes into the folder only once nothing refuses the deploy.
func TestDeployLeavesNoNewBuildInTheFolderWhenItRefuses(t *testing.T) {
	t.Parallel()
	f := newFakeFolder(t)
	c, err := net.Dial("tcp", "127.0.0.1:"+f.args[1])
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()

	out, code, _ := f.deployPiped()
	if code != 1 || !strings.Contains(out, "nothing replaced") {
		t.Errorf("deployed while a device was connected: exit %d", code)
	}
	entries, _ := os.ReadDir(f.dir)
	for _, e := range entries {
		if n := e.Name(); strings.HasSuffix(n, ".exe") && !strings.HasPrefix(n, "delta-remote-old") &&
			!strings.HasPrefix(n, "delta-remote-new") && !strings.HasPrefix(n, "delta-remote-broken") {
			t.Errorf("a refused deploy left %s in the folder", n)
		}
	}
	if !alive(f.first.Process.Pid) {
		t.Error("the old build stopped")
	}
	if _, err := os.Stat(filepath.Join(f.dir, "deploy.json")); !os.IsNotExist(err) {
		t.Error("deploy.json was written")
	}
}

func TestDeployPutsTheNewBuildInPlaceWithTheSameArgumentsAndBack(t *testing.T) {
	t.Parallel()
	f := newFakeFolder(t)
	first := f.first.Process.Pid

	if _, err := f.deploy("-Exe", f.newer); err != nil {
		t.Fatal(err)
	}
	newer := f.await(f.newer)
	if !reflect.DeepEqual(newer.Args, f.args) {
		t.Errorf("the new build got %q, not %q", newer.Args, f.args)
	}
	// -resume starts the build deploy.json names.
	data, _ := os.ReadFile(filepath.Join(f.dir, "deploy.json"))
	var state deployState
	if json.Unmarshal(data, &state) != nil || !strings.EqualFold(state.Current, f.newer) {
		t.Errorf("deploy.json does not name the build it started: %s", data)
	}
	select {
	case <-f.firstExited:
	case <-time.After(10 * time.Second):
		t.Fatal("the old build still runs")
	}

	if _, err := f.deploy("-Back"); err != nil {
		t.Fatal(err)
	}
	back := f.await(f.old, first)
	if !reflect.DeepEqual(back.Args, f.args) {
		t.Errorf("the old build came back with %q, not %q", back.Args, f.args)
	}
	if alive(newer.PID) {
		t.Error("the new build still runs after -Back")
	}

	out, err := f.deploy("-Exe", f.old)
	if err != nil || !strings.Contains(out, "already runs this build") || !alive(back.PID) {
		t.Errorf("deploying the running build replaced it: %v", err)
	}
}

func TestDeployReplacesNothingWhileSomeoneIsConnected(t *testing.T) {
	t.Parallel()
	f := newFakeFolder(t)
	c, err := net.Dial("tcp", "127.0.0.1:"+f.args[1])
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()

	out, err := f.deploy("-Exe", f.newer)
	if err == nil || !strings.Contains(out, "nothing replaced") {
		t.Errorf("deployed while a device was connected: %v", err)
	}
	if !alive(f.first.Process.Pid) {
		t.Error("the old build stopped")
	}
	for _, s := range f.starts() {
		if s.Exe == f.newer {
			t.Error("the new build started")
		}
	}
}

func TestDeployPutsTheOldBuildBackWhenTheNewOneDoesNotStart(t *testing.T) {
	t.Parallel()
	f := newFakeFolder(t)

	out, err := f.deploy("-Exe", f.broken)
	if err == nil || !strings.Contains(out, "runs again") {
		t.Errorf("a build that does not start was left in place: %v", err)
	}
	again := f.await(f.old, f.first.Process.Pid)
	if !reflect.DeepEqual(again.Args, f.args) {
		t.Errorf("the old build came back with %q, not %q", again.Args, f.args)
	}
}

func TestDeployStartsAGatewayThatDiedWithItsArguments(t *testing.T) {
	t.Parallel()
	f := newFakeFolder(t)
	if _, err := f.deploy("-Exe", f.newer); err != nil {
		t.Fatal(err)
	}
	died := f.await(f.newer)
	if p, err := os.FindProcess(died.PID); err == nil {
		p.Kill()
		p.Wait()
	}

	if _, err := f.deploy("-Exe", f.newer); err != nil {
		t.Fatal(err)
	}
	if again := f.await(f.newer, died.PID); !reflect.DeepEqual(again.Args, f.args) {
		t.Errorf("the gateway came back with %q, not %q", again.Args, f.args)
	}
}

// With no gateway running, -Check says why without showing a log line: when Windows started, what the last log holds,
// counted by its fixed prefixes, the last resume, and the build to resume.
func TestDeployCheckSaysWhyNoGatewayRunsWithoutShowingTheLog(t *testing.T) {
	t.Parallel()
	f := newFakeFolder(t)
	if _, err := f.deploy("-Exe", f.newer); err != nil {
		t.Fatal(err)
	}
	died := f.await(f.newer)
	if p, err := os.FindProcess(died.PID); err == nil {
		p.Kill()
		p.Wait()
	}
	var errLog string
	entries, _ := os.ReadDir(f.dir)
	for _, e := range entries {
		if strings.HasPrefix(e.Name(), "err-") {
			errLog = filepath.Join(f.dir, e.Name()) // the last, by its time stamp
		}
	}
	lines := "delta-remote: the QR: Post \"http://127.0.0.1:8807/link?k=SECRET-1\": connection refused\n" +
		"delta-remote: the QR: Post \"http://127.0.0.1:8807/link?k=SECRET-2\": connection refused\n" +
		"delta-remote: guest 3: ICE connected owner@SECRET-3\n" +
		"[gfxcapture] SECRET-4 something ffmpeg said\n"
	if err := os.WriteFile(errLog, []byte(lines), 0o644); err != nil {
		t.Fatal(err)
	}
	resumed := "2026-11-11T01:00:00Z delta-remote-new.exe runs, PID 42\n"
	if err := os.WriteFile(filepath.Join(f.dir, "resume.log"), []byte(resumed), 0o644); err != nil {
		t.Fatal(err)
	}

	out, err := f.deploy("-Check", "-Exe", f.newer)
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"running: no gateway", "Windows started: ", "2 the QR", "1 guest", "1 other",
		"last resume: 2026-11-11T01:00:00Z delta-remote-new.exe runs, PID 42", "to resume: delta-remote-new.exe",
		"a deploy would do: start it"} {
		if !strings.Contains(out, want) {
			t.Errorf("-Check does not say %q", want)
		}
	}
	for _, kept := range []string{"SECRET", "Jabra", "8807", "gfxcapture"} {
		if strings.Contains(out, kept) {
			t.Errorf("-Check shows %q, from a log or an argument", kept)
		}
	}
}

func TestDeployCheckChangesNothing(t *testing.T) {
	t.Parallel()
	f := newFakeFolder(t)

	out, err := f.deploy("-Check", "-Exe", f.newer)
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"connected: 0", "-port -local-port -mic", "a new gateway's job: ", "a deploy would do: replace it"} {
		if !strings.Contains(out, want) {
			t.Errorf("-Check does not say %q", want)
		}
	}
	if strings.Contains(out, "Jabra") {
		t.Error("-Check shows an argument's value")
	}
	if !alive(f.first.Process.Pid) || len(f.starts()) != 1 {
		t.Error("-Check started or stopped a gateway")
	}
	if _, err := os.Stat(filepath.Join(f.dir, "deploy.json")); !os.IsNotExist(err) {
		t.Error("-Check wrote deploy.json")
	}
}
