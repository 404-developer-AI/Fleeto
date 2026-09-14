//go:build windows

package checks

import (
	"context"
	"strings"
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func collect(t *testing.T, spec *agentv1.CheckSpec) []Measurement {
	t.Helper()
	return SystemCollector{}.Collect(context.Background(), spec)
}

func TestDiskFreeOnEveryFixedDrive(t *testing.T) {
	ms := collect(t, &agentv1.CheckSpec{Type: agentv1.CheckType_CHECK_TYPE_DISK_FREE, IntervalSeconds: 60, Parameters: map[string]string{"drive": "*"}})
	if len(ms) == 0 {
		t.Fatal("expected at least one drive")
	}
	for _, m := range ms {
		if m.Error == "" && (m.Value < 0 || m.Value > 100 || !strings.Contains(m.Detail, " free of ")) {
			t.Fatalf("unexpected measurement %+v", m)
		}
	}
	system := collect(t, &agentv1.CheckSpec{Type: agentv1.CheckType_CHECK_TYPE_DISK_FREE, Parameters: map[string]string{"drive": "c"}})
	if len(system) != 1 || system[0].Target != "C:" || system[0].Error != "" {
		t.Fatalf("unexpected C: measurement %+v", system)
	}
	missing := collect(t, &agentv1.CheckSpec{Type: agentv1.CheckType_CHECK_TYPE_DISK_FREE, Parameters: map[string]string{"drive": "Q:"}})
	if len(missing) != 1 || missing[0].Error == "" {
		t.Fatalf("expected an error for a missing drive, got %+v", missing)
	}
}

func TestServiceRunning(t *testing.T) {
	ms := collect(t, &agentv1.CheckSpec{Type: agentv1.CheckType_CHECK_TYPE_SERVICE_RUNNING, Parameters: map[string]string{"service": "RpcSs"}})
	if len(ms) != 1 || ms[0].Value != 1 || ms[0].Target != "RpcSs" || ms[0].Error != "" {
		t.Fatalf("expected RpcSs running, got %+v", ms)
	}
	ms = collect(t, &agentv1.CheckSpec{Type: agentv1.CheckType_CHECK_TYPE_SERVICE_RUNNING, Parameters: map[string]string{"service": "FleetifyDoesNotExist"}})
	if len(ms) != 1 || ms[0].Error != "Service FleetifyDoesNotExist does not exist" {
		t.Fatalf("expected a missing service error, got %+v", ms)
	}
}

func TestMemoryUptimeAndShortCPU(t *testing.T) {
	mem := collect(t, &agentv1.CheckSpec{Type: agentv1.CheckType_CHECK_TYPE_MEMORY_USAGE})
	if mem[0].Error != "" || mem[0].Value <= 0 || mem[0].Value > 100 {
		t.Fatalf("memory: %+v", mem)
	}
	up := collect(t, &agentv1.CheckSpec{Type: agentv1.CheckType_CHECK_TYPE_UPTIME})
	if up[0].Error != "" || up[0].Value < 0 {
		t.Fatalf("uptime: %+v", up)
	}
	cpu := collect(t, &agentv1.CheckSpec{Type: agentv1.CheckType_CHECK_TYPE_CPU_USAGE, IntervalSeconds: 1})
	if cpu[0].Error != "" || cpu[0].Value < 0 || cpu[0].Value > 100 {
		t.Fatalf("cpu: %+v", cpu)
	}
}
