package main

import (
	"syscall"
	"unsafe"
)

var playSound = syscall.NewLazyDLL("winmm.dll").NewProc("PlaySoundW")

const (
	sndSync      = 0x0000
	sndNoDefault = 0x0002
	sndFilename  = 0x00020000
)

// play plays a WAV file to the end, through Windows' own player.
func play(path string) {
	p, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return
	}
	playSound.Call(uintptr(unsafe.Pointer(p)), 0, sndFilename|sndNoDefault|sndSync)
}
