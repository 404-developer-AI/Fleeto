package uninstall

import (
	"os/exec"
	"syscall"

	"golang.org/x/sys/windows"
)

// startDetached starts the uninstall as a detached process of its own. The service control manager does not stop the children of a
// service when the service stops, so the uninstall goes on after it stopped the watchdog and the agent; it runs as SYSTEM, like they do.
func startDetached(agentExe string) error {
	cmd := exec.Command(agentExe, "uninstall") // #nosec G204 -- the installed agent program, a fixed argument.
	cmd.SysProcAttr = &syscall.SysProcAttr{
		CreationFlags: windows.DETACHED_PROCESS | windows.CREATE_NEW_PROCESS_GROUP,
		HideWindow:    true,
	}
	if err := cmd.Start(); err != nil {
		return err
	}
	return cmd.Process.Release()
}
