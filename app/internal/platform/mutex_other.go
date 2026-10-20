//go:build !windows

package platform

// HoldAppMutex is only needed on Windows, where the installer looks for it.
func HoldAppMutex() (alreadyRunning bool) { return false }
