package remote

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
)

// Remote background operations (0.3.0 step 2): the file explorer, services and processes of the endpoint, over the same encrypted
// session as the terminal. The watchdog serves this as SYSTEM or root; the technician is already authorised for the whole endpoint (they
// can open a SYSTEM terminal), so path checks here are for correctness, not a security boundary. Every action that changes the endpoint
// or takes data off it is reported for the audit log (never a file's content).

const (
	// transferWindow is how far a transfer may run ahead of acknowledgements, so a large file never floods the relay.
	transferWindow = 4 * 1024 * 1024
	// fileChunkBytes is the size of one transfer chunk; it fits well inside the frame limit.
	fileChunkBytes = 256 * 1024
	// maxListEntries is how many directory entries one listing returns.
	maxListEntries = 5000
	// partSuffix marks an upload that is still in progress.
	partSuffix = ".fleeto-part"
)

// transferStallTimeout is how long a download waits for the browser to acknowledge before it gives up and closes the file, so a browser
// that stopped listening never keeps a file open (and undeletable on Windows) for the rest of the session. Tests shorten it.
var transferStallTimeout = 2 * time.Minute

// requestBody is the shared shape of a FrameRequest. Fields not used by an op stay zero.
type requestBody struct {
	ID        string `json:"id"`
	Op        string `json:"op"`
	Path      string `json:"path"`
	Name      string `json:"name"`
	Dest      string `json:"dest"`
	Recursive bool   `json:"recursive"`
	Offset    int64  `json:"offset"`
	Size      int64  `json:"size"`
	Action    string `json:"action"`
	StartType string `json:"startType"`
	Pid       int32  `json:"pid"`
	Transfer  uint32 `json:"transfer"`
	// Batch and Index address pasted and copied files in a remote control session (0.3.0 step 4).
	Batch int `json:"batch"`
	Index int `json:"index"`
}

type transferBody struct {
	Transfer uint32 `json:"transfer"`
	Kind     string `json:"kind"`
	Bytes    int64  `json:"bytes"`
	Error    string `json:"error"`
}

// entry is one item in a directory listing.
type entry struct {
	Name     string `json:"name"`
	Dir      bool   `json:"dir"`
	Size     int64  `json:"size"`
	Modified int64  `json:"modified"`
	Symlink  bool   `json:"symlink,omitempty"`
}

type background struct {
	s  *Session
	mu sync.Mutex

	downloads map[uint32]*download
	uploads   map[uint32]*upload
	nextID    uint32
	closed    bool
	// batches are the folders of files pasted in a remote control session, by the number the browser got.
	batches map[int]string
	// busy holds a place for every request that runs; a full channel refuses the next one.
	busy chan struct{}
}

// Limits of one session (security review of 0.3.0 step 7): a browser that asks too much at once gets an answer, not the endpoint's
// memory and file handles.
const (
	// maxRequests is how many requests of a session run at the same time.
	maxRequests = 16
	// maxTransfers is how many uploads and downloads of a session are open at the same time.
	maxTransfers = 16
)

var errBusy = errors.New("the endpoint is busy with other operations of this session; try again when they finish")

func newBackground(s *Session) *background {
	return &background{s: s, downloads: map[uint32]*download{}, uploads: map[uint32]*upload{}, batches: map[int]string{},
		busy: make(chan struct{}, maxRequests)}
}

// run runs a request on a goroutine of its own when a place is free, and answers at once that the endpoint is busy when none is.
func (b *background) run(ctx context.Context, req requestBody, label string, serve func(context.Context, requestBody) (map[string]any, error)) {
	select {
	case b.busy <- struct{}{}:
	default:
		_ = b.s.sendJSON(ctx, FrameResponse, map[string]any{"id": req.ID, "ok": false, "error": errBusy.Error()})
		return
	}
	safego.Go(b.s.opts.Logger, label, func() {
		defer func() { <-b.busy }()
		result, err := serve(ctx, req)
		reply := map[string]any{"id": req.ID, "ok": err == nil}
		if err != nil {
			reply["error"] = err.Error()
		}
		for k, v := range result {
			reply[k] = v
		}
		_ = b.s.sendJSON(ctx, FrameResponse, reply)
	})
}

