//go:build windows

package storage

import (
	"context"
	"fmt"
	"strings"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

const separator = `\`

// volumeRoot is the root folder of a drive: "C:" becomes "C:\".
func volumeRoot(volume string) string { return strings.TrimRight(volume, `\`) + `\` }

// scanPlatform reads an NTFS volume from its master file table and walks any other; when the table cannot be read (no
// administrator rights, for example) the folders are walked instead and note says why.
func scanPlatform(ctx context.Context, volume, filesystem string, t **tree) (agentv1.StorageScanMethod, string, error) {
	if strings.EqualFold(filesystem, "NTFS") {
		incomplete, err := scanMFT(ctx, volume, *t)
		if err == nil {
			return agentv1.StorageScanMethod_STORAGE_SCAN_METHOD_MFT, incomplete, nil
		}
		if ctx.Err() != nil {
			return agentv1.StorageScanMethod_STORAGE_SCAN_METHOD_MFT, "", err
		}
		fresh := newTree((*t).root, separator)
		*t = fresh
		lister, _ := platformLister((*t).root)
		incomplete, werr := walk(ctx, *t, lister)
		note := fmt.Sprintf("the master file table could not be read (%v); the folders were walked instead", err)
		if incomplete != "" {
			note += "; " + incomplete
		}
		return agentv1.StorageScanMethod_STORAGE_SCAN_METHOD_WALK, note, werr
	}
	lister, err := platformLister((*t).root)
	if err != nil {
		return agentv1.StorageScanMethod_STORAGE_SCAN_METHOD_WALK, "", err
	}
	incomplete, err := walk(ctx, *t, lister)
	return agentv1.StorageScanMethod_STORAGE_SCAN_METHOD_WALK, incomplete, err
}
