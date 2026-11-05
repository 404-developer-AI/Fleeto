package patch

import (
	"bytes"
	"os"
	"path/filepath"
	"slices"
	"testing"
	"time"
)

func TestTheSystemdScopeOfAnInstallation(t *testing.T) {
	if !underSystemd("4f6c0d6e9a1b4c3d", false) || !underSystemd("", true) || underSystemd("", false) {
		t.Fatal("under systemd: INVOCATION_ID or /run/systemd/system")
	}
	cases := map[string]string{
		"9d3c1f0a-7b2e-4c11-8f00-2a6b5c4d3e2f": "fleeto-patch-9d3c1f0a7b2e",
		"ABC":                                  "fleeto-patch-abc",
		"../;$(reboot)":                        "fleeto-patch-reboot",
		"--":                                   "",
		"":                                     "",
	}
	for id, want := range cases {
		if got := scopeUnit(id); got != want {
			t.Errorf("scopeUnit(%q) = %q, want %q", id, got, want)
		}
	}
	args := []string{"-y", "-q", "install", "--only-upgrade", "--", "libssl3:amd64=3.0.2-0ubuntu1.12"}
	got := scopeArgv("fleeto-patch-9d3c1f0a7b2e", "/usr/bin/apt-get", args)
	want := append([]string{"--scope", "--quiet", "--collect", "--unit=fleeto-patch-9d3c1f0a7b2e", "--", "/usr/bin/apt-get"}, args...)
	if !slices.Equal(got, want) {
		t.Fatalf("argv %q\nwant %q", got, want)
	}
	if got := scopeArgv("", "/usr/bin/dnf", []string{"-y", "upgrade"}); !slices.Equal(got,
		[]string{"--scope", "--quiet", "--collect", "--", "/usr/bin/dnf", "-y", "upgrade"}) {
		t.Fatalf("argv without a unit %q", got)
	}
}

func TestFollowFileCopiesWhatIsWrittenUntilTheProcessEnded(t *testing.T) {
	path := filepath.Join(t.TempDir(), "package-manager.log")
	w, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	defer w.Close()
	r, err := os.Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer r.Close()
	var out bytes.Buffer
	done := make(chan struct{})
	followed := make(chan struct{})
	go func() {
		defer close(followed)
		followFile(r, &out, done, 5*time.Millisecond)
	}()
	_, _ = w.WriteString("Reading package lists...\n")
	time.Sleep(30 * time.Millisecond)
	_, _ = w.WriteString("Setting up libssl3 ...\n")
	close(done)
	<-followed
	if out.String() != "Reading package lists...\nSetting up libssl3 ...\n" {
		t.Fatalf("output %q", out.String())
	}
}
