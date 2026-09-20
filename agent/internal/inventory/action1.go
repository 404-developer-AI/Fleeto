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
