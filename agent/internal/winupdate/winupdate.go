// Package winupdate turns automatic Windows Update off when the signed configuration asks for it (0.7.0), so only the rounds
// of the patch policy in Action1 install updates. Action1 offers this only in its console, so the agent does it itself.
//
// It sets the Windows Update group policy value NoAutoUpdate to 1 under HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU,
// and only when that value is missing: a value somebody else set, a group policy above all, wins and is never changed. A marker
// under HKLM\SOFTWARE\Fleeto records that the agent set it, so the agent removes only its own value when the configuration no
// longer asks for it, the Action1 agent is gone or the Fleeto agent is uninstalled. The server sends the switch only for a
// managed Windows endpoint that Action1 reports; the agent checks again that the Action1 agent is installed, so an endpoint is
// never left without any updates.
package winupdate

// The states reported in Inventory.windows_update; the server knows the same values (WindowsUpdateRules).
const (
	// StateTurnedOff: the agent turned automatic Windows Update off.
	StateTurnedOff = "turned_off"
	// StatePolicy: the configuration asks for it, but a group policy (or somebody else) sets Windows Update, so the agent left it.
	StatePolicy = "policy"
	// StateNoAction1: the configuration asks for it, but the Action1 agent is not installed, so Windows Update stays on.
	StateNoAction1 = "no_action1"
	// StateUntouched: the configuration does not ask for it; the agent left Windows Update as it is and undid its own change.
	StateUntouched = "untouched"
)

// current is what the endpoint holds.
type current struct {
	// valueSet is true when NoAutoUpdate exists, whoever set it.
	valueSet bool
	// value is NoAutoUpdate; 0 when it is not a number.
	value uint64
	// ours is true when the marker says the agent set NoAutoUpdate.
	ours bool
}

// step is the change to make.
type step int

const (
	stepNone step = iota
	// stepSet writes the marker, then NoAutoUpdate = 1.
	stepSet
	// stepRemove deletes NoAutoUpdate, then the marker.
	stepRemove
	// stepForget deletes only the marker: the value is no longer the one the agent set.
	stepForget
)

// decide returns the change to make and the state to report. want is the switch of the signed configuration, action1 whether the
// Action1 agent is installed.
func decide(want, action1 bool, c current) (step, string) {
	if want && action1 {
		switch {
		case !c.valueSet:
			return stepSet, StateTurnedOff
		case c.ours && c.value == 1:
			return stepNone, StateTurnedOff
		case c.ours:
			// Somebody changed the value the agent set, a group policy most likely: it wins.
			return stepForget, StatePolicy
		default:
			return stepNone, StatePolicy
		}
	}
	state := StateUntouched
	if want {
		state = StateNoAction1
	}
	if !c.ours {
		return stepNone, state
	}
	if c.valueSet && c.value == 1 {
		return stepRemove, state
	}
	return stepForget, state
}
