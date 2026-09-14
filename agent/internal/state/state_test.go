package state

import (
	"errors"
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
)

func TestStoreRoundTripAndUpdate(t *testing.T) {
	store := NewStore(t.TempDir(), platform.AccessCurrentUser)
	if _, err := store.Load(); !errors.Is(err, ErrNotEnrolled) {
		t.Fatalf("expected ErrNotEnrolled, got %v", err)
	}
	st := &State{Server: "localhost:7200", EndpointID: "e1", InstanceID: "i1", AppliedConfig: []byte{1, 2, 3}}
	if err := store.Save(st); err != nil {
		t.Fatal(err)
	}
	if _, err := store.Update(func(s *State) error { s.AppliedConfigVersion = 7; return nil }); err != nil {
		t.Fatal(err)
	}
	got, err := store.Load()
	if err != nil {
		t.Fatal(err)
	}
	if got.AppliedConfigVersion != 7 || string(got.AppliedConfig) != "\x01\x02\x03" || got.InstanceID != "i1" {
		t.Fatalf("unexpected state %+v", got)
	}
}
