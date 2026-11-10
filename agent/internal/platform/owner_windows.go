//go:build windows

package platform

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"unsafe"

	"golang.org/x/sys/windows"
)

// The directories of the services live in C:\ProgramData, where every user may create folders (security audit of 0.9.0). Whoever creates
// a folder owns it, and an owner can always change its access list again, whatever that list says. A user who created
// C:\ProgramData\Fleeto\Agent, the Watchdog folder or an updates folder in it before the install would so keep control over the state
// (the pinned trust) and the binaries SYSTEM runs from there. So a directory for AccessSystem must be owned by SYSTEM or Administrators:
// a new one is created with its access list in one step, an existing one owned by somebody else is taken over with everything in it, and
// a link is never followed.

// systemDirSDDL is the protected access list of a directory for AccessSystem, inherited by what is created in it.
const systemDirSDDL = "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"

// systemFileSDDL is the protected access list of a file that is taken over.
const systemFileSDDL = "D:P(A;;FA;;;SY)(A;;FA;;;BA)"

// dataDirSDDL is the protected access list of C:\ProgramData\Fleeto: SYSTEM and Administrators in full, users may read and pass through
// (the staging folders of jobs and remote control sessions below it give a user access of their own).
const dataDirSDDL = "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)"

// errLink: the path is a link (junction or symbolic link), which a service never follows into a directory it protects.
var errLink = errors.New("it is a link, not a folder")

func ensureProtectedDir(dir string, access Access) error {
	if access != AccessSystem {
		if err := os.MkdirAll(dir, 0o700); err != nil {
			return fmt.Errorf("create directory %s: %w", dir, err)
		}
		return protect(dir, access, true)
	}
	dir = filepath.Clean(dir)
	// The folder that holds the agent and watchdog directories: whoever controls it can rename them away and put others in their place.
	if parent := filepath.Dir(dir); strings.EqualFold(parent, filepath.Clean(DataDir())) {
		if err := ProtectDataDir(); err != nil {
			return err
		}
	}
	if err := os.MkdirAll(filepath.Dir(dir), 0o755); err != nil {
		return fmt.Errorf("create directory %s: %w", filepath.Dir(dir), err)
	}
	// A new directory gets its access list as it is created, so nothing can be put in it before it is protected.
	if err := createDirectory(dir, systemDirSDDL); err != nil && !errors.Is(err, windows.ERROR_ALREADY_EXISTS) {
		return fmt.Errorf("create directory %s: %w", dir, err)
	}
	return secureSystemDir(dir, systemDirSDDL, true)
}

// ProtectDataDir creates C:\ProgramData\Fleeto when needed, makes Administrators its owner and gives it a protected access list in which
// users may only read. Called before the agent or watchdog directory below it is protected; the install calls it too.
func ProtectDataDir() error {
	dir := DataDir()
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return fmt.Errorf("create directory %s: %w", dir, err)
	}
	return secureSystemDir(dir, dataDirSDDL, false)
}

// secureSystemDir gives an existing directory the access list sddl. A directory owned by SYSTEM or Administrators only gets the access
// list (propagated to what inherits it, as before). One owned by anybody else is taken over: Administrators become its owner and, with
// takeOverContent, everything in it is given to Administrators with a protected access list too, so nothing the creator put in it keeps
// access of its own. A link is refused, also inside, and so is a directory the process cannot take over.
func secureSystemDir(dir, sddl string, takeOverContent bool) error {
	info, owner, err := inspect(dir)
	if err != nil && !errors.Is(err, windows.ERROR_ACCESS_DENIED) {
		return fmt.Errorf("protect %s: %w", dir, err)
	}
	if err == nil {
		if info.FileAttributes&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0 {
			return fmt.Errorf("protect %s: %w", dir, errLink)
		}
		if info.FileAttributes&windows.FILE_ATTRIBUTE_DIRECTORY == 0 {
			return fmt.Errorf("protect %s: it is not a folder", dir)
		}
		if TrustedOwner(owner) {
			return setNamedDACL(dir, sddl)
		}
	}
	// Somebody else created it (or keeps even SYSTEM out of it): take it over.
	restore := enableOwnershipPrivileges()
	defer restore()
	administrators, err := windows.CreateWellKnownSid(windows.WinBuiltinAdministratorsSid)
	if err != nil {
		return err
	}
	if _, err := takeOver(dir, administrators, sddl, sddl, true); err != nil {
		return fmt.Errorf("%s was created by another account and cannot be taken over (%w). Remove it, then run the command again as administrator", dir, err)
	}
	if takeOverContent {
		if err := takeOverTree(dir, administrators, systemDirSDDL, systemFileSDDL); err != nil {
			return fmt.Errorf("%s was created by another account and what is in it cannot be taken over (%w). Remove it, then run the command again as administrator", dir, err)
		}
	}
	return nil
}

