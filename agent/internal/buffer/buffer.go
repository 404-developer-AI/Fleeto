// Package buffer keeps check results on disk until the gateway acknowledges them.
//
// Results enter the pending bucket. A batch takes up to 500 of the oldest pending results and gets the next sequence
// number in the same transaction, so a sequence number can never be reused for different content, not even after a
// crash. At most one batch exists at a time; it is deleted only when a BatchAck with its sequence arrives, and it is
// resent unchanged after a reconnect. The buffer is bounded: when full, the oldest pending results are evicted.
package buffer

import (
	"encoding/binary"
	"errors"
	"fmt"
	"log/slog"
	"sync"
	"time"

	bolt "go.etcd.io/bbolt"
	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

const (
	// DefaultMaxResults bounds the number of results kept on disk.
	DefaultMaxResults = 50000
	// MaxBatchResults is the protocol limit of results per batch.
	MaxBatchResults = 500
)

var (
	bucketPending = []byte("pending")
	bucketBatches = []byte("batches")
	bucketMeta    = []byte("meta")
	keyNextSeq    = []byte("nextSequence")
	keyNextID     = []byte("nextResultId")
)

// Buffer is the on-disk result queue.
type Buffer struct {
	db      *bolt.DB
	max     int
	logger  *slog.Logger
	mu      sync.Mutex
	pending int
	batched int
	evicted uint64
}

// Open opens or creates the buffer file.
func Open(path string, maxResults int, logger *slog.Logger) (*Buffer, error) {
	if maxResults <= 0 {
		maxResults = DefaultMaxResults
	}
	db, err := bolt.Open(path, 0o600, &bolt.Options{Timeout: 3 * time.Second})
	if err != nil {
		if errors.Is(err, bolt.ErrTimeout) {
			return nil, fmt.Errorf("the result buffer %s is in use by another agent process", path)
		}
		return nil, fmt.Errorf("open result buffer %s: %w", path, err)
	}
	b := &Buffer{db: db, max: maxResults, logger: logger}
	err = db.Update(func(tx *bolt.Tx) error {
		for _, name := range [][]byte{bucketPending, bucketBatches, bucketMeta} {
			if _, err := tx.CreateBucketIfNotExists(name); err != nil {
				return err
			}
		}
		meta := tx.Bucket(bucketMeta)
		if meta.Get(keyNextSeq) == nil {
			if err := meta.Put(keyNextSeq, u64(1)); err != nil {
				return err
			}
		}
		b.pending = tx.Bucket(bucketPending).Stats().KeyN
		return tx.Bucket(bucketBatches).ForEach(func(_, v []byte) error {
			var batch agentv1.CheckResultBatch
			if err := proto.Unmarshal(v, &batch); err == nil {
				b.batched += len(batch.GetResults())
			}
			return nil
		})
	})
	if err != nil {
		_ = db.Close()
		return nil, fmt.Errorf("initialize result buffer %s: %w", path, err)
	}
	return b, nil
}

// Close closes the file.
func (b *Buffer) Close() error {
	return b.db.Close()
}

// Add stores results. When the buffer is full the oldest pending results are evicted, and the eviction is logged.
// An error (a full disk) means the results were not stored; the caller logs it and carries on.
func (b *Buffer) Add(results ...*agentv1.CheckResult) error {
	if len(results) == 0 {
		return nil
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	if len(results) > b.max {
		results = results[len(results)-b.max:]
	}
	var evicted int
	err := b.db.Update(func(tx *bolt.Tx) error {
		pending := tx.Bucket(bucketPending)
		meta := tx.Bucket(bucketMeta)
		total := b.pending + b.batched
		overflow := total + len(results) - b.max
		if overflow > 0 {
			c := pending.Cursor()
			for k, _ := c.First(); k != nil && evicted < overflow; k, _ = c.First() {
				if err := c.Delete(); err != nil {
					return err
				}
				evicted++
			}
			if evicted < overflow {
				// Everything left is in the outstanding batch; keep the newest results that fit.
				keep := len(results) - (overflow - evicted)
				if keep <= 0 {
					return errDropped
				}
				results = results[len(results)-keep:]
			}
		}
		nextID := uint64(1)
		if v := meta.Get(keyNextID); v != nil {
			nextID = binary.BigEndian.Uint64(v)
		}
		for _, r := range results {
			data, err := proto.Marshal(r)
			if err != nil {
				return err
			}
			if err := pending.Put(u64(nextID), data); err != nil {
				return err
			}
			nextID++
		}
		return meta.Put(keyNextID, u64(nextID))
	})
	if errors.Is(err, errDropped) {
		b.logger.Warn("result buffer is full; dropped new check results", "dropped", len(results))
		return nil
	}
	if err != nil {
		return fmt.Errorf("store check results: %w", err)
	}
	b.pending += len(results) - evicted
	if evicted > 0 {
		b.evicted += uint64(evicted)
		b.logger.Warn("result buffer is full; evicted the oldest check results", "evicted", evicted, "evictedTotal", b.evicted, "max", b.max)
	}
	return nil
}

var errDropped = errors.New("dropped")

// Pending returns the number of results waiting for a batch.
func (b *Buffer) Pending() int {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.pending
}

// Total returns the number of results on disk, pending and batched.
func (b *Buffer) Total() int {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.pending + b.batched
}

// Outstanding returns the unacknowledged batch, or nil.
func (b *Buffer) Outstanding() (*agentv1.CheckResultBatch, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	var batch *agentv1.CheckResultBatch
	err := b.db.View(func(tx *bolt.Tx) error {
		var err error
		batch, err = firstBatch(tx)
		return err
	})
	return batch, err
}

// NextBatch returns the outstanding batch if there is one; otherwise it moves up to maxResults of the oldest pending
// results into a new batch with the next sequence number. It returns nil when there is nothing to send.
func (b *Buffer) NextBatch(maxResults int) (*agentv1.CheckResultBatch, error) {
	if maxResults <= 0 || maxResults > MaxBatchResults {
		maxResults = MaxBatchResults
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	var batch *agentv1.CheckResultBatch
	created := false
	err := b.db.Update(func(tx *bolt.Tx) error {
		existing, err := firstBatch(tx)
		if err != nil {
			// A batch that cannot be read can never be sent: drop it so the queue keeps moving. Its sequence
			// number stays consumed and is never reused.
			b.logger.Error("discarded an unreadable check result batch", "error", err)
			k, _ := tx.Bucket(bucketBatches).Cursor().First()
			return tx.Bucket(bucketBatches).Delete(k)
		}
		if existing != nil {
			batch = existing
			return nil
		}
		pending := tx.Bucket(bucketPending)
		if k, _ := pending.Cursor().First(); k == nil {
			return nil
		}
		meta := tx.Bucket(bucketMeta)
		seq := binary.BigEndian.Uint64(meta.Get(keyNextSeq))
		batch = &agentv1.CheckResultBatch{Sequence: seq}
		c := pending.Cursor()
		for k, v := c.First(); k != nil && len(batch.Results) < maxResults; k, v = c.First() {
			var r agentv1.CheckResult
			if err := proto.Unmarshal(v, &r); err == nil {
				batch.Results = append(batch.Results, &r)
			} else {
				b.logger.Warn("discarded an unreadable buffered check result", "error", err)
			}
			if err := c.Delete(); err != nil {
				return err
			}
			b.pending--
		}
		if len(batch.Results) == 0 {
			batch = nil
			return nil
		}
		data, err := proto.MarshalOptions{Deterministic: true}.Marshal(batch)
		if err != nil {
			return err
		}
		if err := tx.Bucket(bucketBatches).Put(u64(seq), data); err != nil {
			return err
		}
		created = true
		return meta.Put(keyNextSeq, u64(seq+1))
	})
	if err != nil {
		// The transaction rolled back; recount so the cached counters match the file again.
		b.recount()
		return nil, fmt.Errorf("create check result batch: %w", err)
	}
	if created {
		b.batched += len(batch.GetResults())
	}
	return batch, nil
}

// Ack deletes the batch with this sequence. It returns false when no such batch is outstanding (a duplicate ack).
func (b *Buffer) Ack(sequence uint64) (bool, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	removed := 0
	found := false
	err := b.db.Update(func(tx *bolt.Tx) error {
		batches := tx.Bucket(bucketBatches)
		v := batches.Get(u64(sequence))
		if v == nil {
			return nil
		}
		var batch agentv1.CheckResultBatch
		if err := proto.Unmarshal(v, &batch); err == nil {
			removed = len(batch.GetResults())
		}
		found = true
		return batches.Delete(u64(sequence))
	})
	if err != nil {
		return false, fmt.Errorf("remove acknowledged batch %d: %w", sequence, err)
	}
	b.batched -= removed
	if b.batched < 0 {
		b.batched = 0
	}
	return found, nil
}

// NextSequence returns the sequence number the next new batch will get.
func (b *Buffer) NextSequence() (uint64, error) {
	var seq uint64
	err := b.db.View(func(tx *bolt.Tx) error {
		seq = binary.BigEndian.Uint64(tx.Bucket(bucketMeta).Get(keyNextSeq))
		return nil
	})
	return seq, err
}

// Reset removes every result and batch and restarts sequences at 1. Used after a new enrollment: sequences are
// per endpoint.
func (b *Buffer) Reset() error {
	b.mu.Lock()
	defer b.mu.Unlock()
	err := b.db.Update(func(tx *bolt.Tx) error {
		for _, name := range [][]byte{bucketPending, bucketBatches, bucketMeta} {
			if err := tx.DeleteBucket(name); err != nil && !errors.Is(err, bolt.ErrBucketNotFound) {
				return err
			}
			if _, err := tx.CreateBucket(name); err != nil {
				return err
			}
		}
		return tx.Bucket(bucketMeta).Put(keyNextSeq, u64(1))
	})
	if err == nil {
		b.pending, b.batched = 0, 0
	}
	return err
}

func (b *Buffer) recount() {
	_ = b.db.View(func(tx *bolt.Tx) error {
		b.pending = tx.Bucket(bucketPending).Stats().KeyN
		b.batched = 0
		return tx.Bucket(bucketBatches).ForEach(func(_, v []byte) error {
			var batch agentv1.CheckResultBatch
			if proto.Unmarshal(v, &batch) == nil {
				b.batched += len(batch.GetResults())
			}
			return nil
		})
	})
}

func firstBatch(tx *bolt.Tx) (*agentv1.CheckResultBatch, error) {
	k, v := tx.Bucket(bucketBatches).Cursor().First()
	if k == nil {
		return nil, nil
	}
	var batch agentv1.CheckResultBatch
	if err := proto.Unmarshal(v, &batch); err != nil {
		return nil, fmt.Errorf("batch %d on disk is unreadable: %w", binary.BigEndian.Uint64(k), err)
	}
	return &batch, nil
}

func u64(v uint64) []byte {
	b := make([]byte, 8)
	binary.BigEndian.PutUint64(b, v)
	return b
}
