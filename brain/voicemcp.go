package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// mcpConfig is what -mcp runs with. -home is where setup.ps1 installed Delta's ears and voice; flags override it.
type mcpConfig struct {
	home, piper, voice, whisper, whisperModel string
	scene                                     string // the scene's address
	journal                                   string // where to record what Delta heard and said, if anywhere
	noScene, mute, wakeOff                    bool
	threads, audioCtx                         int
}

// journal appends one JSON line per event, timestamped: what Delta heard and where it went, what Claude asked it to say
// and in answer to which utterance, and when Delta said it. It ties a spoken question to its spoken answer.
type journal struct {
	mu sync.Mutex
	f  *os.File
}

func (j *journal) record(event string, fields map[string]any) {
	line := map[string]any{"t": time.Now().Format(time.RFC3339Nano), "event": event}
	for k, v := range fields {
		line[k] = v
	}
	data, err := json.Marshal(line)
	if err != nil {
		return
	}
	j.mu.Lock()
	defer j.mu.Unlock()
	j.f.Write(append(data, '\n'))
}

// runMCP is -mcp: Delta as Claude Code's voice, with Claude as its brain. It answers initialize at once and gets its
// voice, its ears and its window ready behind; until then say queues and listen says why it cannot hear yet. It
// returns when Claude Code closes its stdin, and Windows then stops every child with it.
func runMCP(c mcpConfig) {
	rpc := os.Stdout
	os.Stdout = os.Stderr // stdout carries JSON-RPC and nothing else
	logf := func(format string, args ...any) { fmt.Fprintf(os.Stderr, "delta: "+format+"\n", args...) }
	homePaths(&c)

	var hearing sync.Mutex
	notHearing := errors.New("Delta is still starting: its ears (whisper.cpp) are loading. Try again in a few seconds.")
	m := &mcpServer{version: version, out: rpc, listenMin: 5 * time.Second, listenMax: 300 * time.Second}
	m.gate.window = 20 * time.Second
	m.gate.follow = 15 * time.Second
	m.gate.off = c.wakeOff
	m.ready = func() error {
		hearing.Lock()
		defer hearing.Unlock()
		return notHearing
	}

	if c.journal != "" {
		f, err := os.OpenFile(c.journal, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
		if err != nil {
			logf("journal: %v", err)
		} else {
			j := &journal{f: f}
			m.record = j.record
			j.record("start", map[string]any{"version": version})
		}
	}

	s := startScene(c, logf)
	mo := &mouth{s: s, gate: &m.gate, mute: c.mute, queue: make(chan said, 32), record: m.note}
	m.speak = mo.speak
	m.pushed = func() { // Delta looks thoughtful while Claude works, and idle again if no answer comes
		s.send(map[string]any{"state": "thinking"})
		at := time.Now()
		time.AfterFunc(90*time.Second, func() {
			if !mo.spokeSince(at) {
				s.send(map[string]any{"state": "idle"})
			}
		})
	}

	go func() {
		dir, err := os.MkdirTemp("", "delta-voice-")
		if err == nil {
			var v *voice
			if v, err = startVoice(c.piper, c.voice, dir, "--sentence_silence", "0.1"); err == nil {
				if _, err = v.say("Bonjour."); err == nil { // loads the voice model
					logf("voice ready")
					mo.run(v)
					return
				}
			}
		}
		logf("voice: %v", err)
		mo.fail(err)
	}()

	go func() {
		e, err := startEars(c.whisper, c.whisperModel, freePort(), c.threads, "Delta, Brickyard, Claude Code.", c.audioCtx)
		if err == nil {
			_, err = e.hear(make([]float32, earsRate)) // loads the model's weights and kernels
		}
		hearing.Lock()
		if err != nil {
			notHearing = fmt.Errorf("Delta cannot hear: %v. The delta:setup skill installs what is missing.", err)
			hearing.Unlock()
			logf("ears: %v", err)
			return
		}
		notHearing = nil
		hearing.Unlock()
		logf("ears ready on %s", e.url)
		heard := make(chan utterance)
		go listen(e, s, &mo.busy, heard, false)
		for u := range heard {
			where := "nothing"
			if u.text != "" {
				where = m.hear(u, time.Now())
			}
			logf("heard %q in %d ms: %s", u.text, u.asr.Milliseconds(), where)
			if where == "nothing" || where == "ignored" {
				s.send(map[string]any{"state": "idle"})
			}
			mo.routed()
		}
	}()

	if err := m.serve(os.Stdin); err != nil {
		logf("stdin: %v", err)
	}
}

// homePaths fills the paths -mcp needs from -home, where setup.ps1 puts Piper, a voice and whisper.cpp, or from
// home's delta.json, where it records the user's own copies instead.
func homePaths(c *mcpConfig) {
	if c.home == "" {
		return
	}
	var saved struct {
		Piper        string `json:"piper"`
		Voice        string `json:"voice"`
		Whisper      string `json:"whisper"`
		WhisperModel string `json:"whisper_model"`
		WakeWord     *bool  `json:"wake_word"`
	}
	if data, err := os.ReadFile(filepath.Join(c.home, "delta.json")); err == nil {
		if err := json.Unmarshal(data, &saved); err != nil {
			fmt.Fprintln(os.Stderr, "delta: delta.json:", err)
		}
	}
	pick := func(flag *string, saved string, under ...string) {
		if *flag == "" {
			*flag = saved
		}
		if *flag == "" {
			*flag = filepath.Join(append([]string{c.home}, under...)...)
		}
	}
	pick(&c.piper, saved.Piper, "piper", "piper.exe")
	pick(&c.voice, saved.Voice, "voices", "fr_FR-siwis-medium.onnx")
	pick(&c.whisper, saved.Whisper, "whisper", "whisper-server.exe")
	pick(&c.whisperModel, saved.WhisperModel, "whisper", "ggml-large-v3-turbo-q5_0.bin")
	if saved.WakeWord != nil && !*saved.WakeWord {
		c.wakeOff = true
	}
}

// startScene opens Delta's window, scene.exe beside brain.exe, unless -no-scene or a window already listens on the
// scene's address. The window closes with the brain.
func startScene(c mcpConfig, logf func(string, ...any)) *scene {
	s := dialScene(c.scene)
	if c.noScene || c.scene == "" {
		return s
	}
	if conn, err := net.DialTimeout("tcp", c.scene, 200*time.Millisecond); err == nil {
		conn.Close()
		logf("a scene already listens on %s", c.scene)
		return s
	}
	exe, err := os.Executable()
	if err != nil {
		return s
	}
	path := filepath.Join(filepath.Dir(exe), "scene", "scene.exe")
	_, port, _ := net.SplitHostPort(c.scene)
	cmd := exec.Command(path, "--listen", port)
	hideConsole(cmd)
	if c.home != "" {
		if f, err := os.Create(filepath.Join(c.home, "scene.log")); err == nil {
			cmd.Stdout, cmd.Stderr = f, f
		}
	}
	if err := cmd.Start(); err != nil {
		logf("scene: %v", err)
		return s
	}
	killWithMe(cmd.Process)
	go cmd.Wait()
	logf("scene started on %s", c.scene)
	return s
}

// freePort is a loopback port nothing listens on, so that whisper-server never collides with another.
func freePort() int {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return 8806
	}
	defer l.Close()
	return l.Addr().(*net.TCPAddr).Port
}

