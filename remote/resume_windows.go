package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"syscall"
	"time"
	"unsafe"
)

// resumer starts again, after Windows restarts, the gateway deploy.ps1 last put in a folder: a shortcut in the Startup
// folder runs `delta-remote.exe -resume <folder>` at logon, with no shell. It waits for a LAN address first, since the
// gateway exits without one; it starts nothing when a gateway already runs from the folder; and it writes what it did,
// never the arguments, to <folder>\resume.log.
type resumer struct {
	folder string
	probe  func() error  // nil once the network is up: lanIP's own test by default
	wait   time.Duration // how long to wait for it
	poll   time.Duration
	tries  int           // starts of a build that exits at once
	settle time.Duration // a start still running after this has started
	env    []string      // the gateway's environment; nil inherits this one's
}

func newResumer(folder string) *resumer {
	return &resumer{
		folder: folder,
		probe:  func() error { _, err := lanIP(""); return err },
		wait:   3 * time.Minute,
		poll:   2 * time.Second,
		tries:  3,
		settle: 10 * time.Second,
	}
}

// deployState is what deploy.ps1 keeps in deploy.json: the arguments as one command line, and the build it started.
type deployState struct {
	Arguments string `json:"arguments"`
	Current   string `json:"current"`
}

const createNoWindow = 0x08000000

var givesIP = regexp.MustCompile(`(^|\s)--?ip[\s=]`)

func (r *resumer) run() error {
	if err := r.resume(); err != nil {
		r.log("nothing started: " + err.Error())
		return err
	}
	return nil
}

func (r *resumer) resume() error {
	data, err := os.ReadFile(filepath.Join(r.folder, "deploy.json"))
	if err != nil {
		return fmt.Errorf("no deploy.json: deploy once, then resume: %w", err)
	}
	var st deployState
	if err := json.Unmarshal(data, &st); err != nil {
		return fmt.Errorf("deploy.json: %w", err)
	}
	if st.Current == "" {
		return errors.New("deploy.json names no deployed build: deploy once, then resume")
	}
	name := filepath.Base(st.Current)
	if pid, ok := runningFrom(r.folder); ok {
		r.log(fmt.Sprintf("a gateway already runs from here, PID %d: nothing started", pid))
		return nil
	}
	// A gateway given its address needs no default route.
	if !givesIP.MatchString(st.Arguments) {
		if err := r.awaitNetwork(); err != nil {
			return err
		}
	}
	var last error
	for try := 1; try <= r.tries; try++ {
		pid, err := r.start(st)
		if err == nil {
			r.log(fmt.Sprintf("%s runs, PID %d", name, pid))
			return nil
		}
		last = err
		if try < r.tries {
			time.Sleep(r.settle)
		}
	}
	return fmt.Errorf("%s after %d tries: %w; no gateway runs", name, r.tries, last)
}

func (r *resumer) awaitNetwork() error {
	until := time.Now().Add(r.wait)
	for {
		err := r.probe()
		if err == nil {
			return nil
		}
		if time.Now().After(until) {
			return fmt.Errorf("no LAN address after %v: %w", r.wait, err)
		}
		time.Sleep(r.poll)
	}
}

// start runs the build with its command line as deploy.ps1 kept it, read by no shell, logging where deploy.ps1 does.
// A build still running after settle has started; the gateway outlives this process.
func (r *resumer) start(st deployState) (int, error) {
	stamp := time.Now().Format("0102-150405.000")
	stamp = strings.Replace(stamp, ".", "-", 1)
	out, err := os.Create(filepath.Join(r.folder, "out-"+stamp+".log"))
	if err != nil {
		return 0, err
	}
	defer out.Close()
	errLog, err := os.Create(filepath.Join(r.folder, "err-"+stamp+".log"))
	if err != nil {
		return 0, err
	}
	defer errLog.Close()
	line := `"` + st.Current + `"`
	if st.Arguments != "" {
		line += " " + st.Arguments
	}
	cmd := &exec.Cmd{
		Path:        st.Current,
		Dir:         r.folder,
		Env:         r.env,
		Stdout:      out,
		Stderr:      errLog,
		SysProcAttr: &syscall.SysProcAttr{CmdLine: line, CreationFlags: createNoWindow},
	}
	if err := cmd.Start(); err != nil {
		return 0, err
	}
	pid := cmd.Process.Pid
	exited := make(chan error, 1)
	go func() { exited <- cmd.Wait() }()
	select {
	case err := <-exited:
		if err == nil {
			err = errors.New("exit status 0")
		}
		return 0, fmt.Errorf("it exited at once: %w", err)
	case <-time.After(r.settle):
		return pid, nil
	}
}

func (r *resumer) log(line string) {
	f, err := os.OpenFile(filepath.Join(r.folder, "resume.log"), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return
	}
	defer f.Close()
	fmt.Fprintf(f, "%s %s\n", time.Now().UTC().Format("2006-01-02T15:04:05Z"), line)
}

// runningFrom is a delta-remote*.exe started from folder, as deploy.ps1 finds it.
func runningFrom(folder string) (int, bool) {
	snap, err := syscall.CreateToolhelp32Snapshot(syscall.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return 0, false
	}
	defer syscall.CloseHandle(snap)
	var e syscall.ProcessEntry32
	e.Size = uint32(unsafe.Sizeof(e))
	for err = syscall.Process32First(snap, &e); err == nil; err = syscall.Process32Next(snap, &e) {
		exe := syscall.UTF16ToString(e.ExeFile[:])
		if !strings.HasPrefix(strings.ToLower(exe), "delta-remote") || int(e.ProcessID) == os.Getpid() {
			continue
		}
		if path := imagePath(e.ProcessID); path != "" && strings.EqualFold(filepath.Dir(path), filepath.Clean(folder)) {
			return int(e.ProcessID), true
		}
	}
	return 0, false
}
