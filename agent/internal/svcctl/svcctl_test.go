package svcctl

import "testing"

// systemd 219 (Synology DSM 7, CentOS 7) sets no INVOCATION_ID, so the control group must name the unit of the service itself.
func TestInUnitCgroupFindsTheUnitOfTheServiceOnly(t *testing.T) {
	// cgroup v1 as written by systemd 219 on Synology DSM 7.1.
	v1 := "11:cpuset:/\n10:memory:/system.slice/fleeto-agent.service\n1:name=systemd:/system.slice/fleeto-agent.service\n"
	// cgroup v2, the unified hierarchy.
	v2 := "0::/system.slice/fleeto-watchdog.service\n"
	cases := []struct {
		name    string
		content string
		service string
		want    bool
	}{
		{"v1 agent unit", v1, "fleeto-agent", true},
		{"v1 other unit", v1, "fleeto-watchdog", false},
		{"v2 watchdog unit", v2, "fleeto-watchdog", true},
		{"v2 other unit", v2, "fleeto-agent", false},
		{"only a controller names the unit", "10:memory:/system.slice/fleeto-agent.service\n1:name=systemd:/user.slice\n", "fleeto-agent", false},
		{"a unit whose name ends the same", "0::/system.slice/my-fleeto-agent.service\n", "fleeto-agent", false},
		{"a user session", "0::/user.slice/user-1000.slice/session-3.scope\n", "fleeto-agent", false},
		{"empty", "", "fleeto-agent", false},
	}
	for _, c := range cases {
		if got := InUnitCgroup(c.content, c.service); got != c.want {
			t.Errorf("%s: InUnitCgroup = %v, want %v", c.name, got, c.want)
		}
	}
}
