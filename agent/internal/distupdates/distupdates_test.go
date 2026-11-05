package distupdates

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
)

// fakeSystemctl answers is-enabled from a map and records every other call; it never runs anything.
type fakeSystemctl struct {
	states map[string]string
	fail   map[string]bool
	calls  []string
}

func (f *fakeSystemctl) Run(_ context.Context, name string, args ...string) (string, int, error) {
	if name != "systemctl" {
		return "", -1, errors.New("only systemctl is run")
	}
	call := strings.Join(args, " ")
	if len(args) == 2 && args[0] == "is-enabled" {
		state, ok := f.states[args[1]]
		if !ok {
			return "", 1, nil
		}
		code := 0
		if state != "enabled" {
			code = 1
		}
		return state + "\n", code, nil
	}
	f.calls = append(f.calls, call)
	if f.fail[call] {
		return "Failed to disable unit.", 1, nil
	}
	if len(args) == 3 {
		switch args[0] + " " + args[1] {
		case "disable --now":
			f.states[args[2]] = "disabled"
		case "enable --now":
			f.states[args[2]] = "enabled"
		}
	}
	return "", 0, nil
}

func touch(t *testing.T, root, path, content string) {
	t.Helper()
	full := filepath.Join(root, filepath.FromSlash(path))
	if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(full, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}
}

func dpkgSystem(t *testing.T, unattended bool) System {
	t.Helper()
	root := t.TempDir()
	touch(t, root, aptGet, "")
	touch(t, root, dpkgStatus, "")
	if err := os.MkdirAll(filepath.Join(root, "etc", "apt", "apt.conf.d"), 0o755); err != nil {
		t.Fatal(err)
	}
	if unattended {
		touch(t, root, unattendedUpgradeConfig, "Unattended-Upgrade::Allowed-Origins {};\n")
	}
	return System{Root: root, StateDir: t.TempDir(), Runner: &fakeSystemctl{states: map[string]string{}}}
}

func rpmSystem(t *testing.T, states map[string]string) (System, *fakeSystemctl) {
	t.Helper()
	root := t.TempDir()
	touch(t, root, dnf, "")
	touch(t, root, rpmDB, "")
	fake := &fakeSystemctl{states: states, fail: map[string]bool{}}
	return System{Root: root, StateDir: t.TempDir(), Runner: fake}, fake
}

func readFile(t *testing.T, s System, path string) (string, bool) {
	t.Helper()
	data, err := os.ReadFile(s.path(path))
	if errors.Is(err, os.ErrNotExist) {
		return "", false
	}
	if err != nil {
		t.Fatal(err)
	}
	return string(data), true
}

func apply(t *testing.T, s System, want bool) (string, bool) {
	t.Helper()
	state, changed, err := s.Apply(context.Background(), want)
	if err != nil {
		t.Fatalf("Apply(%v): %v", want, err)
	}
	return state, changed
}

func TestUnattendedUpgradesOffAndBackOn(t *testing.T) {
	s := dpkgSystem(t, true)
	if state, changed := apply(t, s, true); state != StateTurnedOff || !changed {
		t.Fatalf("state %q, changed %v", state, changed)
	}
	content, ok := readFile(t, s, AptFile)
	if !ok || !strings.HasPrefix(content, AptMarker+"\n") || !strings.Contains(content, "\nAPT::Periodic::Unattended-Upgrade \"0\";\n") {
		t.Fatalf("file %q", content)
	}
	if state, changed := apply(t, s, true); state != StateTurnedOff || changed {
		t.Fatalf("a second apply: state %q, changed %v", state, changed)
	}
	if state, changed := apply(t, s, false); state != StateUntouched || !changed {
		t.Fatalf("state %q, changed %v", state, changed)
	}
	if _, ok := readFile(t, s, AptFile); ok {
		t.Fatal("the agent's file must be removed when no longer asked")
	}
	entries, _ := os.ReadDir(filepath.Dir(s.path(AptFile)))
	for _, e := range entries {
		if strings.Contains(e.Name(), "fleeto") {
			t.Fatalf("a temporary file was left: %s", e.Name())
		}
	}
}

