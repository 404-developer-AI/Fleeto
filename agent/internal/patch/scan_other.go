//go:build !linux

package patch

import (
	"context"
	"log/slog"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// Scan makes no report on this platform: Windows follows in a later step of 0.9.0.
func Scan(context.Context, string, *slog.Logger) *agentv1.PatchScanReport { return nil }
