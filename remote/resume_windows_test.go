package main

import (
	"encoding/binary"
	"encoding/json"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"reflect"
	"strings"
	"syscall"
	"testing"
	"time"
	"unsafe"
)

// resumeFolder is a folder as deploy.ps1 leaves it: a fake build, and deploy.json naming it with its arguments.
func resumeFolder(t *testing.T, build string, state map[string]string) (*fakeFolder, string) {
	self, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	f := &fakeFolder{t: t, dir: t.TempDir()}
	t.Cleanup(f.stopAll)
	body, err := os.ReadFile(self)
	if err != nil {
		t.Fatal(err)
	}
	exe := filepath.Join(f.dir, "delta-remote-"+build+".exe")
	if err := os.WriteFile(exe, body, 0o755); err != nil {
		t.Fatal(err)
	}
	if _, ok := state["current"]; !ok {
		state["current"] = exe
	}
	data, _ := json.Marshal(state)
	if err := os.WriteFile(filepath.Join(f.dir, "deploy.json"), data, 0o644); err != nil {
		t.Fatal(err)
	}
	return f, exe
}

func testResumer(dir string, probe func() error) *resumer {
	r := newResumer(dir)
	r.probe, r.wait, r.poll, r.settle, r.env = probe, 2*time.Second, 10*time.Millisecond, time.Second, fakeEnv()
	return r
}

func resumeLog(t *testing.T, dir string) string {
	data, _ := os.ReadFile(filepath.Join(dir, "resume.log"))
	return string(data)
}

func TestResumeStartsTheDeployedBuildWithItsArgumentsOnceTheNetworkIsUp(t *testing.T) {
	t.Parallel()
	port, local := freePort(t), freePort(t)
	f, exe := resumeFolder(t, "new", map[string]string{
		"arguments": `-port ` + port + ` -local-port ` + local +
			` -access-email owner@example.test -mic "Headset Microphone (2- Jabra EVOLVE 20 MS)"`,
	})
	calls := 0
	r := testResumer(f.dir, func() error {
		if calls++; calls < 4 {
			return errors.New("no default route")
		}
		return nil
	})

	if err := r.run(); err != nil {
		t.Fatal(err)
	}
	if calls < 4 {
		t.Errorf("started after %d network probes, before the network was up", calls)
	}
	got := f.await(exe)
	want := []string{"-port", port, "-local-port", local, "-access-email", "owner@example.test",
		"-mic", "Headset Microphone (2- Jabra EVOLVE 20 MS)"}
	if !reflect.DeepEqual(got.Args, want) {
		t.Errorf("the gateway got %q, not %q", got.Args, want)
	}

	if err := r.run(); err != nil || len(f.starts()) != 1 {
		t.Errorf("a second -resume while it runs: %v, %d starts", err, len(f.starts()))
	}
	log := resumeLog(t, f.dir)
	if !strings.Contains(log, "runs, PID") || !strings.Contains(log, "already runs from here") {
		t.Errorf("resume.log does not say what each -resume did:\n%s", log)
	}
	for _, kept := range []string{"example.test", "Jabra", "-port", port} {
		if strings.Contains(log, kept) {
			t.Errorf("resume.log holds an argument, %q:\n%s", kept, log)
		}
	}
}

func TestResumeStartsNothingWhileTheNetworkIsDown(t *testing.T) {
	t.Parallel()
	f, _ := resumeFolder(t, "new", map[string]string{"arguments": "-port " + freePort(t)})
	r := testResumer(f.dir, func() error { return errors.New("no default route") })
	r.wait = 300 * time.Millisecond

	err := r.run()
	if err == nil || !strings.Contains(err.Error(), "no LAN address") {
		t.Errorf("-resume without a network: %v", err)
	}
	if len(f.starts()) != 0 {
		t.Error("a gateway started without a network")
	}
	if !strings.Contains(resumeLog(t, f.dir), "nothing started: no LAN address") {
		t.Errorf("resume.log:\n%s", resumeLog(t, f.dir))
	}
}

