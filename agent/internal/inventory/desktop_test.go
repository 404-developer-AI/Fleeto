package inventory

import "testing"

func TestServerCoreAndNanoServerHaveNoDesktop(t *testing.T) {
	cases := map[string]string{
		"Server Core": DesktopNone,
		"Nano Server": DesktopNone,
		"Server":      DesktopGraphical,
		"Client":      DesktopGraphical,
		" client ":    DesktopGraphical,
		"":            "",
		"Something":   "",
	}
	for installationType, want := range cases {
		if got := windowsDesktop(installationType); got != want {
			t.Errorf("windowsDesktop(%q) = %q, want %q", installationType, got, want)
		}
	}
}

func TestOnlyGraphicalSessionTypesAndXServersCountAsADesktop(t *testing.T) {
	for _, sessionType := range []string{"x11", "wayland", "mir", "x11\n"} {
		if !isGraphicalSessionType(sessionType) {
			t.Errorf("session type %q is graphical", sessionType)
		}
	}
	for _, sessionType := range []string{"tty", "unspecified", ""} {
		if isGraphicalSessionType(sessionType) {
			t.Errorf("session type %q is not graphical", sessionType)
		}
	}
	for _, comm := range []string{"Xorg\n", "X", "Xwayland"} {
		if !isDisplayServerName(comm) {
			t.Errorf("%q is a display server", comm)
		}
	}
	for _, comm := range []string{"Xvfb", "sshd", "Xorg-helper", ""} {
		if isDisplayServerName(comm) {
			t.Errorf("%q is not a display server", comm)
		}
	}
}
