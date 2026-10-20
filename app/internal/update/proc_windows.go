//go:build windows

package update

import (
	"os/exec"
	"syscall"
	"unsafe"
)

const (
	synchronize     = 0x00100000
	infinite        = 0xFFFFFFFF
	createNoWindow  = 0x08000000
	detachedProcess = 0x00000008
	mbOK            = 0x0
	mbIconError     = 0x10
)

var (
	kernel32        = syscall.NewLazyDLL("kernel32.dll")
	user32          = syscall.NewLazyDLL("user32.dll")
	procOpenProcess = kernel32.NewProc("OpenProcess")
	procWait        = kernel32.NewProc("WaitForSingleObject")
	procCloseHandle = kernel32.NewProc("CloseHandle")
	procMessageBoxW = user32.NewProc("MessageBoxW")
)

// waitExit blocks until the process is gone; one that cannot be opened is
// taken to be gone already.
func waitExit(pid int) {
	h, _, _ := procOpenProcess.Call(synchronize, 0, uintptr(pid))
	if h == 0 {
		return
	}
	defer procCloseHandle.Call(h)
	procWait.Call(h, infinite)
}

// launch starts a program, detached and without a console window. Its own
// windows are left as the program wants them: this starts the installer
// wizard too, and a window hidden by the start-up information (SW_HIDE) would
// leave it running where nobody can see it.
func launch(path string) error {
	cmd := exec.Command(path)
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: createNoWindow | detachedProcess}
	return cmd.Start()
}

// StartHelper starts the helper program with the plan, hidden.
func StartHelper(helperExe, planPath string) error {
	cmd := exec.Command(helperExe, "--apply-update", planPath)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: createNoWindow | detachedProcess}
	return cmd.Start()
}

func alert(title, msg string) {
	t, _ := syscall.UTF16PtrFromString(title)
	m, _ := syscall.UTF16PtrFromString(msg)
	procMessageBoxW.Call(0, uintptr(unsafe.Pointer(m)), uintptr(unsafe.Pointer(t)), mbOK|mbIconError)
}