func TestUnattendedUpgradesNotInstalledIsNone(t *testing.T) {
	s := dpkgSystem(t, false)
	if state, changed := apply(t, s, true); state != StateNone || changed {
		t.Fatalf("state %q, changed %v", state, changed)
	}
	if _, ok := readFile(t, s, AptFile); ok {
		t.Fatal("nothing is written without unattended-upgrades")
	}
	touch(t, s.Root, unattendedUpgradeBinary, "")
	if state, _ := apply(t, s, true); state != StateTurnedOff {
		t.Fatalf("the binary alone counts as installed: %q", state)
	}
}

func TestAForeignFileOfTheSameNameIsNeverTouched(t *testing.T) {
	s := dpkgSystem(t, true)
	foreign := "// my own settings\nAPT::Periodic::Unattended-Upgrade \"1\";\n"
	touch(t, s.Root, AptFile, foreign)
	if state, changed := apply(t, s, true); state != StateKept || changed {
		t.Fatalf("state %q, changed %v", state, changed)
	}
	if state, changed := apply(t, s, false); state != StateUntouched || changed {
		t.Fatalf("state %q, changed %v", state, changed)
	}
	if err := s.Restore(context.Background()); err != nil {
		t.Fatal(err)
	}
	if content, _ := readFile(t, s, AptFile); content != foreign {
		t.Fatalf("the foreign file was changed: %q", content)
	}
}

func TestTheAgentsFileIsRewrittenWhenItsBodyChanged(t *testing.T) {
	s := dpkgSystem(t, true)
	touch(t, s.Root, AptFile, AptMarker+"\r\nAPT::Periodic::Unattended-Upgrade \"1\";\n")
	if state, changed := apply(t, s, true); state != StateTurnedOff || !changed {
		t.Fatalf("state %q, changed %v", state, changed)
	}
	if content, _ := readFile(t, s, AptFile); content != aptBody {
		t.Fatalf("file %q", content)
	}
}

func TestDnfAutomaticTimersOffAndBackOn(t *testing.T) {
	s, fake := rpmSystem(t, map[string]string{
		"dnf-automatic.timer":          "disabled",
		"dnf-automatic-install.timer":  "enabled",
		"dnf-automatic-download.timer": "enabled",
	})
	if state, changed := apply(t, s, true); state != StateTurnedOff || !changed {
		t.Fatalf("state %q, changed %v", state, changed)
	}
	want := []string{"disable --now dnf-automatic-install.timer", "disable --now dnf-automatic-download.timer"}
	if !slices.Equal(fake.calls, want) {
		t.Fatalf("calls %v, want %v", fake.calls, want)
	}
	recorded, ok, err := s.readMarker()
	if err != nil || !ok || !slices.Equal(recorded, []string{"dnf-automatic-install.timer", "dnf-automatic-download.timer"}) {
		t.Fatalf("marker %v %v %v", recorded, ok, err)
	}

	fake.calls = nil
	if state, changed := apply(t, s, true); state != StateTurnedOff || changed || len(fake.calls) != 0 {
		t.Fatalf("a second apply: state %q, changed %v, calls %v", state, changed, fake.calls)
	}

	// The timer that was disabled before Fleeto stays disabled.
	if state, changed := apply(t, s, false); state != StateUntouched || !changed {
		t.Fatalf("state %q, changed %v", state, changed)
	}
	want = []string{"enable --now dnf-automatic-install.timer", "enable --now dnf-automatic-download.timer"}
	if !slices.Equal(fake.calls, want) {
		t.Fatalf("calls %v, want %v", fake.calls, want)
	}
	if fake.states["dnf-automatic.timer"] != "disabled" {
		t.Fatal("a timer the agent did not disable must not be enabled")
	}
	if _, ok, _ := s.readMarker(); ok {
		t.Fatal("the marker must be deleted")
	}
}

func TestDnfAutomaticWithoutTimersIsNoneAndMaskedIsKept(t *testing.T) {
	s, fake := rpmSystem(t, map[string]string{"dnf-automatic.timer": "disabled"})
	if state, changed := apply(t, s, true); state != StateNone || changed || len(fake.calls) != 0 {
		t.Fatalf("state %q, changed %v, calls %v", state, changed, fake.calls)
	}
	fake.states["dnf-automatic-install.timer"] = "masked"
	if state, changed := apply(t, s, true); state != StateKept || changed || len(fake.calls) != 0 {
		t.Fatalf("state %q, changed %v, calls %v", state, changed, fake.calls)
	}
	if _, ok, _ := s.readMarker(); ok {
		t.Fatal("nothing was recorded")
	}
}

