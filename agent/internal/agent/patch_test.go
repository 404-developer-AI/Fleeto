package agent

import (
	"context"
	"sync/atomic"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func configurePatch(t *testing.T, g *fakeGateway, c *gwConn, version uint64, tier agentv1.Tier, hours uint32) {
	t.Helper()
	cfg := &agentv1.AgentConfig{InstanceId: gwInstance, EndpointId: gwEndpoint, Version: version, Tier: tier, PatchScanIntervalHours: hours}
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_Config{Config: g.signed(t, cfg, g.signingKey)}})
	c.expect(t, "ConfigApplied", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_ConfigApplied](m) })
}

func patchRequest(id string) *agentv1.ServerMessage {
	return &agentv1.ServerMessage{Body: &agentv1.ServerMessage_PatchScanRequest{PatchScanRequest: &agentv1.PatchScanRequest{RequestId: id}}}
}

func expectNoPatchReport(t *testing.T, c *gwConn) {
	t.Helper()
	for range 2 {
		c.expect(t, "a heartbeat", func(m *agentv1.AgentMessage) bool {
			if isType[*agentv1.AgentMessage_PatchScan](m) {
				t.Fatal("no patch scan report was expected")
			}
			return isType[*agentv1.AgentMessage_Heartbeat](m)
		})
	}
}

func TestAPatchScanRequestIsReportedAndResentUntilAcknowledged(t *testing.T) {
	g := newFakeGateway(t)
	store := enrollForTest(t, g)
	var scans atomic.Int32
	opts := testOptions(store)
	opts.BatchAckTimeout = time.Hour
	opts.PatchScan = func(_ context.Context, requestID string) *agentv1.PatchScanReport {
		scans.Add(1)
		return &agentv1.PatchScanReport{Engine: "apt", RestartRequired: true, Updates: []*agentv1.PatchUpdate{
			{Id: "libssl3:amd64", Name: "libssl3", Version: "3.0.2-0ubuntu1.19", InstalledVersion: "3.0.2-0ubuntu1.18",
				Classification: "security", Origin: "jammy-security"},
		}}
	}
	a, _, _ := startAgent(t, opts)
	c := g.accept(t)
	c.expect(t, "Hello", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Hello](m) })
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_HelloAck{HelloAck: &agentv1.HelloAck{HeartbeatIntervalSeconds: 1}}})

	// Agent-only: the request is ignored, also with an interval (which the server never sets there).
	configurePatch(t, g, c, 2, agentv1.Tier_TIER_AGENT_ONLY, 6)
	c.send(t, patchRequest("agent-only"))
	expectNoPatchReport(t, c)
	// Managed without patch management by Fleeto: ignored too.
	configurePatch(t, g, c, 3, agentv1.Tier_TIER_MANAGED, 0)
	c.send(t, patchRequest("not-on"))
	expectNoPatchReport(t, c)
	if scans.Load() != 0 {
		t.Fatal("no scan without a managed configuration with a patch scan interval")
	}

	configurePatch(t, g, c, 4, agentv1.Tier_TIER_MANAGED, 6)
	c.send(t, patchRequest("req-1"))
	first := c.expect(t, "a patch scan report", func(m *agentv1.AgentMessage) bool {
		return isType[*agentv1.AgentMessage_PatchScan](m)
	}).GetPatchScan()
	if first.GetRequestId() != "req-1" || first.GetEngine() != "apt" || len(first.GetUpdates()) != 1 || !first.GetRestartRequired() ||
		first.GetStartedAt() == nil || first.GetScanId() == "" {
		t.Fatalf("report = %v", first)
	}
	if !a.State().PatchScanStartedAt.After(time.Now().Add(-time.Minute)) {
		t.Fatal("the start of the scan is kept in the state file")
	}

	// Without an acknowledgement the report is sent again on the next connection.
	c.close()
	c = g.accept(t)
	c.expect(t, "Hello", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Hello](m) })
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_HelloAck{HelloAck: &agentv1.HelloAck{HeartbeatIntervalSeconds: 1}}})
	again := c.expect(t, "the report again", func(m *agentv1.AgentMessage) bool {
		return isType[*agentv1.AgentMessage_PatchScan](m)
	}).GetPatchScan()
	if again.GetScanId() != first.GetScanId() {
		t.Fatalf("resent %s, want %s", again.GetScanId(), first.GetScanId())
	}
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_PatchScanAck{PatchScanAck: &agentv1.PatchScanAck{ScanId: first.GetScanId()}}})
	deadline := time.Now().Add(5 * time.Second)
	for len(a.patch.Pending()) != 0 {
		if time.Now().After(deadline) {
			t.Fatal("the acknowledged report stays in the spool")
		}
		time.Sleep(10 * time.Millisecond)
	}

	// A second request within 15 minutes is ignored.
	c.send(t, patchRequest("req-2"))
	expectNoPatchReport(t, c)
	if scans.Load() != 1 {
		t.Fatalf("scans = %d", scans.Load())
	}
}
