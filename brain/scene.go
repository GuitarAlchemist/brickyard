package main

import (
	"encoding/json"
	"net"
	"os"
	"sync"
	"time"
)

// scene tells the window what Delta is doing, one JSON line per change. Without a window it does nothing, and a lost
// window is dialled again at the next change. A window that crashed, or that the user asks for (summon), is started
// again at the next line nobody answers; one the user closed stays closed until then, even one another brain started:
// the window leaves a marker when the user closes it.
type scene struct {
	mu       sync.Mutex
	addr     string
	conn     net.Conn
	hello    []byte    // the line each new connection starts with, if any: what a window must know whatever it missed
	launch   func()    // starts the window, when the brain may (startScene)
	wanted   bool      // the window should be on screen
	launched time.Time // its last start, so that a window that cannot start is not started at every line
	closed   string    // the file the window leaves when the user closes it, if known
}

// relaunchEvery is the least time between two starts of the window.
const relaunchEvery = 10 * time.Second

// summon asks for the window, as a request to move it or to open its shelf does: closed, it comes back.
func (s *scene) summon() {
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.wanted = true
	if s.closed != "" {
		os.Remove(s.closed)
	}
}

// userClosed says whether the user closed the window, whoever started it. The caller holds mu.
func (s *scene) userClosed() bool {
	if s.closed == "" {
		return false
	}
	_, err := os.Stat(s.closed)
	return err == nil
}

// exited records how a window the brain started ended: crashed, it comes back with the next line; closed, or crashed
// within half a minute of its start, as without a graphics card, it waits until it is summoned.
func (s *scene) exited(crashed bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.wanted = crashed && time.Since(s.launched) > 30*time.Second
}

func dialScene(addr string) *scene { return &scene{addr: addr} }

// greet sets the line each new connection starts with, such as Delta's language: a window started later, or by
// another brain, then learns it too.
func (s *scene) greet(event map[string]any) {
	if s == nil {
		return
	}
	line, _ := json.Marshal(event)
	s.mu.Lock()
	defer s.mu.Unlock()
	s.hello = append(line, '\n')
}

func (s *scene) send(event map[string]any) {
	if s == nil || s.addr == "" {
		return
	}
	line, _ := json.Marshal(event)
	s.mu.Lock()
	defer s.mu.Unlock()
	for attempt := 0; attempt < 2; attempt++ {
		fresh := s.conn == nil
		if fresh {
			c, err := net.DialTimeout("tcp", s.addr, 200*time.Millisecond)
			if err != nil {
				if s.launch != nil && s.wanted && !s.userClosed() && time.Since(s.launched) > relaunchEvery {
					s.launched = time.Now()
					go s.launch()
				}
				return
			}
			s.conn = c
		}
		s.conn.SetWriteDeadline(time.Now().Add(200 * time.Millisecond))
		out := append(line, '\n')
		if fresh && s.hello != nil {
			out = append(append([]byte(nil), s.hello...), out...)
		}
		if _, err := s.conn.Write(out); err == nil {
			return
		}
		s.conn.Close()
		s.conn = nil
	}
}
