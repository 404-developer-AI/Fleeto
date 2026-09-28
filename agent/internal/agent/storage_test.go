package agent

import (
	"context"
	"crypto/rand"
	"fmt"
	"sync/atomic"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func testScanID() string {
	var b [16]byte
	_, _ = rand.Read(b[:])
	b[6] = b[6]&0x0f | 0x40
	b[8] = b[8]&0x3f | 0x80
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

func configure(t *testing.T, g *fakeGateway, c *gwConn, version uint64, tier agentv1.Tier) {
	t.Helper()
	cfg := &agentv1.AgentConfig{InstanceId: gwInstance, EndpointId: gwEndpoint, Version: version, Tier: tier}
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_Config{Config: g.signed(t, cfg, g.signingKey)}})
	c.expect(t, "ConfigApplied", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_ConfigApplied](m) })
}

func TestAStorageScanRequestIsReportedAndResentUntilAcknowledged(t *testing.T) {
	g := newFakeGateway(t)
	store := enrollForTest(t, g)
	var scans atomic.Int32
	opts := testOptions(store)
	opts.BatchAckTimeout = time.Hour
	opts.StorageScan = func(_ context.Context, requestID string) []*agentv1.StorageScanReport {
		scans.Add(1)
		return []*agentv1.StorageScanReport{{
			ScanId: testScanID(), Volume: "C:", RequestId: requestID, Complete: true,
			Method:  agentv1.StorageScanMethod_STORAGE_SCAN_METHOD_MFT,
			Folders: []*agentv1.StorageFolder{{Path: `C:\`, SizeBytes: 100}},
		}}
	}
	a, _, _ := startAgent(t, opts)
	c := g.accept(t)
	c.expect(t, "Hello", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Hello](m) })
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_HelloAck{HelloAck: &agentv1.HelloAck{HeartbeatIntervalSeconds: 1}}})

	// Agent-only: the request is ignored.
	configure(t, g, c, 2, agentv1.Tier_TIER_AGENT_ONLY)
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_StorageScanRequest{StorageScanRequest: &agentv1.StorageScanRequest{RequestId: "ignored"}}})
	c.expect(t, "a heartbeat", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Heartbeat](m) })
	c.expect(t, "a heartbeat", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Heartbeat](m) })
	if scans.Load() != 0 {
		t.Fatal("an agent-only endpoint must not scan")
	}

	configure(t, g, c, 3, agentv1.Tier_TIER_MANAGED)
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_StorageScanRequest{StorageScanRequest: &agentv1.StorageScanRequest{RequestId: "req-1"}}})
	first := c.expect(t, "a storage scan report", func(m *agentv1.AgentMessage) bool {
		return isType[*agentv1.AgentMessage_StorageScan](m)
	}).GetStorageScan()
	if first.GetRequestId() != "req-1" || first.GetVolume() != "C:" {
		t.Fatalf("report = %v", first)
	}
	if !a.State().StorageScanStartedAt.After(time.Now().Add(-time.Minute)) {
		t.Fatal("the start of the scan is kept in the state file")
	}

	// Without an acknowledgement the report is sent again on the next connection.
	c.close()
	c = g.accept(t)
	c.expect(t, "Hello", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Hello](m) })
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_HelloAck{HelloAck: &agentv1.HelloAck{HeartbeatIntervalSeconds: 1}}})
	again := c.expect(t, "the report again", func(m *agentv1.AgentMessage) bool {
		return isType[*agentv1.AgentMessage_StorageScan](m)
	}).GetStorageScan()
	if again.GetScanId() != first.GetScanId() {
		t.Fatalf("resent %s, want %s", again.GetScanId(), first.GetScanId())
	}
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_StorageScanAck{StorageScanAck: &agentv1.StorageScanAck{ScanId: first.GetScanId()}}})
	deadline := time.Now().Add(5 * time.Second)
	for len(a.storage.Pending()) != 0 {
		if time.Now().After(deadline) {
			t.Fatal("the acknowledged report stays in the spool")
		}
		time.Sleep(10 * time.Millisecond)
	}

	// A second request within 15 minutes is ignored.
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_StorageScanRequest{StorageScanRequest: &agentv1.StorageScanRequest{RequestId: "req-2"}}})
	c.expect(t, "a heartbeat", func(m *agentv1.AgentMessage) bool {
		if isType[*agentv1.AgentMessage_StorageScan](m) {
			t.Fatal("a request within 15 minutes must be ignored")
		}
		return isType[*agentv1.AgentMessage_Heartbeat](m)
	})
	if scans.Load() != 1 {
		t.Fatalf("scans = %d", scans.Load())
	}
}
