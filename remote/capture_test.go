package main

import (
	"errors"
	"slices"
	"strings"
	"testing"

	"github.com/pion/rtp"
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

// A capture whose window and ffmpeg are fakes: find returns what window holds, and each start and stop is noted.
type fakeCapture struct {
	*capture
	window  func() (uintptr, error)
	started []uintptr
	stopped []uintptr
	exited  func(error) // the last ffmpeg's way of saying it ended
}

func newFakeCapture() *fakeCapture {
	f := &fakeCapture{capture: &capture{}}
	f.find = func() (uintptr, error) { return f.window() }
	f.start = func(hwnd uintptr, exited func(error)) (func(), error) {
		f.started = append(f.started, hwnd)
		f.exited = exited
		return func() { f.stopped = append(f.stopped, hwnd) }, nil
	}
	return f
}

func (f *fakeCapture) shows(hwnd uintptr) { f.window = func() (uintptr, error) { return hwnd, nil } }

func (f *fakeCapture) showsTwo() {
	f.window = func() (uintptr, error) { return 0, errors.New(`2 windows "Brickyard" of scene.exe are open`) }
}

func (f *fakeCapture) want(t *testing.T, step string, started, stopped []uintptr) {
	t.Helper()
	if !slices.Equal(f.started, started) || !slices.Equal(f.stopped, stopped) {
		t.Fatalf("%s: started %v and stopped %v, want %v and %v", step, f.started, f.stopped, started, stopped)
	}
}

// On 2026-10-23 a second scene window was open when the tablet connected, and then the brain replaced Delta's
// window: the capture had looked once, found no single window, and the tablet showed no picture until reloaded.
func TestThePictureFollowsDeltasWindowWhileADeviceWatches(t *testing.T) {
	f := newFakeCapture()
	f.showsTwo()
	f.run(true)
	f.want(t, "two windows when the device connects", nil, nil)

	f.shows(7)
	f.follow()
	f.want(t, "one window left", []uintptr{7}, nil)
	f.follow()
	f.want(t, "the same window", []uintptr{7}, nil)

	f.shows(9)
	f.follow()
	f.want(t, "the brain's new window", []uintptr{7, 9}, []uintptr{7})

	f.showsTwo()
	f.follow()
	f.want(t, "a second window beside it", []uintptr{7, 9}, []uintptr{7})

	f.run(false)
	f.want(t, "the device gone", []uintptr{7, 9}, []uintptr{7, 9})
	f.shows(11)
	f.follow()
	f.want(t, "a window while nobody watches", []uintptr{7, 9}, []uintptr{7, 9})
}

func TestTheCaptureStartsAgainWhenFfmpegEnds(t *testing.T) {
	f := newFakeCapture()
	f.shows(7)
	f.run(true)
	f.exited(errors.New("exit status 1"))
	f.follow()
	f.want(t, "ffmpeg ended", []uintptr{7, 7}, nil)

	// A capture stopped on purpose is not taken for one that ended.
	stale := f.exited
	f.shows(9)
	f.follow()
	stale(errors.New("killed"))
	f.follow()
	f.want(t, "the old ffmpeg's exit", []uintptr{7, 7, 9}, []uintptr{7})
}

// Each ffmpeg numbers its packets from its own random start, and a browser drops those that seem older than the ones
// it has seen, so the packets of a capture started again carry on from the last ones sent.
func TestTheVideoCarriesOnAcrossCaptures(t *testing.T) {
	var r renumber
	var got []rtp.Header
	send := func(ssrc uint32, seq uint16, ts uint32) {
		p := rtp.Packet{Header: rtp.Header{SSRC: ssrc, SequenceNumber: seq, Timestamp: ts}}
		r.apply(&p)
		got = append(got, p.Header)
	}
	send(1, 500, 90000)
	send(1, 501, 93000)
	send(2, 60000, 7)
	send(2, 60001, 3007)
	for i, want := range []struct {
		seq uint16
		ts  uint32
	}{{500, 90000}, {501, 93000}, {502, 96000}, {503, 99000}} {
		if got[i].SequenceNumber != want.seq || got[i].Timestamp != want.ts {
			t.Errorf("packet %d: sequence %d at %d, want %d at %d", i, got[i].SequenceNumber, got[i].Timestamp, want.seq, want.ts)
		}
	}
}
