//go:build windows

package platform

import (
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"

	"golang.org/x/sys/windows"
)

func securityOf(t *testing.T, path string) (owner *windows.SID, sddl string) {
	t.Helper()
	sd, err := windows.GetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.OWNER_SECURITY_INFORMATION|windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		t.Fatalf("read the security of %s: %v", path, err)
	}
	owner, _, err = sd.Owner()
	if err != nil {
		t.Fatal(err)
	}
	return owner, sd.String()
}

func setDACL(t *testing.T, path, sddl string) {
	t.Helper()
	sd, err := windows.SecurityDescriptorFromString(sddl)
	if err != nil {
		t.Fatal(err)
	}
	dacl, _, _ := sd.DACL()
	if err := windows.SetNamedSecurityInfo(path, windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION, nil, nil, dacl, nil); err != nil {
		t.Fatal(err)
	}
}

func junction(t *testing.T, link, target string) {
	t.Helper()
	if out, err := exec.Command("cmd", "/c", "mklink", "/J", link, target).CombinedOutput(); err != nil {
		t.Fatalf("create junction: %v\n%s", err, out)
	}
}

func skipWhenElevated(t *testing.T) {
	t.Helper()
	if IsElevated() {
		t.Skip("an elevated process can take over the directory; this test needs a standard user")
	}
}

// A standard user can create folders in C:\ProgramData, and the owner of a folder can always change its access list again. A state or
// watchdog directory a standard user created before the install must therefore never be protected and used as it is: the service takes
// it over (see the elevated test) or, as here where the process cannot, refuses it. Before, the creator stayed its owner.
func TestEnsureProtectedDirRefusesADirectoryAnotherAccountOwns(t *testing.T) {
	skipWhenElevated(t)
	dir := filepath.Join(t.TempDir(), "Fleeto", "Agent")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "state.json"), []byte(`{"forged":true}`), 0o600); err != nil {
		t.Fatal(err)
	}
	err := EnsureProtectedDir(dir, AccessSystem)
	if err == nil || !strings.Contains(err.Error(), "created by another account") {
		t.Fatalf("expected a refusal of a directory this process cannot take over, got %v", err)
	}
	if owner, _ := securityOf(t, dir); TrustedOwner(owner) {
		t.Fatalf("a standard user cannot have made %s the owner", owner)
	}
}

// The same for a watchdog directory whose updates folder, where the agent binary is staged, the user gave an access list of its own.
func TestEnsureProtectedDirRefusesAWatchdogDirectoryWithAPlantedUpdatesFolder(t *testing.T) {
	skipWhenElevated(t)
	me, err := currentUserSID()
	if err != nil {
		t.Fatal(err)
	}
	watchdog := filepath.Join(t.TempDir(), "Fleeto", "Watchdog")
	updates := filepath.Join(watchdog, "updates")
	if err := os.MkdirAll(updates, 0o700); err != nil {
		t.Fatal(err)
	}
	setDACL(t, updates, "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;"+me+")")
	if err := EnsureProtectedDir(watchdog, AccessSystem); err == nil {
		t.Fatal("a watchdog directory another account owns was protected and kept its planted updates folder")
	}
	if err := os.WriteFile(filepath.Join(updates, "probe"), nil, 0o600); err != nil {
		t.Fatalf("the refused directory must be left as it was: %v", err)
	}
}

// A directory of a service that is a link is refused, and what it points to keeps its access list.
func TestEnsureProtectedDirRefusesALink(t *testing.T) {
	base := t.TempDir()
	target := filepath.Join(base, "elsewhere")
	if err := os.Mkdir(target, 0o700); err != nil {
		t.Fatal(err)
	}
	_, before := securityOf(t, target)
	link := filepath.Join(base, "Agent")
	junction(t, link, target)
	defer os.Remove(link)
	if err := EnsureProtectedDir(link, AccessSystem); !errors.Is(err, errLink) {
		t.Fatalf("expected a refusal of a link, got %v", err)
	}
	if _, after := securityOf(t, target); after != before {
		t.Fatalf("the access list of the link target changed:\nbefore %s\nafter  %s", before, after)
	}
}

