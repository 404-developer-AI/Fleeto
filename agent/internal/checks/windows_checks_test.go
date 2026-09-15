//go:build windows

package checks

import (
	"strings"
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func TestEventLogQueryIsBuiltFromValidatedParameters(t *testing.T) {
	query, err := EventLogQuery(map[string]string{"log": "System", "source": "Service Control Manager", "event_ids": "7031, 7034", "level": "error",
		"window_minutes": "30"})
	if err != nil {
		t.Fatal(err)
	}
	want := "*[System[(Level=1 or Level=2) and Provider[@Name='Service Control Manager'] and (EventID=7031 or EventID=7034) and TimeCreated[timediff(@SystemTime) <= 1800000]]]"
	if query != want {
		t.Fatalf("query:\n%s\nwant:\n%s", query, want)
	}
	for _, bad := range []map[string]string{
		{"log": "System' or '1'='1"},
		{"log": "System", "source": "x']]|*[System["},
		{"log": "System", "event_ids": "1,abc"},
		{"log": "System", "level": "verbose"},
	} {
		if _, err := EventLogQuery(bad); err == nil {
			t.Fatalf("parameters %v must be refused", bad)
		}
	}
}

func TestEventLogCheckCountsEventsOfARealLog(t *testing.T) {
	m := run(t, agentv1.CheckType_CHECK_TYPE_EVENT_LOG, map[string]string{"log": "System", "level": "any", "window_minutes": "1440"})
	if len(m) != 1 || m[0].Error != "" || m[0].Value < 0 {
		t.Fatalf("event log: %+v", m)
	}
	missing := run(t, agentv1.CheckType_CHECK_TYPE_EVENT_LOG, map[string]string{"log": "Fleeto-Does-Not-Exist", "level": "any"})
	if missing[0].Error == "" || !strings.Contains(missing[0].Error, "Fleeto-Does-Not-Exist") {
		t.Fatalf("missing log: %+v", missing)
	}
}

func TestPendingRebootAndSecurityCenterReadWindows(t *testing.T) {
	reboot := run(t, agentv1.CheckType_CHECK_TYPE_PENDING_REBOOT, nil)
	if len(reboot) != 1 || reboot[0].Error != "" || (reboot[0].Value != 0 && reboot[0].Value != 1) {
		t.Fatalf("pending reboot: %+v", reboot)
	}
	firewall := run(t, agentv1.CheckType_CHECK_TYPE_SECURITY_CENTER, map[string]string{"component": "firewall"})
	if len(firewall) != 1 || firewall[0].Error != "" || firewall[0].Detail == "" {
		t.Fatalf("firewall: %+v", firewall)
	}
	antivirus := run(t, agentv1.CheckType_CHECK_TYPE_SECURITY_CENTER, map[string]string{"component": "antivirus"})
	if len(antivirus) != 1 || (antivirus[0].Error == "" && antivirus[0].Detail == "") {
		t.Fatalf("antivirus: %+v", antivirus)
	}
}

func TestSecurityCenterProductStateDecoding(t *testing.T) {
	cases := map[uint32][2]bool{
		0x061100: {true, true},  // Defender on, up to date
		0x061110: {true, false}, // on, definitions out of date
		0x060100: {false, true}, // off
		0x041000: {true, true},  // third-party product on
		0x000000: {false, true},
	}
	for state, want := range cases {
		on, current := DecodeProductState(state)
		if on != want[0] || current != want[1] {
			t.Fatalf("state %#x: on=%v current=%v, want %v", state, on, current, want)
		}
	}
}