// transfersFull reports, with b.mu held, whether the session has the most transfers open it may.
func (b *background) transfersFull() bool {
	return len(b.uploads)+len(b.downloads) >= maxTransfers
}

func (b *background) handle(ctx context.Context, frameType byte, body []byte) {
	switch frameType {
	case FrameRequest:
		var req requestBody
		if json.Unmarshal(body, &req) != nil || req.ID == "" {
			return
		}
		// Each request runs on its own goroutine: a directory scan or a copy must not block the session's frame loop.
		b.run(ctx, req, "remote background request", b.dispatch)
	case FrameChunk:
		if len(body) < 4 {
			return
		}
		b.chunk(binary.BigEndian.Uint32(body[:4]), body[4:])
	case FrameTransfer:
		var t transferBody
		if json.Unmarshal(body, &t) == nil {
			b.transferControl(t)
		}
	}
}

func (b *background) dispatch(ctx context.Context, req requestBody) (map[string]any, error) {
	switch req.Op {
	case "home":
		return map[string]any{"path": defaultPath(), "roots": driveRoots(), "separator": string(os.PathSeparator)}, nil
	case "list":
		return b.list(req.Path)
	case "mkdir":
		return b.mkdir(req)
	case "rename":
		return b.rename(req)
	case "delete":
		return b.delete(req)
	case "copy":
		return b.copy(ctx, req)
	case "download":
		return b.startDownload(ctx, req, "file.download")
	case "upload":
		return b.startUpload(req, "file.upload")
	case "cancel":
		b.cancelTransfer(req.Transfer, "cancelled by the technician")
		return nil, nil
	case "services":
		return b.services()
	case "service":
		return b.serviceAction(ctx, req)
	case "processes":
		return b.processes(ctx)
	case "process":
		return b.processAction(req)
	default:
		return nil, fmt.Errorf("this endpoint does not support %q; update the agent", req.Op)
	}
}

// ---------------------------------------------------------------------------------------------------------------
// Files
// ---------------------------------------------------------------------------------------------------------------

// cleanPath requires an absolute path and cleans it. It is not a security boundary (the technician has full access anyway); it keeps
// listings and operations predictable and rejects a NUL byte, which the OS would reject anyway.
func cleanPath(p string) (string, error) {
	if p == "" {
		return "", errors.New("no path was given")
	}
	if strings.ContainsRune(p, 0) {
		return "", errors.New("the path is invalid")
	}
	if !filepath.IsAbs(p) {
		return "", errors.New("the path must be absolute")
	}
	return filepath.Clean(p), nil
}

func (b *background) list(p string) (map[string]any, error) {
	dir, err := cleanPath(p)
	if err != nil {
		return nil, err
	}
	items, err := os.ReadDir(dir)
	if err != nil {
		return nil, listError(err)
	}
	entries := make([]entry, 0, len(items))
	for _, item := range items {
		if len(entries) >= maxListEntries {
			break
		}
		e := entry{Name: item.Name(), Dir: item.IsDir(), Symlink: item.Type()&os.ModeSymlink != 0}
		if info, err := item.Info(); err == nil {
			e.Size = info.Size()
			e.Modified = info.ModTime().UnixMilli()
			if e.Symlink {
				// Follow the link only to tell a directory from a file, so the browser shows the right icon.
				if target, err := os.Stat(filepath.Join(dir, item.Name())); err == nil {
					e.Dir = target.IsDir()
				}
			}
		}
		entries = append(entries, e)
	}
	sort.Slice(entries, func(i, j int) bool {
		if entries[i].Dir != entries[j].Dir {
			return entries[i].Dir
		}
		return strings.ToLower(entries[i].Name) < strings.ToLower(entries[j].Name)
	})
	return map[string]any{"path": dir, "parent": parentPath(dir), "entries": entries, "truncated": len(items) > len(entries)}, nil
}