// A gateway given -ip needs no default route, so -resume does not wait for one.
func TestResumeDoesNotWaitForARouteAGivenAddressDoesNotNeed(t *testing.T) {
	t.Parallel()
	f, exe := resumeFolder(t, "new", map[string]string{"arguments": "-ip 127.0.0.1 -port " + freePort(t)})
	r := testResumer(f.dir, func() error { return errors.New("no default route") })
	r.wait = 0

	if err := r.run(); err != nil {
		t.Fatal(err)
	}
	f.await(exe)
}

func TestResumeSaysWhyABuildThatExitsAtOnceDoesNotRun(t *testing.T) {
	t.Parallel()
	f, _ := resumeFolder(t, "broken", map[string]string{"arguments": "-port " + freePort(t)})
	r := testResumer(f.dir, func() error { return nil })
	r.tries = 2

	err := r.run()
	if err == nil || !strings.Contains(err.Error(), "after 2 tries") || !strings.Contains(err.Error(), "no gateway runs") {
		t.Errorf("a build that exits at once: %v", err)
	}
	if !strings.Contains(resumeLog(t, f.dir), "after 2 tries") {
		t.Errorf("resume.log:\n%s", resumeLog(t, f.dir))
	}
}

// A folder deployed before deploy.json named its build: nothing to resume, and it says so.
func TestResumeNeedsTheBuildDeployJSONNames(t *testing.T) {
	t.Parallel()
	f, _ := resumeFolder(t, "new", map[string]string{"arguments": "-port 1", "current": ""})
	r := testResumer(f.dir, func() error { return nil })

	if err := r.run(); err == nil || !strings.Contains(err.Error(), "names no deployed build") {
		t.Errorf("deploy.json without a build: %v", err)
	}
	if len(f.starts()) != 0 {
		t.Error("a gateway started")
	}
}

// fakeLauncherEnv names the folder a copy of this test binary resumes, as `delta-remote -resume` would, once the test
// has put it in a job and written "go" there.
const fakeLauncherEnv = "DELTA_REMOTE_FAKE_LAUNCHER"

func fakeLauncher(folder string) {
	for until := time.Now().Add(15 * time.Second); time.Now().Before(until); time.Sleep(50 * time.Millisecond) {
		if _, err := os.Stat(filepath.Join(folder, "go")); err == nil {
			break
		}
	}
	r := newResumer(folder)
	r.probe, r.settle, r.env = func() error { return nil }, time.Second, fakeEnv()
	if r.run() != nil {
		os.Exit(1)
	}
}

var (
	procCreateJobObjectW         = kernel32.NewProc("CreateJobObjectW")
	procSetInformationJobObject  = kernel32.NewProc("SetInformationJobObject")
	procAssignProcessToJobObject = kernel32.NewProc("AssignProcessToJobObject")
	procIsProcessInJob           = kernel32.NewProc("IsProcessInJob")
	procQueryInformationJob      = kernel32.NewProc("QueryInformationJobObject")
)

const (
	jobExtendedLimits = 9
	jobKillOnClose    = 0x2000
	jobBreakawayOK    = 0x800
)

// newJob is a job object with these limit flags; JOBOBJECT_EXTENDED_LIMIT_INFORMATION is 144 bytes on amd64, its
// LimitFlags after two 8-byte times.
func newJob(t *testing.T, flags uint32) syscall.Handle {
	h, _, err := procCreateJobObjectW.Call(0, 0)
	if h == 0 {
		t.Fatal(err)
	}
	info := make([]byte, 144)
	binary.LittleEndian.PutUint32(info[16:], flags)
	if r, _, err := procSetInformationJobObject.Call(h, jobExtendedLimits, uintptr(unsafe.Pointer(&info[0])), 144); r == 0 {
		t.Fatal(err)
	}
	return syscall.Handle(h)
}

