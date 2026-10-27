//go:build !windows

package platform

// CancelPendingDeletes has nothing to do outside Windows: a Linux uninstall deletes its files at once.
func CancelPendingDeletes(string) ([]string, error) { return nil, nil }
