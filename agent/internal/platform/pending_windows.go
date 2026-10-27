package platform

import (
	"errors"
	"path/filepath"
	"strings"

	"golang.org/x/sys/windows/registry"
)

const (
	sessionManagerKey = `SYSTEM\CurrentControlSet\Control\Session Manager`
	pendingRenames    = "PendingFileRenameOperations"
)

// CancelPendingDeletes removes the deletions Windows would carry out at the next restart for dir and everything below it, and returns
// the paths it removed. An uninstall started from the installed binary schedules its own deletion (MOVEFILE_DELAY_UNTIL_REBOOT); Windows
// keeps that by path, so an agent installed again before the restart would be deleted at it (found on a test endpoint, 0.8.0-alpha.6).
// Renames and the deletions of other paths are left as they are.
func CancelPendingDeletes(dir string) ([]string, error) {
	key, err := registry.OpenKey(registry.LOCAL_MACHINE, sessionManagerKey, registry.QUERY_VALUE|registry.SET_VALUE)
	if err != nil {
		return nil, err
	}
	defer key.Close()
	values, _, err := key.GetStringsValue(pendingRenames)
	if errors.Is(err, registry.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	kept, removed := withoutDeletesBelow(values, dir)
	if len(removed) == 0 {
		return nil, nil
	}
	if len(kept) == 0 {
		return removed, key.DeleteValue(pendingRenames)
	}
	return removed, key.SetStringsValue(pendingRenames, kept)
}

// withoutDeletesBelow splits the pairs of PendingFileRenameOperations (source, destination; an empty destination deletes the source) into
// the pairs to keep and the deleted paths at or below dir. An odd list is kept whole: it is not a list Windows wrote.
func withoutDeletesBelow(values []string, dir string) (kept, removed []string) {
	if len(values)%2 != 0 {
		return values, nil
	}
	base := strings.ToLower(filepath.Clean(dir))
	for i := 0; i < len(values); i += 2 {
		source, target := values[i], values[i+1]
		path := strings.ToLower(filepath.Clean(strings.TrimPrefix(strings.TrimPrefix(source, `\??\`), `*`)))
		if target == "" && (path == base || strings.HasPrefix(path, base+`\`)) {
			removed = append(removed, source)
			continue
		}
		kept = append(kept, source, target)
	}
	return kept, removed
}
