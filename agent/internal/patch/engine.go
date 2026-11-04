package patch

import (
	"context"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// scanWith picks the engine of the endpoint: apt where dpkg is the package database (Ubuntu, Debian, Proxmox VE), dnf on the RHEL
// family (AlmaLinux). Without either there is nothing Fleeto can scan, and no report is made.
func scanWith(ctx context.Context, sys system) *agentv1.PatchScanReport {
	switch {
	case sys.LookPath("apt-get") && sys.LookPath("dpkg-query"):
		return scanApt(ctx, sys)
	case sys.LookPath("dnf") && sys.LookPath("rpm"):
		return scanDnf(ctx, sys)
	}
	return nil
}
