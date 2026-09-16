package update

import (
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/release"
)

// ReleasesPath is the gateway path of release binaries.
const ReleasesPath = "/v1/releases"

// RetryLaterError means the gateway is busy (503) or the endpoint downloaded too often (429).
type RetryLaterError struct {
	After time.Duration
}

func (e *RetryLaterError) Error() string {
	return fmt.Sprintf("the gateway asks to retry the download in %s", e.After)
}

// TransientError marks a failure that passes on its own within minutes: the gateway or the signer could not answer right now. The
// updater retries it soon with backoff; any other failure waits an hour.
type TransientError struct {
	Err error
}

func (e *TransientError) Error() string { return e.Err.Error() }

func (e *TransientError) Unwrap() error { return e.Err }

// Transient marks err as transient; nil stays nil.
func Transient(err error) error {
	if err == nil {
		return nil
	}
	return &TransientError{Err: err}
}

// IsTransient reports whether err, or an error it wraps, is transient.
func IsTransient(err error) bool {
	var transient *TransientError
	return errors.As(err, &transient)
}

// Download fetches one binary of a verified release from the gateway into dest. The file is written to a temporary name, checked
// against the size and SHA-256 of the manifest while it is written, and renamed into place only when both match.
func Download(ctx context.Context, client *http.Client, server, version string, b release.Binary, dest string, access platform.Access) error {
	if err := os.MkdirAll(filepath.Dir(dest), 0o700); err != nil {
		return fmt.Errorf("create %s: %w", filepath.Dir(dest), err)
	}
	url := "https://" + server + ReleasesPath + "/" + version + "/" + b.File
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	resp, err := client.Do(req)
	if err != nil {
		if ctx.Err() != nil {
			return err
		}
		// No connection or no answer: the gateway is restarting or the network is down for now.
		return Transient(fmt.Errorf("download %s: %w", b.File, err))
	}
	defer resp.Body.Close()
	switch resp.StatusCode {
	case http.StatusOK:
	case http.StatusServiceUnavailable, http.StatusTooManyRequests:
		after := time.Minute
		if seconds, err := strconv.Atoi(resp.Header.Get("Retry-After")); err == nil && seconds > 0 && seconds <= 24*3600 {
			after = time.Duration(seconds) * time.Second
		}
		return &RetryLaterError{After: after}
	case http.StatusInternalServerError, http.StatusBadGateway, http.StatusGatewayTimeout:
		return Transient(fmt.Errorf("download %s: the gateway answered HTTP %d", b.File, resp.StatusCode))
	default:
		return fmt.Errorf("download %s: the gateway answered HTTP %d", b.File, resp.StatusCode)
	}

	tmp := dest + ".part"
	f, err := os.OpenFile(tmp, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o700)
	if err != nil {
		return fmt.Errorf("create %s: %w", tmp, err)
	}
	hash := sha256.New()
	body := &readErrors{r: io.LimitReader(resp.Body, b.Size+1)}
	written, copyErr := io.Copy(io.MultiWriter(f, hash), body)
	syncErr := f.Sync()
	closeErr := f.Close()
	fail := func(err error) error {
		_ = os.Remove(tmp)
		return err
	}
	if body.err != nil && ctx.Err() == nil {
		// The connection dropped during the download.
		return fail(Transient(fmt.Errorf("download %s: %w", b.File, body.err)))
	}
	if err := errors.Join(copyErr, syncErr, closeErr); err != nil {
		return fail(fmt.Errorf("write %s: %w", tmp, err))
	}
	if written != b.Size {
		return fail(fmt.Errorf("the downloaded %s has %d bytes; the signed manifest lists %d", b.File, written, b.Size))
	}
	if !equalHex(hex.EncodeToString(hash.Sum(nil)), b.SHA256) {
		return fail(fmt.Errorf("the downloaded %s does not match the SHA-256 in the signed manifest", b.File))
	}
	if err := platform.ProtectExecutable(tmp, access); err != nil {
		return fail(err)
	}
	if err := os.Rename(tmp, dest); err != nil {
		return fail(fmt.Errorf("move %s into place: %w", dest, err))
	}
	return nil
}

// VerifyFile checks that path has exactly size bytes with the given SHA-256. Used again right before a staged binary is installed.
func VerifyFile(path, sha string, size int64) error {
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	hash := sha256.New()
	n, err := io.Copy(hash, io.LimitReader(f, size+1))
	if err != nil {
		return fmt.Errorf("read %s: %w", path, err)
	}
	if n != size || !equalHex(hex.EncodeToString(hash.Sum(nil)), sha) {
		return fmt.Errorf("%s does not match the signed manifest", filepath.Base(path))
	}
	return nil
}

// ProbeVersion runs "<exe> version --short" and returns the version the binary reports. A verified binary is run this way before it
// replaces the installed one, so a release whose binary reports another version than its manifest is never installed.
func ProbeVersion(ctx context.Context, exe string) (string, error) {
	ctx, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, exe, "version", "--short")
	var out limitedBuffer
	cmd.Stdout = &out
	cmd.Stderr = io.Discard
	if err := cmd.Run(); err != nil {
		return "", fmt.Errorf("run %s version: %w", filepath.Base(exe), err)
	}
	version := strings.TrimSpace(out.String())
	if _, ok := release.ParseVersion(version); !ok {
		return "", fmt.Errorf("%s reports an invalid version %q", filepath.Base(exe), truncate(version, 60))
	}
	return version, nil
}

func equalHex(a, b string) bool {
	return len(a) == len(b) && subtle.ConstantTimeCompare([]byte(strings.ToLower(a)), []byte(strings.ToLower(b))) == 1
}

// readErrors remembers the error of reading the response, to tell a dropped connection from a local write error.
type readErrors struct {
	r   io.Reader
	err error
}

func (r *readErrors) Read(p []byte) (int, error) {
	n, err := r.r.Read(p)
	if err != nil && err != io.EOF {
		r.err = err
	}
	return n, err
}

type limitedBuffer struct {
	data []byte
}

func (b *limitedBuffer) Write(p []byte) (int, error) {
	if room := 512 - len(b.data); room > 0 {
		if len(p) < room {
			room = len(p)
		}
		b.data = append(b.data, p[:room]...)
	}
	return len(p), nil
}

func (b *limitedBuffer) String() string { return string(b.data) }

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n]
}
