//go:build !windows

package patch

import "log/slog"

// ApplyMicrosoftUpdate does nothing outside Windows and reports no outcome.
func ApplyMicrosoftUpdate(bool, *slog.Logger) (string, bool, error) { return "", false, nil }

// RestoreMicrosoftUpdate does nothing outside Windows.
func RestoreMicrosoftUpdate(*slog.Logger) error { return nil }
