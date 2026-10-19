//go:build windows

package winupdate

import (
	"errors"
	"fmt"

	"golang.org/x/sys/windows/registry"
)

// keys are the registry locations; tests point them at HKEY_CURRENT_USER.
type keys struct {
	root        registry.Key
	policyPath  string
	policyValue string
	markerPath  string
	markerValue string
}

var machine = keys{
	root:        registry.LOCAL_MACHINE,
	policyPath:  `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU`,
	policyValue: "NoAutoUpdate",
	markerPath:  `SOFTWARE\Fleeto`,
	markerValue: "WindowsUpdateTurnedOff",
}

// Apply brings automatic Windows Update in line with the configuration and returns the state to report and whether it changed
// anything. want is the switch of the signed configuration (false for an agent-only endpoint), action1 whether the Action1 agent
// is installed. It is cheap and idempotent, so the agent calls it with every inventory.
func Apply(want, action1 bool) (string, bool, error) {
	return machine.apply(want, action1)
}

// Restore removes the value the agent set, if it is still the one it set, and the key of its marker: for the uninstall of the agent.
func Restore() error {
	if _, _, err := machine.apply(false, false); err != nil {
		return err
	}
	if err := registry.DeleteKey(machine.root, machine.markerPath); err != nil && !errors.Is(err, registry.ErrNotExist) {
		return fmt.Errorf("delete the registry key of the agent: %w", err)
	}
	return nil
}

func (k keys) apply(want, action1 bool) (string, bool, error) {
	c, err := k.read()
	if err != nil {
		return "", false, err
	}
	next, state := decide(want, action1, c)
	switch next {
	case stepSet:
		// The marker first: when the value cannot be written after it, the next run finds the marker without a value and tries again.
		if err := k.setMarker(); err != nil {
			return "", false, err
		}
		if err := k.setPolicy(); err != nil {
			return "", false, err
		}
	case stepRemove:
		if err := k.deletePolicy(); err != nil {
			return "", false, err
		}
		if err := k.deleteMarker(); err != nil {
			return "", false, err
		}
	case stepForget:
		if err := k.deleteMarker(); err != nil {
			return "", false, err
		}
	}
	return state, next != stepNone, nil
}

func (k keys) read() (current, error) {
	var c current
	value, set, err := readInteger(k.root, k.policyPath, k.policyValue)
	if err != nil {
		return c, fmt.Errorf("read the Windows Update group policy: %w", err)
	}
	c.valueSet, c.value = set, value
	marker, _, err := readInteger(k.root, k.markerPath, k.markerValue)
	if err != nil {
		return c, fmt.Errorf("read what the agent changed in Windows Update: %w", err)
	}
	c.ours = marker == 1
	return c, nil
}

// readInteger returns a DWORD or QWORD value and whether it exists; a value of another type exists with value 0.
func readInteger(root registry.Key, path, name string) (uint64, bool, error) {
	key, err := registry.OpenKey(root, path, registry.QUERY_VALUE|registry.WOW64_64KEY)
	if errors.Is(err, registry.ErrNotExist) {
		return 0, false, nil
	}
	if err != nil {
		return 0, false, err
	}
	defer key.Close()
	value, _, err := key.GetIntegerValue(name)
	switch {
	case errors.Is(err, registry.ErrNotExist):
		return 0, false, nil
	case errors.Is(err, registry.ErrUnexpectedType):
		return 0, true, nil
	case err != nil:
		return 0, false, err
	}
	return value, true, nil
}

func (k keys) setPolicy() error {
	return setDWord(k.root, k.policyPath, k.policyValue, "turn automatic Windows Update off")
}

func (k keys) setMarker() error {
	return setDWord(k.root, k.markerPath, k.markerValue, "record that the agent turns Windows Update off")
}

func (k keys) deletePolicy() error {
	return deleteValue(k.root, k.policyPath, k.policyValue, "turn automatic Windows Update back on")
}

func (k keys) deleteMarker() error {
	return deleteValue(k.root, k.markerPath, k.markerValue, "forget that the agent turned Windows Update off")
}

func setDWord(root registry.Key, path, name, what string) error {
	key, _, err := registry.CreateKey(root, path, registry.SET_VALUE|registry.WOW64_64KEY)
	if err != nil {
		return fmt.Errorf("%s: %w", what, err)
	}
	defer key.Close()
	if err := key.SetDWordValue(name, 1); err != nil {
		return fmt.Errorf("%s: %w", what, err)
	}
	return nil
}

// deleteValue removes a value and leaves its key: other values, of a group policy for one, may live in it.
func deleteValue(root registry.Key, path, name, what string) error {
	key, err := registry.OpenKey(root, path, registry.SET_VALUE|registry.WOW64_64KEY)
	if errors.Is(err, registry.ErrNotExist) {
		return nil
	}
	if err != nil {
		return fmt.Errorf("%s: %w", what, err)
	}
	defer key.Close()
	if err := key.DeleteValue(name); err != nil && !errors.Is(err, registry.ErrNotExist) {
		return fmt.Errorf("%s: %w", what, err)
	}
	return nil
}
