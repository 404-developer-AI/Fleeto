package uninstall

import (
	"fmt"
	"os/exec"
	"syscall"
	"time"
)

// startDetached starts the uninstall outside the systemd unit of the service: stopping a unit kills every process in its control
// group, so a plain child would die as soon as the uninstall stops the service that started it. systemd-run puts it in a transient
// unit of its own. Without systemd the child gets its own session, which is enough where no service manager kills the group.
func startDetached(agentExe string) error {
	if runner, err := exec.LookPath("systemd-run"); err == nil {
		unit := fmt.Sprintf("fleeto-uninstall-%d", time.Now().Unix())
		// #nosec G204 -- fixed tool and arguments, the installed agent program.
		return exec.Command(runner, "--quiet", "--collect", "--unit="+unit, agentExe, "uninstall").Run()
	}
	cmd := exec.Command(agentExe, "uninstall") // #nosec G204 -- the installed agent program, a fixed argument.
	cmd.SysProcAttr = &syscall.SysProcAttr{Setsid: true}
	if err := cmd.Start(); err != nil {
		return err
	}
	return cmd.Process.Release()
}
