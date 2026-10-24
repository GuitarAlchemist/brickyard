package main

import (
	"fmt"
	"net"
	"os"
	"os/exec"
	"sync"
	"time"

	"github.com/pion/rtp"
	"github.com/pion/webrtc/v4"
)

// captureArgs has ffmpeg capture the window hwnd (Windows Graphics Capture, so the window is seen even when covered)
// and send it as constrained-baseline H.264 over RTP to 127.0.0.1:port. gfxcapture sends a frame only when the
// window changes, so fps=30 repeats the last one: x264 then keeps making frames, and a keyframe every second. The
// frame is 1280×720 whatever the window's size, and a window resized during the capture is scaled into it whole.
func captureArgs(hwnd uintptr, port int) []string {
	return []string{
		"-hide_banner", "-loglevel", "error",
		"-filter_complex", fmt.Sprintf("gfxcapture=hwnd=%d:capture_cursor=0:max_framerate=30:"+
			"width=1280:height=720:resize_mode=scale_aspect,"+
			"hwdownload,format=bgra,fps=30,scale=-2:720,format=yuv420p", hwnd),
		"-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
		"-profile:v", "baseline", "-level:v", "3.1",
		"-g", "30", "-bf", "0", "-b:v", "2500k", "-maxrate", "2500k", "-bufsize", "1000k",
		"-an", "-f", "rtp", fmt.Sprintf("rtp://127.0.0.1:%d?pkt_size=1200&localaddr=127.0.0.1", port),
	}
}

// capture runs ffmpeg on Delta's window while a device is connected and forwards its RTP packets to the video
// track. It looks for the window again every followEvery, so that the picture comes back by itself when the window
// appears late, or when the brain replaces it with a new one.
type capture struct {
	conn  *net.UDPConn
	track *webrtc.TrackLocalStaticRTP
	gap   uint32                                                 // the RTP ticks left between two captures
	find  func() (uintptr, error)                                // Delta's window
	start func(hwnd uintptr, exited func(error)) (func(), error) // runs ffmpeg on it; the func returned stops it

	mu     sync.Mutex
	on     bool    // a device is connected
	stop   func()  // stops the ffmpeg that runs, or is nil
	hwnd   uintptr // the window it shows
	runs   int     // counts the ffmpeg started, so that the end of one stopped on purpose is not taken for the next's
	missed string  // the last lookup error printed, so that it is said once and not every followEvery
}

const followEvery = 2 * time.Second

func newCapture(ffmpeg, title, exe string, track *webrtc.TrackLocalStaticRTP) (*capture, error) {
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		return nil, err
	}
	port := conn.LocalAddr().(*net.UDPAddr).Port
	c := &capture{conn: conn, track: track, gap: frameTicks, find: func() (uintptr, error) { return findWindow(title, exe) }}
	c.start = func(hwnd uintptr, exited func(error)) (func(), error) {
		return startFFmpeg(ffmpeg, captureArgs(hwnd, port), exited)
	}
	c.begin()
	return c, nil
}

// micArgs has ffmpeg capture the microphone named device and send it as Opus over RTP to 127.0.0.1:port, in 20 ms
// packets, mono, at a bitrate made for speech.
func micArgs(device string, port int) []string {
	return []string{
		"-hide_banner", "-loglevel", "error",
		"-f", "dshow", "-audio_buffer_size", "20", "-i", "audio=" + device,
		"-ac", "1", "-ar", "48000", "-c:a", "libopus", "-application", "voip", "-b:a", "32k", "-frame_duration", "20",
		"-vn", "-f", "rtp", fmt.Sprintf("rtp://127.0.0.1:%d?pkt_size=1200&localaddr=127.0.0.1", port),
	}
}

// opusTicks is 20 ms on Opus's 48 kHz RTP clock, the gap left between two microphone captures.
const opusTicks = 48000 / 50

