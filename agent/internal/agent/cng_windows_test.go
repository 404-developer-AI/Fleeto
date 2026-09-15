//go:build windows

package agent

import (
	"context"
	"fmt"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/logging"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
)

// The mTLS handshake signs with a CNG key (per-user, so no elevation), the same code path as the service's machine key.
func TestEnrollAndConnectWithCNGKey(t *testing.T) {
	g := newFakeGateway(t)
	dir := t.TempDir()
	ref := state.KeyRef{Kind: keystore.KindCNG, Name: fmt.Sprintf("Fleeto Agent Session Test %d", time.Now().UnixNano())}
	st, err := Enroll(context.Background(), EnrollParams{
		StateDir: dir, Access: platform.AccessCurrentUser, Key: ref,
		Server: g.addr(), Token: gwToken, CAFingerprint: g.ca.Fingerprint(), Logger: logging.Discard(),
	})
	if err != nil {
		t.Fatalf("enroll: %v", err)
	}
	t.Cleanup(func() { _ = keystore.Delete(dir, st.Key) })
	startAgent(t, testOptions(state.NewStore(dir, platform.AccessCurrentUser)))
	c := g.accept(t)
	hello := c.expect(t, "Hello", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Hello](m) })
	if hello.GetHello().GetAgentVersion() == "" {
		t.Fatal("expected an agent version in Hello")
	}
	t.Logf("connected with key in %s", st.Key.Provider)
}
