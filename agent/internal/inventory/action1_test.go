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
