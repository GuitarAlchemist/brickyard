package main

import (
	"fmt"
	"net"
	"os"
	"os/exec"
	"sync"

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

// capture runs ffmpeg while a device is connected and forwards its RTP packets to the video track.
type capture struct {
	ffmpeg, title, exe string
	conn               *net.UDPConn
	track              *webrtc.TrackLocalStaticRTP

	mu  sync.Mutex
	cmd *exec.Cmd
}

func newCapture(ffmpeg, title, exe string, track *webrtc.TrackLocalStaticRTP) (*capture, error) {
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		return nil, err
	}
	c := &capture{ffmpeg: ffmpeg, title: title, exe: exe, conn: conn, track: track}
	go c.forward()
	return c, nil
}

func (c *capture) forward() {
	buf := make([]byte, 1600)
	var p rtp.Packet
	for {
		n, _, err := c.conn.ReadFromUDP(buf)
		if err != nil {
			return
		}
		if p.Unmarshal(buf[:n]) == nil {
			c.track.WriteRTP(&p)
		}
	}
}

// run starts ffmpeg when on is true and stops it otherwise. The window is looked up again at each start.
func (c *capture) run(on bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if !on {
		if c.cmd != nil {
			c.cmd.Process.Kill()
			c.cmd = nil
		}
		return
	}
	if c.cmd != nil {
		return
	}
	hwnd, err := findWindow(c.title, c.exe)
	if err != nil {
		fmt.Fprintln(os.Stderr, "delta-remote:", err)
		return
	}
	cmd := exec.Command(c.ffmpeg, captureArgs(hwnd, c.conn.LocalAddr().(*net.UDPAddr).Port)...)
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		fmt.Fprintln(os.Stderr, "delta-remote: ffmpeg:", err)
		return
	}
	c.cmd = cmd
	go func() {
		err := cmd.Wait()
		c.mu.Lock()
		defer c.mu.Unlock()
		if c.cmd == cmd {
			fmt.Fprintln(os.Stderr, "delta-remote: the capture stopped:", err)
			c.cmd = nil
		}
	}()
}
