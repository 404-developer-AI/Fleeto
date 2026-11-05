package patch

import "fmt"

// Registering Microsoft Update (0.9.0, PATCHING.md section 4 and section 14 decision 15). With AgentConfig.microsoft_update the
// agent registers the Microsoft Update service with the Windows Update Agent when it applies the configuration, so the scans and
// installations also find Office, SQL Server and .NET updates. A scan never registers anything. A marker under HKLM\SOFTWARE\Fleeto
// records that the agent registered it, so the agent removes only its own registration: when the configuration no longer asks for
// it, the endpoint becomes agent-only, a WSUS takes over or the agent is uninstalled. A registration somebody else made is never
// removed. This file holds the decision, tested on every platform; msupdate_windows.go makes the COM and registry calls.

// The outcome of bringing the registration in line, for the agent log.
const (
	// MicrosoftUpdateRegistered: the configuration asks for it and the service is registered (by the agent or before it).
	MicrosoftUpdateRegistered = "registered"
	// MicrosoftUpdateWSUS: the configuration asks for it, but a WSUS is configured, so nothing is registered.
	MicrosoftUpdateWSUS = "wsus"
	// MicrosoftUpdateUntouched: the configuration does not ask for it; the agent removed only its own registration.
	MicrosoftUpdateUntouched = "untouched"
)

// muStep is the change to make.
type muStep int

const (
	muNone muStep = iota
	// muAdd writes the marker, then registers the service (AddService2).
	muAdd
	// muRemove removes the service (RemoveService), then the marker.
	muRemove
	// muForget deletes only the marker: the service is no longer registered.
	muForget
)

// muCurrent is what the endpoint holds.
type muCurrent struct {
	// wsus: a WSUS is configured by group policy (WUServer with AU\UseWUServer = 1).
	wsus bool
	// registered: the Microsoft Update service is registered, whoever registered it.
	registered bool
	// ours: the marker says the agent registered it.
	ours bool
}

// decideMicrosoftUpdate returns the change to make and the outcome. want is microsoft_update of a managed configuration of an
// endpoint Fleeto patches (patch_scan_interval_hours above 0).
func decideMicrosoftUpdate(want bool, c muCurrent) (muStep, string) {
	if want && !c.wsus {
		if c.registered {
			// Registered, by the agent or by somebody else: nothing to do; a registration of somebody else stays theirs.
			return muNone, MicrosoftUpdateRegistered
		}
		// Not registered, also when only the marker is left (somebody removed it while the configuration asks for it).
		return muAdd, MicrosoftUpdateRegistered
	}
	outcome := MicrosoftUpdateUntouched
	if want {
		outcome = MicrosoftUpdateWSUS
	}
	switch {
	case !c.ours:
		return muNone, outcome
	case c.registered:
		return muRemove, outcome
	default:
		return muForget, outcome
	}
}

// muSystem is the registration and the marker on the endpoint.
type muSystem interface {
	// WSUS reports whether a WSUS is configured by group policy.
	WSUS() bool
	// Registered reports whether the Microsoft Update service is registered.
	Registered() (bool, error)
	// Ours reads the marker.
	Ours() (bool, error)
	SetOurs() error
	ClearOurs() error
	// Add registers the service (IUpdateServiceManager2.AddService2 with flags 7); Remove removes it (RemoveService).
	Add() error
	Remove() error
}

// applyMicrosoftUpdate brings the registration in line with want and returns the outcome and whether it changed anything. The
// service list is only read when it matters: when the configuration asks for it or the marker says the agent registered it.
func applyMicrosoftUpdate(sys muSystem, want bool) (string, bool, error) {
	ours, err := sys.Ours()
	if err != nil {
		return "", false, fmt.Errorf("read whether the agent registered Microsoft Update: %w", err)
	}
	c := muCurrent{ours: ours}
	if !want && !ours {
		return MicrosoftUpdateUntouched, false, nil
	}
	c.wsus = sys.WSUS()
	if want && !c.wsus || ours {
		if c.registered, err = sys.Registered(); err != nil {
			return "", false, fmt.Errorf("read the services of the Windows Update Agent: %w", err)
		}
	}
	step, outcome := decideMicrosoftUpdate(want, c)
	switch step {
	case muAdd:
		// The marker first: when the registration fails after it, the next run finds the marker without the service and tries again,
		// and an uninstall in between removes nothing that is not registered.
		if err := sys.SetOurs(); err != nil {
			return "", false, fmt.Errorf("record that the agent registers Microsoft Update: %w", err)
		}
		if err := sys.Add(); err != nil {
			return "", false, fmt.Errorf("register Microsoft Update: %w", err)
		}
	case muRemove:
		if err := sys.Remove(); err != nil {
			return "", false, fmt.Errorf("remove the Microsoft Update registration of the agent: %w", err)
		}
		if err := sys.ClearOurs(); err != nil {
			return "", false, fmt.Errorf("forget that the agent registered Microsoft Update: %w", err)
		}
	case muForget:
		if err := sys.ClearOurs(); err != nil {
			return "", false, fmt.Errorf("forget that the agent registered Microsoft Update: %w", err)
		}
	}
	return outcome, step != muNone, nil
}
