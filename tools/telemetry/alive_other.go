//go:build !windows

package main

import (
	"os"
	"syscall"
)

// alive reports whether pid names a process, by sending it no signal.
func alive(pid int) bool {
	p, err := os.FindProcess(pid)
	return err == nil && p.Signal(syscall.Signal(0)) == nil
}
