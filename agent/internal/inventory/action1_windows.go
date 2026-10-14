//go:build windows

package inventory

import (
	"strings"

	"golang.org/x/sys/windows/registry"
)

// The Action1 agent keeps one value per installation, its own id, under this key. Action1 documents the 32-bit view
// (WOW6432Node); the plain path is read as well in case a later agent writes there. 0.4.0.
var action1Keys = []string{`SOFTWARE\WOW6432Node\Action1`, `SOFTWARE\Action1`}

const action1IDValue = "agent.guid"

// action1AgentID returns the id of the Action1 agent installed on this endpoint, or an empty string when Action1 is not
// installed or the value is not an id. The server matches patch state on it, so a value that is not a GUID is dropped
// rather than reported: a wrong match would show one machine's patch state on another. An id Action1 left behind when it
// was uninstalled is not reported either (0.6.0).
func action1AgentID() string {
	for _, path := range action1Keys {
		for _, view := range []uint32{registry.WOW64_64KEY, registry.WOW64_32KEY} {
			k, err := registry.OpenKey(registry.LOCAL_MACHINE, path, registry.QUERY_VALUE|view)
			if err != nil {
				continue
			}
			value := stringValue(k, action1IDValue)
			k.Close()
			if id := normalizeAgentID(value); id != "" {
				return action1Reported(id, action1Installed())
			}
		}
	}
	return ""
}

// action1Installed reports whether the installed programs hold the Action1 agent, by the rule the jobs of Fleeto use
// (Find-Action1Agent): an entry named "Action1 Agent" under a product code, in either registry view. Unknown when the
// installed programs cannot be read at all.
func action1Installed() installState {
	readable := false
	for _, view := range []uint32{registry.WOW64_64KEY, registry.WOW64_32KEY} {
		root, err := registry.OpenKey(registry.LOCAL_MACHINE, uninstallPath, registry.ENUMERATE_SUB_KEYS|registry.QUERY_VALUE|view)
		if err != nil {
			continue
		}
		names, err := root.ReadSubKeyNames(-1)
		if err != nil {
			root.Close()
			continue
		}
		readable = true
		for _, name := range names {
			if !strings.HasPrefix(name, "{") || normalizeAgentID(name) == "" {
				continue
			}
			k, err := registry.OpenKey(root, name, registry.QUERY_VALUE|view)
			if err != nil {
				continue
			}
			display := stringValue(k, "DisplayName")
			k.Close()
			if isAction1AgentName(display) {
				root.Close()
				return installYes
			}
		}
		root.Close()
	}
	if !readable {
		return installUnknown
	}
	return installNo
}
