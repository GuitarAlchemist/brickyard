package main

import (
	"strings"
	"testing"
)

// What a phone can decode, and what fits in a packet once SRTP is added. Each line was a way to get a black or a
// frozen picture.
func TestTheCaptureSendsWhatAPhoneDecodes(t *testing.T) {
	args := strings.Join(captureArgs(0x1234, 40000), " ")
	for _, want := range []string{
		"gfxcapture=hwnd=4660:", // that one window, found by the gateway, never a pattern another window could match
		"capture_cursor=0",      // the desk's pointer stays out of the picture
		"fps=30",                // gfxcapture sends a frame only when the window changes: no frames, no keyframes
		"format=yuv420p",        // x264 fed BGRA picks 4:4:4, which phones do not decode
		"scale=-2:720",          // even width, and a size level 3.1 covers
		"-profile:v baseline",   // the profile the answer advertises, 42e01f
		"-level:v 3.1",          //
		"-g 30",                 // a keyframe every second
		"-bf 0",                 // no B-frames, for latency
		// A window maximized or resized while a device watches is scaled into the frame, whole; by default gfxcapture
		// keeps the first size and crops, so the phone saw a corner of the maximized scene.
		"width=1280:height=720:resize_mode=scale_aspect",
		// Room for SRTP under a 1500-byte MTU. localaddr: without it ffmpeg binds its RTP and RTCP sockets on 0.0.0.0,
		// and Windows asks at each start whether ffmpeg may listen on public networks.
		"rtp://127.0.0.1:40000?pkt_size=1200&localaddr=127.0.0.1",
	} {
		if !strings.Contains(args, want) {
			t.Errorf("the ffmpeg arguments lack %q:\n%s", want, args)
		}
	}
}
