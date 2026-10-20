//go:build windows

package platform

import (
	"syscall"
	"unsafe"
)

var procCreateMutexW = syscall.NewLazyDLL("kernel32.dll").NewProc("CreateMutexW")

// appMutex is kept for the life of the process.
var appMutex uintptr

// HoldAppMutex takes the named mutex that 1.0.10 and older take and that the
// installer's AppMutex setting looks for ("maccy_mutex"): while it exists the
// installer knows the program runs and asks it to close. It says whether
// another instance already holds it.
func HoldAppMutex() (alreadyRunning bool) {
	name, _ := syscall.UTF16PtrFromString("maccy_mutex")
	h, _, err := procCreateMutexW.Call(0, 0, uintptr(unsafe.Pointer(name)))
	if h == 0 {
		return false
	}
	appMutex = h
	const errorAlreadyExists = syscall.Errno(183)
	return err == errorAlreadyExists
}
