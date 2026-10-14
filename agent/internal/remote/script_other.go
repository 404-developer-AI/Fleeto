//go:build !windows && !linux

package remote

import (
	"errors"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
)

// OpenScript is not supported on this platform.
func OpenScript(string, platform.Access) ScriptOpener {
	return func(ScriptRequest, int, int) (Terminal, string, error) {
		return nil, "", errors.New("scripts do not run in a terminal on this operating system")
	}
}
