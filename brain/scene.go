package main

import (
	"encoding/json"
	"net"
	"sync"
	"time"
)

// scene tells the window what Delta is doing, one JSON line per change. Without a window it does nothing, and a lost
// window is dialled again at the next change.
type scene struct {
	mu    sync.Mutex
	addr  string
	conn  net.Conn
	hello []byte // the line each new connection starts with, if any: what a window must know whatever it missed
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
