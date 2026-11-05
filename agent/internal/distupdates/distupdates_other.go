//go:build !linux

package distupdates

// Apply does nothing outside Linux and reports no state.
func Apply(string, bool) (string, bool, error) { return "", false, nil }

// Restore does nothing outside Linux.
func Restore(string) error { return nil }
