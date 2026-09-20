//go:build !windows && !linux

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
	// Hardware and software inventory beyond Windows and Linux follows when a platform needs it.
	return platformInfo{}, nil
}

func installedSoftware() ([]*agentv1.SoftwareItem, error) {
	return nil, nil
}

func services() ([]*agentv1.ServiceItem, error) {
	// launchd jobs follow if macOS is ever supported.
	return nil, nil
}

func action1AgentID() string {
	return ""
}
