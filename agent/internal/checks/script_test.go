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

func TestAScriptCheckReportsItsWholeOutputUpToTheLimit(t *testing.T) {
	collector := SystemCollector{ScriptDir: t.TempDir(), Access: platform.AccessCurrentUser}
	body := nativeBody("@echo off\r\necho line one\r\necho line two\r\necho problem 1>&2\r\nexit /b 0\r\n",
		"echo line one\necho line two\necho problem >&2\nexit 0\n")

	result := collector.Collect(context.Background(), scriptSpec(body, map[string]string{"timeout_seconds": "60"}))[0]

	out := result.ScriptOutput
	if out == nil || !strings.Contains(out.GetStdout(), "line one") || !strings.Contains(out.GetStdout(), "line two") ||
		!strings.Contains(out.GetStderr(), "problem") {
		t.Fatalf("the whole output must be reported: %+v", out)
	}
	if out.GetLimitBytes() != jobs.DefaultCheckOutputLimit || out.GetTotalBytes() != uint64(len(out.GetStdout())+len(out.GetStderr())) {
		t.Fatalf("an output within the default limit must be complete: %+v", out)
	}
}

func TestAScriptCheckCutsItsOutputAtTheLimitAndCountsTheRest(t *testing.T) {
	collector := SystemCollector{ScriptDir: t.TempDir(), Access: platform.AccessCurrentUser}
	body := nativeBody("@echo off\r\nfor /L %%i in (1,1,300) do echo 0123456789012345678901234567890123456789\r\nexit /b 0\r\n",
		"i=0; while [ $i -lt 300 ]; do echo 0123456789012345678901234567890123456789; i=$((i+1)); done\nexit 0\n")

	out := collector.Collect(context.Background(), scriptSpec(body, map[string]string{"timeout_seconds": "60", "output_limit_kb": "2"}))[0].ScriptOutput

	if out == nil || out.GetLimitBytes() != 2048 || len(out.GetStdout()) != 2048 || out.GetTotalBytes() <= 2048*5 {
		t.Fatalf("the output must be cut at 2 KB and its total size counted: limit %d, kept %d, total %d",
			out.GetLimitBytes(), len(out.GetStdout()), out.GetTotalBytes())
	}
}

func TestTheOutputLimitStaysWithinItsBounds(t *testing.T) {
	for value, want := range map[string]int{"": 8192, "0": 8192, "abc": 8192, "1": 1024, "64": 65536, "256": 262144, "100000": 262144} {
		if got := ScriptOutputLimit(&agentv1.CheckSpec{Parameters: map[string]string{"output_limit_kb": value}}); got != want {
			t.Fatalf("output_limit_kb %q: got %d, want %d", value, got, want)
		}
	}
}

func TestUnchangedScriptOutputIsSentOncePerDay(t *testing.T) {
	r := &runner{}
	start := time.Date(2026, 11, 9, 10, 0, 0, 0, time.UTC)
	output := func(text string) *agentv1.ScriptOutput {
		return &agentv1.ScriptOutput{Stdout: text, TotalBytes: uint64(len(text)), LimitBytes: 8192}
	}

	if first := r.dedupeOutput(output("ok"), start); first.GetUnchanged() || first.GetStdout() != "ok" {
		t.Fatalf("the first output must be sent: %+v", first)
	}
	same := r.dedupeOutput(output("ok"), start.Add(time.Minute))
	if !same.GetUnchanged() || same.GetStdout() != "" || same.GetTotalBytes() != 2 || same.GetLimitBytes() != 8192 {
		t.Fatalf("unchanged output must be left out with its size and limit: %+v", same)
	}
	if changed := r.dedupeOutput(output("changed"), start.Add(2*time.Minute)); changed.GetUnchanged() || changed.GetStdout() != "changed" {
		t.Fatalf("changed output must be sent: %+v", changed)
	}
	if again := r.dedupeOutput(output("changed"), start.Add(2*time.Minute+ScriptOutputResend)); again.GetUnchanged() {
		t.Fatal("unchanged output must be sent again after a day")
	}
	// The same text on stdout and stderr is not the same output.
	split := &runner{}
	split.dedupeOutput(&agentv1.ScriptOutput{Stdout: "ab"}, start)
	if moved := split.dedupeOutput(&agentv1.ScriptOutput{Stdout: "a", Stderr: "b"}, start.Add(time.Minute)); moved.GetUnchanged() {
		t.Fatal("output that moved between the streams must be sent")
	}
	if r.dedupeOutput(nil, start) != nil {
		t.Fatal("a check without script output must stay without")
	}
}