func inJob(pid int, job syscall.Handle) bool {
	h, err := syscall.OpenProcess(processQueryLimitedInformation, false, uint32(pid))
	if err != nil {
		return false
	}
	defer syscall.CloseHandle(h)
	var in int32
	procIsProcessInJob.Call(uintptr(h), uintptr(job), uintptr(unsafe.Pointer(&in)))
	return in != 0
}

// ownJobLetsChildrenGo: this test's own job, if any, lets a child break away, as this tool's shells do; a CI runner's
// may not, and then the launcher cannot leave a job it was put in either.
func ownJobLetsChildrenGo() bool {
	var in int32
	procIsProcessInJob.Call(uintptr(^uintptr(0)), 0, uintptr(unsafe.Pointer(&in))) // the current process
	if in == 0 {
		return true
	}
	info := make([]byte, 144)
	if r, _, _ := procQueryInformationJob.Call(0, jobExtendedLimits, uintptr(unsafe.Pointer(&info[0])), 144, 0); r == 0 {
		return false
	}
	return binary.LittleEndian.Uint32(info[16:])&jobBreakawayOK != 0
}

// launchInJob runs a launcher that resumes folder from inside a new job with these flags, and waits for it to end.
func launchInJob(t *testing.T, folder string, flags uint32) syscall.Handle {
	self, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	body, _ := os.ReadFile(self)
	launcher := filepath.Join(t.TempDir(), "launcher.exe") // not in folder, and not named delta-remote
	if err := os.WriteFile(launcher, body, 0o755); err != nil {
		t.Fatal(err)
	}
	job := newJob(t, flags)
	cmd := exec.Command(launcher)
	cmd.Env = append(os.Environ(), fakeLauncherEnv+"="+folder)
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	h, err := syscall.OpenProcess(0x0100|0x0001, false, uint32(cmd.Process.Pid)) // PROCESS_SET_QUOTA | PROCESS_TERMINATE
	if err != nil {
		t.Fatal(err)
	}
	r, _, aerr := procAssignProcessToJobObject.Call(uintptr(job), uintptr(h))
	syscall.CloseHandle(h)
	if r == 0 {
		t.Fatal(aerr)
	}
	if err := os.WriteFile(filepath.Join(folder, "go"), nil, 0o644); err != nil {
		t.Fatal(err)
	}
	if err := cmd.Wait(); err != nil {
		t.Fatalf("the launcher: %v", err)
	}
	return job
}

// A logon's launcher may run in a job that ends its processes when it closes, as a shell's may: the gateway leaves it.
func TestResumeStartsTheGatewayOutsideTheLauncherJob(t *testing.T) {
	t.Parallel()
	if !ownJobLetsChildrenGo() {
		t.Skip("this test's own job lets no child break away")
	}
	f, exe := resumeFolder(t, "new", map[string]string{"arguments": "-port " + freePort(t)})
	job := launchInJob(t, f.dir, jobKillOnClose|jobBreakawayOK)
	got := f.await(exe)
	if inJob(got.PID, job) {
		t.Error("the gateway is in the launcher's job")
	}
	syscall.CloseHandle(job)
	time.Sleep(time.Second)
	if !alive(got.PID) {
		t.Error("the gateway ended with the launcher's job")
	}
}

// A job that lets no child go: the gateway still starts, in it, and resume.log says it may end with it.
func TestResumeSaysWhenTheGatewayCannotLeaveTheLauncherJob(t *testing.T) {
	t.Parallel()
	f, exe := resumeFolder(t, "new", map[string]string{"arguments": "-port " + freePort(t)})
	job := launchInJob(t, f.dir, jobKillOnClose)
	defer syscall.CloseHandle(job)
	got := f.await(exe)
	if !inJob(got.PID, job) {
		t.Error("the gateway left a job that lets no child go")
	}
	if log := resumeLog(t, f.dir); !strings.Contains(log, "runs, PID") || !strings.Contains(log, "in the launcher's job") {
		t.Errorf("resume.log does not say the gateway stays in the launcher's job:\n%s", log)
	}
}
