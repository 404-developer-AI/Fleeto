//go:build !windows

package checks

import (
	"context"
	"crypto/x509"
	"errors"
	"os"
	"os/exec"
	"runtime"
	"strings"
	"time"
)

func certificatesFromStore(string) ([]*x509.Certificate, error) {
	return nil, errors.New("a certificate store exists on Windows only; check a certificate file or folder instead")
}

func eventLogCheck(context.Context, map[string]string) Measurement {
	return Measurement{Error: "event log checks run on Windows only"}
}

func securityCenterCheck(context.Context, string) Measurement {
	return Measurement{Error: "antivirus and firewall checks run on Windows only"}
}

// pendingReboot reads the restart indicators of the common Linux distributions.
func pendingReboot(ctx context.Context) Measurement {
	if runtime.GOOS != "linux" {
		return Measurement{Error: "pending restart checks run on Windows and Linux only"}
	}
	// Debian and Ubuntu.
	if data, err := os.ReadFile("/var/run/reboot-required.pkgs"); err == nil {
		pkgs := strings.Fields(string(data))
		detail := "Restart pending for package updates"
		if len(pkgs) > 0 {
			detail += " (" + strings.Join(limitStrings(pkgs, 5), ", ") + ")"
		}
		return Measurement{Value: 0, Detail: detail}
	}
	if _, err := os.Stat("/var/run/reboot-required"); err == nil {
		return Measurement{Value: 0, Detail: "Restart pending for package updates"}
	}
	if _, err := os.Stat("/var/lib/dpkg"); err == nil {
		return Measurement{Value: 1, Detail: "No restart pending"}
	}
	// Red Hat family: needs-restarting -r exits 1 when a restart is needed.
	if path, err := exec.LookPath("needs-restarting"); err == nil {
		cctx, cancel := context.WithTimeout(ctx, 60*time.Second)
		defer cancel()
		cmd := exec.CommandContext(cctx, path, "-r") // #nosec G204 -- fixed binary and argument.
		err := cmd.Run()
		var exit *exec.ExitError
		switch {
		case err == nil:
			return Measurement{Value: 1, Detail: "No restart pending"}
		case errors.As(err, &exit) && exit.ExitCode() == 1:
			return Measurement{Value: 0, Detail: "Restart pending: core libraries or services were updated"}
		default:
			return Measurement{Error: "needs-restarting could not run: " + err.Error()}
		}
	}
	return Measurement{Error: "this distribution has no restart indicator Fleeto can read (reboot-required or needs-restarting)"}
}

func limitStrings(values []string, n int) []string {
	if len(values) <= n {
		return values
	}
	return append(values[:n:n], "...")
}
