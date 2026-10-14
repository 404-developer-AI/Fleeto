//go:build linux

package remote

import (
	"github.com/404-developer-AI/Fleeto/agent/internal/jobs"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
)

// OpenScript runs library scripts staged under dir (0.6.0) in a pseudo terminal, as root like the terminal or as the signed-in user.
func OpenScript(dir string, access platform.Access) ScriptOpener {
	return func(request ScriptRequest, cols, rows int) (Terminal, string, error) {
		script, err := request.Job()
		if err != nil {
			return nil, "", err
		}
		staged, err := jobs.StageInteractive(script, dir, access, request.RunAs == RunAsUser, request.User)
		if err != nil {
			return nil, "", err
		}
		path, args := staged.Command()
		env := staged.Environment()
		if env == nil {
			env = rootEnvironment(path, "/root")
		}
		terminal, err := openPTY(path, args, staged.Dir, env, staged.Credential(), staged.UserID(), cols, rows)
		if err != nil {
			staged.Release()
			return nil, "", err
		}
		return withRelease(terminal, staged.Release), staged.Account, nil
	}
}
