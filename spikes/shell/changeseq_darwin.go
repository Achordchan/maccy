//go:build darwin

package main

import (
	"github.com/ebitengine/purego"
)

// changeSeq returns NSPasteboard's changeCount, which grows on every
// clipboard change. It is cheap, so the poller can ask it often and read the
// clipboard only when it moved. -1 means it is not available.
var (
	pbID       uintptr
	pbCountSel uintptr
	msgSend    func(obj, sel uintptr) uintptr
	pbReady    bool
)

func init() {
	objc, err := purego.Dlopen("/usr/lib/libobjc.A.dylib", purego.RTLD_LAZY|purego.RTLD_GLOBAL)
	if err != nil {
		return
	}
	if _, err := purego.Dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", purego.RTLD_LAZY|purego.RTLD_GLOBAL); err != nil {
		return
	}
	var (
		getClass func(name string) uintptr
		selReg   func(name string) uintptr
	)
	purego.RegisterLibFunc(&getClass, objc, "objc_getClass")
	purego.RegisterLibFunc(&selReg, objc, "sel_registerName")
	purego.RegisterLibFunc(&msgSend, objc, "objc_msgSend")

	cls := getClass("NSPasteboard")
	if cls == 0 {
		return
	}
	// generalPasteboard is a process-wide singleton: ask once and keep it, so
	// later calls from a goroutine allocate nothing autoreleased.
	pbID = msgSend(cls, selReg("generalPasteboard"))
	pbCountSel = selReg("changeCount")
	pbReady = pbID != 0
}

func changeSeq() int64 {
	if !pbReady {
		return -1
	}
	return int64(msgSend(pbID, pbCountSel))
}