func TestARecordedTimerSomebodyElseChangedIsLeftAlone(t *testing.T) {
	s, fake := rpmSystem(t, map[string]string{"dnf-automatic.timer": "enabled", "dnf-automatic-install.timer": "enabled"})
	apply(t, s, true)
	// Somebody masks one timer and enables the other again by hand.
	fake.states["dnf-automatic.timer"] = "masked"
	fake.states["dnf-automatic-install.timer"] = "enabled"
	fake.calls = nil
	if err := s.Restore(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(fake.calls) != 0 {
		t.Fatalf("calls %v", fake.calls)
	}
	if _, ok, _ := s.readMarker(); ok {
		t.Fatal("the marker must be deleted")
	}
}

func TestAFailedEnableKeepsWhatIsLeftInTheMarker(t *testing.T) {
	s, fake := rpmSystem(t, map[string]string{"dnf-automatic.timer": "enabled", "dnf-automatic-install.timer": "enabled"})
	apply(t, s, true)
	fake.fail["enable --now dnf-automatic-install.timer"] = true
	if _, _, err := s.Apply(context.Background(), false); err == nil {
		t.Fatal("a failed enable is an error")
	}
	recorded, _, _ := s.readMarker()
	if !slices.Equal(recorded, []string{"dnf-automatic-install.timer"}) {
		t.Fatalf("marker %v", recorded)
	}
	delete(fake.fail, "enable --now dnf-automatic-install.timer")
	if state, _ := apply(t, s, false); state != StateUntouched || fake.states["dnf-automatic-install.timer"] != "enabled" {
		t.Fatalf("state %q, timers %v", state, fake.states)
	}
}

func TestAFailedDisableIsRecordedFirst(t *testing.T) {
	s, fake := rpmSystem(t, map[string]string{"dnf-automatic.timer": "enabled"})
	fake.fail["disable --now dnf-automatic.timer"] = true
	if _, _, err := s.Apply(context.Background(), true); err == nil {
		t.Fatal("a failed disable is an error")
	}
	if recorded, _, _ := s.readMarker(); !slices.Equal(recorded, []string{"dnf-automatic.timer"}) {
		t.Fatalf("marker %v", recorded)
	}
}

func TestAnUnreadableMarkerDisablesNothingMore(t *testing.T) {
	s, fake := rpmSystem(t, map[string]string{"dnf-automatic.timer": "enabled"})
	if err := os.WriteFile(filepath.Join(s.StateDir, MarkerFile), []byte("{"), 0o600); err != nil {
		t.Fatal(err)
	}
	state, _, err := s.Apply(context.Background(), true)
	if err == nil || state != StateKept || len(fake.calls) != 0 {
		t.Fatalf("state %q, err %v, calls %v", state, err, fake.calls)
	}
	if _, _, err := s.Apply(context.Background(), false); err == nil || len(fake.calls) != 0 {
		t.Fatalf("err %v, calls %v", err, fake.calls)
	}
}

func TestOnlyKnownTimerNamesInTheMarkerCount(t *testing.T) {
	s, fake := rpmSystem(t, map[string]string{"sshd.service": "disabled"})
	if err := os.WriteFile(filepath.Join(s.StateDir, MarkerFile), []byte(`{"timers":["sshd.service","dnf-automatic.timer"]}`), 0o600); err != nil {
		t.Fatal(err)
	}
	fake.states["dnf-automatic.timer"] = "disabled"
	apply(t, s, false)
	if !slices.Equal(fake.calls, []string{"enable --now dnf-automatic.timer"}) {
		t.Fatalf("calls %v", fake.calls)
	}
}

func TestNeitherDpkgNorRpm(t *testing.T) {
	s := System{Root: t.TempDir(), StateDir: t.TempDir(), Runner: &fakeSystemctl{states: map[string]string{}}}
	if state, changed := apply(t, s, true); state != StateNone || changed {
		t.Fatalf("state %q, changed %v", state, changed)
	}
	if state, changed := apply(t, s, false); state != StateUntouched || changed {
		t.Fatalf("state %q, changed %v", state, changed)
	}
	// Without a state directory nothing was recorded, and undoing needs none.
	if _, _, err := (System{Root: s.Root}).Apply(context.Background(), false); err != nil {
		t.Fatal(err)
	}
}
