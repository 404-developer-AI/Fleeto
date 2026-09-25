package inventory

import "strings"

// Values of Inventory.desktop (0.6.0): whether the endpoint has a graphical desktop that remote control can show. The server
// offers remote control only when it is not DesktopNone; remote background works either way.
const (
	DesktopGraphical = "graphical"
	DesktopNone      = "none"
)

// windowsDesktop maps InstallationType of HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion: Server Core and Nano Server have no
// desktop, Client and Server (with Desktop Experience) have one. An unknown or unreadable value reports nothing, so the server keeps
// offering remote control.
func windowsDesktop(installationType string) string {
	switch strings.ToLower(strings.TrimSpace(installationType)) {
	case "server core", "nano server":
		return DesktopNone
	case "client", "server":
		return DesktopGraphical
	}
	return ""
}

// isGraphicalSessionType is true for a logind session type that draws a desktop.
func isGraphicalSessionType(sessionType string) bool {
	switch strings.TrimSpace(sessionType) {
	case "x11", "wayland", "mir":
		return true
	}
	return false
}

// isDisplayServerName is true for the process name (comm) of an X server or of Xwayland. It is a hint for the inventory only:
// remote control itself verifies the X server before it shows anything.
func isDisplayServerName(comm string) bool {
	switch strings.TrimSpace(comm) {
	case "Xorg", "X", "Xwayland":
		return true
	}
	return false
}
