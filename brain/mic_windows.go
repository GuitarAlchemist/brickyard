package main

import (
	"encoding/binary"
	"fmt"
	"sync/atomic"
	"syscall"
	"time"
	"unsafe"
)

// The default microphone through winmm's waveIn: 16 kHz mono 16-bit, in 20 ms buffers that are polled for completion
// and queued again. Go's collector does not move heap objects, and the buffers stay referenced until the device closes.
var (
	waveIn                = syscall.NewLazyDLL("winmm.dll")
	waveInOpen            = waveIn.NewProc("waveInOpen")
	waveInPrepareHeader   = waveIn.NewProc("waveInPrepareHeader")
	waveInUnprepareHeader = waveIn.NewProc("waveInUnprepareHeader")
	waveInAddBuffer       = waveIn.NewProc("waveInAddBuffer")
	waveInStart           = waveIn.NewProc("waveInStart")
	waveInReset           = waveIn.NewProc("waveInReset")
	waveInClose           = waveIn.NewProc("waveInClose")
)

type waveFormat struct {
	formatTag, channels           uint16
	samplesPerSec, avgBytesPerSec uint32
	blockAlign, bitsPerSample     uint16
	extra                         uint16
}

type waveHeader struct {
	data          *byte
	bufferLength  uint32
	bytesRecorded uint32
	user          uintptr
	flags         uint32
	loops         uint32
	next          uintptr
	reserved      uintptr
}

const whdrDone = 1

type mic struct {
	handle  uintptr
	headers []*waveHeader
	buffers [][]byte
	stop    atomic.Bool
	done    chan struct{}
}

// openMic starts recording and sends each 20 ms frame, as samples from -1 to 1, to frames until close. A reader that
// falls behind loses frames rather than stalling the device.
func openMic(frames chan<- []float32) (*mic, error) {
	format := waveFormat{formatTag: 1, channels: 1, samplesPerSec: earsRate, avgBytesPerSec: earsRate * 2, blockAlign: 2, bitsPerSample: 16}
	m := &mic{done: make(chan struct{})}
	const waveMapper = 0xFFFFFFFF
	if r, _, _ := waveInOpen.Call(uintptr(unsafe.Pointer(&m.handle)), waveMapper, uintptr(unsafe.Pointer(&format)), 0, 0, 0); r != 0 {
		return nil, fmt.Errorf("mic: waveInOpen failed (MMRESULT %d)", r)
	}
	size := unsafe.Sizeof(waveHeader{})
	for range 25 { // half a second of buffers
		buf := make([]byte, frameSamples*2)
		h := &waveHeader{data: &buf[0], bufferLength: uint32(len(buf))}
		m.buffers = append(m.buffers, buf)
		m.headers = append(m.headers, h)
		waveInPrepareHeader.Call(m.handle, uintptr(unsafe.Pointer(h)), size)
		waveInAddBuffer.Call(m.handle, uintptr(unsafe.Pointer(h)), size)
	}
	if r, _, _ := waveInStart.Call(m.handle); r != 0 {
		m.closeDevice()
		return nil, fmt.Errorf("mic: waveInStart failed (MMRESULT %d)", r)
	}
	go func() {
		defer close(m.done)
		for next := 0; !m.stop.Load(); {
			h := m.headers[next]
			flags := atomic.LoadUint32(&h.flags)
			if flags&whdrDone == 0 {
				time.Sleep(5 * time.Millisecond)
				continue
			}
			buf := m.buffers[next][:h.bytesRecorded]
			frame := make([]float32, len(buf)/2)
			for i := range frame {
				frame[i] = float32(int16(binary.LittleEndian.Uint16(buf[2*i:]))) / 32768
			}
			select {
			case frames <- frame:
			default:
			}
			atomic.StoreUint32(&h.flags, flags&^whdrDone)
			waveInAddBuffer.Call(m.handle, uintptr(unsafe.Pointer(h)), size)
			next = (next + 1) % len(m.headers)
		}
	}()
	return m, nil
}

func (m *mic) close() {
	m.stop.Store(true)
	<-m.done
	m.closeDevice()
}

func (m *mic) closeDevice() {
	waveInReset.Call(m.handle)
	for _, h := range m.headers {
		waveInUnprepareHeader.Call(m.handle, uintptr(unsafe.Pointer(h)), unsafe.Sizeof(*h))
	}
	waveInClose.Call(m.handle)
}
