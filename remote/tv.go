package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

// Delta on a television, through a Chromecast and nothing registered with Google: Delta's window asks the gateway, on
// its own entry (POST /tv/start), to cast. The gateway then
//   - finds the Chromecasts on the network (mdns.go),
//   - has a second ffmpeg make an HLS stream of Delta's window, in half-second segments, in a folder of its own,
//   - serves that folder on this network, over plain HTTP, under a path no one can guess, since a Chromecast trusts no
//     certificate of the gateway's,
//   - and has the Chromecast's default media receiver play it (castv2.go).
// The default receiver plays HLS, not WebRTC: the television shows Delta seconds late, as late as its player stays
// behind the stream's live edge; livePlaylist asks it to stay 1.5 s behind instead of three segments. The stream ends
// with POST /tv/stop, when the Chromecast stops it, or when its ffmpeg ends.

// hlsArgs has ffmpeg capture source, a filter that makes frames (Delta's window, as captureArgs captures it), and write
// a live HLS stream into dir: H.264 with a keyframe at each half-second segment, the last six kept, and a silent AAC
// track, which some Chromecast players want beside the picture.
func hlsArgs(source, dir string) []string {
	return []string{
		"-hide_banner", "-loglevel", "error",
		"-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
		"-filter_complex", source + ",fps=30,scale=-2:720,format=yuv420p[v]",
		"-map", "[v]", "-map", "0:a",
		"-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-profile:v", "main", "-level:v", "4.0",
		"-g", "15", "-keyint_min", "15", "-sc_threshold", "0", "-bf", "0",
		"-b:v", "4000k", "-maxrate", "4000k", "-bufsize", "4000k",
		"-c:a", "aac", "-b:a", "64k",
		"-f", "hls", "-hls_time", "0.5", "-hls_list_size", "6",
		"-hls_flags", "delete_segments+independent_segments+omit_endlist",
		"-hls_segment_filename", filepath.Join(dir, "s%05d.ts"),
		filepath.Join(dir, "index.m3u8"),
	}
}

// windowSource is the capture of the window hwnd, as captureArgs has it.
func windowSource(hwnd uintptr) string {
	return fmt.Sprintf("gfxcapture=hwnd=%d:capture_cursor=0:max_framerate=30:width=1280:height=720:"+
		"resize_mode=scale_aspect,hwdownload,format=bgra", hwnd)
}

// tv casts Delta's window to one Chromecast at a time.
type tv struct {
	host string // ip:port this network reaches the stream at
	// find lists the Chromecasts on the network; stream starts ffmpeg's HLS into dir and returns how to stop it, and
	// calls exited when it ends; play has a Chromecast play url until ctx ends, as castPlay does.
	find   func() ([]chromecast, error)
	stream func(dir string, exited func(error)) (func(), error)
	play   func(ctx context.Context, addr, url string, playing func()) error
	ready  time.Duration // how long the stream may take to have its first segments

	mu      sync.Mutex
	token   string             // the path the stream is served under; "": none
	dir     string             // its folder
	on      string             // the Chromecast it is cast to
	playing bool               // the Chromecast has taken it
	cancel  context.CancelFunc // ends the cast
	why     string             // why the last cast ended
}

func newTV(ip net.IP, port int, ffmpeg string, window func() (uintptr, error)) *tv {
	return &tv{
		host: net.JoinHostPort(ip.String(), strconv.Itoa(port)),
		find: func() ([]chromecast, error) { return findChromecasts(ip, 2*time.Second) },
		stream: func(dir string, exited func(error)) (func(), error) {
			hwnd, err := window()
			if err != nil {
				return nil, err
			}
			return startFFmpeg(ffmpeg, hlsArgs(windowSource(hwnd), dir), exited)
		},
		play:  castPlay,
		ready: 10 * time.Second,
	}
}

// streamFiles are the names an HLS stream's folder serves: its playlist and its segments.
var streamFiles = regexp.MustCompile(`^(index\.m3u8|s[0-9]{5}\.ts)$`)

// ServeHTTP serves the stream that is cast, at /tv/<token>/<file>, and nothing else. The Chromecast's player fetches it
// from another origin, so every answer lets any origin read it.
func (t *tv) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Access-Control-Allow-Origin", "*")
	t.mu.Lock()
	token, dir := t.token, t.dir
	t.mu.Unlock()
	file := r.PathValue("file")
	if r.Method != http.MethodGet || token == "" || r.PathValue("token") != token || !streamFiles.MatchString(file) {
		http.NotFound(w, r)
		return
	}
	kind := "video/mp2t"
	if file == "index.m3u8" {
		kind = "application/vnd.apple.mpegurl"
	}
	body, err := os.ReadFile(filepath.Join(dir, file))
	if err != nil {
		http.NotFound(w, r)
		return
	}
	if file == "index.m3u8" {
		body = livePlaylist(body)
	}
	w.Header().Set("Content-Type", kind)
	w.Header().Set("Cache-Control", "no-store")
	w.Write(body)
}

// livePlaylist is ffmpeg's playlist as the Chromecast's player reads it. ffmpeg writes a target duration of 0 for
// half-second segments, which HLS forbids: it becomes 1. And a player starts and stays three target durations behind
// the live edge unless the playlist says otherwise: HOLD-BACK and EXT-X-START say 1.5 s, three segments.
func livePlaylist(playlist []byte) []byte {
	s, found := strings.CutPrefix(string(playlist), "#EXTM3U\n")
	if !found {
		return playlist
	}
	s = strings.Replace(s, "#EXT-X-TARGETDURATION:0\n", "#EXT-X-TARGETDURATION:1\n", 1)
	return []byte("#EXTM3U\n#EXT-X-SERVER-CONTROL:HOLD-BACK=1.5\n#EXT-X-START:TIME-OFFSET=-1.5\n" + s)
}

