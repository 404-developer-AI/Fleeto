//go:build !linux && !windows

package patch

import (
	"context"
	"log/slog"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// Scan makes no report on this platform: Fleeto scans for missing updates on Linux (apt, dnf) and Windows (the Windows Update
// Agent) only.
func Scan(context.Context, string, *slog.Logger) *agentv1.PatchScanReport { return nil }
