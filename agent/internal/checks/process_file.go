package checks

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/shirou/gopsutil/v4/process"
)

// Folder walks for the file check stop here, so a check can never keep a disk busy for long.
const (
	maxWalkEntries  = 200000
	maxWalkDuration = 30 * time.Second
)

func processCheck(ctx context.Context, name string) Measurement {
	name = strings.TrimSpace(name)
	if name == "" || strings.ContainsAny(name, `\/"*?`) || hasControlChars(name) {
		return Measurement{Target: name, Error: "the process check has no valid process parameter"}
	}
	procs, err := process.ProcessesWithContext(ctx)
	if err != nil {
		return Measurement{Target: name, Error: fmt.Sprintf("processes could not be listed: %v", err)}
	}
	want := strings.TrimSuffix(strings.ToLower(name), ".exe")
	count := 0
	for _, p := range procs {
		n, err := p.NameWithContext(ctx)
		if err != nil {
			continue
		}
		if strings.TrimSuffix(strings.ToLower(n), ".exe") == want {
			count++
		}
	}
	if count == 0 {
		return Measurement{Target: name, Value: 0, Detail: "Not running"}
	}
	return Measurement{Target: name, Value: float64(count), Detail: fmt.Sprintf("%d running", count)}
}

// absolutePath mirrors the server validation: a full Windows, UNC or Unix path.
func absolutePath(path string) bool {
	if path == "" || hasControlChars(path) || strings.Contains(path, `"`) {
		return false
	}
	if strings.HasPrefix(path, "/") || strings.HasPrefix(path, `\\`) {
		return true
	}
	return len(path) >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/') &&
		((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'))
}

func fileCheck(ctx context.Context, params map[string]string) Measurement {
	path := strings.TrimSpace(params["path"])
	condition := params["condition"]
	if !absolutePath(path) {
		return Measurement{Target: path, Error: "the file check needs a full path"}
	}
	info, err := os.Stat(path)
	missing := errors.Is(err, fs.ErrNotExist)
	if err != nil && !missing {
		return Measurement{Target: path, Error: fmt.Sprintf("%s could not be read: %v", path, err)}
	}
	switch condition {
	case "exists", "":
		if missing {
			return Measurement{Target: path, Value: 0, Detail: "Does not exist"}
		}
		return Measurement{Target: path, Value: 1, Detail: describeEntry(info)}
	case "missing":
		if missing {
			return Measurement{Target: path, Value: 1, Detail: "Does not exist"}
		}
		return Measurement{Target: path, Value: 0, Detail: "Exists: " + describeEntry(info)}
	case "size", "age":
		if missing {
			return Measurement{Target: path, Error: fmt.Sprintf("%s does not exist", path)}
		}
		size, newest, files, err := measureEntry(ctx, path, info)
		if err != nil {
			return Measurement{Target: path, Error: err.Error()}
		}
		if condition == "size" {
			mb := float64(size) / (1024 * 1024)
			return Measurement{Target: path, Value: round(mb, 1), Detail: sizeDetail(size, files, info.IsDir())}
		}
		hours := time.Since(newest).Hours()
		if hours < 0 {
			hours = 0
		}
		return Measurement{Target: path, Value: round(hours, 1), Detail: "Last changed " + newest.UTC().Format(time.RFC3339)}
	default:
		return Measurement{Target: path, Error: fmt.Sprintf("the file check has an unknown condition %q", condition)}
	}
}

func describeEntry(info fs.FileInfo) string {
	if info.IsDir() {
		return "Folder, last changed " + info.ModTime().UTC().Format(time.RFC3339)
	}
	return fmt.Sprintf("%s, last changed %s", FormatBytes(uint64(info.Size())), info.ModTime().UTC().Format(time.RFC3339))
}

func sizeDetail(size int64, files int, dir bool) string {
	if !dir {
		return FormatBytes(uint64(size))
	}
	return fmt.Sprintf("%s in %d files", FormatBytes(uint64(size)), files)
}

// measureEntry returns the size of a file, or for a folder the total size of every file in it and the time of its newest file.
func measureEntry(ctx context.Context, path string, info fs.FileInfo) (int64, time.Time, int, error) {
	if !info.IsDir() {
		return info.Size(), info.ModTime(), 1, nil
	}
	var size int64
	newest := info.ModTime()
	newestFile := time.Time{}
	files, entries := 0, 0
	deadline := time.Now().Add(maxWalkDuration)
	err := filepath.WalkDir(path, func(p string, d fs.DirEntry, err error) error {
		if err != nil {
			// An unreadable subfolder is skipped rather than failing the whole check.
			if d != nil && d.IsDir() && p != path {
				return fs.SkipDir
			}
			return nil
		}
		entries++
		if entries > maxWalkEntries {
			return fmt.Errorf("the folder has more than %d entries; check a smaller folder", maxWalkEntries)
		}
		if ctx.Err() != nil || time.Now().After(deadline) {
			return errors.New("the folder could not be measured within 30 seconds; check a smaller folder")
		}
		if d.IsDir() || d.Type()&fs.ModeSymlink != 0 {
			return nil
		}
		fi, err := d.Info()
		if err != nil {
			return nil
		}
		files++
		size += fi.Size()
		if fi.ModTime().After(newestFile) {
			newestFile = fi.ModTime()
		}
		return nil
	})
	if err != nil {
		return 0, time.Time{}, 0, err
	}
	if !newestFile.IsZero() {
		newest = newestFile
	}
	return size, newest, files, nil
}
