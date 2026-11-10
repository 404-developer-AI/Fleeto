package remote

import (
	"context"
	"errors"
	"fmt"
	"io"
)

// Folders opened without following links (security review of 0.9.0). The watchdog changes files as SYSTEM or root in folders a
// local user may be able to write in. A path is therefore never handed to the operating system as a whole: every component is opened on
// its own, relative to the folder before it, and a component that is a link (a junction or symbolic link on Windows, a symbolic link on
// Linux) is refused. Every change is then made relative to the handle of the verified folder, so a link planted or swapped in after the
// check cannot redirect it. The technician still works with links: the listing marks them and names where they point, and the
// technician opens that folder instead.

// linkError reports a path component that is a link.
type linkError struct{ path string }

func (e *linkError) Error() string {
	return fmt.Sprintf("%s is a link (a junction or symbolic link), which a local user could point elsewhere; open the folder it points to and try again there", e.path)
}

// errNotDir reports a path component that is a file where a folder was needed.
var errNotDir = errors.New("a part of the path is not a folder")

// removeAll removes a file, a link (the link itself, never what it points to) or a folder with everything in it.
func (d *safeDir) removeAll(ctx context.Context, name string) error {
	sub, err := d.openChild(name)
	var le *linkError
	switch {
	case err == nil:
		names, err := sub.names()
		if err == nil {
			for _, n := range names {
				if ctx.Err() != nil {
					err = ctx.Err()
					break
				}
				if err = sub.removeAll(ctx, n); err != nil {
					break
				}
			}
		}
		sub.close()
		if err != nil {
			return err
		}
	case errors.As(err, &le), errors.Is(err, errNotDir):
		// Not a folder of its own: removed as it is.
	default:
		return err
	}
	return d.remove(name)
}

// copyEntry copies srcName in src to destName in dest, which must not exist yet. Folders are created new and filled through their own
// handles; links, devices and sockets inside a folder are skipped, as before. A link as the item itself is refused.
func copyEntry(ctx context.Context, src *safeDir, srcName string, dest *safeDir, destName string, top bool) error {
	if ctx.Err() != nil {
		return ctx.Err()
	}
	sub, err := src.openChild(srcName)
	var le *linkError
	switch {
	case err == nil:
		defer sub.close()
		made, err := dest.mkdir(destName)
		if err != nil {
			return err
		}
		defer made.close()
		names, err := sub.names()
		if err != nil {
			return err
		}
		for _, n := range names {
			if err := copyEntry(ctx, sub, n, made, n, false); err != nil {
				return err
			}
		}
		return nil
	case errors.Is(err, errNotDir):
		in, err := src.openRead(srcName)
		if err != nil {
			if !top && errors.Is(err, errNotPlain) {
				return nil // skip devices, sockets and pipes
			}
			return err
		}
		defer in.Close()
		info, err := in.Stat()
		if err != nil {
			return err
		}
		out, err := dest.create(destName, info.Mode().Perm())
		if err != nil {
			return err
		}
		if _, err := io.Copy(out, in); err != nil {
			dest.discardCreated(out, destName)
			return err
		}
		return out.Close()
	case errors.As(err, &le):
		if !top {
			return nil // skip links inside a copied folder
		}
		return err
	default:
		return err
	}
}

// errNotPlain reports an item that is not a regular file (a device, socket, pipe or link).
var errNotPlain = errors.New("only a regular file can be copied")
