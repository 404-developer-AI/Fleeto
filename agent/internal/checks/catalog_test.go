package checks

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"fmt"
	"math/big"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func run(t *testing.T, typ agentv1.CheckType, params map[string]string) []Measurement {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	return SystemCollector{}.Collect(ctx, &agentv1.CheckSpec{Type: typ, IntervalSeconds: 60, Parameters: params})
}

func TestTCPCheckMeasuresAnOpenPortAndReportsAClosedOne(t *testing.T) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	port := listener.Addr().(*net.TCPAddr).Port
	go func() {
		for {
			c, err := listener.Accept()
			if err != nil {
				return
			}
			_ = c.Close()
		}
	}()
	open := run(t, agentv1.CheckType_CHECK_TYPE_TCP_PORT, map[string]string{"host": "127.0.0.1", "port": strconv.Itoa(port)})
	if len(open) != 1 || open[0].Error != "" || open[0].Value < 0 || open[0].Target != fmt.Sprintf("127.0.0.1:%d", port) {
		t.Fatalf("open port: %+v", open)
	}
	_ = listener.Close()
	closed := run(t, agentv1.CheckType_CHECK_TYPE_TCP_PORT, map[string]string{"host": "127.0.0.1", "port": strconv.Itoa(port), "timeout_seconds": "2"})
	if len(closed) != 1 || closed[0].Value != Unreachable || closed[0].Error != "" {
		t.Fatalf("closed port: %+v", closed)
	}
	invalid := run(t, agentv1.CheckType_CHECK_TYPE_TCP_PORT, map[string]string{"host": "-oProxy=x", "port": "22"})
	if invalid[0].Error == "" {
		t.Fatalf("an option-like host must be refused: %+v", invalid)
	}
}

func TestHTTPCheckJudgesStatusTextAndReportsTheCertificate(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/missing" {
			http.NotFound(w, r)
			return
		}
		_, _ = w.Write([]byte("status: healthy"))
	}))
	defer server.Close()

	untrusted := run(t, agentv1.CheckType_CHECK_TYPE_HTTP, map[string]string{"url": server.URL})
	if len(untrusted) != 1 || untrusted[0].Value != Unreachable || !strings.Contains(untrusted[0].Detail, "not trusted") {
		t.Fatalf("an untrusted certificate must fail: %+v", untrusted)
	}

	ok := run(t, agentv1.CheckType_CHECK_TYPE_HTTP, map[string]string{"url": server.URL, "ignore_certificate_errors": "true", "contains": "healthy"})
	if len(ok) != 2 || ok[0].Value < 0 || ok[1].Target != CertificateTarget || ok[1].Value <= 0 {
		t.Fatalf("healthy URL: %+v", ok)
	}

	missingText := run(t, agentv1.CheckType_CHECK_TYPE_HTTP, map[string]string{"url": server.URL, "ignore_certificate_errors": "true", "contains": "degraded"})
	if missingText[0].Value != Unreachable || !strings.Contains(missingText[0].Detail, "does not contain") {
		t.Fatalf("missing text: %+v", missingText)
	}

	notFound := run(t, agentv1.CheckType_CHECK_TYPE_HTTP, map[string]string{"url": server.URL + "/missing", "ignore_certificate_errors": "true",
		"expected_status": "200-299"})
	if notFound[0].Value != Unreachable || !strings.Contains(notFound[0].Detail, "Status 404") {
		t.Fatalf("unexpected status: %+v", notFound)
	}

	for _, bad := range []string{"ftp://example.com", "https://user:pass@example.com", "https://intranet.invalid/a\x01b"} {
		if m := run(t, agentv1.CheckType_CHECK_TYPE_HTTP, map[string]string{"url": bad}); m[0].Error == "" {
			t.Fatalf("URL %q must be refused: %+v", bad, m)
		}
	}
}

func TestPingOfTheLoopbackAddressGetsAReply(t *testing.T) {
	m := run(t, agentv1.CheckType_CHECK_TYPE_PING, map[string]string{"host": "127.0.0.1", "count": "2"})
	if len(m) != 1 || m[0].Error != "" {
		t.Fatalf("ping: %+v", m)
	}
	if m[0].Value == Unreachable {
		if runtime.GOOS != "windows" && os.Geteuid() != 0 {
			t.Skip("unprivileged ICMP sockets are not allowed on this system")
		}
		t.Fatalf("no reply from 127.0.0.1: %+v", m)
	}
}

func TestProcessCheckFindsTheTestProcess(t *testing.T) {
	self, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	name := filepath.Base(self)
	m := run(t, agentv1.CheckType_CHECK_TYPE_PROCESS_RUNNING, map[string]string{"process": strings.ToUpper(strings.TrimSuffix(name, ".exe"))})
	if len(m) != 1 || m[0].Value < 1 {
		t.Fatalf("the test process %s was not found: %+v", name, m)
	}
	absent := run(t, agentv1.CheckType_CHECK_TYPE_PROCESS_RUNNING, map[string]string{"process": "fleeto-no-such-process"})
	if absent[0].Value != 0 || absent[0].Error != "" {
		t.Fatalf("absent process: %+v", absent)
	}
	if path := run(t, agentv1.CheckType_CHECK_TYPE_PROCESS_RUNNING, map[string]string{"process": `C:\Windows\explorer.exe`}); path[0].Error == "" {
		t.Fatal("a path must be refused")
	}
}

