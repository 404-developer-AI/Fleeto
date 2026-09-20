package remote

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"sync/atomic"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
)

// File transfers over the encrypted session (0.3.0 step 2). A download streams a file from the endpoint to the browser with flow
// control (never more than transferWindow bytes ahead of acknowledgements); an upload writes to a "<path>.fleeto-part" file and renames
// it into place when the browser signals the end. Both take a byte offset, so a transfer resumes after the session was re-established.

// ---------------------------------------------------------------------------------------------------------------
// Download (endpoint to browser)
// ---------------------------------------------------------------------------------------------------------------

type download struct {
	b     *background
	id    uint32
	file  *os.File
	size  int64
	acked atomic.Int64
	wake  chan struct{}
	done  chan struct{}
	once  sync.Once
}

func (b *background) startDownload(ctx context.Context, req requestBody, action string) (map[string]any, error) {
	p, err := cleanPath(req.Path)
	if err != nil {
		return nil, err
	}
	info, err := os.Stat(p)
	if err != nil {
		return nil, opError("download", err)
	}
	if info.IsDir() {
		return nil, errors.New("a folder cannot be downloaded; open it instead")
	}
	if !info.Mode().IsRegular() {
		return nil, errors.New("only a regular file can be downloaded")
	}
	file, err := os.Open(p) // #nosec G304 -- an absolute path the authorised technician chose on their own endpoint.
	if err != nil {
		return nil, opError("download", err)
	}
	return b.serveDownload(ctx, file, p, req.Offset, action)
}

// serveDownload sends an opened file. The checks use the opened file itself, so it cannot be swapped between check and read.
func (b *background) serveDownload(ctx context.Context, file *os.File, target string, offset int64, action string) (map[string]any, error) {
	info, err := file.Stat()
	if err != nil || !info.Mode().IsRegular() {
		_ = file.Close()
		return nil, errors.New("only a regular file can be downloaded")
	}
	if info.Size() > b.maxFileBytes() {
		_ = file.Close()
		return nil, fmt.Errorf("this file is larger than the %d MB a transfer may carry", b.maxFileBytes()/(1024*1024))
	}
	if offset < 0 || offset > info.Size() {
		_ = file.Close()
		return nil, errors.New("the download cannot resume from there")
	}
	if _, err := file.Seek(offset, 0); err != nil {
		_ = file.Close()
		return nil, opError("download", err)
	}

	d := &download{b: b, file: file, size: info.Size(), wake: make(chan struct{}, 1), done: make(chan struct{})}
	d.acked.Store(offset)
	b.mu.Lock()
	if b.closed {
		b.mu.Unlock()
		_ = file.Close()
		return nil, errors.New("the session is closing")
	}
	if b.transfersFull() {
		b.mu.Unlock()
		_ = file.Close()
		return nil, errBusy
	}
	d.id = b.newTransferID()
	b.downloads[d.id] = d
	b.mu.Unlock()

	safego.Go(b.s.opts.Logger, "remote download", func() { d.run(ctx, offset) })
	// Only the download itself is the audited action, once, with the whole file as the target.
	b.report(action, target, "")
	return map[string]any{"transfer": d.id, "size": info.Size(), "name": info.Name(), "modified": info.ModTime().UnixMilli()}, nil
}

func (d *download) run(ctx context.Context, offset int64) {
	defer func() {
		_ = d.file.Close()
		d.b.mu.Lock()
		delete(d.b.downloads, d.id)
		d.b.mu.Unlock()
	}()
	buffer := make([]byte, fileChunkBytes)
	sent := offset
	for sent < d.size {
		if err := d.waitForWindow(ctx, sent); err != nil {
			d.b.sendTransfer(d.id, "error", sent, err.Error())
			return
		}
		n, err := d.file.Read(buffer)
		if n > 0 {
			if werr := d.b.sendChunk(d.id, buffer[:n]); werr != nil {
				return // the relay is gone; the session ends on its own
			}
			sent += int64(n)
		}
		if err != nil {
			if errors.Is(err, os.ErrClosed) {
				return
			}
			d.b.sendTransfer(d.id, "error", sent, "the file could not be read")
			return
		}
	}
	d.b.sendTransfer(d.id, "end", sent, "")
}

// waitForWindow blocks while the browser is more than one window behind, so a large file never floods the relay. It gives up when no
// acknowledgement arrives within transferStallTimeout.
func (d *download) waitForWindow(ctx context.Context, sent int64) error {
	for sent-d.acked.Load() > transferWindow {
		timer := time.NewTimer(transferStallTimeout)
		select {
		case <-d.wake:
			timer.Stop()
		case <-timer.C:
			return errors.New("the browser stopped receiving the download")
		case <-d.done:
			timer.Stop()
			return errors.New("cancelled")
		case <-ctx.Done():
			timer.Stop()
			return ctx.Err()
		}
	}
	return nil
}

func (d *download) ack(bytes int64) {
	for {
		current := d.acked.Load()
		if bytes <= current {
			break
		}
		if d.acked.CompareAndSwap(current, bytes) {
			break
		}
	}
	select {
	case d.wake <- struct{}{}:
	default:
	}
}

