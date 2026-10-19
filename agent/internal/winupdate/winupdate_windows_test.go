//go:build windows

package winupdate

import (
	"testing"

	"golang.org/x/sys/windows/registry"
)

// testKeys points at a throwaway key under HKEY_CURRENT_USER, so the test needs no administrator rights and never touches the
// Windows Update settings of the machine.
func testKeys(t *testing.T) keys {
	t.Helper()
	base := `SOFTWARE\FleetoTest\winupdate-` + t.Name()
	k := keys{
		root:        registry.CURRENT_USER,
		policyPath:  base + `\Policies\AU`,
		policyValue: "NoAutoUpdate",
		markerPath:  base + `\Fleeto`,
		markerValue: "WindowsUpdateTurnedOff",
	}
	t.Cleanup(func() {
		for _, path := range []string{k.policyPath, base + `\Policies`, k.markerPath, base} {
			_ = registry.DeleteKey(registry.CURRENT_USER, path)
		}
	})
	return k
}

func policyValue(t *testing.T, k keys) (uint64, bool) {
	t.Helper()
	value, set, err := readInteger(k.root, k.policyPath, k.policyValue)
	if err != nil {
		t.Fatal(err)
	}
	return value, set
}

func TestApplyTurnsOffAndBackOn(t *testing.T) {
	k := testKeys(t)

	state, changed, err := k.apply(true, true)
	if err != nil || state != StateTurnedOff || !changed {
		t.Fatalf("apply(true, true) = (%q, %v, %v)", state, changed, err)
	}
	if value, set := policyValue(t, k); !set || value != 1 {
		t.Fatalf("NoAutoUpdate = %d (set %v), want 1", value, set)
	}

	state, changed, err = k.apply(true, true)
	if err != nil || state != StateTurnedOff || changed {
		t.Fatalf("second apply(true, true) = (%q, %v, %v), want no change", state, changed, err)
	}

	state, changed, err = k.apply(false, true)
	if err != nil || state != StateUntouched || !changed {
		t.Fatalf("apply(false, true) = (%q, %v, %v)", state, changed, err)
	}
	if _, set := policyValue(t, k); set {
		t.Fatal("NoAutoUpdate is still set after the configuration stopped asking for it")
	}
	if marker, _, _ := readInteger(k.root, k.markerPath, k.markerValue); marker != 0 {
		t.Fatal("the marker is still set")
	}
}

func TestApplyLeavesAGroupPolicyValue(t *testing.T) {
	k := testKeys(t)
	// A group policy keeps automatic updates on.
	key, _, err := registry.CreateKey(k.root, k.policyPath, registry.SET_VALUE)
	if err != nil {
		t.Fatal(err)
	}
	if err := key.SetDWordValue(k.policyValue, 0); err != nil {
		t.Fatal(err)
	}
	key.Close()

	state, changed, err := k.apply(true, true)
	if err != nil || state != StatePolicy || changed {
		t.Fatalf("apply(true, true) = (%q, %v, %v), want the group policy to win", state, changed, err)
	}
	if _, _, err := k.apply(false, true); err != nil {
		t.Fatal(err)
	}
	if value, set := policyValue(t, k); !set || value != 0 {
		t.Fatalf("NoAutoUpdate = %d (set %v), want the group policy value 0 kept", value, set)
	}
}

func TestApplyNeverTurnsOffWithoutAction1(t *testing.T) {
	k := testKeys(t)
	state, changed, err := k.apply(true, false)
	if err != nil || state != StateNoAction1 || changed {
		t.Fatalf("apply(true, false) = (%q, %v, %v)", state, changed, err)
	}
	if _, set := policyValue(t, k); set {
		t.Fatal("NoAutoUpdate was set without the Action1 agent")
	}
}
