//go:build !windows && !linux

package inventory

// timeZone is not read on other platforms; the server then uses UTC.
func timeZone() string { return "" }