// TrustedOwner reports whether sid may own a directory of a service: SYSTEM or the built-in Administrators group.
func TrustedOwner(sid *windows.SID) bool {
	return sid != nil && (sid.IsWellKnown(windows.WinLocalSystemSid) || sid.IsWellKnown(windows.WinBuiltinAdministratorsSid))
}

// CheckSystemOwned returns an error unless path exists, is no link and is owned by SYSTEM or Administrators. For what a service takes
// from a location it did not create itself, such as the state of an agent from before the rename.
func CheckSystemOwned(path string) error {
	info, owner, err := inspect(path)
	if err != nil {
		return err
	}
	if info.FileAttributes&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0 {
		return fmt.Errorf("%s: %w", path, errLink)
	}
	if !TrustedOwner(owner) {
		name := owner.String()
		if account, domain, _, err := owner.LookupAccount(""); err == nil {
			name = domain + `\` + account
		}
		return fmt.Errorf("%s is owned by %s, not by SYSTEM or Administrators", path, name)
	}
	return nil
}

// inspect reads the attributes and owner of path itself, never of what a link points to.
func inspect(path string) (windows.ByHandleFileInformation, *windows.SID, error) {
	var info windows.ByHandleFileInformation
	h, err := openNoFollow(path, windows.READ_CONTROL)
	if err != nil {
		return info, nil, err
	}
	defer windows.CloseHandle(h)
	if err := windows.GetFileInformationByHandle(h, &info); err != nil {
		return info, nil, err
	}
	sd, err := windows.GetSecurityInfo(h, windows.SE_FILE_OBJECT, windows.OWNER_SECURITY_INFORMATION)
	if err != nil {
		return info, nil, err
	}
	owner, _, err := sd.Owner()
	if err != nil {
		return info, nil, err
	}
	return info, owner, nil
}

// takeOver makes owner the owner of path (when not nil) and gives it the access list dirSDDL or fileSDDL, through a handle to path
// itself; it reports whether path is a folder. A link is refused, and so is a file with more than one name (a hard link would carry the
// new access list to a file elsewhere).
func takeOver(path string, owner *windows.SID, dirSDDL, fileSDDL string, wantDir bool) (isDir bool, err error) {
	access := uint32(windows.READ_CONTROL | windows.WRITE_DAC)
	if owner != nil {
		access |= windows.WRITE_OWNER
	}
	h, err := openNoFollow(path, access)
	if err != nil {
		return false, err
	}
	defer windows.CloseHandle(h)
	var info windows.ByHandleFileInformation
	if err := windows.GetFileInformationByHandle(h, &info); err != nil {
		return false, err
	}
	isDir = info.FileAttributes&windows.FILE_ATTRIBUTE_DIRECTORY != 0
	switch {
	case info.FileAttributes&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0:
		return false, fmt.Errorf("%s: %w", path, errLink)
	case wantDir && !isDir:
		return false, fmt.Errorf("%s is not a folder", path)
	case !isDir && info.NumberOfLinks > 1:
		return false, fmt.Errorf("%s has more than one name (a hard link)", path)
	}
	sddl := fileSDDL
	if isDir {
		sddl = dirSDDL
	}
	sd, err := windows.SecurityDescriptorFromString(sddl)
	if err != nil {
		return false, fmt.Errorf("build security descriptor: %w", err)
	}
	dacl, _, err := sd.DACL()
	if err != nil {
		return false, fmt.Errorf("read DACL: %w", err)
	}
	information := windows.SECURITY_INFORMATION(windows.DACL_SECURITY_INFORMATION | windows.PROTECTED_DACL_SECURITY_INFORMATION)
	if owner != nil {
		information |= windows.OWNER_SECURITY_INFORMATION
	}
	if err := windows.SetSecurityInfo(h, windows.SE_FILE_OBJECT, information, owner, nil, dacl, nil); err != nil {
		return false, fmt.Errorf("restrict access to %s: %w", path, err)
	}
	return isDir, nil
}

// takeOverTree takes over everything below dir: folders get dirSDDL, files fileSDDL, both the owner (when not nil). It stops at the first
// link or anything it cannot take over.
func takeOverTree(dir string, owner *windows.SID, dirSDDL, fileSDDL string) error {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return err
	}
	for _, entry := range entries {
		path := filepath.Join(dir, entry.Name())
		isDir, err := takeOver(path, owner, dirSDDL, fileSDDL, false)
		if err != nil {
			return err
		}
		if isDir {
			if err := takeOverTree(path, owner, dirSDDL, fileSDDL); err != nil {
				return err
			}
		}
	}
	return nil
}

// openNoFollow opens path itself (also a folder, also a link) for its security information. With the backup and restore privileges
// enabled the access list does not keep the process out.
func openNoFollow(path string, access uint32) (windows.Handle, error) {
	name, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return 0, err
	}
	return windows.CreateFile(name, access, windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE|windows.FILE_SHARE_DELETE, nil,
		windows.OPEN_EXISTING, windows.FILE_FLAG_BACKUP_SEMANTICS|windows.FILE_FLAG_OPEN_REPARSE_POINT, 0)
}

// createDirectory creates a directory with its access list in one step; it fails with ERROR_ALREADY_EXISTS when the name is taken.
func createDirectory(dir, sddl string) error {
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
	return windows.CreateDirectory(name, attributes)
}

// setNamedDACL sets the protected access list sddl on path, propagated to what inherits it below.
func setNamedDACL(path, sddl string) error {
	sd, err := windows.SecurityDescriptorFromString(sddl)
	if err != nil {
		return fmt.Errorf("build security descriptor: %w", err)
	}
	dacl, _, err := sd.DACL()
	if err != nil {
		return fmt.Errorf("read DACL: %w", err)
	}
	if err := windows.SetNamedSecurityInfo(path, windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION, nil, nil, dacl, nil); err != nil {
		return fmt.Errorf("restrict access to %s: %w", path, err)
	}
	return nil
}

// enableOwnershipPrivileges enables the privileges that let the process take over a file whatever its access list says (SYSTEM and an
// elevated administrator hold them, disabled) and returns a function that puts them back as they were. A privilege the process does
// not hold stays off, and taking over then fails with access denied.
func enableOwnershipPrivileges() (restore func()) {
	var token windows.Token
	if err := windows.OpenProcessToken(windows.CurrentProcess(), windows.TOKEN_ADJUST_PRIVILEGES|windows.TOKEN_QUERY, &token); err != nil {
		return func() {}
	}
	var undo []windows.Tokenprivileges
	for _, name := range []string{"SeTakeOwnershipPrivilege", "SeRestorePrivilege", "SeBackupPrivilege"} {
		var luid windows.LUID
		if err := windows.LookupPrivilegeValue(nil, windows.StringToUTF16Ptr(name), &luid); err != nil {
			continue
		}
		enable := windows.Tokenprivileges{PrivilegeCount: 1}
		enable.Privileges[0] = windows.LUIDAndAttributes{Luid: luid, Attributes: windows.SE_PRIVILEGE_ENABLED}
		var previous windows.Tokenprivileges
		var size uint32
		if err := windows.AdjustTokenPrivileges(token, false, &enable, uint32(unsafe.Sizeof(previous)), &previous, &size); err != nil {
			continue
		}
		if previous.PrivilegeCount > 0 {
			undo = append(undo, previous)
		}
	}
	return func() {
		for i := range undo {
			_ = windows.AdjustTokenPrivileges(token, false, &undo[i], 0, nil, nil)
		}
		_ = token.Close()
	}
}
