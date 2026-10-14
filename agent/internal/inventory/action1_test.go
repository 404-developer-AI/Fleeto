package inventory

import "testing"

func TestAnAction1AgentIDIsOnlyReportedWhenItIsAGuid(t *testing.T) {
	cases := []struct {
		name  string
		value string
		want  string
	}{
		{"plain", "EF17C844-5B7C-4B32-9724-F2716B596639", "ef17c844-5b7c-4b32-9724-f2716b596639"},
		{"braces and spaces", "  {ef17c844-5b7c-4b32-9724-f2716b596639}  ", "ef17c844-5b7c-4b32-9724-f2716b596639"},
		{"empty", "", ""},
		{"not a guid", "not-installed", ""},
		{"too short", "ef17c844-5b7c-4b32-9724-f2716b59663", ""},
		{"wrong separators", "ef17c844:5b7c:4b32:9724:f2716b596639x", ""},
		{"not hex", "zf17c844-5b7c-4b32-9724-f2716b596639", ""},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := normalizeAgentID(c.value); got != c.want {
				t.Fatalf("normalizeAgentID(%q) = %q, want %q", c.value, got, c.want)
			}
		})
	}
}

func TestAnAction1IDLeftBehindByAnUninstallIsNotReported(t *testing.T) {
	const id = "ef17c844-5b7c-4b32-9724-f2716b596639"
	if got := action1Reported(id, installYes); got != id {
		t.Fatalf("an installed agent must be reported, got %q", got)
	}
	if got := action1Reported(id, installNo); got != "" {
		t.Fatalf("an id without an installed agent must not be reported, got %q", got)
	}
	if got := action1Reported(id, installUnknown); got != id {
		t.Fatalf("when the installed programs cannot be read the id must be kept, got %q", got)
	}
	for _, name := range []string{"Action1 Agent", "action1 agent 5.2", "  Action1 Agent (x64)"} {
		if !isAction1AgentName(name) {
			t.Fatalf("%q is the Action1 agent", name)
		}
	}
	for _, name := range []string{"", "Action1 Connector", "My Action1 Agent"} {
		if isAction1AgentName(name) {
			t.Fatalf("%q is not the Action1 agent", name)
		}
	}
}
