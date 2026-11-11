package main

import (
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"time"
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
