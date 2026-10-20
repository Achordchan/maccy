//go:build !darwin && !windows

package main

// changeSeq has no cheap source on this platform: -1 makes the poller fall
// back to hashing what it reads.
func changeSeq() int64 { return -1 }