func (d *download) cancel(string) {
	d.once.Do(func() { close(d.done) })
}

// ---------------------------------------------------------------------------------------------------------------
// Upload (browser to endpoint)
// ---------------------------------------------------------------------------------------------------------------

type upload struct {
	b        *background
	id       uint32
	file     *os.File
	part     *partFile
	destName string
	destPath string
	written  int64
	size     int64
	acked    int64
	failed   bool
	action   string
}

func (b *background) startUpload(req requestBody, action string) (map[string]any, error) {
	dir, err := cleanPath(req.Path)
	if err != nil {
		return nil, err
	}
	name, err := safeName(req.Name)
	if err != nil {
		return nil, err
	}
	if req.Size < 0 || req.Size > b.maxFileBytes() {
		return nil, fmt.Errorf("a file may be at most %d MB", b.maxFileBytes()/(1024*1024))
	}
	dest := filepath.Join(dir, name)
	if info, err := os.Lstat(dest); err == nil && info.IsDir() {
		return nil, fmt.Errorf("%s is a folder", name)
	}
	// The part file is made without following links (partfile_*.go): the folder may belong to a local user.
	part, resumed, err := openPart(dir, name+partSuffix, req.Offset > 0)
	if err != nil {
		return nil, opError("upload", err)
	}
	file := part.file

	// Resume: keep at most the bytes the browser confirms it already sent, and continue there.
	resume := int64(0)
	if resumed {
		if info, err := file.Stat(); err == nil {
			resume = min(req.Offset, info.Size())
		}
	}
	if err := file.Truncate(resume); err != nil {
		_ = file.Close()
		part.discard()
		return nil, opError("upload", err)
	}
	if _, err := file.Seek(resume, 0); err != nil {
		_ = file.Close()
		part.discard()
		return nil, opError("upload", err)
	}

	u := &upload{b: b, file: file, part: part, destName: name, destPath: dest, written: resume, size: req.Size, acked: resume, action: action}
	b.mu.Lock()
	if b.closed {
		b.mu.Unlock()
		_ = file.Close()
		part.discard()
		return nil, errors.New("the session is closing")
	}
	if b.transfersFull() {
		b.mu.Unlock()
		_ = file.Close()
		part.discard()
		return nil, errBusy
	}
	u.id = b.newTransferID()
	b.uploads[u.id] = u
	b.mu.Unlock()
	return map[string]any{"transfer": u.id, "resumeOffset": resume}, nil
}

// chunk writes an upload chunk. It runs in the session frame loop, so it stays ordered with the end signal; writes are small and fast.
func (b *background) chunk(transfer uint32, data []byte) {
	b.mu.Lock()
	u := b.uploads[transfer]
	b.mu.Unlock()
	if u == nil || u.failed {
		return
	}
	if u.written+int64(len(data)) > u.size {
		u.fail("the upload sent more data than it announced")
		return
	}
	if _, err := u.file.Write(data); err != nil {
		u.fail("the file could not be written: " + err.Error())
		return
	}
	u.written += int64(len(data))
	// Acknowledge every half window, so the browser keeps at most one window in flight.
	if u.written-u.acked >= transferWindow/2 || u.written == u.size {
		u.acked = u.written
		b.sendTransfer(transfer, "ack", u.written, "")
	}
}

func (b *background) finishUpload(transfer uint32, browserError string) {
	b.mu.Lock()
	u := b.uploads[transfer]
	delete(b.uploads, transfer)
	b.mu.Unlock()
	if u == nil {
		return
	}
	if browserError != "" || u.failed {
		u.abort()
		return
	}
	if u.written != u.size {
		// A file that is not whole never replaces anything.
		u.failWith("the upload ended before the whole file arrived")
		return
	}
	if err := u.file.Sync(); err != nil {
		u.failWith("the file could not be saved")
		return
	}
	if err := u.file.Close(); err != nil {
		u.part.discard()
		b.sendTransfer(transfer, "error", u.written, "the file could not be saved")
		return
	}
	if err := u.part.commit(u.destName); err != nil {
		b.sendTransfer(transfer, "error", u.written, opError("save the upload", err).Error())
		return
	}
	b.report(u.action, u.destPath, "")
	b.sendTransfer(transfer, "end", u.written, "")
}

func (u *upload) fail(reason string) {
	u.failWith(reason)
}

func (u *upload) failWith(reason string) {
	if u.failed {
		return
	}
	u.failed = true
	_ = u.file.Close()
	u.part.discard()
	u.b.sendTransfer(u.id, "error", u.written, reason)
}

func (u *upload) abort() {
	if u.failed {
		return
	}
	u.failed = true
	_ = u.file.Close()
	u.part.discard()
}

func (b *background) maxFileBytes() int64 {
	if b.s.opts.Token != nil {
		return b.s.opts.Token.MaxFileBytes()
	}
	return DefaultMaxFileBytes
}
