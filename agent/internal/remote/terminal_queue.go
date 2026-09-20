package remote

import (
	"errors"
	"sync"
)

// terminalInputQueue is how many pieces of input wait for a terminal whose program does not read. A terminal takes input into a small
// kernel buffer only; when the program in it does not read (a long command), a write would block, and it must never block the session's
// frame loop, which also carries End and the idle timeout (security review of 0.3.0 step 7). Input beyond the queue is dropped: nothing
// reads it anyway.
const terminalInputQueue = 256

// queuedTerminal writes the input of a terminal on a goroutine of its own.
type queuedTerminal struct {
	Terminal
	input chan []byte
	mu    sync.Mutex
	done  bool
}

func newQueuedTerminal(t Terminal) *queuedTerminal {
	q := &queuedTerminal{Terminal: t, input: make(chan []byte, terminalInputQueue)}
	go func() {
		for data := range q.input {
			if _, err := q.Terminal.Write(data); err != nil {
				// The terminal ended; drain what is queued so no sender waits.
				for range q.input {
				}
				return
			}
		}
	}()
	return q
}

// Write queues input and never blocks.
func (q *queuedTerminal) Write(p []byte) (int, error) {
	q.mu.Lock()
	defer q.mu.Unlock()
	if q.done {
		return 0, errors.New("the terminal is closed")
	}
	data := make([]byte, len(p))
	copy(data, p)
	select {
	case q.input <- data:
		return len(p), nil
	default:
		return 0, errors.New("the terminal does not read its input")
	}
}

func (q *queuedTerminal) Close() error {
	q.mu.Lock()
	if !q.done {
		q.done = true
		close(q.input)
	}
	q.mu.Unlock()
	return q.Terminal.Close()
}
