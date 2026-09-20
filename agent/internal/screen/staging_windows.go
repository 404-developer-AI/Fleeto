//go:build windows

package screen

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"unsafe"

	"golang.org/x/sys/windows"
)

// The folders of files pasted into remote control sessions on Windows (security review of 0.3.0 step 7). C:\ProgramData\Fleeto inherits
// the ProgramData access list, in which every user may create folders; a user who planted a junction where the agent (SYSTEM) creates a
// staging folder could make it write pasted files anywhere. So the base folder is owned by Administrators with a protected access list,
// and every staging folder is created in one step with its own access list: nobody can put anything in its place.

// baseSDDL: SYSTEM and Administrators in full, users may read (nothing of theirs is created here).
const baseSDDL = "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)"

// rootSDDL: the staging root, SYSTEM and Administrators only; each session folder below it adds its user. The agent creates them, so it
// owns them already.
const rootSDDL = "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"

// PrepareStaging secures the base folder and makes an empty staging root when the agent starts. Files left from sessions that did not end
// cleanly are deleted. On an error files cannot be pasted, and the agent says so.
func PrepareStaging(root string) error {
	base := filepath.Dir(root)
	if err := os.MkdirAll(base, 0o700); err != nil {
		return err
	}
	if err := secureFolder(base, baseSDDL); err != nil {
		return fmt.Errorf("protect %s: %w", base, err)
	}
	// RemoveAll removes a link itself, never what it points to.
	if err := os.RemoveAll(root); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return createFolder(root, rootSDDL)
}

// secureFolder sets the owner and a protected access list through a handle to the folder itself: a folder that is a link is refused, so
// the access list of another folder is never changed.
func secureFolder(dir, sddl string) error {
	name, err := windows.UTF16PtrFromString(dir)
	if err != nil {
		return err
	}
	h, err := windows.CreateFile(name, windows.READ_CONTROL|windows.WRITE_DAC|windows.WRITE_OWNER, windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE,
		nil, windows.OPEN_EXISTING, windows.FILE_FLAG_BACKUP_SEMANTICS|windows.FILE_FLAG_OPEN_REPARSE_POINT, 0)
	if err != nil {
		return err
	}
	defer windows.CloseHandle(h)
	var info windows.ByHandleFileInformation
	if err := windows.GetFileInformationByHandle(h, &info); err != nil {
		return err
	}
	if info.FileAttributes&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0 || info.FileAttributes&windows.FILE_ATTRIBUTE_DIRECTORY == 0 {
		return errors.New("it is a link or not a folder")
	}
	sd, err := windows.SecurityDescriptorFromString(sddl)
	if err != nil {
		return err
	}
	owner, _, err := sd.Owner()
	if err != nil {
		return err
	}
	dacl, _, err := sd.DACL()
	if err != nil {
		return err
	}
	return windows.SetSecurityInfo(h, windows.SE_FILE_OBJECT,
		windows.OWNER_SECURITY_INFORMATION|windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION, owner, nil, dacl, nil)
}

// createFolder creates a folder with its access list in one step; it fails when anything has the name already.
func createFolder(dir, sddl string) error {
	sd, err := windows.SecurityDescriptorFromString(sddl)
	if err != nil {
		return err
	}
	attributes := &windows.SecurityAttributes{SecurityDescriptor: sd}
	attributes.Length = uint32(unsafe.Sizeof(*attributes))
	name, err := windows.UTF16PtrFromString(dir)
	if err != nil {
		return err
	}
	if err := windows.CreateDirectory(name, attributes); err != nil {
		return &os.PathError{Op: "create", Path: dir, Err: err}
	}
	return nil
}