func (b *background) mkdir(req requestBody) (map[string]any, error) {
	dir, err := cleanPath(req.Path)
	if err != nil {
		return nil, err
	}
	name, err := safeName(req.Name)
	if err != nil {
		return nil, err
	}
	target := filepath.Join(dir, name)
	if err := os.Mkdir(target, 0o755); err != nil {
		return nil, opError("create the folder", err)
	}
	b.report("file.mkdir", target, "")
	return nil, nil
}

func (b *background) rename(req requestBody) (map[string]any, error) {
	src, err := cleanPath(req.Path)
	if err != nil {
		return nil, err
	}
	name, err := safeName(req.Name)
	if err != nil {
		return nil, err
	}
	target := filepath.Join(filepath.Dir(src), name)
	if _, err := os.Lstat(target); err == nil {
		return nil, fmt.Errorf("%s already exists here", name)
	}
	if err := os.Rename(src, target); err != nil {
		return nil, opError("rename", err)
	}
	b.report("file.rename", src, "to "+name)
	return map[string]any{"path": target}, nil
}

func (b *background) delete(req requestBody) (map[string]any, error) {
	target, err := cleanPath(req.Path)
	if err != nil {
		return nil, err
	}
	if parentPath(target) == "" {
		return nil, errors.New("a drive or the root cannot be deleted")
	}
	info, err := os.Lstat(target)
	if err != nil {
		return nil, opError("delete", err)
	}
	if info.IsDir() && !info.Mode().IsRegular() {
		if req.Recursive {
			err = os.RemoveAll(target)
		} else {
			err = os.Remove(target) // fails when not empty
		}
	} else {
		err = os.Remove(target)
	}
	if err != nil {
		return nil, opError("delete", err)
	}
	b.report("file.delete", target, "")
	return nil, nil
}

func (b *background) copy(ctx context.Context, req requestBody) (map[string]any, error) {
	src, err := cleanPath(req.Path)
	if err != nil {
		return nil, err
	}
	dest, err := cleanPath(req.Dest)
	if err != nil {
		return nil, err
	}
	info, err := os.Lstat(src)
	if err != nil {
		return nil, opError("copy", err)
	}
	// When the destination is an existing directory, copy into it under the source name.
	if d, err := os.Stat(dest); err == nil && d.IsDir() {
		dest = filepath.Join(dest, filepath.Base(src))
	}
	if pathsOverlap(src, dest) {
		return nil, errors.New("a file or folder cannot be copied onto itself or into itself")
	}
	if info.IsDir() {
		err = copyTree(ctx, src, dest)
	} else {
		err = copyFile(src, dest, info.Mode())
	}
	if err != nil {
		return nil, opError("copy", err)
	}
	b.report("file.copy", src, "to "+dest)
	return map[string]any{"path": dest}, nil
}

// ---------------------------------------------------------------------------------------------------------------
// Sending frames
// ---------------------------------------------------------------------------------------------------------------

func (b *background) sendTransfer(transfer uint32, kind string, bytes int64, errText string) {
	ctx, cancel := context.WithTimeout(context.Background(), writeTimeout)
	defer cancel()
	body := transferBody{Transfer: transfer, Kind: kind, Bytes: bytes, Error: errText}
	_ = b.s.sendJSON(ctx, FrameTransfer, body)
}

func (b *background) sendChunk(transfer uint32, data []byte) error {
	frame := make([]byte, 4, 4+len(data))
	binary.BigEndian.PutUint32(frame, transfer)
	frame = append(frame, data...)
	ctx, cancel := context.WithTimeout(context.Background(), writeTimeout)
	defer cancel()
	return b.s.sendChunkFrame(ctx, frame)
}

func (b *background) report(action, target, detail string) {
	if b.s.opts.Report != nil {
		b.s.opts.Report(action, target, detail)
	}
}

func (b *background) close() {
	b.mu.Lock()
	b.closed = true
	downloads := b.downloads
	uploads := b.uploads
	b.downloads = map[uint32]*download{}
	b.uploads = map[uint32]*upload{}
	b.mu.Unlock()
	for _, d := range downloads {
		d.cancel("the session ended")
	}
	for _, u := range uploads {
		u.abort()
	}
}

