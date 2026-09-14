// Package backoff implements exponential backoff with full jitter.
package backoff

import (
	"math/rand/v2"
	"time"
)

// Backoff grows from Base to Max. Delay returns a random duration in [0, current ceiling] (full jitter), so many
// agents that lose the gateway at the same moment do not reconnect in the same second.
type Backoff struct {
	Base    time.Duration
	Max     time.Duration
	attempt int
}

// New returns a backoff starting at base and capped at max.
func New(base, max time.Duration) *Backoff {
	return &Backoff{Base: base, Max: max}
}

// Ceiling returns the upper bound for the next delay without advancing.
func (b *Backoff) Ceiling() time.Duration {
	ceiling := b.Base
	for i := 0; i < b.attempt && ceiling < b.Max; i++ {
		ceiling *= 2
	}
	if ceiling > b.Max {
		ceiling = b.Max
	}
	return ceiling
}

// Next returns the next delay and advances the attempt counter.
func (b *Backoff) Next() time.Duration {
	ceiling := b.Ceiling()
	if b.attempt < 64 {
		b.attempt++
	}
	if ceiling <= 0 {
		return 0
	}
	return time.Duration(rand.Int64N(int64(ceiling) + 1))
}

// Reset starts again at Base, after a connection has been healthy.
func (b *Backoff) Reset() {
	b.attempt = 0
}
