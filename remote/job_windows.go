package main

import (
	"encoding/binary"
	"errors"
	"os"
	"sync"
	"syscall"
	"unsafe"
)

var (
	procCreateJobObjectW         = kernel32.NewProc("CreateJobObjectW")
	procSetInformationJobObject  = kernel32.NewProc("SetInformationJobObject")
	procAssignProcessToJobObject = kernel32.NewProc("AssignProcessToJobObject")
)

const (
	jobExtendedLimits = 9      // JobObjectExtendedLimitInformation
	jobKillOnClose    = 0x2000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
	processSetQuota   = 0x0100
	processTerminate  = 0x0001
)

// children is a job that ends its processes when its last handle closes. Only the gateway holds one, never closed, so
// Windows closes it when the gateway ends, however it ends: Stop-Process -Force, as each deploy does, gives it no
// chance to stop its ffmpeg. JOBOBJECT_EXTENDED_LIMIT_INFORMATION is 144 bytes on amd64, its LimitFlags after two
// 8-byte times.
var children = sync.OnceValues(func() (syscall.Handle, error) {
	h, _, err := procCreateJobObjectW.Call(0, 0)
	if h == 0 {
		return 0, err
	}
	info := make([]byte, 144)
	binary.LittleEndian.PutUint32(info[16:], jobKillOnClose)
	if r, _, err := procSetInformationJobObject.Call(h, jobExtendedLimits, uintptr(unsafe.Pointer(&info[0])), 144); r == 0 {
		syscall.CloseHandle(syscall.Handle(h))
		return 0, err
	}
	return syscall.Handle(h), nil
})

// endWithMe puts p in children, so that it ends with this process.
func endWithMe(p *os.Process) error {
	job, err := children()
	if err != nil {
		return err
	}
	h, err := syscall.OpenProcess(processSetQuota|processTerminate, false, uint32(p.Pid))
	if err != nil {
		return err
	}
	defer syscall.CloseHandle(h)
	if r, _, err := procAssignProcessToJobObject.Call(uintptr(job), uintptr(h)); r == 0 {
		return errors.Join(errors.New("AssignProcessToJobObject"), err)
	}
	return nil
}
