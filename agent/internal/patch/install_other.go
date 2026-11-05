//go:build !linux && !windows

package patch

import (
	"context"
	"io"
	"log/slog"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// InstallEngine is empty: Fleeto installs updates on Linux and Windows only.
func InstallEngine() string { return "" }

// Install installs nothing on this platform.
func Install(_ context.Context, job *agentv1.PatchInstallJob, _ InstallOptions, tool, lines io.Writer, _ *slog.Logger) InstallResult {
	return failAll(newInstallOutput(tool, lines), job, "Fleeto installs updates on Linux and Windows endpoints only.")
}
