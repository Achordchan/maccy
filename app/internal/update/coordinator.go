package update

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"time"
)

// Phase is what the coordinator is doing.
type Phase int

const (
	Idle        Phase = iota
	Checking          // looking at the manifest
	UpToDateNow       // the user asked, and there is nothing newer
	Available         // Info is a newer version
	Downloading       // Read/Total of the download
	Applying          // the helper is started, the program is about to exit
	Problem           // Message says what went wrong
)

// State is what a window shows.
type State struct {
	Phase   Phase
	Info    *Info
	Message string
	// Check says that a Problem came from looking for a version, not from
	// installing one (so there is nothing to retry but the look).
	Check bool
	Read  int64
	Total int64
}

// Coordinator is UpdateCoordinator without the windows: it keeps the newest
// version found, downloads it and hands the replacing to the helper.
type Coordinator struct {
	Client *Client
	// Root is the updates folder of the data directory.
	Root string
	// Exe is the running program; its folder is what gets updated.
	Exe string
	// PID is this process (os.Getpid unless a test sets it).
	PID int
	// Shutdown ends the program, once the helper is started.
	Shutdown func()
	// OnChange is called after each change of the state, from any goroutine.
	OnChange func()
	// Supported says whether this system can replace its own files; without
	// it the update is offered as a download to do by hand.
	Supported bool
	// StartHelper starts the helper (the package's own by default).
	StartHelper func(helperExe, planPath string) error
	// Now is time.Now unless a test sets it.
	Now func() time.Time

	// fsMu keeps the cleaning of old files and the preparing of an update from
	// working in the same folders at once.
	fsMu     sync.Mutex
	mu       sync.Mutex
	state    State
	busy     bool
	applying bool
}

// NewCoordinator returns a Coordinator for the running program.
func NewCoordinator(c *Client, root, exe string, shutdown func()) *Coordinator {
	return &Coordinator{Client: c, Root: root, Exe: exe, Shutdown: shutdown, Supported: runtime.GOOS == "windows"}
}

// State returns a copy of the state.
func (c *Coordinator) State() State {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.state
}

// Available is the newer version found, nil when none is known.
func (c *Coordinator) Available() *Info {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.state.Info == nil {
		return nil
	}
	i := *c.state.Info
	return &i
}

func (c *Coordinator) set(f func(*State)) {
	c.mu.Lock()
	f(&c.state)
	c.mu.Unlock()
	if c.OnChange != nil {
		c.OnChange()
	}
}

// Refresh looks for a newer version without a word to the user (the check at
// start and every few hours): it only changes what Available says, and an
// error leaves it as it was.
func (c *Coordinator) Refresh(ctx context.Context) {
	c.mu.Lock()
	if c.busy || c.applying {
		c.mu.Unlock()
		return
	}
	c.busy = true
	c.mu.Unlock()
	defer func() { c.mu.Lock(); c.busy = false; c.mu.Unlock() }()

	res := c.Client.Check(ctx, nil)
	switch res.Status {
	case UpdateAvailable:
		c.set(func(s *State) {
			if !c.applying { // an update under way is not for a quiet check to overwrite
				*s = State{Phase: Available, Info: res.Info}
			}
		})
	case UpToDate:
		c.set(func(s *State) {
			if !c.applying {
				*s = State{}
			}
		})
	}
}

