//go:build linux

package checks

import "testing"

func TestKeepPartitionSkipsImagesContainerLayersAndNetworkShares(t *testing.T) {
	cases := []struct {
		fstype, mount string
		keep          bool
	}{
		{"ext4", "/", true},
		{"xfs", "/var", true},
		{"vfat", "/boot/efi", true},
		{"zfs", "/rpool/data", true},
		{"squashfs", "/snap/core22/1122", false},
		{"ext4", "/snap/firefox/x1", false},
		{"overlay", "/var/lib/docker/overlay2/abc/merged", false},
		{"ext4", "/var/lib/kubelet/pods/x/volume", false},
		{"nfs4", "/mnt/backup", false},
		{"cifs", "/mnt/share", false},
		{"fuse.sshfs", "/mnt/remote", false},
		{"tmpfs", "/run/user/1000", false},
	}
	for _, c := range cases {
		if got := keepPartition(c.fstype, c.mount); got != c.keep {
			t.Errorf("keepPartition(%q, %q) = %v, want %v", c.fstype, c.mount, got, c.keep)
		}
	}
}

func TestNormalizeDriveKeepsMountPointsAndTheRoot(t *testing.T) {
	cases := map[string]string{"/": "/", "/var/": "/var", " /srv/data ": "/srv/data", "/mnt//": "/mnt"}
	for in, want := range cases {
		if got := NormalizeDrive(in); got != want {
			t.Errorf("NormalizeDrive(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestServiceCheckNeedsAServiceName(t *testing.T) {
	m := serviceRunning("  ")
	if m.Error == "" {
		t.Fatal("a service check without a unit name must report an error")
	}
}
