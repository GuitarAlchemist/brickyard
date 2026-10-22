package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
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
	lang                                      string // what Delta hears and speaks: "en" or "fr" (lang.go)
	scene                                     string // the scene's address
	journal                                   string // where to record what Delta heard and said, if anywhere
	ollama, triage                            string // Ollama's address, and the model that sorts what Delta hears, if any
	hooks                                     string // where Claude Code's hooks post, if anywhere (hooks.go)
	fx                                        string // the effect on Delta's voice: "warm" or "none" (fx.go)
	noScene, mute, wakeOff                    bool
	threads, audioCtx                         int
}

// journal appends one JSON line per event, timestamped: what Delta heard and where it went, what Claude asked it to say
// and in answer to which utterance, and when Delta said it. It ties a spoken question to its spoken answer.
type journal struct {
	mu  sync.Mutex
	f   io.Writer
	seq int
}

// record writes one line. Each line carries the brain's pid, which tells two brains apart, and journal_seq and t_ms,
// with which a reader that tails the file, such as IX's ixctl, can resume and order events across its own restarts.
func (j *journal) record(event string, fields map[string]any) {
	now := time.Now()
	line := map[string]any{"t": now.Format(time.RFC3339Nano), "t_ms": now.UnixMilli(), "event": event, "pid": os.Getpid()}
	for k, v := range fields {
		line[k] = v
	}
	j.mu.Lock()
	defer j.mu.Unlock()
	j.seq++
	line["journal_seq"] = j.seq
	data, err := json.Marshal(line)
	if err != nil {
		return
	}
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
	lang, err := languageOf(c.lang)
	if err != nil {
		logf("%v", err)
	}

	var hearing sync.Mutex
	notHearing := errors.New("Delta is still starting: its ears (whisper.cpp) are loading. Try again in a few seconds.")
	m := &mcpServer{version: version, lang: lang, out: rpc, listenMin: 5 * time.Second, listenMax: 300 * time.Second}
	m.gate.window = 20 * time.Second
	m.gate.follow = 15 * time.Second
	m.gate.off = c.wakeOff
	if c.triage != "" {
		m.triage = ollamaTriage(c.ollama, c.triage, lang)
		m.triageMax = 4 * time.Second
		go func() {
			start := time.Now()
			if err := warmTriage(m.triage, lang); err != nil {
				logf("triage: %v; what Delta hears goes to Claude", err)
				return
			}
			logf("triage ready: %s in %.1f s", c.triage, time.Since(start).Seconds())
		}()
	}
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
			m.about = " This Delta records what it hears and says, with each sentence's route and the triage's verdict, " +
				"one JSON line each, in " + c.journal + "."
		}
	}
	// One Delta at a time on this machine, whichever install or Claude Code session started it: one microphone, one
	// window, one voice. The newest brain takes over; the one it replaces tells its session, then stops.
	if lock, err := brainLock(); err != nil {
		logf("brain lock: %v", err)
	} else if err := takeOver(lock, os.Getpid(), 2*time.Second, func(other string) {
		logf("brain %s took over the microphone; stopping", other)
		m.note("stop", map[string]any{"replaced_by": other})
		m.notify("notifications/claude/channel", map[string]any{
			"content": "Delta stopped here: another Claude Code session started Delta (brain pid " + other + "), and only one " +
				"Delta runs at a time. Its say and listen tools no longer work in this session.",
			"meta": map[string]string{"event": "replaced"},
		})
		time.Sleep(300 * time.Millisecond) // lets the notice reach Claude Code before the pipe closes
		os.Exit(0)
	}); err != nil {
		logf("brain lock: %v", err)
	}
	if c.home != "" { // brains built before the machine-wide lock watch <home>\brain.pid: this stops them too
		os.WriteFile(filepath.Join(c.home, "brain.pid"), []byte(fmt.Sprint(os.Getpid())), 0o644)
	}

	s := startScene(c, logf)
	s.greet(map[string]any{"language": lang.code}) // the shelf's labels (Words.cs)
	mo := &mouth{s: s, gate: &m.gate, mute: c.mute, queue: make(chan said, 32), record: m.note, lang: lang.code}
	if fx, err := effect(c.fx); err != nil {
		logf("%v; Delta speaks without an effect", err)
	} else {
		mo.fx = fx
	}
	m.speak = mo.speak
	if !c.noScene && c.scene != "" { // Delta moves its window, walks its shelf and changes its face (window.go)
		m.show = s.send
	}

	// The robot shows how full Claude's context is, from the session's transcript, and when Claude Code compacts it,
	// from its hooks.
	h := &hooks{own: ownSession, logf: logf, maxCompact: 10 * time.Minute, lang: lang.code,
		compacting: func(trigger string) {
			m.note("compacting", map[string]any{"trigger": trigger})
			s.send(map[string]any{"compacting": true})
		},
		compacted: func(trigger string, seconds float64) {
			m.note("compacted", map[string]any{"trigger": trigger, "seconds": seconds})
			s.send(map[string]any{"compacting": false})
		},
		context: func(used, limit int) {
			s.send(map[string]any{"context": map[string]int{"used": used, "limit": limit}})
		},
	}
	m.busy = h.busy
	h.gesture = func(g gesture) { m.gesture(g, time.Now()) }

	// Another device speaks to Delta and hears it through a gateway on this computer, such as IX's (remote.go).
	rm := &remote{route: func(u utterance) (int, string) {
		id, where := m.heardAs(u, time.Now())
		logf("heard %q from another device in %d ms: %s", u.text, u.asr.Milliseconds(), where)
		if where == "ignored" || where == "dropped" || where == "local" {
			s.send(map[string]any{"state": "idle"})
		}
		return id, where
	}}
	if chime, err := chimeFile(os.TempDir()); err != nil {
		logf("remote: %v; no chime", err)
	} else {
		rm.chime = func() { // only while Delta is quiet: one sound stops another
			if !c.mute && mo.quiet() {
				mo.busy.Store(true) // the microphone does not hear the chime
				play(chime)
				mo.routed()
			}
		}
	}
	if secret, path, err := newRemoteSecret(); err != nil {
		logf("remote: %v; no other device can speak to Delta", err)
	} else {
		rm.secret = secret
		logf("remote: a gateway's secret is in %s", path)
	}
	h.remote = rm
	mo.onClip = rm.publish
	if c.hooks != "" {
		go h.serve(c.hooks)
	}
	go h.watch(5 * time.Second)
	m.pushed = func() { // Delta looks thoughtful while Claude works, and idle again if no answer comes
		s.send(map[string]any{"state": "thinking", "cost": paid})
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
				if _, err = v.say(lang.hello); err == nil { // loads the voice model
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
		e, err := startEars(c.whisper, c.whisperModel, lang.code, freePort(), c.threads, "Delta, Brickyard, Claude Code, IX, Gaia.", c.audioCtx)
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
		rm.hearWith(e.hear)
		heard := make(chan utterance)
		go listen(e, s, &mo.busy, heard, false)
		for u := range heard {
			where := "nothing"
			if u.text != "" {
				where = m.hear(u, time.Now())
			}
			logf("heard %q in %d ms: %s", u.text, u.asr.Milliseconds(), where)
			if where == "nothing" || where == "ignored" || where == "dropped" || where == "local" {
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
		Triage       string `json:"triage"`
		Language     string `json:"language"`
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
	if c.lang == "" {
		c.lang = saved.Language
	}
	lang, _ := languageOf(c.lang) // runMCP reports an unknown language
	pick(&c.piper, saved.Piper, "piper", "piper.exe")
	pick(&c.voice, saved.Voice, "voices", lang.voice)
	pick(&c.whisper, saved.Whisper, "whisper", "whisper-server.exe")
	pick(&c.whisperModel, saved.WhisperModel, "whisper", "ggml-large-v3-turbo-q5_0.bin")
	if c.triage == "" {
		c.triage = saved.Triage
	}
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
	fx     func(rate int) *warm                           // a fresh effect for each say call, or nil for Piper's own voice
	lang   string                                         // the code of the language Delta speaks, for speakable
	onClip func(say int, text string, w wav, path string) // each sentence as it starts, for remote.go, or nil
	busy   atomic.Bool                                    // the microphone's: deaf while Delta speaks or while a transcript is routed

	mu      sync.Mutex
	pending int // say calls not yet spoken to the end
	failed  error
	spoke   time.Time
}

// said is one say call: its text, its number, which the journal's say, speaking and spoken events share, what it
// cost, which Delta's window shows, and the expression Delta's face takes as it starts, if any.
type said struct {
	text string
	id   int
	cost string
	mood string
}

func (mo *mouth) speak(text string, id int, cost, mood string) (string, error) {
	mo.mu.Lock()
	failed := mo.failed
	mo.mu.Unlock()
	if failed != nil {
		return "", failed
	}
	select {
	case mo.queue <- said{text, id, cost, mood}:
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
		path, text  string
		say         int
		cost, mood  string
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
				clips <- clip{say: s.id, cost: s.cost, mood: s.mood, first: true, last: true}
			}
			for i, part := range parts {
				path, err := v.say(speakable(part, mo.lang))
				if err != nil {
					fmt.Fprintln(os.Stderr, "delta: voice:", err)
					path = ""
				}
				clips <- clip{path: path, text: part, say: s.id, cost: s.cost, mood: s.mood, first: i == 0, last: i == len(parts)-1}
			}
		}
	}()
	var fx *warm    // the say call's effect, which runs through all its sentences
	var mood string // the say call's expression, until a sentence carries it to the window
	for c := range clips {
		if c.first {
			mo.record("speaking", map[string]any{"say_id": c.say, "muted": mo.mute, "cost": c.cost})
			fx, mood = nil, c.mood
		}
		if c.path != "" {
			w, err := readWav(c.path)
			if err == nil && mo.fx != nil {
				if fx == nil {
					fx = mo.fx(w.rate)
				}
				if w, err = warmed(c.path, w, fx, c.last); err != nil {
					fmt.Fprintln(os.Stderr, "delta: voice effect:", err)
				}
			}
			if err == nil {
				line := map[string]any{"state": "speaking", "seconds": w.seconds, "envelope": w.envelope, "cost": c.cost}
				if mood != "" {
					line["mood"], mood = mood, ""
				}
				mo.s.send(line)
				if mo.onClip != nil {
					mo.onClip(c.say, c.text, w, c.path)
				}
			}
			if !mo.mute {
				play(c.path)
			}
			os.Remove(c.path)
		}
		if c.last {
			if mood != "" {
				mo.s.send(map[string]any{"mood": mood})
			}
			mo.record("spoken", map[string]any{"say_id": c.say})
			mo.finished()
		}
	}
}

// warmed runs one sentence through its say call's effect, with the effect's tail after the call's last sentence, and
// rewrites the sentence's file for the speaker.
func warmed(path string, w wav, fx *warm, last bool) (wav, error) {
	fx.process(w.samples)
	if last {
		w.samples = append(w.samples, fx.tail()...)
	}
	if err := writeWav(path, w.rate, w.samples); err != nil {
		return wav{}, err
	}
	w.seconds = float64(len(w.samples)) / float64(w.rate)
	w.envelope = envelope(w.samples, w.rate/50)
	return w, nil
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

// quiet says whether Delta has nothing left to say.
func (mo *mouth) quiet() bool {
	mo.mu.Lock()
	defer mo.mu.Unlock()
	return mo.pending == 0
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
