package main

import (
	"os"
	"os/exec"
	"syscall"
	"unsafe"
)

// hideConsole keeps a console program from opening a console window of its own. Under Claude Code the brain has no
// console to share, and Piper, whisper-server and the scene are all console programs.
func hideConsole(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: 0x08000000} // CREATE_NO_WINDOW
}

var (
	kernel32                 = syscall.NewLazyDLL("kernel32.dll")
	createJobObject          = kernel32.NewProc("CreateJobObjectW")
	setInformationJobObject  = kernel32.NewProc("SetInformationJobObject")
	assignProcessToJobObject = kernel32.NewProc("AssignProcessToJobObject")
	job                      uintptr
)

// jobLimits is JOBOBJECT_EXTENDED_LIMIT_INFORMATION, 144 bytes on 64-bit Windows.
type jobLimits struct {
	perProcessUserTimeLimit, perJobUserTimeLimit int64
	limitFlags                                   uint32
	minimumWorkingSetSize, maximumWorkingSetSize uintptr
	activeProcessLimit                           uint32
	affinity                                     uintptr
	priorityClass, schedulingClass               uint32
	io                                           [6]uint64
	processMemoryLimit, jobMemoryLimit           uintptr
	peakProcessMemoryUsed, peakJobMemoryUsed     uintptr
}

// killWithMe puts a child in a job that Windows closes when the brain exits, however it exits, and that kills the
// child with it: a brain stopped from outside leaves no whisper-server holding its port.
func killWithMe(p *os.Process) {
	if job == 0 {
		h, _, _ := createJobObject.Call(0, 0)
		if h == 0 {
			return
		}
		limits := jobLimits{limitFlags: 0x2000} // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
		const extendedLimitInformation = 9
		if r, _, _ := setInformationJobObject.Call(h, extendedLimitInformation, uintptr(unsafe.Pointer(&limits)), unsafe.Sizeof(limits)); r == 0 {
			syscall.CloseHandle(syscall.Handle(h))
			return
		}
		job = h
	}
	const access = 0x0100 | 0x0001 // PROCESS_SET_QUOTA | PROCESS_TERMINATE
	h, err := syscall.OpenProcess(access, false, uint32(p.Pid))
	if err != nil {
		return
	}
	defer syscall.CloseHandle(h)
	assignProcessToJobObject.Call(job, uintptr(h))
}