// Check is the user asking: it moves through Checking to UpToDateNow,
// Available or Problem, with the texts of the C# window.
func (c *Coordinator) Check(ctx context.Context) {
	waited := false
	for {
		c.mu.Lock()
		if c.applying {
			c.mu.Unlock()
			c.set(func(s *State) { s.Message = "更新正在进行中，请稍后..." })
			return
		}
		if !c.busy {
			break // the lock is held: this check is the one that runs
		}
		c.mu.Unlock()
		// A check is under way (the quiet one at start, say): wait for it to
		// end and then make our own, so that the window gets an answer of its
		// own and is not left "checking" for a request that has ended.
		if !waited {
			waited = true
			c.set(func(s *State) { s.Phase, s.Message = Checking, "已有检查正在进行，稍后继续..." })
		}
		select {
		case <-ctx.Done():
			return
		case <-time.After(50 * time.Millisecond):
		}
	}
	c.busy = true
	c.mu.Unlock()
	defer func() { c.mu.Lock(); c.busy = false; c.mu.Unlock() }()

	c.mu.Lock()
	known := c.state.Info // an update found before stays known if this look fails
	c.mu.Unlock()
	c.set(func(s *State) {
		*s = State{Phase: Checking, Message: "正在连接更新服务器，请稍候...", Info: known}
	})
	res := c.Client.Check(ctx, func(m string) { c.set(func(s *State) { s.Message = m }) })
	switch res.Status {
	case UpToDate:
		c.set(func(s *State) { *s = State{Phase: UpToDateNow, Message: "当前已经是最新版本。"} })
	case Failed:
		c.set(func(s *State) {
			*s = State{Phase: Problem, Message: "检查更新失败：" + res.Error, Info: known, Check: true}
		})
	default:
		c.set(func(s *State) { *s = State{Phase: Available, Info: res.Info} })
	}
}

// Dismiss clears a finished message (UpToDateNow, Problem) and, for a version
// the user postponed, goes back to Available.
func (c *Coordinator) Dismiss() {
	c.mu.Lock()
	info := c.state.Info
	phase := c.state.Phase
	c.mu.Unlock()
	if phase == Applying || phase == Downloading {
		return
	}
	c.set(func(s *State) {
		if info != nil {
			*s = State{Phase: Available, Info: info}
		} else {
			*s = State{}
		}
	})
}

// Apply downloads the newer version and starts the helper that replaces the
// files; then it asks the program to exit. The light package comes first;
// when it fails and there is an installer, that is used instead, as in C#.
func (c *Coordinator) Apply(ctx context.Context) {
	c.mu.Lock()
	info := c.state.Info
	if info == nil || c.applying {
		c.mu.Unlock()
		return
	}
	c.applying = true
	c.mu.Unlock()
	fail := func(msg string) {
		c.mu.Lock()
		c.applying = false
		c.mu.Unlock()
		c.set(func(s *State) { s.Phase, s.Message = Problem, msg })
	}

	if !c.Supported {
		fail("当前系统不能自动更新，请到发布页下载新版本：" + strings.TrimSpace(firstNonEmpty(info.InstallerURL, info.PackageURL)))
		return
	}
	progress := func(read, total int64) {
		c.set(func(s *State) { s.Phase, s.Read, s.Total = Downloading, read, total })
	}
	c.set(func(s *State) { s.Phase, s.Read, s.Total, s.Message = Downloading, 0, 0, "" })

	// The folders this uses are the ones CleanOld empties: not both at once.
	c.fsMu.Lock()
	defer c.fsMu.Unlock()

	notice := ""
	if info.HasSupportedPackage(c.Client.runtime()) {
		prep, err := c.Client.DownloadPackage(ctx, *info, c.Root, progress)
		if err == nil {
			err = c.startPackage(prep)
			if err == nil {
				c.done()
				return
			}
			if strings.TrimSpace(info.InstallerURL) == "" {
				fail("启动轻量更新失败：" + err.Error())
				return
			}
			notice = "启动轻量更新失败：" + err.Error() + "\n\n将改用完整安装包更新。"
		} else {
			if strings.TrimSpace(info.InstallerURL) == "" {
				fail("轻量更新失败：" + err.Error())
				return
			}
			notice = "轻量更新失败：" + err.Error() + "\n\n将改用完整安装包更新。"
		}
	}
	if strings.TrimSpace(info.InstallerURL) == "" {
		fail("没有可用的安装包下载地址。")
		return
	}
	c.set(func(s *State) { s.Phase, s.Read, s.Total, s.Message = Downloading, 0, 0, notice })
	path, err := c.Client.DownloadInstaller(ctx, *info, c.Root, progress)
	if err != nil {
		fail("下载失败：" + err.Error())
		return
	}
	if err := c.startInstaller(path, info.Version); err != nil {
		fail("启动安装程序失败：" + err.Error())
		return
	}
	c.done()
}