// Taking over a directory replaces the access list of everything in it, so a folder the creator planted with access of its own does
// not keep it; a link inside is refused.
func TestTakeOverTreeReplacesTheAccessOfEverythingInside(t *testing.T) {
	me, err := currentUserSID()
	if err != nil {
		t.Fatal(err)
	}
	dirSDDL := "D:P(A;OICI;FA;;;" + me + ")"
	fileSDDL := "D:P(A;;FA;;;" + me + ")"
	dir := t.TempDir()
	updates := filepath.Join(dir, "updates")
	staged := filepath.Join(updates, "fleeto-agent.exe")
	if err := os.MkdirAll(updates, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(staged, []byte("planted"), 0o700); err != nil {
		t.Fatal(err)
	}
	// Everyone may write the planted folder and file.
	setDACL(t, updates, "D:P(A;OICI;FA;;;WD)(A;OICI;FA;;;"+me+")")
	setDACL(t, staged, "D:P(A;;FA;;;WD)(A;;FA;;;"+me+")")

	if err := takeOverTree(dir, nil, dirSDDL, fileSDDL); err != nil {
		t.Fatal(err)
	}
	for _, path := range []string{updates, staged} {
		if _, sddl := securityOf(t, path); strings.Contains(sddl, ";WD)") {
			t.Errorf("%s kept its own access list: %s", path, sddl)
		}
	}

	junction(t, filepath.Join(updates, "link"), t.TempDir())
	defer os.Remove(filepath.Join(updates, "link"))
	if err := takeOverTree(dir, nil, dirSDDL, fileSDDL); !errors.Is(err, errLink) {
		t.Fatalf("expected a refusal of a link inside, got %v", err)
	}
}

func TestTrustedOwnerIsSystemOrAdministrators(t *testing.T) {
	for sidType, want := range map[windows.WELL_KNOWN_SID_TYPE]bool{
		windows.WinLocalSystemSid:           true,
		windows.WinBuiltinAdministratorsSid: true,
		windows.WinBuiltinUsersSid:          false,
		windows.WinWorldSid:                 false,
		windows.WinAuthenticatedUserSid:     false,
	} {
		sid, err := windows.CreateWellKnownSid(sidType)
		if err != nil {
			t.Fatal(err)
		}
		if got := TrustedOwner(sid); got != want {
			t.Errorf("TrustedOwner(%s) = %v, want %v", sid, got, want)
		}
	}
	me, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		t.Fatal(err)
	}
	if !me.User.Sid.IsWellKnown(windows.WinLocalSystemSid) && TrustedOwner(me.User.Sid) {
		t.Errorf("the user %s is not a trusted owner", me.User.Sid)
	}
}

// Elevated (as the install and the service run), a directory another account owns is taken over: Administrators own it and everything in
// it, and nothing in it keeps an access list of its own.
func TestEnsureProtectedDirTakesOverADirectoryAnotherAccountOwns(t *testing.T) {
	if !IsElevated() {
		t.Skip("taking over a directory needs an elevated process")
	}
	restore := enableOwnershipPrivileges()
	users, err := windows.CreateWellKnownSid(windows.WinBuiltinUsersSid)
	if err != nil {
		t.Fatal(err)
	}
	dir := filepath.Join(t.TempDir(), "Watchdog")
	updates := filepath.Join(dir, "updates")
	if err := os.MkdirAll(updates, 0o700); err != nil {
		t.Fatal(err)
	}
	for _, path := range []string{dir, updates} {
		if err := windows.SetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.OWNER_SECURITY_INFORMATION, users, nil, nil, nil); err != nil {
			t.Fatal(err)
		}
	}
	setDACL(t, updates, "D:P(A;OICI;FA;;;WD)")
	restore()

	if err := EnsureProtectedDir(dir, AccessSystem); err != nil {
		t.Fatal(err)
	}
	for _, path := range []string{dir, updates} {
		owner, sddl := securityOf(t, path)
		if !owner.IsWellKnown(windows.WinBuiltinAdministratorsSid) || strings.Contains(sddl, ";WD)") {
			t.Errorf("%s was not taken over: %s", path, sddl)
		}
	}
}
