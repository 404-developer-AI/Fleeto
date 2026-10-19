//go:build !windows

package winupdate

// Apply does nothing outside Windows and reports no state.
func Apply(bool, bool) (string, bool, error) { return "", false, nil }

// Restore does nothing outside Windows.
func Restore() error { return nil }
