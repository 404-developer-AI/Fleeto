package remote

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/binary"
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"testing"
	"time"
)

// action is one audited action the endpoint reported.
type action struct{ verb, target, detail string }

// backgroundSession is a running session driven from the browser side over the in-memory pipe, with the actions it reports captured.
type backgroundSession struct {
	t       *testing.T
	browser *browser
	done    chan string
	actions chan action
	nextID  int
}

func startBackground(t *testing.T) *backgroundSession {
	t.Helper()
	f := newFixture(t)
	token, endpoint, browserKeys := f.handshake(t)
	p := newPipe()
	actions := make(chan action, 32)
	session, err := NewSession(SessionOptions{
		Token: token, Keys: endpoint.Keys, Transport: endpointSide{p}, Hello: Hello{Hostname: "SRV-01", Platform: runtime.GOOS},
		Open: func(string, int, int) (Terminal, error) { return newFakeTerminal(), nil },
		Report: func(verb, target, detail string) {
			select {
			case actions <- action{verb, target, detail}:
			default:
			}
		},
		Now: time.Now, Tick: time.Hour,
	})
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan string, 1)
	go func() { done <- session.Run(context.Background()) }()
	send, _ := NewCipher(browserKeys.BrowserToEndpoint)
	recv, _ := NewCipher(browserKeys.EndpointToBrowser)
	b := &browser{t: t, p: p, send: send, recv: recv}
	bs := &backgroundSession{t: t, browser: b, done: done, actions: actions}
	// The first frame is Hello.
	if kind, _ := b.read(); kind != FrameHello {
		t.Fatalf("expected hello, got %x", kind)
	}
	return bs
}

// request sends a FrameRequest and returns the FrameResponse body, skipping frames of other kinds and other request ids.
func (bs *backgroundSession) request(op string, params map[string]any) map[string]any {
	bs.t.Helper()
	bs.nextID++
	id := "t" + string(rune('0'+bs.nextID))
	body := map[string]any{"id": id, "op": op}
	for k, v := range params {
		body[k] = v
	}
	data, _ := json.Marshal(body)
	bs.browser.write(FrameRequest, data)
	deadline := time.After(10 * time.Second)
	for {
		select {
		case <-deadline:
			bs.t.Fatalf("no response to %s", op)
		default:
		}
		kind, payload := bs.browser.read()
		if kind != FrameResponse {
			continue
		}
		var result map[string]any
		if err := json.Unmarshal(payload, &result); err != nil {
			bs.t.Fatal(err)
		}
		if result["id"] == id {
			return result
		}
	}
}

func (bs *backgroundSession) ok(op string, params map[string]any) map[string]any {
	result := bs.request(op, params)
	if result["ok"] != true {
		bs.t.Fatalf("%s failed: %v", op, result["error"])
	}
	return result
}

func (bs *backgroundSession) expectAction(verb string) action {
	bs.t.Helper()
	select {
	case a := <-bs.actions:
		if a.verb != verb {
			bs.t.Fatalf("expected action %s, got %s", verb, a.verb)
		}
		return a
	case <-time.After(2 * time.Second):
		bs.t.Fatalf("no %s action was reported", verb)
		return action{}
	}
}

