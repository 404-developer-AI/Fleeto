//go:build windows

package checks

import (
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func TestPingOfTheIPv6LoopbackAddressGetsAReply(t *testing.T) {
	m := run(t, agentv1.CheckType_CHECK_TYPE_PING, map[string]string{"host": "::1", "count": "2"})
	if len(m) != 1 || m[0].Error != "" || m[0].Value == Unreachable {
		t.Fatalf("ping ::1: %+v", m)
	}
}
