//go:build !windows && !linux

package storage

import "os"

// platformLister reads folders with the standard library on other platforms: sizes are the file sizes, links are not entered.
func platformLister(string) (lister, error) {
	return func(path string) ([]entry, error) {
		items, err := os.ReadDir(path)
		if err != nil && len(items) == 0 {
			return nil, err
		}
		out := make([]entry, 0, len(items))
		for _, item := range items {
			info, err := item.Info()
			if err != nil {
				continue
			}
			e := entry{name: item.Name(), modified: info.ModTime()}
			switch {
			case info.Mode()&os.ModeSymlink != 0:
			case info.IsDir():
				e.dir = true
			default:
				e.size = uint64(max(info.Size(), 0))
			}
			out = append(out, e)
		}
		return out, nil
	}, nil
}
