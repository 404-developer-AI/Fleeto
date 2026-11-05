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
		name                  string
		want, action1, fleeto bool
		c                     current
		step                  step
		state                 string
	}{
		{"turns off when the value is missing", true, true, false, missing, stepSet, StateTurnedOff},
		{"keeps its own value", true, true, false, ours, stepNone, StateTurnedOff},
		{"sets the value again when only its marker is left", true, true, false, markerOnly, stepSet, StateTurnedOff},
		{"leaves a group policy that turns it off", true, true, false, policyOn, stepNone, StatePolicy},
		{"leaves a group policy that keeps it on", true, true, false, policyOff, stepNone, StatePolicy},
		{"gives way when somebody changed its value", true, true, false, changed, stepForget, StatePolicy},
		{"never turns off without the Action1 agent or Fleeto", true, false, false, missing, stepNone, StateNoAction1},
		{"turns back on when the Action1 agent is gone", true, false, false, ours, stepRemove, StateNoAction1},
		{"turns back on when no longer asked", false, true, false, ours, stepRemove, StateUntouched},
		{"turns back on for the uninstall", false, false, false, ours, stepRemove, StateUntouched},
		{"never removes a group policy value", false, true, false, policyOn, stepNone, StateUntouched},
		{"forgets a value somebody changed", false, true, false, changed, stepForget, StateUntouched},
		{"forgets a marker without a value", false, false, false, markerOnly, stepForget, StateUntouched},
		{"does nothing when not asked", false, false, false, missing, stepNone, StateUntouched},
		{"turns off when Fleeto patches the endpoint", true, false, true, missing, stepSet, StateTurnedOff},
		{"keeps its own value while Fleeto patches the endpoint", true, false, true, ours, stepNone, StateTurnedOff},
		{"leaves a group policy while Fleeto patches the endpoint", true, false, true, policyOff, stepNone, StatePolicy},
		{"turns back on when Fleeto stops patching the endpoint", true, false, false, ours, stepRemove, StateNoAction1},
		{"turns back on when no longer asked while Fleeto patches", false, false, true, ours, stepRemove, StateUntouched},
		{"never turns off unasked while Fleeto patches", false, false, true, missing, stepNone, StateUntouched},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			step, state := decide(tc.want, tc.action1, tc.fleeto, tc.c)
			if step != tc.step || state != tc.state {
				t.Fatalf("decide(%v, %v, %v, %+v) = (%v, %q), want (%v, %q)", tc.want, tc.action1, tc.fleeto, tc.c, step, state,
					tc.step, tc.state)
			}
		})
	}
}