func TestFileCheckExistenceSizeAndAge(t *testing.T) {
	dir := t.TempDir()
	file := filepath.Join(dir, "backup.bak")
	if err := os.WriteFile(file, make([]byte, 3*1024*1024), 0o600); err != nil {
		t.Fatal(err)
	}
	old := time.Now().Add(-30 * time.Hour)
	_ = os.Chtimes(file, old, old)
	sub := filepath.Join(dir, "sub")
	_ = os.Mkdir(sub, 0o700)
	_ = os.WriteFile(filepath.Join(sub, "small.txt"), make([]byte, 1024*1024), 0o600)

	cases := []struct {
		params map[string]string
		check  func(Measurement) bool
	}{
		{map[string]string{"path": file, "condition": "exists"}, func(m Measurement) bool { return m.Value == 1 }},
		{map[string]string{"path": file + ".missing", "condition": "exists"}, func(m Measurement) bool { return m.Value == 0 && m.Error == "" }},
		{map[string]string{"path": file + ".missing", "condition": "missing"}, func(m Measurement) bool { return m.Value == 1 }},
		{map[string]string{"path": file, "condition": "size"}, func(m Measurement) bool { return m.Value == 3 }},
		{map[string]string{"path": dir, "condition": "size"}, func(m Measurement) bool { return m.Value == 4 && strings.Contains(m.Detail, "2 files") }},
		{map[string]string{"path": file, "condition": "age"}, func(m Measurement) bool { return m.Value >= 29.9 && m.Value <= 30.1 }},
		{map[string]string{"path": dir, "condition": "age"}, func(m Measurement) bool { return m.Value < 1 }},
		{map[string]string{"path": "relative/path", "condition": "exists"}, func(m Measurement) bool { return m.Error != "" }},
		{map[string]string{"path": file + ".missing", "condition": "size"}, func(m Measurement) bool { return m.Error != "" }},
	}
	for i, c := range cases {
		m := run(t, agentv1.CheckType_CHECK_TYPE_FILE, c.params)
		if len(m) != 1 || !c.check(m[0]) {
			t.Fatalf("case %d %v: %+v", i, c.params, m)
		}
	}
}

func certificate(t *testing.T, cn string, notAfter time.Time) []byte {
	t.Helper()
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	serial, _ := rand.Int(rand.Reader, big.NewInt(1<<62))
	template := &x509.Certificate{SerialNumber: serial, Subject: pkix.Name{CommonName: cn}, NotBefore: notAfter.Add(-365 * 24 * time.Hour), NotAfter: notAfter}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	return der
}

func TestCertificateCheckReadsFilesAndSkipsReplacedCertificates(t *testing.T) {
	now := time.Now()
	dir := t.TempDir()
	oldWeb := certificate(t, "web.example", now.Add(-5*24*time.Hour))
	newWeb := certificate(t, "web.example", now.Add(60*24*time.Hour))
	mail := certificate(t, "mail.example", now.Add(10*24*time.Hour))
	bundle := append(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: oldWeb}), pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: newWeb})...)
	_ = os.WriteFile(filepath.Join(dir, "web.pem"), bundle, 0o600)
	_ = os.WriteFile(filepath.Join(dir, "mail.der"), mail, 0o600)
	_ = os.WriteFile(filepath.Join(dir, "notes.txt"), []byte("not a certificate"), 0o600)

	m := certificateCheck(map[string]string{"location": "path", "path": dir}, now)
	if len(m) != 2 || !strings.HasPrefix(m[0].Target, "mail.example (") || m[0].Value < 9.9 || m[0].Value > 10.1 ||
		!strings.HasPrefix(m[1].Target, "web.example (") || m[1].Value < 59 {
		t.Fatalf("certificates: %+v", m)
	}
	filtered := certificateCheck(map[string]string{"location": "path", "path": dir, "subject": "MAIL"}, now)
	if len(filtered) != 1 || !strings.HasPrefix(filtered[0].Target, "mail.example") {
		t.Fatalf("subject filter: %+v", filtered)
	}
	none := certificateCheck(map[string]string{"location": "path", "path": dir, "subject": "vpn"}, now)
	if len(none) != 1 || none[0].Error == "" {
		t.Fatalf("no match must be an error: %+v", none)
	}
	if runtime.GOOS == "windows" {
		store := certificateCheck(map[string]string{"location": "store", "store": `LocalMachine\Root`}, now)
		if len(store) == 0 || store[0].Error != "" {
			t.Fatalf("the Root store must list certificates: %+v", store)
		}
	}
	if bad := certificateCheck(map[string]string{"location": "store", "store": `CurrentUser\My`}, now); bad[0].Error == "" {
		t.Fatal("a store outside LocalMachine must be refused")
	}
}
