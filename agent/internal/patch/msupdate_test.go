package patch

import (
	"errors"
	"slices"
	"testing"
)

func TestDecideMicrosoftUpdate(t *testing.T) {
	cases := []struct {
		name    string
		want    bool
		c       muCurrent
		step    muStep
		outcome string
	}{
		{"registers when asked", true, muCurrent{}, muAdd, MicrosoftUpdateRegistered},
		{"registers again when only its marker is left", true, muCurrent{ours: true}, muAdd, MicrosoftUpdateRegistered},
		{"keeps its own registration", true, muCurrent{registered: true, ours: true}, muNone, MicrosoftUpdateRegistered},
		{"leaves a registration of somebody else", true, muCurrent{registered: true}, muNone, MicrosoftUpdateRegistered},
		{"never registers under a WSUS", true, muCurrent{wsus: true}, muNone, MicrosoftUpdateWSUS},
		{"removes its own when a WSUS takes over", true, muCurrent{wsus: true, registered: true, ours: true}, muRemove, MicrosoftUpdateWSUS},
		{"leaves a registration of somebody else under a WSUS", true, muCurrent{wsus: true, registered: true}, muNone, MicrosoftUpdateWSUS},
		{"removes its own when no longer asked", false, muCurrent{registered: true, ours: true}, muRemove, MicrosoftUpdateUntouched},
		{"never removes a registration of somebody else", false, muCurrent{registered: true}, muNone, MicrosoftUpdateUntouched},
		{"forgets a marker without a registration", false, muCurrent{ours: true}, muForget, MicrosoftUpdateUntouched},
		{"does nothing when not asked", false, muCurrent{}, muNone, MicrosoftUpdateUntouched},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			step, outcome := decideMicrosoftUpdate(tc.want, tc.c)
			if step != tc.step || outcome != tc.outcome {
				t.Fatalf("decide(%v, %+v) = (%v, %q), want (%v, %q)", tc.want, tc.c, step, outcome, tc.step, tc.outcome)
			}
		})
	}
}

// fakeMU is a Windows Update Agent and registry that record the changes.
type fakeMU struct {
	wsus, registered, ours bool
	addErr, registeredErr  error
	calls                  []string
}

func (f *fakeMU) WSUS() bool { f.calls = append(f.calls, "wsus"); return f.wsus }
func (f *fakeMU) Registered() (bool, error) {
	f.calls = append(f.calls, "registered")
	return f.registered, f.registeredErr
}
func (f *fakeMU) Ours() (bool, error) { return f.ours, nil }
func (f *fakeMU) SetOurs() error      { f.calls = append(f.calls, "set-marker"); f.ours = true; return nil }
func (f *fakeMU) ClearOurs() error {
	f.calls = append(f.calls, "clear-marker")
	f.ours = false
	return nil
}
func (f *fakeMU) Add() error {
	f.calls = append(f.calls, "add")
	if f.addErr != nil {
		return f.addErr
	}
	f.registered = true
	return nil
}
func (f *fakeMU) Remove() error {
	f.calls = append(f.calls, "remove")
	f.registered = false
	return nil
}

func TestApplyMicrosoftUpdateRegistersAndRemovesOnlyItsOwn(t *testing.T) {
	f := &fakeMU{}
	outcome, changed, err := applyMicrosoftUpdate(f, true)
	if err != nil || outcome != MicrosoftUpdateRegistered || !changed || !f.registered || !f.ours {
		t.Fatalf("(%q, %v, %v), %+v", outcome, changed, err, f)
	}
	if !slices.Equal(f.calls, []string{"wsus", "registered", "set-marker", "add"}) {
		t.Fatalf("the marker is written before the registration: %v", f.calls)
	}
	f.calls = nil
	if _, changed, _ := applyMicrosoftUpdate(f, true); changed || slices.Contains(f.calls, "add") {
		t.Fatalf("a second apply changes nothing: %v", f.calls)
	}
	f.calls = nil
	outcome, changed, err = applyMicrosoftUpdate(f, false)
	if err != nil || outcome != MicrosoftUpdateUntouched || !changed || f.registered || f.ours {
		t.Fatalf("(%q, %v, %v), %+v", outcome, changed, err, f)
	}
	if !slices.Equal(f.calls, []string{"wsus", "registered", "remove", "clear-marker"}) {
		t.Fatalf("calls %v", f.calls)
	}
}

func TestApplyMicrosoftUpdateLooksAtNothingWhenNotAskedAndNotItsOwn(t *testing.T) {
	f := &fakeMU{registered: true}
	outcome, changed, err := applyMicrosoftUpdate(f, false)
	if err != nil || outcome != MicrosoftUpdateUntouched || changed || len(f.calls) != 0 || !f.registered {
		t.Fatalf("(%q, %v, %v), calls %v", outcome, changed, err, f.calls)
	}
}

func TestApplyMicrosoftUpdateUnderAWSUSRegistersNothing(t *testing.T) {
	f := &fakeMU{wsus: true}
	outcome, changed, err := applyMicrosoftUpdate(f, true)
	if err != nil || outcome != MicrosoftUpdateWSUS || changed || f.registered || slices.Contains(f.calls, "registered") {
		t.Fatalf("(%q, %v, %v), calls %v", outcome, changed, err, f.calls)
	}
}

func TestApplyMicrosoftUpdateKeepsItsMarkerWhenTheRegistrationFails(t *testing.T) {
	f := &fakeMU{addErr: errors.New("0x8024401C")}
	if _, _, err := applyMicrosoftUpdate(f, true); err == nil || !f.ours {
		t.Fatalf("err %v, marker %v", err, f.ours)
	}
	if _, _, err := applyMicrosoftUpdate(&fakeMU{registeredErr: errors.New("no")}, true); err == nil {
		t.Fatal("a service list that cannot be read is an error, never a registration")
	}
}
