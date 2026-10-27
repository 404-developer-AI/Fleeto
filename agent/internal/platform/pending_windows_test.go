//go:build windows

package platform

import (
	"slices"
	"testing"
)

func TestOnlyTheDeletionsOfTheProgramFolderAreCancelled(t *testing.T) {
	values := []string{
		`\??\C:\Program Files\Fleeto\Agent\fleeto-agent.exe`, "",
		`\??\C:\Program Files\Fleeto\Agent`, "",
		`\??\C:\Program Files\Fleeto\AgentOther\x.dll`, "",
		`\??\C:\Windows\Temp\a.tmp`, "",
		`\??\C:\Program Files\Fleeto\Agent\old.exe`, `!\??\C:\Program Files\Fleeto\Agent\new.exe`,
	}
	kept, removed := withoutDeletesBelow(values, `C:\Program Files\Fleeto\Agent`)
	if !slices.Equal(removed, []string{`\??\C:\Program Files\Fleeto\Agent\fleeto-agent.exe`, `\??\C:\Program Files\Fleeto\Agent`}) {
		t.Fatalf("removed = %v", removed)
	}
	if !slices.Equal(kept, values[4:]) {
		t.Fatalf("kept = %v", kept)
	}
	if kept, removed := withoutDeletesBelow(values[:3], `C:\Program Files\Fleeto\Agent`); len(removed) != 0 || len(kept) != 3 {
		t.Fatal("an odd list was changed")
	}
}