func firstNonEmpty(v ...string) string {
	for _, s := range v {
		if strings.TrimSpace(s) != "" {
			return s
		}
	}
	return ""
}

func (c *Coordinator) done() {
	c.set(func(s *State) { s.Phase, s.Message = Applying, "正在重启以完成更新..." })
	if c.Shutdown != nil {
		c.Shutdown()
	}
}

func (c *Coordinator) pid() int {
	if c.PID != 0 {
		return c.PID
	}
	return os.Getpid()
}

func (c *Coordinator) now() time.Time {
	if c.Now != nil {
		return c.Now()
	}
	return time.Now()
}

// helperCopy copies the running program to a file of its own, which the
// helper runs from: the program's own file cannot be replaced while it runs.
func (c *Coordinator) helperCopy(version string) (string, error) {
	if strings.TrimSpace(c.Exe) == "" {
		return "", errors.New("current executable path is unavailable")
	}
	dir := filepath.Join(c.Root, "helper")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", err
	}
	name := "maccy-update-" + version
	if runtime.GOOS == "windows" {
		name += ".exe"
	}
	dst := filepath.Join(dir, name)
	if err := copyFile(c.Exe, dst); err != nil {
		return "", err
	}
	return dst, nil
}

func (c *Coordinator) spawn(helper, plan string) error {
	if c.StartHelper != nil {
		return c.StartHelper(helper, plan)
	}
	return StartHelper(helper, plan)
}

func (c *Coordinator) startPackage(p *Prepared) error {
	if strings.TrimSpace(c.Exe) == "" {
		return errors.New("current executable path is unavailable")
	}
	installDir := filepath.Dir(c.Exe)
	plan := Plan{
		PID:               c.pid(),
		InstallDir:        installDir,
		StagingDir:        p.StagingPath,
		PackageManifest:   p.PackageManifest,
		InstalledManifest: filepath.Join(c.Root, "installed-package-files.json"),
		BackupDir:         filepath.Join(c.Root, "backups", p.Version+"-"+c.now().Format("20060102-150405")),
		LogPath:           filepath.Join(c.Root, "last_light_update.log"),
	}
	return c.launchPlan(plan, p.Version)
}

func (c *Coordinator) startInstaller(path, version string) error {
	return c.launchPlan(Plan{PID: c.pid(), Installer: path, RestartExe: c.Exe, LogPath: filepath.Join(c.Root, "last_light_update.log")}, version)
}

func (c *Coordinator) launchPlan(plan Plan, version string) error {
	helper, err := c.helperCopy(version)
	if err != nil {
		return err
	}
	planPath := filepath.Join(c.Root, fmt.Sprintf("apply-update-%s.json", version))
	if err := WritePlan(planPath, plan); err != nil {
		return err
	}
	return c.spawn(helper, planPath)
}

// CleanOld removes what earlier updates left that is of no more use: the
// helper copies of the program and the unpacked packages. Backups stay.
func (c *Coordinator) CleanOld() {
	c.fsMu.Lock()
	defer c.fsMu.Unlock()
	// If an update has been started, what is in these folders is not old: the
	// helper and the staged files are for the update that is about to run.
	c.mu.Lock()
	applying := c.applying
	c.mu.Unlock()
	if applying {
		return
	}
	_ = os.RemoveAll(filepath.Join(c.Root, "helper"))
	_ = os.RemoveAll(filepath.Join(c.Root, "staging"))
}
