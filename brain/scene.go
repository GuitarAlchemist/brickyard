package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"io"
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
	hello    []byte                     // the line each new connection starts with, if any: what a window must know whatever it missed
	recall   func() map[string]any      // a line each new connection gets after hello, made then, as a pairing code with the seconds it has left; nil for none
	asked    func(event map[string]any) // what the window asks for, such as {"language":"fr+en"}, or nil
	launch   func()                     // starts the window, when the brain may (startScene)
	wanted   bool                       // the window should be on screen
	launched time.Time                  // its last start, so that a window that cannot start is not started at every line
	closed   string                     // the file the window leaves when the user closes it, if known
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

// lastPairing keeps the gateway's last pairing code, so that a window started after it came still shows it while it
// lives (recall). The code is a credential: it stays in memory, never in a log or the journal.
type lastPairing struct {
	mu    sync.Mutex
	rows  []string
	until time.Time
}

func (p *lastPairing) set(rows []string, now time.Time) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.rows, p.until = rows, now.Add(pairingSeconds*time.Second)
}

// line is the pairing line for a window that connects now, or nil when no code lives a second more.
func (p *lastPairing) line(now time.Time) map[string]any {
	p.mu.Lock()
	defer p.mu.Unlock()
	left := p.until.Sub(now).Seconds()
	if p.rows == nil || left < 1 {
		return nil
	}
	return map[string]any{"pairing": map[string]any{"rows": p.rows, "seconds": left}}
}

// read hands each JSON line the window writes back on c to asked, until c closes.
func (s *scene) read(c net.Conn) {
	lines := bufio.NewScanner(c)
	for lines.Scan() {
		var event map[string]any
		if json.Unmarshal(lines.Bytes(), &event) == nil && s.asked != nil {
			s.asked(event)
		}
	}
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
			go s.read(c)
		}
		s.conn.SetWriteDeadline(time.Now().Add(200 * time.Millisecond))
		out := append(line, '\n')
		if fresh && s.recall != nil {
			if event := s.recall(); event != nil {
				later, _ := json.Marshal(event)
				out = append(append(later, '\n'), out...)
			}
		}
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

// waitsForEdit says whether the dotnet watch that writes the log named waits for an edit because the window it ran
// exited, as after a crash: its last line then says so.
func waitsForEdit(name string) bool {
	f, err := os.Open(name)
	if err != nil {
		return false
	}
	defer f.Close()
	if info, err := f.Stat(); err == nil && info.Size() > 4096 {
		f.Seek(-4096, io.SeekEnd)
	}
	tail, _ := io.ReadAll(f)
	lines := bytes.Split(bytes.TrimSpace(tail), []byte("\n"))
	return bytes.Contains(lines[len(lines)-1], []byte("Waiting for a file to change"))
}
