package remote

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// Files on the clipboard of a remote control session (0.3.0 step 4). The technician pastes or drags files into the window: the browser
// asks for a batch, uploads each file into that batch's folder (created by the screen, readable by the user of the Windows session only,
// deleted when the session ends) and then places the batch on the endpoint clipboard, as RDP does. Files copied on the endpoint are offered
// to the browser by index; the technician can download those and nothing else. Uploads and downloads use the transfers of remote
// background and are audited as clipboard actions.

// maxFilesPerBatch bounds how many files one paste places on the clipboard.
const maxFilesPerBatch = 100

var errClipboardOff = errors.New("the clipboard is turned off for this endpoint by policy")

// handleControl serves a request in a remote control session: only the clipboard files, whatever else the browser asks.
func (b *background) handleControl(ctx context.Context, body []byte) {
	var req requestBody
	if json.Unmarshal(body, &req) != nil || req.ID == "" {
		return
	}
	b.run(ctx, req, "remote control clipboard request", b.dispatchControl)
}

func (b *background) dispatchControl(ctx context.Context, req requestBody) (map[string]any, error) {
	if req.Op == "cancel" {
		b.cancelTransfer(req.Transfer, "cancelled by the technician")
		return nil, nil
	}
	if !strings.HasPrefix(req.Op, "clipboard.") {
		return nil, fmt.Errorf("%q is not available in a remote control session", req.Op)
	}
	if !b.s.opts.Token.GetClipboardEnabled() {
		return nil, errClipboardOff
	}
	files, ok := b.s.screen.(ClipboardFiles)
	if !ok || b.s.screen == nil {
		return nil, errors.New("this endpoint cannot transfer clipboard files; update the agent")
	}
	switch req.Op {
	case "clipboard.begin":
		dir, err := files.StagingBatch()
		if err != nil {
			return nil, err
		}
		b.mu.Lock()
		defer b.mu.Unlock()
		id := len(b.batches) + 1
		b.batches[id] = dir
		return map[string]any{"batch": id}, nil
	case "clipboard.upload":
		dir, err := b.batch(req.Batch)
		if err != nil {
			return nil, err
		}
		if entries, err := os.ReadDir(dir); err == nil && len(entries) >= maxFilesPerBatch {
			return nil, fmt.Errorf("one paste can carry at most %d files", maxFilesPerBatch)
		}
		return b.startUpload(requestBody{Path: dir, Name: req.Name, Size: req.Size, Offset: req.Offset}, "clipboard.upload")
	case "clipboard.place":
		dir, err := b.batch(req.Batch)
		if err != nil {
			return nil, err
		}
		paths, err := stagedFiles(dir)
		if err != nil {
			return nil, err
		}
		if err := files.PlaceFiles(paths); err != nil {
			return nil, err
		}
		return map[string]any{"count": len(paths)}, nil
	case "clipboard.download":
		file, path, err := files.OpenCopiedFile(req.Index)
		if err != nil {
			return nil, err
		}
		return b.serveDownload(ctx, file, path, req.Offset, "clipboard.download")
	default:
		return nil, fmt.Errorf("%q is not available in a remote control session", req.Op)
	}
}

func (b *background) batch(id int) (string, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	dir, ok := b.batches[id]
	if !ok {
		return "", errors.New("these files were not prepared on the endpoint; paste them again")
	}
	return dir, nil
}

// stagedFiles lists the finished uploads of a batch: regular files that are not still being written.
func stagedFiles(dir string) ([]string, error) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, errors.New("the pasted files are no longer on the endpoint; paste them again")
	}
	var paths []string
	for _, e := range entries {
		if e.Type().IsRegular() && !strings.HasSuffix(e.Name(), partSuffix) {
			paths = append(paths, filepath.Join(dir, e.Name()))
		}
	}
	if len(paths) == 0 {
		return nil, errors.New("none of the pasted files arrived on the endpoint")
	}
	return paths, nil
}
