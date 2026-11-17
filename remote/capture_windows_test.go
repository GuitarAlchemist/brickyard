package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

// ffmpegParentEnv makes TestAnFFmpegEndsWithTheGateway, run again in a child, start a stand-in ffmpeg, write its PID to
// the file named, and wait to be killed.
const ffmpegParentEnv = "DELTA_REMOTE_FFMPEG_PARENT"

// deploy.ps1 stops the gateway with Stop-Process -Force, which gives it no chance to stop its ffmpeg: that ffmpeg
// must end with it, not go on capturing Delta's window for nobody.
func TestAnFFmpegEndsWithTheGateway(t *testing.T) {
	if file := os.Getenv(ffmpegParentEnv); file != "" {
		stop, err := startFFmpeg("ping", []string{"-n", "120", "127.0.0.1"}, func(error) {})
		if err != nil {
			t.Fatal(err)
		}
		defer stop()
		pid := strconv.Itoa(childPID(t, "PING.EXE"))
		if err := os.WriteFile(file, []byte(pid), 0o644); err != nil {
			t.Fatal(err)
		}
		time.Sleep(time.Minute)
		return
	}
	t.Parallel()
	self, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	file := filepath.Join(t.TempDir(), "ffmpeg.pid")
	gateway := exec.Command(self, "-test.run=^"+t.Name()+"$")
	gateway.Env = append(os.Environ(), ffmpegParentEnv+"="+file)
	if err := gateway.Start(); err != nil {
		t.Fatal(err)
	}
	var ffmpeg int
	for until := time.Now().Add(15 * time.Second); ffmpeg == 0 && time.Now().Before(until); time.Sleep(50 * time.Millisecond) {
		if data, err := os.ReadFile(file); err == nil {
			ffmpeg, _ = strconv.Atoi(strings.TrimSpace(string(data)))
		}
	}
	if ffmpeg == 0 {
		gateway.Process.Kill()
		t.Fatal("the stand-in gateway started no ffmpeg")
	}
	t.Cleanup(func() {
		if p, err := os.FindProcess(ffmpeg); err == nil && alive(ffmpeg) {
			p.Kill()
		}
	})

	gateway.Process.Kill() // as Stop-Process -Force does
	gateway.Wait()
	for until := time.Now().Add(5 * time.Second); alive(ffmpeg) && time.Now().Before(until); time.Sleep(100 * time.Millisecond) {
	}
	if alive(ffmpeg) {
		t.Errorf("the ffmpeg, PID %d, outlived the gateway that started it", ffmpeg)
	}
}

// childPID is the PID of this process's child named exe.
func childPID(t *testing.T, exe string) int {
	out, err := exec.Command("powershell", "-NoProfile", "-Command",
		"(Get-CimInstance Win32_Process -Filter \"ParentProcessId="+strconv.Itoa(os.Getpid())+" and Name='"+exe+"'\").ProcessId").Output()
	if err != nil {
		t.Fatal(err)
	}
	pid, err := strconv.Atoi(strings.TrimSpace(string(out)))
	if err != nil {
		t.Fatalf("no %s child: %q", exe, out)
	}
	return pid
}
