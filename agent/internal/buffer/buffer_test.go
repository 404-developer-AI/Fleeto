package buffer

import (
	"fmt"
	"path/filepath"
	"strings"
	"testing"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/logging"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func result(i int) *agentv1.CheckResult {
	return &agentv1.CheckResult{CheckId: "c1", ConfigVersion: 1, Value: float64(i), Target: fmt.Sprint(i)}
}

func open(t *testing.T, path string, max int) *Buffer {
	t.Helper()
	b, err := Open(path, max, logging.Discard())
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func TestBatchIsKeptUntilAckAndResentAfterRestart(t *testing.T) {
	path := filepath.Join(t.TempDir(), "results.db")
	b := open(t, path, 1000)
	for i := range 700 {
		if err := b.Add(result(i)); err != nil {
			t.Fatal(err)
		}
	}
	first, err := b.NextBatch(500)
	if err != nil || first == nil {
		t.Fatalf("expected a batch, got %v", err)
	}
	if first.Sequence != 1 || len(first.Results) != 500 || first.Results[0].Target != "0" {
		t.Fatalf("unexpected first batch: seq %d, %d results", first.Sequence, len(first.Results))
	}
	// Without an ack the same batch comes back, never a new one.
	again, _ := b.NextBatch(500)
	if again.Sequence != 1 || len(again.Results) != 500 {
		t.Fatalf("expected the outstanding batch again, got seq %d", again.Sequence)
	}
	if b.Total() != 700 {
		t.Fatalf("expected 700 results on disk, got %d", b.Total())
	}

	// Restart: the outstanding batch survives with the same sequence and content.
	if err := b.Close(); err != nil {
		t.Fatal(err)
	}
	b = open(t, path, 1000)
	defer b.Close()
	resent, err := b.Outstanding()
	if err != nil || resent == nil || resent.Sequence != 1 || len(resent.Results) != 500 || resent.Results[499].Target != "499" {
		t.Fatalf("expected batch 1 after restart, got %v %v", resent, err)
	}
	if b.Total() != 700 || b.Pending() != 200 {
		t.Fatalf("counters after restart: total %d pending %d", b.Total(), b.Pending())
	}

	if ok, err := b.Ack(99); ok || err != nil {
		t.Fatalf("an ack for an unknown sequence must be ignored, got %v %v", ok, err)
	}
	if ok, err := b.Ack(1); !ok || err != nil {
		t.Fatalf("ack 1: %v %v", ok, err)
	}
	if ok, _ := b.Ack(1); ok {
		t.Fatal("a duplicate ack must report false")
	}
	second, _ := b.NextBatch(500)
	if second.Sequence != 2 || len(second.Results) != 200 || second.Results[0].Target != "500" {
		t.Fatalf("unexpected second batch: seq %d, %d results", second.Sequence, len(second.Results))
	}
	if _, err := b.Ack(2); err != nil {
		t.Fatal(err)
	}
	if empty, _ := b.NextBatch(500); empty != nil {
		t.Fatal("expected no batch when nothing is pending")
	}
}

func TestSequenceSurvivesRestartWithoutReuse(t *testing.T) {
	path := filepath.Join(t.TempDir(), "results.db")
	b := open(t, path, 100)
	for seq := uint64(1); seq <= 3; seq++ {
		_ = b.Add(result(int(seq)))
		batch, _ := b.NextBatch(500)
		if batch.Sequence != seq {
			t.Fatalf("expected sequence %d, got %d", seq, batch.Sequence)
		}
		_, _ = b.Ack(seq)
	}
	_ = b.Close()
	b = open(t, path, 100)
	defer b.Close()
	next, _ := b.NextSequence()
	if next != 4 {
		t.Fatalf("expected next sequence 4 after restart, got %d", next)
	}
	_ = b.Add(result(9))
	batch, _ := b.NextBatch(500)
	if batch.Sequence != 4 {
		t.Fatalf("expected sequence 4, got %d", batch.Sequence)
	}
}

func TestOldestResultsAreEvictedWhenFull(t *testing.T) {
	b := open(t, filepath.Join(t.TempDir(), "results.db"), 10)
	defer b.Close()
	for i := range 15 {
		if err := b.Add(result(i)); err != nil {
			t.Fatal(err)
		}
	}
	if b.Total() != 10 {
		t.Fatalf("expected 10 results, got %d", b.Total())
	}
	batch, _ := b.NextBatch(500)
	if len(batch.Results) != 10 || batch.Results[0].Target != "5" || batch.Results[9].Target != "14" {
		t.Fatalf("expected results 5..14, got %d starting at %s", len(batch.Results), batch.Results[0].Target)
	}
	// The outstanding batch is never evicted; new results still fit up to the limit.
	for i := 15; i < 30; i++ {
		_ = b.Add(result(i))
	}
	if b.Total() != 10 {
		t.Fatalf("expected the limit to hold, got %d", b.Total())
	}
	if again, _ := b.Outstanding(); again.Sequence != batch.Sequence || len(again.Results) != 10 {
		t.Fatal("the outstanding batch must stay unchanged")
	}
}

func TestResetRestartsSequences(t *testing.T) {
	b := open(t, filepath.Join(t.TempDir(), "results.db"), 100)
	defer b.Close()
	_ = b.Add(result(1))
	_, _ = b.NextBatch(500)
	if err := b.Reset(); err != nil {
		t.Fatal(err)
	}
	if next, _ := b.NextSequence(); next != 1 || b.Total() != 0 {
		t.Fatalf("expected a clean buffer, got next %d total %d", next, b.Total())
	}
}

func outputResult(i, size int) *agentv1.CheckResult {
	r := result(i)
	r.ScriptOutput = &agentv1.ScriptOutput{Stdout: strings.Repeat("x", size), TotalBytes: uint64(size)}
	return r
}

func TestABatchStaysBelowItsByteLimit(t *testing.T) {
	b := open(t, filepath.Join(t.TempDir(), "results.db"), 100)
	defer b.Close()
	for i := range 20 {
		if err := b.Add(outputResult(i, 256*1024)); err != nil {
			t.Fatal(err)
		}
	}
	batch, err := b.NextBatch(500)
	if err != nil {
		t.Fatal(err)
	}
	if size := proto.Size(batch); size > MaxBatchBytes+1024 || len(batch.Results) == 0 || len(batch.Results) >= 20 {
		t.Fatalf("a batch must stop at about %d bytes, got %d results of %d bytes", MaxBatchBytes, len(batch.Results), size)
	}

	// One result larger than the byte limit still goes out on its own, so the queue never stalls.
	large := open(t, filepath.Join(t.TempDir(), "results.db"), 100)
	defer large.Close()
	_ = large.Add(outputResult(1, MaxBatchBytes+10))
	if one, _ := large.NextBatch(500); one == nil || len(one.Results) != 1 {
		t.Fatal("a single large result must form a batch of its own")
	}
}

func TestOldestResultsAreEvictedWhenTheByteLimitIsReached(t *testing.T) {
	path := filepath.Join(t.TempDir(), "results.db")
	b := open(t, path, 1000)
	b.SetMaxBytes(1024 * 1024)
	for i := range 10 {
		if err := b.Add(outputResult(i, 200*1024)); err != nil {
			t.Fatal(err)
		}
	}
	if b.Bytes() > 1024*1024 || b.Total() != 5 {
		t.Fatalf("expected the 5 newest results within 1 MiB, got %d results of %d bytes", b.Total(), b.Bytes())
	}
	batch, _ := b.NextBatch(500)
	if batch.Results[0].Target != "5" {
		t.Fatalf("the oldest results must be evicted first, got %s", batch.Results[0].Target)
	}
	if _, err := b.Ack(batch.Sequence); err != nil {
		t.Fatal(err)
	}
	if b.Bytes() != 0 {
		t.Fatalf("an acknowledged batch must free its bytes, %d left", b.Bytes())
	}
	_ = b.Add(outputResult(20, 1000))
	want := b.Bytes()
	b.Close()

	reopened := open(t, path, 1000)
	defer reopened.Close()
	if reopened.Bytes() != want {
		t.Fatalf("the byte count must survive a restart: %d, want %d", reopened.Bytes(), want)
	}
}
