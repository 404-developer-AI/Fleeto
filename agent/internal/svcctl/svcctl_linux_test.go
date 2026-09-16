//go:build linux

package svcctl

import (
	"strings"
	"testing"
)

func TestUnitFileStartsTheServiceAtBootAndRestartsIt(t *testing.T) {
	unit := UnitFile(Definition{
		Name:        "fleeto-watchdog",
		DisplayName: "Fleeto Watchdog",
		Description: "Fleeto Watchdog: keeps the Fleeto Agent running and installs its updates.",
		Executable:  "/opt/fleeto-agent/fleeto-watchdog",
		Args:        []string{"run"},
	})

	want := []string{
		"Description=Fleeto Watchdog: keeps the Fleeto Agent running and installs its updates.",
		"ExecStart=/opt/fleeto-agent/fleeto-watchdog run",
		"Restart=always",
		"WantedBy=multi-user.target",
	}
	for _, line := range want {
		if !strings.Contains(unit, "\n"+line+"\n") {
			t.Errorf("the unit file has no line %q:\n%s", line, unit)
		}
	}
	if strings.Contains(unit, "User=") {
		t.Errorf("the unit must run as root, so it sets no user:\n%s", unit)
	}
}

func TestUnitNameAddsTheServiceSuffix(t *testing.T) {
	if got := unitName("fleeto-agent"); got != "fleeto-agent.service" {
		t.Errorf("unitName = %q, want fleeto-agent.service", got)
	}
}