func TestFileOperations(t *testing.T) {
	bs := startBackground(t)
	dir := t.TempDir()

	// list an empty directory
	list := bs.ok("list", map[string]any{"path": dir})
	if entries, _ := list["entries"].([]any); len(entries) != 0 {
		t.Fatalf("expected an empty directory, got %v", list["entries"])
	}

	// mkdir
	bs.ok("mkdir", map[string]any{"path": dir, "name": "sub"})
	if a := bs.expectAction("file.mkdir"); a.target != filepath.Join(dir, "sub") {
		t.Fatalf("mkdir target %q", a.target)
	}

	// write a file directly, then rename it
	src := filepath.Join(dir, "a.txt")
	if err := os.WriteFile(src, []byte("hello"), 0o644); err != nil {
		t.Fatal(err)
	}
	bs.ok("rename", map[string]any{"path": src, "name": "b.txt"})
	bs.expectAction("file.rename")
	if _, err := os.Stat(filepath.Join(dir, "b.txt")); err != nil {
		t.Fatalf("the file was not renamed: %v", err)
	}

	// copy into the subfolder
	bs.ok("copy", map[string]any{"path": filepath.Join(dir, "b.txt"), "dest": filepath.Join(dir, "sub")})
	bs.expectAction("file.copy")
	if _, err := os.Stat(filepath.Join(dir, "sub", "b.txt")); err != nil {
		t.Fatalf("the file was not copied: %v", err)
	}

	// delete the copy
	bs.ok("delete", map[string]any{"path": filepath.Join(dir, "sub", "b.txt")})
	bs.expectAction("file.delete")

	// a name with a separator is refused
	if bs.request("mkdir", map[string]any{"path": dir, "name": "a/b"})["ok"] == true {
		t.Fatal("a name with a separator was accepted")
	}
	// a relative path is refused
	if bs.request("list", map[string]any{"path": "relative"})["ok"] == true {
		t.Fatal("a relative path was accepted")
	}
}

func TestDownloadRoundTrip(t *testing.T) {
	bs := startBackground(t)
	dir := t.TempDir()
	data := make([]byte, 900*1024) // spans several chunks and crosses the flow-control window
	if _, err := rand.Read(data); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(dir, "download.bin")
	if err := os.WriteFile(path, data, 0o644); err != nil {
		t.Fatal(err)
	}

	response := bs.ok("download", map[string]any{"path": path, "offset": 0})
	bs.expectAction("file.download")
	transfer := uint32(response["transfer"].(float64))
	if int64(response["size"].(float64)) != int64(len(data)) {
		t.Fatalf("size %v", response["size"])
	}

	received := bytes.Buffer{}
	for {
		kind, payload := bs.browser.read()
		switch kind {
		case FrameChunk:
			if binary.BigEndian.Uint32(payload[:4]) == transfer {
				received.Write(payload[4:])
				bs.browser.write(FrameTransfer, mustJSON(transferBody{Transfer: transfer, Kind: "ack", Bytes: int64(received.Len())}))
			}
		case FrameTransfer:
			var tb transferBody
			_ = json.Unmarshal(payload, &tb)
			if tb.Transfer == transfer && tb.Kind == "end" {
				if !bytes.Equal(received.Bytes(), data) {
					t.Fatalf("downloaded %d bytes, wanted %d", received.Len(), len(data))
				}
				return
			}
			if tb.Kind == "error" {
				t.Fatalf("download error: %s", tb.Error)
			}
		}
	}
}

func TestUploadRoundTrip(t *testing.T) {
	bs := startBackground(t)
	dir := t.TempDir()
	data := make([]byte, 700*1024)
	if _, err := rand.Read(data); err != nil {
		t.Fatal(err)
	}

	response := bs.ok("upload", map[string]any{"path": dir, "name": "up.bin", "size": int64(len(data)), "offset": 0})
	transfer := uint32(response["transfer"].(float64))

	sent := 0
	for sent < len(data) {
		end := min(sent+256*1024, len(data))
		frame := make([]byte, 4)
		binary.BigEndian.PutUint32(frame, transfer)
		frame = append(frame, data[sent:end]...)
		bs.browser.write(FrameChunk, frame)
		sent = end
	}
	bs.browser.write(FrameTransfer, mustJSON(transferBody{Transfer: transfer, Kind: "end"}))

	// Wait for the endpoint's end acknowledgement.
	for {
		kind, payload := bs.browser.read()
		if kind != FrameTransfer {
			continue
		}
		var tb transferBody
		_ = json.Unmarshal(payload, &tb)
		if tb.Transfer == transfer && tb.Kind == "end" {
			break
		}
		if tb.Kind == "error" {
			t.Fatalf("upload error: %s", tb.Error)
		}
	}
	bs.expectAction("file.upload")

	written, err := os.ReadFile(filepath.Join(dir, "up.bin"))
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(written, data) {
		t.Fatalf("uploaded file differs (%d vs %d bytes)", len(written), len(data))
	}
	// The part file is gone.
	if _, err := os.Stat(filepath.Join(dir, "up.bin"+partSuffix)); !os.IsNotExist(err) {
		t.Fatal("the .part file was left behind")
	}
}

