package agent

import (
	"sync"
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/distupdates"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/winupdate"
)

// A managed configuration of an endpoint Fleeto patches (0.9.0) turns Windows Update off without the Action1 agent, registers
// Microsoft Update and turns the automatic updates of the distribution off, the state of which travels with the inventory. Without
// patch_scan_interval_hours, or on an agent-only endpoint, none of them is asked for, whatever the configuration carries.
func TestTheConfigurationOfAnEndpointFleetoPatches(t *testing.T) {
	g := newFakeGateway(t)
	store := enrollForTest(t, g)
	type asked struct{ windowsOff, fleeto, microsoftUpdate, distOff bool }
	var mu sync.Mutex
	var last asked
	opts := testOptions(store)
	opts.WindowsUpdate = func(turnOff, _, fleeto bool) (string, bool, error) {
		mu.Lock()
		defer mu.Unlock()
		last.windowsOff, last.fleeto = turnOff, fleeto
		return winupdate.StateUntouched, false, nil
	}
	opts.MicrosoftUpdate = func(want bool) (string, bool, error) {
		mu.Lock()
		defer mu.Unlock()
		last.microsoftUpdate = want
		return "", false, nil
	}
	opts.DistributionUpdates = func(turnOff bool) (string, bool, error) {
		mu.Lock()
		defer mu.Unlock()
		last.distOff = turnOff
		if turnOff {
			return distupdates.StateTurnedOff, true, nil
		}
		return distupdates.StateUntouched, false, nil
	}
	startAgent(t, opts)
	c := g.accept(t)
	c.expect(t, "Hello", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Hello](m) })
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_HelloAck{HelloAck: &agentv1.HelloAck{HeartbeatIntervalSeconds: 30}}})
	c.expect(t, "the first inventory", func(m *agentv1.AgentMessage) bool {
		return m.GetInventory().GetInventory().GetDistributionUpdates() == distupdates.StateUntouched
	})

	patched := &agentv1.AgentConfig{InstanceId: gwInstance, EndpointId: gwEndpoint, Version: 1, Tier: agentv1.Tier_TIER_MANAGED,
		PatchScanIntervalHours: 6, TurnOffWindowsUpdate: true, MicrosoftUpdate: true, TurnOffDistributionUpdates: true}
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_Config{Config: g.signed(t, patched, g.signingKey)}})
	c.expect(t, "an inventory with the distribution's updates turned off", func(m *agentv1.AgentMessage) bool {
		return m.GetInventory().GetInventory().GetDistributionUpdates() == distupdates.StateTurnedOff
	})
	mu.Lock()
	if last != (asked{true, true, true, true}) {
		t.Fatalf("asked %+v", last)
	}
	mu.Unlock()

	notScanning := &agentv1.AgentConfig{InstanceId: gwInstance, EndpointId: gwEndpoint, Version: 2, Tier: agentv1.Tier_TIER_MANAGED,
		TurnOffWindowsUpdate: true, MicrosoftUpdate: true, TurnOffDistributionUpdates: true}
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_Config{Config: g.signed(t, notScanning, g.signingKey)}})
	c.expect(t, "an inventory with the distribution's updates back on", func(m *agentv1.AgentMessage) bool {
		return m.GetInventory().GetInventory().GetDistributionUpdates() == distupdates.StateUntouched
	})
	mu.Lock()
	if last != (asked{windowsOff: true}) {
		t.Fatalf("without scans only the Windows Update switch is passed on, without Fleeto: %+v", last)
	}
	mu.Unlock()
}
