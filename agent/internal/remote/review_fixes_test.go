package remote

import (
	"io"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"
)

// Fixes of the security review of 0.3.0 step 7.

func TestAFolderIsNeverCopiedIntoItself(t *testing.T) {
	bs := startBackground(t)
	src := filepath.Join(t.TempDir(), "a")
	if err := os.MkdirAll(filepath.Join(src, "b"), 0o700); err != nil {
		t.Fatal(err)
	}
	if bs.request("copy", map[string]any{"path": src, "dest": filepath.Join(src, "b")})["ok"] == true {
		t.Fatal("a folder was copied into one of its own folders")
	}
	if !pathsOverlap(src, src) || pathsOverlap(src, src+"-copy") {
		t.Fatal("pathsOverlap")
	}
}

func TestAServiceNameThatLooksLikeAnOptionIsRefused(t *testing.T) {
	if _, err := serviceName("-Hroot@example"); err == nil {
		t.Fatal("a name starting with a dash was accepted")
	}
	if _, err := serviceName("ssh"); err != nil {
		t.Fatal(err)
	}
}

// blockingTerminal is a terminal whose program never reads its input.
type blockingTerminal struct {
	release chan struct{}
	closed  sync.Once
}

func (b *blockingTerminal) Read([]byte) (int, error) { <-b.release; return 0, io.EOF }
func (b *blockingTerminal) Write(p []byte) (int, error) {
	<-b.release
	return len(p), nil
}
func (b *blockingTerminal) Resize(int, int) error { return nil }
func (b *blockingTerminal) Close() error {
	b.closed.Do(func() { close(b.release) })
	return nil
}
func (b *blockingTerminal) PTY() bool          { return true }
func (b *blockingTerminal) Wait() (int, error) { <-b.release; return 0, nil }

func TestTerminalInputNeverBlocksWhenTheProgramDoesNotRead(t *testing.T) {
	q := newQueuedTerminal(&blockingTerminal{release: make(chan struct{})})
	done := make(chan struct{})
	go func() {
		for i := 0; i < terminalInputQueue*4; i++ {
			_, _ = q.Write(make([]byte, 4096))
		}
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("writing to a terminal that does not read blocked the caller")
	}
	if err := q.Close(); err != nil {
		t.Fatal(err)
	}
	if _, err := q.Write([]byte("x")); err == nil {
		t.Fatal("a closed terminal took input")
	}
}
