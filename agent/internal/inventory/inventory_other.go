//go:build !windows

package inventory

import (
	"context"
	"runtime"
	"strings"

	"github.com/shirou/gopsutil/v4/host"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// OSInfo returns the operating system description from gopsutil.
func OSInfo(ctx context.Context) *agentv1.OsInfo {
	info := &agentv1.OsInfo{Platform: runtime.GOOS, Architecture: goArch()}
	if h, err := host.InfoWithContext(ctx); err == nil {
		info.Name = strings.TrimSpace(h.Platform + " " + h.PlatformVersion)
		info.Version = h.KernelVersion
	}
	return info
}

func collectPlatform(context.Context) (platformInfo, error) {
	// Hardware and software inventory for Linux and macOS follows in a later release.
	return platformInfo{}, nil
}

func installedSoftware() ([]*agentv1.SoftwareItem, error) {
	return nil, nil
}
