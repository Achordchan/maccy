//go:build windows

package update

import "golang.org/x/sys/windows"

// The helper creates this marker before waiting for the old process to exit.
// Kernel ownership releases it even if the helper crashes.
func holdUpdateExclusion() (func(), error) {
	name, _ := windows.UTF16PtrFromString("klip_update_in_progress")
	h, err := windows.CreateMutex(nil, false, name)
	if err != nil {
		return nil, err
	}
	return func() { windows.CloseHandle(h) }, nil
}
func UpdateInProgress() bool {
	name, _ := windows.UTF16PtrFromString("klip_update_in_progress")
	h, err := windows.OpenMutex(windows.SYNCHRONIZE, false, name)
	if err != nil {
		return false
	}
	windows.CloseHandle(h)
	return true
}