// newMicCapture runs ffmpeg on the microphone named device while run(true) holds, as capture does on Delta's window,
// and forwards its Opus packets to track. It starts again within followEvery when ffmpeg ends.
func newMicCapture(ffmpeg, device string, track *webrtc.TrackLocalStaticRTP) (*capture, error) {
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		return nil, err
	}
	port := conn.LocalAddr().(*net.UDPAddr).Port
	c := &capture{conn: conn, track: track, gap: opusTicks, find: func() (uintptr, error) { return 1, nil }}
	c.start = func(_ uintptr, exited func(error)) (func(), error) {
		return startFFmpeg(ffmpeg, micArgs(device, port), exited)
	}
	c.begin()
	return c, nil
}

func startFFmpeg(ffmpeg string, args []string, exited func(error)) (func(), error) {
	cmd := exec.Command(ffmpeg, args...)
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		return nil, err
	}
	go func() { exited(cmd.Wait()) }()
	return func() { cmd.Process.Kill() }, nil
}

// begin forwards the packets and follows what is captured.
func (c *capture) begin() {
	go c.forward()
	go func() {
		for range time.Tick(followEvery) {
			c.follow()
		}
	}()
}

func (c *capture) forward() {
	buf := make([]byte, 1600)
	var p rtp.Packet
	r := renumber{gap: c.gap}
	for {
		n, _, err := c.conn.ReadFromUDP(buf)
		if err != nil {
			return
		}
		if p.Unmarshal(buf[:n]) == nil {
			r.apply(&p)
			c.track.WriteRTP(&p)
		}
	}
}

// renumber makes the packets of successive ffmpeg, each with its own SSRC and its own random first sequence number
// and timestamp, one stream that carries on: a browser drops packets that seem older than the ones it has seen.
type renumber struct {
	gap       uint32 // RTP ticks between the last packet of a capture and the first of the next; 0: frameTicks
	ssrc      uint32
	begun     bool
	seq       uint16 // added to each packet's sequence number
	ts        uint32 // and to its timestamp
	lastSeq   uint16
	lastStamp uint32
}

// frameTicks is a frame at 30 fps on H.264's 90 kHz RTP clock, the gap left between two captures.
const frameTicks = 90000 / 30

func (r *renumber) apply(p *rtp.Packet) {
	if !r.begun || p.SSRC != r.ssrc {
		if r.begun {
			gap := r.gap
			if gap == 0 {
				gap = frameTicks
			}
			r.seq = r.lastSeq + 1 - p.SequenceNumber
			r.ts = r.lastStamp + gap - p.Timestamp
		}
		r.ssrc, r.begun = p.SSRC, true
	}
	p.SequenceNumber += r.seq
	p.Timestamp += r.ts
	r.lastSeq, r.lastStamp = p.SequenceNumber, p.Timestamp
}

// run starts the capture when on is true and stops it otherwise.
func (c *capture) run(on bool) {
	c.mu.Lock()
	c.on = on
	c.mu.Unlock()
	c.follow()
}

// follow brings the capture in line: stopped while no device is connected, else on the one window found, and started
// again on a new one. When no single window is found, what runs is left alone, since the window it shows may still
// be open beside a second one.
func (c *capture) follow() {
	c.mu.Lock()
	defer c.mu.Unlock()
	if !c.on {
		c.halt()
		c.missed = ""
		return
	}
	hwnd, err := c.find()
	if err != nil {
		if err.Error() != c.missed {
			fmt.Fprintln(os.Stderr, "delta-remote:", err)
			c.missed = err.Error()
		}
		return
	}
	c.missed = ""
	if c.stop != nil && hwnd == c.hwnd {
		return
	}
	c.halt()
	c.runs++
	run := c.runs
	stop, err := c.start(hwnd, func(err error) {
		c.mu.Lock()
		defer c.mu.Unlock()
		if c.runs == run && c.stop != nil {
			fmt.Fprintln(os.Stderr, "delta-remote: the capture stopped:", err)
			c.stop = nil
		}
	})
	if err != nil {
		fmt.Fprintln(os.Stderr, "delta-remote: ffmpeg:", err)
		return
	}
	c.stop, c.hwnd = stop, hwnd
}

func (c *capture) halt() {
	if c.stop != nil {
		c.stop()
		c.stop = nil
	}
}
