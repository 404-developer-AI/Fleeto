package inventory

import "strings"

// normalizeAgentID accepts the id of an Action1 agent with or without braces and returns it lowercased without them.
// Anything that is not a GUID gives an empty string: the server matches patch state on this value, and a wrong match
// would show one machine's patch state on another (0.4.0).
func normalizeAgentID(value string) string {
	id := strings.ToLower(strings.TrimSpace(value))
	id = strings.TrimPrefix(id, "{")
	id = strings.TrimSuffix(id, "}")
	if len(id) != 36 {
		return ""
	}
	for i, c := range id {
		switch i {
		case 8, 13, 18, 23:
			if c != '-' {
				return ""
			}
		default:
			if (c < '0' || c > '9') && (c < 'a' || c > 'f') {
				return ""
			}
		}
	}
	return id
}

// installState is whether a program is installed, as far as the endpoint can tell.
type installState int

const (
	installUnknown installState = iota
	installYes
	installNo
)

// isAction1AgentName matches the name the Action1 agent has in the installed programs, as Find-Action1Agent does.
func isAction1AgentName(display string) bool {
	return strings.HasPrefix(strings.ToLower(strings.TrimSpace(display)), "action1 agent")
}

// action1Reported is the id to report: Action1 leaves its id in the registry when it is uninstalled (found testing 0.6.0 on
// 2026-10-14), and reporting that id kept an endpoint without the agent from installing it again. When the installed programs
// cannot be read the id is kept, so a read error never takes the patch state of an endpoint away.
func action1Reported(id string, installed installState) string {
	if installed == installNo {
		return ""
	}
	return id
}
