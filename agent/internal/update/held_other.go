//go:build !windows

package update

import "os"

// openHeld opens a staged binary for reading. Outside Windows a file cannot be held against writes; its directory is root's alone.
func openHeld(path string) (*os.File, error) {
	return os.Open(path)
}
