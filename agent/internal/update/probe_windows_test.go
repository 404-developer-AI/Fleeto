//go:build windows

package update

import (
	"crypto/sha256"
	"encoding/hex"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/release"
)

// buildProbeBinary builds a program that reports version 9.9.9 and leaves marker when it runs.
func buildProbeBinary(t *testing.T, work, marker string) []byte {
	t.Helper()
	src := filepath.Join(work, "probe", "main.go")
	if err := os.MkdirAll(filepath.Dir(src), 0o700); err != nil {
		t.Fatal(err)
	}
	quoted := strings.ReplaceAll(`"`+marker+`"`, `\`, `\\`)
	prog := "package main\nimport (\"fmt\";\"os\")\nfunc main(){ _ = os.WriteFile(" + quoted + ", []byte(\"ran\"), 0o600); fmt.Println(\"9.9.9\") }\n"
	if err := os.WriteFile(src, []byte(prog), 0o600); err != nil {
		t.Fatal(err)
	}
	exe := filepath.Join(work, "probe.exe")
	build := exec.Command("go", "build", "-o", exe, src)
	build.Dir = filepath.Dir(src)
	build.Env = append(os.Environ(), "GO111MODULE=off", "CGO_ENABLED=0")
	if out, err := build.CombinedOutput(); err != nil {
		t.Fatalf("build the probe binary: %v\n%s", err, out)
	}
	data, err := os.ReadFile(exe)
	if err != nil {
		t.Fatal(err)
	}
	return data
}

func binaryFor(data []byte) release.Binary {
	sum := sha256.Sum256(data)
	return release.Binary{Component: release.ComponentAgent, File: "fleeto-agent.exe", SHA256: hex.EncodeToString(sum[:]), Size: int64(len(data))}
}

// A staged binary runs as the service account (LocalSystem) to report its version. It runs only when it still matches the signed
// manifest, and it cannot be changed between that check and the run: a file swapped in after the download is never executed.
func TestProbeStagedRunsOnlyTheBinaryOfTheSignedManifest(t *testing.T) {
	work := t.TempDir()
	marker := filepath.Join(work, "probe-ran.txt")
	data := buildProbeBinary(t, work, marker)
	staged := filepath.Join(work, "updates", "fleeto-agent.exe")
	if err := os.MkdirAll(filepath.Dir(staged), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(staged, data, 0o700); err != nil {
		t.Fatal(err)
	}

	// Not the binary the manifest lists (as after a swap): refused, and never run.
	if _, err := ProbeStaged(t.Context(), staged, binaryFor([]byte("MZ the release binary"))); err == nil ||
		!strings.Contains(err.Error(), "does not match the signed manifest") {
		t.Fatalf("expected a refusal of a binary the manifest does not list, got %v", err)
	}
	if _, err := os.Stat(marker); err == nil {
		t.Fatal("a binary that does not match the signed manifest was run")
	}

	// The binary the manifest lists runs.
	reported, err := ProbeStaged(t.Context(), staged, binaryFor(data))
	if err != nil || reported != "9.9.9" {
		t.Fatalf("ProbeStaged = %q, %v; want 9.9.9", reported, err)
	}
	if _, err := os.Stat(marker); err != nil {
		t.Fatalf("the verified binary did not run: %v", err)
	}
}

// While a staged binary is held for its probe, nobody can write, rename or delete it.
func TestHeldStagedBinaryCannotBeReplaced(t *testing.T) {
	staged := filepath.Join(t.TempDir(), "fleeto-agent.exe")
	if err := os.WriteFile(staged, []byte("MZ the release binary"), 0o700); err != nil {
		t.Fatal(err)
	}
	f, err := openHeld(staged)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	if err := os.WriteFile(staged, []byte("swapped"), 0o700); err == nil {
		t.Error("the held binary could be overwritten")
	}
	if err := os.Rename(staged, staged+".moved"); err == nil {
		t.Error("the held binary could be renamed")
	}
	if err := os.Remove(staged); err == nil {
		t.Error("the held binary could be deleted")
	}
	if err := verifyOpen(f, staged, binaryFor([]byte("MZ the release binary")).SHA256, int64(len("MZ the release binary"))); err != nil {
		t.Fatalf("the held binary changed: %v", err)
	}
}
