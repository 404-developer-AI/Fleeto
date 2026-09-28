package storage

import (
	"context"
	"crypto/rand"
	"errors"
	"fmt"
	"log/slog"
	"strings"
	"time"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/checks"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

const (
	// VolumeTimeLimit stops the scan of one volume; the report then says it is incomplete.
	VolumeTimeLimit = 15 * time.Minute
	// MaxReportBytes leaves room for the envelope under the 4 MiB message limit.
	MaxReportBytes = 4*1024*1024 - 1024
)

// ScanAll scans every fixed volume, one after the other, and returns one report per volume. It returns nil when ctx is
// cancelled (the agent stops); a volume that stopped on its own time limit still gets a report.
func ScanAll(ctx context.Context, requestID string, logger *slog.Logger) []*agentv1.StorageScanReport {
	drives, err := checks.FixedDrives()
	if err != nil {
		logger.Warn("storage scan: the volumes could not be listed", "error", err)
		return nil
	}
	var reports []*agentv1.StorageScanReport
	for _, d := range drives {
		if d.Total == 0 {
			continue
		}
		report := ScanVolume(ctx, d, requestID)
		if ctx.Err() != nil {
			return nil
		}
		logger.Info("storage scan finished", "volume", report.GetVolume(), "method", report.GetMethod().String(),
			"complete", report.GetComplete(), "files", report.GetFileCount(), "folders", report.GetFolderCount(),
			"duration", (time.Duration(report.GetDurationMs()) * time.Millisecond).String())
		reports = append(reports, report)
	}
	return reports
}

// ScanVolume scans one volume within VolumeTimeLimit.
func ScanVolume(ctx context.Context, d checks.DriveUsage, requestID string) *agentv1.StorageScanReport {
	started := time.Now()
	volumeCtx, cancel := context.WithTimeout(ctx, VolumeTimeLimit)
	defer cancel()
	t := newTree(volumeRoot(d.Name), separator)
	method, note, err := scanPlatform(volumeCtx, d.Name, d.Filesystem, &t)
	complete := err == nil
	switch {
	case err == nil:
		// A note on a finished scan (the table could not be read, so the folders were walked) keeps the scan complete, unless
		// the walk itself skipped folders.
		if strings.HasSuffix(note, errUnreadable) {
			complete = false
		}
	case errors.Is(err, context.DeadlineExceeded) && ctx.Err() == nil:
		note = joinNote(note, fmt.Sprintf("stopped after %d minutes", int(VolumeTimeLimit/time.Minute)))
	default:
		note = joinNote(note, err.Error())
	}
	res := t.result()
	report := &agentv1.StorageScanReport{
		ScanId:      newUUID(),
		Volume:      d.Name,
		Filesystem:  d.Filesystem,
		TotalBytes:  d.Total,
		FreeBytes:   d.Free,
		StartedAt:   timestamppb.New(started),
		DurationMs:  uint32(min(time.Since(started).Milliseconds(), int64(^uint32(0)))),
		Method:      method,
		Complete:    complete,
		Error:       note,
		FileCount:   res.FileCount,
		FolderCount: res.FolderCount,
		RequestId:   requestID,
	}
	for _, f := range res.Folders {
		report.Folders = append(report.Folders, &agentv1.StorageFolder{Path: f.Path, SizeBytes: f.SizeBytes, FileCount: f.FileCount, FolderCount: f.FolderCount})
	}
	for _, f := range res.Files {
		file := &agentv1.StorageFile{Path: f.Path, SizeBytes: f.SizeBytes}
		if !f.ModifiedAt.IsZero() {
			file.ModifiedAt = timestamppb.New(f.ModifiedAt)
		}
		report.Files = append(report.Files, file)
	}
	Fit(report, MaxReportBytes)
	return report
}

// Fit drops files, then folders from the end (the smallest), until the report fits in limit bytes.
func Fit(report *agentv1.StorageScanReport, limit int) {
	for proto.Size(report) > limit && len(report.Files) > 0 {
		report.Files = report.Files[:len(report.Files)/2]
	}
	for proto.Size(report) > limit && len(report.Folders) > 1 {
		report.Folders = report.Folders[:len(report.Folders)/2]
	}
}

func joinNote(a, b string) string {
	if a == "" {
		return b
	}
	return a + "; " + b
}

// newUUID returns a random UUID version 4.
func newUUID() string {
	var b [16]byte
	_, _ = rand.Read(b[:])
	b[6] = b[6]&0x0f | 0x40
	b[8] = b[8]&0x3f | 0x80
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}
