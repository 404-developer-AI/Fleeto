//go:build windows

package inventory

import "golang.org/x/sys/windows/registry"

// The Action1 agent keeps one value per installation, its own id, under this key. Action1 documents the 32-bit view
// (WOW6432Node); the plain path is read as well in case a later agent writes there. 0.4.0.
var action1Keys = []string{`SOFTWARE\WOW6432Node\Action1`, `SOFTWARE\Action1`}

const action1IDValue = "agent.guid"

// action1AgentID returns the id of the Action1 agent installed on this endpoint, or an empty string when Action1 is not
// installed or the value is not an id. The server matches patch state on it, so a value that is not a GUID is dropped
// rather than reported: a wrong match would show one machine's patch state on another.
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
				return id
			}
		}
	}
	return ""
}
