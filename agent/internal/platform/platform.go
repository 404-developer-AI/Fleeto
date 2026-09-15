// Package platform contains operating-system specific paths, privilege checks and file protection.
package platform

import (
	"fmt"
	"os"
	"path/filepath"
	"runtime"
)

// Access says who may read a protected directory or file.
type Access int

const (
	// AccessSystem limits access to SYSTEM and Administrators on Windows, root on other systems (service mode).
	AccessSystem Access = iota
	// AccessCurrentUser limits access to the user running the process (foreground development mode).
	AccessCurrentUser
)

// EnsureProtectedDir creates dir when needed and restricts it to the given access. Files created inside inherit it.
func EnsureProtectedDir(dir string, access Access) error {
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return fmt.Errorf("create directory %s: %w", dir, err)
	}
	return protect(dir, access, true)
}

// ProtectFile restricts an existing file to the given access.
func ProtectFile(path string, access Access) error {
	return protect(path, access, false)
}

// ProtectExecutable restricts an existing program file to the given access and keeps it executable for its owner (0700 outside
// Windows, where ProtectFile would leave 0600 and the program could not run).
func ProtectExecutable(path string, access Access) error {
	if err := protect(path, access, false); err != nil {
		return err
	}
	if runtime.GOOS == "windows" {
		return nil
	}
	if err := os.Chmod(path, 0o700); err != nil {
		return fmt.Errorf("make %s executable: %w", path, err)
	}
	return nil
}

// WriteFileAtomic writes data to a temporary file in the same directory, flushes it and renames it over path, so a
// crash or power loss leaves either the old or the new content, never a partial file.
func WriteFileAtomic(path string, data []byte, access Access) error {
	dir := filepath.Dir(path)
	tmp, err := os.CreateTemp(dir, "."+filepath.Base(path)+".tmp-*")
	if err != nil {
		return fmt.Errorf("create temporary file in %s: %w", dir, err)
	}
	tmpName := tmp.Name()
	cleanup := func() { _ = os.Remove(tmpName) }
	if _, err := tmp.Write(data); err != nil {
		_ = tmp.Close()
		cleanup()
		return fmt.Errorf("write %s: %w", tmpName, err)
	}
	if err := tmp.Sync(); err != nil {
		_ = tmp.Close()
		cleanup()
		return fmt.Errorf("flush %s: %w", tmpName, err)
	}
	if err := tmp.Close(); err != nil {
		cleanup()
		return fmt.Errorf("close %s: %w", tmpName, err)
	}
	if err := protect(tmpName, access, false); err != nil {
		cleanup()
		return err
	}
	if err := os.Rename(tmpName, path); err != nil {
		cleanup()
		return fmt.Errorf("replace %s: %w", path, err)
	}
	return nil
}
