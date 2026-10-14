package remote

import (
	"encoding/json"
	"strings"
	"sync"
	"testing"
	"time"
)

const scriptOpen = `{"channel":2,"service":"script","cols":120,"rows":30,"script":{"name":"Clean temp","version":3,"language":"powershell",` +
	`"body":"Write-Host hi","sha256":"ABCDEF","runAs":"user","user":"S-1-5-21-1"}}`

// TestALibraryScriptRunsInATerminalAndIsAuditedByNameAndVersion covers 0.6.0: the hello says scripts can run, the script gets a terminal
// channel of its own, and the audit report names the script, its version, the account and the hash, never the body.
func TestALibraryScriptRunsInATerminalAndIsAuditedByNameAndVersion(t *testing.T) {
	var mu sync.Mutex
	var requested ScriptRequest
	var reports []string
	b, _ := startSessionWith(t, time.Now, time.Hour, func(opts *SessionOptions) {
		opts.OpenScript = func(request ScriptRequest, cols, rows int) (Terminal, string, error) {
			mu.Lock()
			requested = request
			mu.Unlock()
			return newFakeTerminal(), `CONTOSO\anna`, nil
		}
		opts.Report = func(action, target, detail string) {
			mu.Lock()
			reports = append(reports, action+"|"+target+"|"+detail)
			mu.Unlock()
		}
	})
	kind, body := b.read()
	var hello Hello
	if kind != FrameHello || json.Unmarshal(body, &hello) != nil || !hello.Scripts {
		t.Fatalf("the hello must say that scripts can run: %x %s", kind, body)
	}

	b.write(FrameOpen, []byte(scriptOpen))
	if kind, body := b.read(); kind != FrameOpened || !strings.Contains(string(body), `"channel":2`) || strings.Contains(string(body), "error") {
		t.Fatalf("unexpected opened %x %s", kind, body)
	}
	mu.Lock()
	defer mu.Unlock()
	if requested.Name != "Clean temp" || requested.Body != "Write-Host hi" || requested.RunAs != RunAsUser || requested.User != "S-1-5-21-1" {
		t.Fatalf("the script request did not reach the opener: %+v", requested)
	}
	if len(reports) != 1 || reports[0] != `terminal.script|Clean temp (version 3)|as CONTOSO\anna, sha256 abcdef` {
		t.Fatalf("unexpected audit report %q", reports)
	}
	if strings.Contains(strings.Join(reports, ""), "Write-Host") {
		t.Fatal("the body of a script must never be reported")
	}
}

// TestAnEndpointWithoutScriptsSaysToUpdateTheAgent covers a service that cannot run scripts.
func TestAnEndpointWithoutScriptsSaysToUpdateTheAgent(t *testing.T) {
	b, _ := startSession(t, time.Now, time.Hour)
	kind, body := b.read()
	var hello Hello
	if kind != FrameHello || json.Unmarshal(body, &hello) != nil || hello.Scripts {
		t.Fatalf("the hello must not offer scripts: %x %s", kind, body)
	}
	b.write(FrameOpen, []byte(scriptOpen))
	if kind, body := b.read(); kind != FrameOpened || !strings.Contains(string(body), "Update the agent") {
		t.Fatalf("unexpected opened %x %s", kind, body)
	}
}

func TestAScriptRequestNeedsAKnownLanguageAndRunAs(t *testing.T) {
	for _, request := range []ScriptRequest{
		{Language: "python", RunAs: RunAsSystem},
		{Language: "bash", RunAs: "admin"},
		{Language: "bash", RunAs: RunAsSystem, User: "1000"},
	} {
		if _, err := request.Job(); err == nil {
			t.Fatalf("request %+v must be refused", request)
		}
	}
	job, err := ScriptRequest{Name: "x", Version: 2, Language: "Batch", Body: "echo", Sha256: "AB", RunAs: RunAsUser}.Job()
	if err != nil || job.GetSha256() != "ab" || job.GetVersion() != 2 {
		t.Fatalf("unexpected job %v %v", job, err)
	}
}
