// Package logging writes the agent log to a daily file and removes files older than the retention period.
//
// Nothing that is a secret may be logged: enrollment tokens, private keys and certificate private parts never reach
// a logger. Callers log identifiers (endpoint id, key id, sequence numbers), never payloads.
package logging

import (
	"fmt"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

const (
	filePrefix = "fleetify-agent-"
	fileSuffix = ".log"
	// Retention is how long log files are kept.
	Retention = 7 * 24 * time.Hour
)

// DailyFile is an io.Writer that writes to <dir>/fleetify-agent-YYYYMMDD.log (local date) and rotates at midnight.
type DailyFile struct {
	dir     string
	prefix  string
	now     func() time.Time
	mu      sync.Mutex
	day     string
	file    *os.File
	lastErr time.Time
}

// NewDailyFile creates the log directory when needed.
func NewDailyFile(dir string) (*DailyFile, error) {
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return nil, fmt.Errorf("create log directory %s: %w", dir, err)
	}
	return &DailyFile{dir: dir, prefix: filePrefix, now: time.Now}, nil
}

// WatchdogPrefix names the log files of the watchdog: fleetify-watchdog-YYYYMMDD.log (0.2.1).
const WatchdogPrefix = "fleetify-watchdog-"

// NewNamed builds a logger like New with another file name prefix.
func NewNamed(dir, prefix string, console bool, level slog.Level) (*slog.Logger, io.Closer, error) {
	return newLogger(dir, prefix, console, level)
}

// Write appends p to the file of the current day. Write errors (a full disk) are swallowed after reporting once a
// minute on stderr: logging must never stop the agent.
func (d *DailyFile) Write(p []byte) (int, error) {
	d.mu.Lock()
	defer d.mu.Unlock()
	now := d.now()
	day := now.Format("20060102")
	if d.file == nil || day != d.day {
		if d.file != nil {
			_ = d.file.Close()
			d.file = nil
		}
		f, err := os.OpenFile(filepath.Join(d.dir, d.prefix+day+fileSuffix), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
		if err != nil {
			d.reportError(now, err)
			return len(p), nil
		}
		d.file = f
		d.day = day
		d.prune(now)
	}
	if _, err := d.file.Write(p); err != nil {
		d.reportError(now, err)
	}
	return len(p), nil
}

// Close closes the current file.
func (d *DailyFile) Close() error {
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.file == nil {
		return nil
	}
	err := d.file.Close()
	d.file = nil
	return err
}

func (d *DailyFile) reportError(now time.Time, err error) {
	if now.Sub(d.lastErr) < time.Minute {
		return
	}
	d.lastErr = now
	fmt.Fprintf(os.Stderr, "fleetify-agent: cannot write log file: %v\n", err)
}

// prune removes log files whose date is older than the retention period.
func (d *DailyFile) prune(now time.Time) {
	entries, err := os.ReadDir(d.dir)
	if err != nil {
		return
	}
	cutoff := now.Add(-Retention)
	for _, e := range entries {
		name := e.Name()
		if e.IsDir() || !strings.HasPrefix(name, d.prefix) || !strings.HasSuffix(name, fileSuffix) {
			continue
		}
		day, err := time.ParseInLocation("20060102", strings.TrimSuffix(strings.TrimPrefix(name, d.prefix), fileSuffix), now.Location())
		if err != nil {
			continue
		}
		// A file is dated at the start of its day; keep it until its last moment is older than the cutoff.
		if day.Add(24 * time.Hour).Before(cutoff) {
			_ = os.Remove(filepath.Join(d.dir, name))
		}
	}
}

// New builds the agent logger. When console is true, records also go to stderr (foreground mode).
func New(dir string, console bool, level slog.Level) (*slog.Logger, io.Closer, error) {
	return newLogger(dir, filePrefix, console, level)
}

func newLogger(dir, prefix string, console bool, level slog.Level) (*slog.Logger, io.Closer, error) {
	file, err := NewDailyFile(dir)
	if err != nil {
		return nil, nil, err
	}
	file.prefix = prefix
	var w io.Writer = file
	if console {
		w = io.MultiWriter(file, os.Stderr)
	}
	handler := slog.NewTextHandler(w, &slog.HandlerOptions{Level: level})
	return slog.New(handler), file, nil
}

// Discard returns a logger that drops everything, for tests and short-lived commands.
func Discard() *slog.Logger {
	return slog.New(slog.DiscardHandler)
}
