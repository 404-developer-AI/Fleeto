//go:build windows

package inventory

import (
	"context"
	"strings"
	"testing"
)

// The WMI class name is not derived from the Go type name (win32ComputerSystem has no underscore), so every query
// must name its class explicitly. A wrong class fails silently into a fallback, hence this test on a real machine.
func TestWMIQueriesReturnRealSystemInformation(t *testing.T) {
	info, err := collectPlatform(context.Background())
	if err != nil {
		t.Fatalf("collect platform information: %v", err)
	}
	if strings.TrimSpace(info.Manufacturer) == "" {
		t.Fatal("Win32_ComputerSystem returned no manufacturer")
	}

	os := OSInfo(context.Background())
	if os.GetName() == "Windows" || !strings.Contains(os.GetName(), "Windows") {
		t.Fatalf("Win32_OperatingSystem was not used: product name %q", os.GetName())
	}
}