// mouth speaks what Claude passes to say, one call after another, each cut into sentences that Piper synthesises while
// the one before plays. The microphone is deaf while Delta speaks and for 300 ms after, so that Delta never hears
// itself.
type mouth struct {
	s      *scene
	gate   *wakeGate
	mute   bool
	queue  chan said
	record func(event string, fields map[string]any)
	busy   atomic.Bool // the microphone's: deaf while Delta speaks or while a transcript is routed

	mu      sync.Mutex
	pending int // say calls not yet spoken to the end
	failed  error
	spoke   time.Time
}

// said is one say call: its text and its number, which the journal's say, speaking and spoken events share.
type said struct {
	text string
	id   int
}

func (mo *mouth) speak(text string, id int) (string, error) {
	mo.mu.Lock()
	failed := mo.failed
	mo.mu.Unlock()
	if failed != nil {
		return "", failed
	}
	select {
	case mo.queue <- said{text, id}:
	default:
		return "", errors.New("Delta already has 32 things to say: wait until it has said them")
	}
	words := len(strings.Fields(text))
	return fmt.Sprintf("%d words, about %.0f s", words, 0.5+float64(words)/2.5), nil
}

func (mo *mouth) fail(err error) {
	mo.mu.Lock()
	mo.failed = fmt.Errorf("Delta cannot speak: %v. The delta:setup skill installs what is missing.", err)
	mo.mu.Unlock()
}

func (mo *mouth) run(v *voice) {
	type clip struct {
		path        string
		say         int
		first, last bool // of its say call
	}
	clips := make(chan clip, 8)
	go func() {
		for s := range mo.queue {
			var cut sentences
			parts := cut.feed(s.text)
			if rest := cut.flush(); rest != "" {
				parts = append(parts, rest)
			}
			mo.mu.Lock()
			mo.pending++
			mo.busy.Store(true)
			mo.mu.Unlock()
			if len(parts) == 0 {
				clips <- clip{say: s.id, first: true, last: true}
			}
			for i, part := range parts {
				path, err := v.say(part)
				if err != nil {
					fmt.Fprintln(os.Stderr, "delta: voice:", err)
					path = ""
				}
				clips <- clip{path: path, say: s.id, first: i == 0, last: i == len(parts)-1}
			}
		}
	}()
	for c := range clips {
		if c.first {
			mo.record("speaking", map[string]any{"say_id": c.say, "muted": mo.mute})
		}
		if c.path != "" {
			if w, err := readWav(c.path); err == nil {
				mo.s.send(map[string]any{"state": "speaking", "seconds": w.seconds, "envelope": w.envelope})
			}
			if !mo.mute {
				play(c.path)
			}
			os.Remove(c.path)
		}
		if c.last {
			mo.record("spoken", map[string]any{"say_id": c.say})
			mo.finished()
		}
	}
}

// finished is the end of one say call. When nothing else waits, Delta goes idle, and after the room's echo the
// microphone listens again, the conversation open for the wake gate's window.
func (mo *mouth) finished() {
	mo.mu.Lock()
	mo.pending--
	quiet := mo.pending == 0
	mo.mu.Unlock()
	if !quiet {
		return
	}
	mo.s.send(map[string]any{"state": "idle"})
	time.Sleep(300 * time.Millisecond)
	mo.mu.Lock()
	if mo.pending == 0 {
		mo.busy.Store(false)
	}
	mo.spoke = time.Now()
	mo.mu.Unlock()
	mo.gate.open(time.Now())
}

// routed is the end of one transcript's routing: the microphone listens again, unless Delta is speaking.
func (mo *mouth) routed() {
	mo.mu.Lock()
	if mo.pending == 0 {
		mo.busy.Store(false)
	}
	mo.mu.Unlock()
}

func (mo *mouth) spokeSince(t time.Time) bool {
	mo.mu.Lock()
	defer mo.mu.Unlock()
	return mo.pending > 0 || mo.spoke.After(t)
}