func (b *background) transferControl(t transferBody) {
	switch t.Kind {
	case "ack":
		b.mu.Lock()
		d := b.downloads[t.Transfer]
		b.mu.Unlock()
		if d != nil {
			d.ack(t.Bytes)
		}
	case "end":
		b.finishUpload(t.Transfer, t.Error)
	case "cancel":
		b.cancelTransfer(t.Transfer, "cancelled by the technician")
	}
}

func (b *background) cancelTransfer(transfer uint32, reason string) {
	b.mu.Lock()
	d := b.downloads[transfer]
	u := b.uploads[transfer]
	delete(b.downloads, transfer)
	delete(b.uploads, transfer)
	b.mu.Unlock()
	if d != nil {
		d.cancel(reason)
	}
	if u != nil {
		u.abort()
	}
}

func (b *background) newTransferID() uint32 {
	b.nextID++
	return b.nextID
}

// listError turns an os.ReadDir error into a message with a next step.
func listError(err error) error {
	switch {
	case errors.Is(err, os.ErrNotExist):
		return errors.New("this folder no longer exists")
	case errors.Is(err, os.ErrPermission):
		return errors.New("this folder cannot be opened")
	default:
		var pathErr *os.PathError
		if errors.As(err, &pathErr) {
			return fmt.Errorf("this folder cannot be opened: %s", pathErr.Err)
		}
		return errors.New("this folder cannot be opened")
	}
}

func opError(what string, err error) error {
	switch {
	case errors.Is(err, os.ErrNotExist):
		return fmt.Errorf("cannot %s: it no longer exists", what)
	case errors.Is(err, os.ErrPermission):
		return fmt.Errorf("cannot %s: access is denied", what)
	case errors.Is(err, os.ErrExist):
		return fmt.Errorf("cannot %s: it already exists", what)
	case inUse(err):
		return fmt.Errorf("cannot %s: another program has it open; close that program or end its process and try again", what)
	default:
		return fmt.Errorf("cannot %s", what)
	}
}

// safeName rejects a name that is empty or contains a path separator, so an operation stays in one directory.
func safeName(name string) (string, error) {
	name = strings.TrimSpace(name)
	if name == "" || name == "." || name == ".." {
		return "", errors.New("enter a name")
	}
	if strings.ContainsAny(name, `/\`) || strings.ContainsRune(name, 0) {
		return "", errors.New("a name cannot contain a path separator")
	}
	return name, nil
}

func parentPath(p string) string {
	parent := filepath.Dir(p)
	if parent == p {
		return ""
	}
	return parent
}

// pathsOverlap reports whether a copy of src to dest would copy onto itself or into itself (a folder into one of its own folders, which
// would copy without end).
func pathsOverlap(src, dest string) bool {
	if samePathName(src, dest) {
		return true
	}
	prefix := src
	if !strings.HasSuffix(prefix, string(filepath.Separator)) {
		prefix += string(filepath.Separator)
	}
	return len(dest) > len(prefix) && samePathName(dest[:len(prefix)], prefix)
}

func copyFile(src, dest string, mode os.FileMode) error {
	in, err := os.Open(src) // #nosec G304 -- an absolute path the authorised technician chose on their own endpoint.
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.OpenFile(dest, os.O_WRONLY|os.O_CREATE|os.O_EXCL, mode.Perm()) // #nosec G304
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		_ = out.Close()
		_ = os.Remove(dest)
		return err
	}
	return out.Close()
}

func copyTree(ctx context.Context, src, dest string) error {
	return filepath.WalkDir(src, func(p string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if ctx.Err() != nil {
			return ctx.Err()
		}
		rel, err := filepath.Rel(src, p)
		if err != nil {
			return err
		}
		target := filepath.Join(dest, rel)
		if d.IsDir() {
			return os.MkdirAll(target, 0o755)
		}
		info, err := d.Info()
		if err != nil {
			return err
		}
		if !info.Mode().IsRegular() {
			return nil // skip symlinks, devices and sockets
		}
		return copyFile(p, target, info.Mode())
	})
}
