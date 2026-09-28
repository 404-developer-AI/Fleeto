//go:build !windows

package storage

import (
	"context"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

const separator = "/"

// volumeRoot is the mount point itself.
func volumeRoot(volume string) string { return volume }

// scanPlatform walks the tree below the mount point.
func scanPlatform(ctx context.Context, _, _ string, t **tree) (agentv1.StorageScanMethod, string, error) {
	lister, err := platformLister((*t).root)
	if err != nil {
		return agentv1.StorageScanMethod_STORAGE_SCAN_METHOD_WALK, "", err
	}
	incomplete, err := walk(ctx, *t, lister)
	return agentv1.StorageScanMethod_STORAGE_SCAN_METHOD_WALK, incomplete, err
}
