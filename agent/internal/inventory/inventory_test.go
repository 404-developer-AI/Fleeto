package inventory

import (
	"context"
	"runtime"
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/logging"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func TestHashIgnoresListOrderAndDuplicates(t *testing.T) {
	a := &agentv1.Inventory{Hostname: "h", Software: []*agentv1.SoftwareItem{
		{Name: "Zeta", Version: "1"}, {Name: "alpha", Version: "2"}, {Name: "Zeta", Version: "1"},
	}}
	b := &agentv1.Inventory{Hostname: "h", Software: []*agentv1.SoftwareItem{
		{Name: "alpha", Version: "2"}, {Name: "Zeta", Version: "1"},
	}}
	ha, err := Hash(a)
	if err != nil {
		t.Fatal(err)
	}
	hb, _ := Hash(b)
	if ha != hb || len(ha) != 64 {
		t.Fatalf("hashes differ: %s %s", ha, hb)
	}
	b.Software[0].Version = "3"
	if hc, _ := Hash(b); hc == ha {
		t.Fatal("a changed version must change the hash")
	}
}

func TestCollectReturnsTheBasics(t *testing.T) {
	inv := Collect(context.Background(), logging.Discard())
	if inv.GetHostname() == "" || inv.GetOs().GetPlatform() != runtime.GOOS || inv.GetMemoryTotalBytes() == 0 {
		t.Fatalf("incomplete inventory: %v", inv)
	}
	if runtime.GOOS == "windows" {
		if inv.GetOs().GetName() == "" || inv.GetOs().GetVersion() == "" || len(inv.GetDisks()) == 0 || len(inv.GetSoftware()) == 0 {
			t.Fatalf("incomplete Windows inventory: os=%v disks=%d software=%d", inv.GetOs(), len(inv.GetDisks()), len(inv.GetSoftware()))
		}
		for _, s := range inv.GetSoftware() {
			if s.GetName() == "" {
				t.Fatal("software without a name must be skipped")
			}
		}
	}
	h1, _ := Hash(inv)
	h2, _ := Hash(inv)
	if h1 != h2 {
		t.Fatal("hash is not stable")
	}
}
