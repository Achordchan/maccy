//go:build windows

package main

import "syscall"

var procSeq = syscall.NewLazyDLL("user32.dll").NewProc("GetClipboardSequenceNumber")

// changeSeq returns the clipboard sequence number, which Windows bumps on
// every change.
func changeSeq() int64 {
	r, _, _ := procSeq.Call()
	return int64(r)
}
