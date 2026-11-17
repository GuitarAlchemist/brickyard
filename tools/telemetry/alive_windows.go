package main

import "syscall"

// alive reports whether pid names a process that has not exited. It opens the process for its exit code only.
func alive(pid int) bool {
	const queryLimited = 0x1000 // PROCESS_QUERY_LIMITED_INFORMATION
	const stillActive = 259     // STILL_ACTIVE
	h, err := syscall.OpenProcess(queryLimited, false, uint32(pid))
	if err != nil {
		return false
	}
	defer syscall.CloseHandle(h)
	var code uint32
	return syscall.GetExitCodeProcess(h, &code) == nil && code == stillActive
}
