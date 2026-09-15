package checks

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"os"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/jobs"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func scriptSpec(body string, params map[string]string) *agentv1.CheckSpec {
	language := agentv1.ScriptLanguage_SCRIPT_LANGUAGE_SHELL
	if runtime.GOOS == "windows" {
		language = agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BATCH
	}
	sum := sha256.Sum256([]byte(body))
	return &agentv1.CheckSpec{
		Id: "7d0c8d1e-0000-4000-8000-000000000001", Type: agentv1.CheckType_CHECK_TYPE_SCRIPT, IntervalSeconds: 60, Parameters: params,
		Script: &agentv1.ScriptJob{Language: language, Name: "check", Version: 1, Body: body, Sha256: hex.EncodeToString(sum[:])},
	}
}

func nativeBody(windows, unix string) string {
	if runtime.GOOS == "windows" {
		return windows
	}
	return unix
}

func TestAScriptCheckReportsItsExitCodeAndFirstLineOfOutput(t *testing.T) {
	dir := t.TempDir()
	collector := SystemCollector{ScriptDir: dir, Access: platform.AccessCurrentUser}
	body := nativeBody("@echo off\r\necho.\r\necho 3 updates pending\r\necho second line\r\nexit /b 1\r\n", "echo\necho '3 updates pending'\necho second line\nexit 1\n")

	results := collector.Collect(context.Background(), scriptSpec(body, map[string]string{"timeout_seconds": "60"}))

	if len(results) != 1 || results[0].Error != "" || results[0].Value != 1 || results[0].Detail != "3 updates pending" {
		t.Fatalf("unexpected result: %+v", results)
	}
	if entries, _ := os.ReadDir(dir); len(entries) != 0 {
		t.Fatalf("the script must be removed after the run, found %d entries", len(entries))
	}
}

func TestAScriptCheckDoesNotRunWhatTheSignedConfigurationDoesNotAllow(t *testing.T) {
	collector := SystemCollector{ScriptDir: t.TempDir(), Access: platform.AccessCurrentUser}
	body := nativeBody("@echo off\r\nexit /b 0\r\n", "exit 0\n")

	unavailable := collector.Collect(context.Background(), scriptSpec(body, map[string]string{"unavailable": "No version is approved."}))
	if unavailable[0].Error != "No version is approved." {
		t.Fatalf("an unavailable script must report the server's reason: %+v", unavailable)
	}

	tampered := scriptSpec(body, nil)
	tampered.Script.Body = nativeBody("@echo off\r\nexit /b 2\r\n", "exit 2\n")
	if result := collector.Collect(context.Background(), tampered); !strings.Contains(result[0].Error, "signed hash") {
		t.Fatalf("a body without its signed hash must not run: %+v", result)
	}

	missing := scriptSpec(body, nil)
	missing.Script = nil
	if result := collector.Collect(context.Background(), missing); result[0].Error == "" {
		t.Fatalf("a script check without a script must report an error: %+v", result)
	}

	if result := (SystemCollector{}).Collect(context.Background(), scriptSpec(body, nil)); result[0].Error == "" {
		t.Fatalf("without a script directory the check must not run: %+v", result)
	}
}

func TestAScriptCheckThatRunsTooLongIsEnded(t *testing.T) {
	collector := SystemCollector{ScriptDir: t.TempDir(), Access: platform.AccessCurrentUser}
	body := nativeBody("@echo off\r\nping -n 60 127.0.0.1 >nul\r\n", "sleep 60\n")
	started := time.Now()

	result := collector.Collect(context.Background(), scriptSpec(body, map[string]string{"timeout_seconds": "1"}))

	if !strings.Contains(result[0].Error, "did not finish") || time.Since(started) > jobs.MinCheckScriptTimeout+15*time.Second {
		t.Fatalf("the script must be ended at its timeout: %+v after %s", result, time.Since(started))
	}
}

func TestFirstLineIsCleanAndShort(t *testing.T) {
	if got := jobs.FirstLine([]byte("\xEF\xBB\xBF\r\n  \tdisk \x1b[31mfull\r\nnext")); got != "disk  [31mfull" {
		t.Fatalf("unexpected first line %q", got)
	}
	if got := jobs.FirstLine([]byte(strings.Repeat("é", 500))); len([]rune(got)) != 200 {
		t.Fatalf("the first line must be cut at 200 characters, got %d", len([]rune(got)))
	}
	if got := jobs.FirstLine([]byte{0xff, 0xfe, 'o', 'k'}); !strings.HasSuffix(got, "ok") {
		t.Fatalf("invalid UTF-8 must be replaced, got %q", got)
	}
}