// uploadWhole sends a whole file and returns the endpoint's last transfer message for it.
func (bs *backgroundSession) uploadWhole(t *testing.T, dir, name string, data []byte) transferBody {
	t.Helper()
	response := bs.ok("upload", map[string]any{"path": dir, "name": name, "size": int64(len(data)), "offset": 0})
	transfer := uint32(response["transfer"].(float64))
	frame := make([]byte, 4)
	binary.BigEndian.PutUint32(frame, transfer)
	bs.browser.write(FrameChunk, append(frame, data...))
	bs.browser.write(FrameTransfer, mustJSON(transferBody{Transfer: transfer, Kind: "end"}))
	for {
		kind, payload := bs.browser.read()
		if kind != FrameTransfer {
			continue
		}
		var tb transferBody
		_ = json.Unmarshal(payload, &tb)
		if tb.Transfer == transfer && (tb.Kind == "end" || tb.Kind == "error") {
			return tb
		}
	}
}

// A local user who owns the folder plants a link with the name of the part file: the agent (root or SYSTEM) must never write through it
// (security review of 0.3.0 step 7).
func TestAnUploadNeverWritesThroughALinkNamedLikeThePartFile(t *testing.T) {
	bs := startBackground(t)
	dir := t.TempDir()
	victim := filepath.Join(t.TempDir(), "victim.txt")
	if err := os.WriteFile(victim, []byte("do not touch"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(victim, filepath.Join(dir, "setup.sh"+partSuffix)); err != nil {
		t.Skipf("this machine cannot create symbolic links: %v", err)
	}
	if tb := bs.uploadWhole(t, dir, "setup.sh", []byte("the technician's file")); tb.Kind != "end" {
		t.Fatalf("upload: %s", tb.Error)
	}
	if got, _ := os.ReadFile(victim); string(got) != "do not touch" {
		t.Fatalf("the file behind the link was changed to %q", got)
	}
	info, err := os.Lstat(filepath.Join(dir, "setup.sh"))
	if err != nil || !info.Mode().IsRegular() {
		t.Fatalf("the upload is not a regular file of its own: %v %v", info, err)
	}
	if got, _ := os.ReadFile(filepath.Join(dir, "setup.sh")); string(got) != "the technician's file" {
		t.Fatalf("upload content %q", got)
	}
}

func TestAnUploadIntoAFolderThatIsALinkIsRefused(t *testing.T) {
	bs := startBackground(t)
	target := t.TempDir()
	link := filepath.Join(t.TempDir(), "Downloads")
	if err := os.Symlink(target, link); err != nil {
		t.Skipf("this machine cannot create symbolic links: %v", err)
	}
	if bs.request("upload", map[string]any{"path": link, "name": "a.txt", "size": 1, "offset": 0})["ok"] == true {
		t.Fatal("an upload into a linked folder was accepted")
	}
}

func TestAnIncompleteUploadNeverReplacesTheFile(t *testing.T) {
	bs := startBackground(t)
	dir := t.TempDir()
	dest := filepath.Join(dir, "config.txt")
	if err := os.WriteFile(dest, []byte("original"), 0o600); err != nil {
		t.Fatal(err)
	}
	response := bs.ok("upload", map[string]any{"path": dir, "name": "config.txt", "size": 100, "offset": 0})
	transfer := uint32(response["transfer"].(float64))
	frame := make([]byte, 4)
	binary.BigEndian.PutUint32(frame, transfer)
	bs.browser.write(FrameChunk, append(frame, []byte("only part")...))
	bs.browser.write(FrameTransfer, mustJSON(transferBody{Transfer: transfer, Kind: "end"}))
	for {
		kind, payload := bs.browser.read()
		var tb transferBody
		if kind != FrameTransfer || json.Unmarshal(payload, &tb) != nil || tb.Transfer != transfer {
			continue
		}
		if tb.Kind != "error" {
			t.Fatalf("an incomplete upload ended as %q", tb.Kind)
		}
		break
	}
	if got, _ := os.ReadFile(dest); string(got) != "original" {
		t.Fatalf("the file was replaced by an incomplete upload: %q", got)
	}
}

func TestUploadRejectsAFileOverTheCap(t *testing.T) {
	bs := startBackground(t)
	dir := t.TempDir()
	// The test token has no cap, so it defaults to the maximum; ask for more than that.
	over := int64(DefaultMaxFileBytes) + 1
	if bs.request("upload", map[string]any{"path": dir, "name": "big.bin", "size": over, "offset": 0})["ok"] == true {
		t.Fatal("an upload over the cap was accepted")
	}
}

func TestProcessesListAndEnd(t *testing.T) {
	bs := startBackground(t)
	result := bs.ok("processes", nil)
	procs, _ := result["processes"].([]any)
	if len(procs) == 0 {
		t.Fatal("no processes were listed")
	}

	// Start a benign child process and end it through the session.
	cmd := sleeper()
	if err := cmd.Start(); err != nil {
		t.Skipf("could not start a test process: %v", err)
	}
	defer func() { _ = cmd.Process.Kill() }()
	pid := cmd.Process.Pid

	bs.ok("process", map[string]any{"pid": pid, "action": "end"})
	bs.expectAction("process.end")
	done := make(chan struct{})
	go func() { _, _ = cmd.Process.Wait(); close(done) }()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("the process was not ended")
	}
}

func mustJSON(v any) []byte {
	data, _ := json.Marshal(v)
	return data
}

func sleeper() *exec.Cmd {
	if runtime.GOOS == "windows" {
		return exec.Command("cmd", "/c", "ping -n 30 127.0.0.1 >nul")
	}
	return exec.Command("sleep", "30")
}

// A browser that never acknowledges (it lost track of the transfer) must not keep the file open: the download gives up after the stall
// timeout, reports an error and closes the file, so it can be deleted again.
func TestDownloadGivesUpWhenTheBrowserStopsAcknowledging(t *testing.T) {
	previous := transferStallTimeout
	transferStallTimeout = 200 * time.Millisecond
	t.Cleanup(func() { transferStallTimeout = previous })

	bs := startBackground(t)
	path := filepath.Join(t.TempDir(), "stalled.bin")
	if err := os.WriteFile(path, make([]byte, transferWindow+2*fileChunkBytes), 0o644); err != nil {
		t.Fatal(err)
	}
	response := bs.ok("download", map[string]any{"path": path, "offset": 0})
	bs.expectAction("file.download")
	transfer := uint32(response["transfer"].(float64))

	deadline := time.After(10 * time.Second)
	for {
		select {
		case <-deadline:
			t.Fatal("the stalled download never gave up")
		default:
		}
		kind, payload := bs.browser.read()
		if kind != FrameTransfer {
			continue // chunks are ignored: this browser never acknowledges
		}
		var tb transferBody
		_ = json.Unmarshal(payload, &tb)
		if tb.Transfer != transfer {
			continue
		}
		if tb.Kind != "error" {
			t.Fatalf("expected an error, got %q", tb.Kind)
		}
		break
	}
	// The file is closed again: deleting it works (on Windows an open file cannot be deleted).
	result := bs.request("delete", map[string]any{"path": path})
	for i := 0; result["ok"] != true && i < 20; i++ {
		time.Sleep(50 * time.Millisecond)
		result = bs.request("delete", map[string]any{"path": path})
	}
	if result["ok"] != true {
		t.Fatalf("delete after a stalled download failed: %v", result["error"])
	}
}
