package main

import (
	"bufio"
	"net"
	"testing"
	"time"
)

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
