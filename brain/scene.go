package main

import (
	"encoding/json"
	"net"
	"sync"
	"time"
)

// scene tells the window what Pinion is doing, one JSON line per change. Without a window it does nothing, and a lost
// window is dialled again at the next change.
type scene struct {
	mu   sync.Mutex
	addr string
	conn net.Conn
}

func dialScene(addr string) *scene { return &scene{addr: addr} }

func (s *scene) send(event map[string]any) {
	if s == nil || s.addr == "" {
		return
	}
	line, _ := json.Marshal(event)
	s.mu.Lock()
	defer s.mu.Unlock()
	for attempt := 0; attempt < 2; attempt++ {
		if s.conn == nil {
			c, err := net.DialTimeout("tcp", s.addr, 200*time.Millisecond)
			if err != nil {
				return
			}
			s.conn = c
		}
		s.conn.SetWriteDeadline(time.Now().Add(200 * time.Millisecond))
		if _, err := s.conn.Write(append(line, '\n')); err == nil {
			return
		}
		s.conn.Close()
		s.conn = nil
	}
}