func (t *tv) handler() http.Handler {
	mux := http.NewServeMux()
	mux.Handle("/tv/{token}/{file}", t)
	return mux
}

var errNoChromecast = errors.New("no Chromecast answers on this network")

// start casts to the Chromecast named name, or to the only one when name is "", and says which.
func (t *tv) start(name string) (string, error) {
	found, err := t.find()
	if err != nil {
		return "", err
	}
	var to *chromecast
	for i, c := range found {
		if c.Name == name || (name == "" && len(found) == 1) {
			to = &found[i]
		}
	}
	switch {
	case len(found) == 0:
		return "", errNoChromecast
	case to == nil && name == "":
		return "", fmt.Errorf("%d Chromecasts answer: name one", len(found))
	case to == nil:
		return "", fmt.Errorf("no Chromecast named %q answers", name)
	}
	t.stop()
	dir, err := os.MkdirTemp("", "delta-tv-")
	if err != nil {
		return "", err
	}
	ctx, cancel := context.WithCancel(context.Background())
	halt, err := t.stream(dir, func(err error) {
		t.end(ctx, fmt.Sprintf("the stream's ffmpeg ended: %v", err))
		cancel()
	})
	if err != nil {
		cancel()
		os.RemoveAll(dir)
		return "", err
	}
	t.mu.Lock()
	t.token, t.dir, t.on, t.playing, t.why = token(), dir, to.Name, false, ""
	t.cancel = func() { cancel(); halt() }
	url := "http://" + t.host + "/tv/" + t.token + "/index.m3u8"
	t.mu.Unlock()
	logf("tv: casting to %s", to.Name)
	go func() {
		// The Chromecast is told once the playlist names a few segments: it gives up on an empty one.
		if !waitFor(ctx, filepath.Join(dir, "s00002.ts"), t.ready) {
			t.end(ctx, "the stream did not start")
			return
		}
		err := t.play(ctx, to.addr, url, func() {
			t.mu.Lock()
			t.playing = true
			t.mu.Unlock()
		})
		if err == nil {
			err = errStoppedOnTV
		}
		t.end(ctx, err.Error())
	}()
	return to.Name, nil
}

// waitFor is whether path exists within wait, before ctx ends.
func waitFor(ctx context.Context, path string, wait time.Duration) bool {
	deadline := time.Now().Add(wait)
	for time.Now().Before(deadline) && ctx.Err() == nil {
		if _, err := os.Stat(path); err == nil {
			return true
		}
		time.Sleep(200 * time.Millisecond)
	}
	return false
}

// end stops the cast that ctx belongs to, if it is still the one running, and keeps why.
func (t *tv) end(ctx context.Context, why string) {
	t.mu.Lock()
	running := t.cancel != nil && ctx.Err() == nil
	t.mu.Unlock()
	if !running {
		return
	}
	logf("tv: the cast ended: %s", why)
	t.stop()
	t.mu.Lock()
	t.why = why
	t.mu.Unlock()
}

// stop ends the cast, if any: the Chromecast's receiver closes, ffmpeg stops and its folder goes.
func (t *tv) stop() {
	t.mu.Lock()
	cancel, dir := t.cancel, t.dir
	t.token, t.dir, t.on, t.playing, t.cancel = "", "", "", false, nil
	t.mu.Unlock()
	if cancel == nil {
		return
	}
	cancel()
	go func() {
		time.Sleep(time.Second) // ffmpeg lets go of its files
		os.RemoveAll(dir)
	}()
}

// tvState is what /tv/state answers.
type tvState struct {
	On      string `json:"on,omitempty"`      // the Chromecast cast to
	Playing bool   `json:"playing,omitempty"` // it has taken the stream
	Ended   string `json:"ended,omitempty"`   // why the last cast ended, when none runs
}

func (t *tv) state() tvState {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.on != "" {
		return tvState{On: t.on, Playing: t.playing}
	}
	return tvState{Ended: t.why}
}

// The routes of Delta's window's entry for the television.

func (s *server) tvFind(w http.ResponseWriter, r *http.Request) {
	found, err := s.tv.find()
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadGateway)
		return
	}
	if found == nil {
		found = []chromecast{}
	}
	answer(w, map[string]any{"chromecasts": found})
}

func (s *server) tvStart(w http.ResponseWriter, r *http.Request) {
	var req struct{ Name string }
	if r.ContentLength != 0 {
		if err := json.NewDecoder(io.LimitReader(r.Body, 1<<10)).Decode(&req); err != nil {
			http.Error(w, `{"name": "<a Chromecast>"} or nothing is needed`, http.StatusBadRequest)
			return
		}
	}
	name, err := s.tv.start(req.Name)
	if err != nil {
		status := http.StatusConflict
		if errors.Is(err, errNoChromecast) {
			status = http.StatusNotFound
		}
		http.Error(w, err.Error(), status)
		return
	}
	answer(w, tvState{On: name})
}

func (s *server) tvStop(w http.ResponseWriter, r *http.Request) {
	s.tv.stop()
	w.WriteHeader(http.StatusNoContent)
}

func (s *server) tvAsk(w http.ResponseWriter, r *http.Request) { answer(w, s.tv.state()) }

func answer(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	json.NewEncoder(w).Encode(v)
}
