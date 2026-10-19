package winupdate

import "testing"

func TestDecide(t *testing.T) {
	missing := current{}
	ours := current{valueSet: true, value: 1, ours: true}
	policyOn := current{valueSet: true, value: 1}
	policyOff := current{valueSet: true, value: 0}
	changed := current{valueSet: true, value: 0, ours: true}
	markerOnly := current{ours: true}

	cases := []struct {
		name          string
		want, action1 bool
		c             current
		step          step
		state         string
	}{
		{"turns off when the value is missing", true, true, missing, stepSet, StateTurnedOff},
		{"keeps its own value", true, true, ours, stepNone, StateTurnedOff},
		{"sets the value again when only its marker is left", true, true, markerOnly, stepSet, StateTurnedOff},
		{"leaves a group policy that turns it off", true, true, policyOn, stepNone, StatePolicy},
		{"leaves a group policy that keeps it on", true, true, policyOff, stepNone, StatePolicy},
		{"gives way when somebody changed its value", true, true, changed, stepForget, StatePolicy},
		{"never turns off without the Action1 agent", true, false, missing, stepNone, StateNoAction1},
		{"turns back on when the Action1 agent is gone", true, false, ours, stepRemove, StateNoAction1},
		{"turns back on when no longer asked", false, true, ours, stepRemove, StateUntouched},
		{"turns back on for the uninstall", false, false, ours, stepRemove, StateUntouched},
		{"never removes a group policy value", false, true, policyOn, stepNone, StateUntouched},
		{"forgets a value somebody changed", false, true, changed, stepForget, StateUntouched},
		{"forgets a marker without a value", false, false, markerOnly, stepForget, StateUntouched},
		{"does nothing when not asked", false, false, missing, stepNone, StateUntouched},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			step, state := decide(tc.want, tc.action1, tc.c)
			if step != tc.step || state != tc.state {
				t.Fatalf("decide(%v, %v, %+v) = (%v, %q), want (%v, %q)", tc.want, tc.action1, tc.c, step, state, tc.step, tc.state)
			}
		})
	}
}
