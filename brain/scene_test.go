package main

import (
	"bufio"
	"net"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// A window that crashed, or that the user asks for, comes back with the next line; one the user closed stays closed.
func TestTheBrainBringsTheWindowBack(t *testing.T) {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	addr := l.Addr().String()
	l.Close() // nothing answers there
	s := dialScene(addr)
	launches := make(chan struct{}, 8)
	s.launch = func() { launches <- struct{}{} }
	started := func() int {
		time.Sleep(100 * time.Millisecond) // send starts the window in the background
		return len(launches)
	}
	s.exited(false)
	s.send(map[string]any{"state": "idle"})
	if started() != 0 {
		t.Fatal("a window the user closed came back by itself")
	}
	s.summon()
	s.send(map[string]any{"window": "show"})
	if started() != 1 {
		t.Fatal("a window the user asked for stayed away")
	}
	s.send(map[string]any{"window": "show"})
	if started() != 1 {
		t.Fatal("started again before it could answer")
	}
	s.mu.Lock()
	s.launched = time.Time{}
	s.mu.Unlock()
	s.exited(true)
	s.send(map[string]any{"state": "speaking"})
	if started() != 2 {
		t.Fatal("a window that crashed stayed away")
	}
	// A window another brain started, which the user closed: its marker keeps it closed until summoned.
	s.closed = filepath.Join(t.TempDir(), "scene-8805.closed")
	os.WriteFile(s.closed, []byte("now"), 0o644)
	s.mu.Lock()
	s.launched, s.wanted = time.Time{}, true
	s.mu.Unlock()
	s.send(map[string]any{"state": "idle"})
	if started() != 2 {
		t.Fatal("a window the user closed came back, though another brain had started it")
	}
	s.summon()
	if _, err := os.Stat(s.closed); err == nil {
		t.Fatal("asking for the window left its marker")
	}
	s.send(map[string]any{"window": "show"})
	if started() != 3 {
		t.Fatal("a window the user closed, then asked for, stayed away")
	}

	s.mu.Lock()
	s.launched = time.Now().Add(-relaunchEvery - time.Second) // it crashed 11 s after its start
	s.mu.Unlock()
	s.exited(true)
	s.send(map[string]any{"state": "idle"})
	if started() != 3 {
		t.Fatal("a window that cannot start was started again and again")
	}
}

// A window learns Delta's language on every connection: the one the brain started, and one started later or by
// another brain, which heard nothing before.
func TestTheWindowLearnsTheLanguageOnEachConnection(t *testing.T) {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	lines := make(chan string, 8)
	go func() {
		for {
			c, err := l.Accept()
			if err != nil {
				return
			}
			go func() {
				in := bufio.NewScanner(c)
				for in.Scan() {
					lines <- in.Text()
				}
			}()
		}
	}()
	next := func() string {
		select {
		case line := <-lines:
			return line
		case <-time.After(2 * time.Second):
			t.Fatal("the window heard nothing")
			return ""
		}
	}
	s := dialScene(l.Addr().String())
	s.greet(map[string]any{"language": "fr"})
	s.send(map[string]any{"state": "idle"})
	if a, b := next(), next(); a != `{"language":"fr"}` || b != `{"state":"idle"}` {
		t.Fatalf("%s then %s", a, b)
	}
	s.send(map[string]any{"state": "listening"})
	if got := next(); got != `{"state":"listening"}` {
		t.Fatalf("greeted twice on one connection: %s", got)
	}
	s.mu.Lock()
	s.conn.Close() // the window closed; a new one listens on the same address
	s.mu.Unlock()
	s.send(map[string]any{"state": "thinking"}) // finds the connection gone, dials again, greets again
	if a, b := next(), next(); a != `{"language":"fr"}` || b != `{"state":"thinking"}` {
		t.Fatalf("%s then %s", a, b)
	}
}

// A window that starts after the gateway posted its pairing link still gets the code, with the seconds it has left
// when it connects, after the hello; once the code expired, a new window gets none.
func TestALaterWindowGetsThePairingCodeStillAlive(t *testing.T) {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	lines := make(chan string, 8)
	go func() {
		for {
			c, err := l.Accept()
			if err != nil {
				return
			}
			go func() {
				in := bufio.NewScanner(c)
				for in.Scan() {
					lines <- in.Text()
				}
			}()
		}
	}()
	next := func() string {
		select {
		case line := <-lines:
			return line
		case <-time.After(2 * time.Second):
			t.Fatal("the window heard nothing")
			return ""
		}
	}
	now := time.Date(2026, 11, 12, 10, 0, 0, 0, time.UTC)
	var p lastPairing
	rows := []string{"101", "010", "101"}
	p.set(rows, now)
	s := dialScene(l.Addr().String())
	s.greet(map[string]any{"language": "fr"})
	s.recall = func() map[string]any { return p.line(now.Add(30 * time.Second)) }
	s.send(map[string]any{"state": "idle"})
	if a, b, c := next(), next(), next(); a != `{"language":"fr"}` || b != `{"pairing":{"rows":["101","010","101"],"seconds":90}}` || c != `{"state":"idle"}` {
		t.Fatalf("%s, %s, %s", a, b, c)
	}
	s.send(map[string]any{"state": "listening"})
	if got := next(); got != `{"state":"listening"}` {
		t.Fatalf("recalled twice on one connection: %s", got)
	}
	s.mu.Lock()
	s.conn.Close()
	s.mu.Unlock()
	s.recall = func() map[string]any { return p.line(now.Add(pairingSeconds * time.Second)) }
	s.send(map[string]any{"state": "thinking"})
	if a, b := next(), next(); a != `{"language":"fr"}` || b != `{"state":"thinking"}` {
		t.Fatalf("an expired code was recalled: %s then %s", a, b)
	}
	if p.line(now.Add(119500*time.Millisecond)) != nil {
		t.Fatal("a code with less than a second left is not worth showing")
	}
}

// The window writes back what the user asks of it, such as another language, one JSON line each, on the connection
// the brain dialled.
func TestTheBrainHearsWhatTheWindowAsks(t *testing.T) {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	go func() {
		c, err := l.Accept()
		if err != nil {
			return
		}
		defer c.Close()
		in := bufio.NewScanner(c)
		if in.Scan() {
			c.Write([]byte("{\"language\":\"fr+en\"}\nnot json\n"))
		}
		time.Sleep(time.Second)
	}()
	asked := make(chan map[string]any, 2)
	s := dialScene(l.Addr().String())
	s.asked = func(event map[string]any) { asked <- event }
	s.send(map[string]any{"state": "idle"})
	select {
	case event := <-asked:
		if event["language"] != "fr+en" {
			t.Fatalf("%v", event)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("the brain never heard the window")
	}
}

// Under -scene-source, the brain starts dotnet watch again when the window it ran exited, and not while it builds.
func TestTheBrainSeesWhenDotnetWatchWaitsForAnEdit(t *testing.T) {
	name := filepath.Join(t.TempDir(), "scene.log")
	for log, want := range map[string]bool{
		"dotnet watch ⌚ Building ...\n": false,
		"Unhandled exception.\ndotnet watch ❌ [Scene (net10.0)] Exited with error code -532462766\ndotnet watch ⏳ Waiting for a file to change before restarting ...\n": true,
		"dotnet watch ⏳ Waiting for a file to change before restarting ...\ndotnet watch ⌚ File changed: .\\Panel.cs\n":                                                 false,
		strings.Repeat("SCENE idle\n", 1000) + "dotnet watch ⏳ Waiting for a file to change before restarting ...\r\n":                                                  true,
		"": false,
	} {
		os.WriteFile(name, []byte(log), 0o644)
		if waitsForEdit(name) != want {
			t.Errorf("%q: want %v", log[max(0, len(log)-80):], want)
		}
	}
	if waitsForEdit(filepath.Join(t.TempDir(), "none.log")) {
		t.Error("no log waits for nothing")
	}
}
