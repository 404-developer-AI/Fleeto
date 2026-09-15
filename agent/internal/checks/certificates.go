package checks

import (
	"crypto/sha1" // #nosec G505 -- the thumbprint only identifies a certificate for people, as Windows shows it.
	"crypto/x509"
	"encoding/hex"
	"encoding/pem"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"time"
)

// maxCertificates bounds the results of one certificate check.
const maxCertificates = 100

var storePattern = regexp.MustCompile(`^LocalMachine\\[A-Za-z0-9 _\-]{1,64}$`)

func certificateCheck(params map[string]string, now time.Time) []Measurement {
	subject := strings.TrimSpace(params["subject"])
	if hasControlChars(subject) || len(subject) > 200 {
		return []Measurement{{Error: "the certificate check has an invalid subject parameter"}}
	}
	var certs []*x509.Certificate
	var err error
	switch params["location"] {
	case "path":
		path := strings.TrimSpace(params["path"])
		if !absolutePath(path) {
			return []Measurement{{Error: "the certificate check needs a full path"}}
		}
		certs, err = certificatesFromPath(path)
	case "store", "":
		store := strings.TrimSpace(params["store"])
		if store == "" {
			store = `LocalMachine\My`
		}
		if !storePattern.MatchString(store) {
			return []Measurement{{Error: `the certificate check needs a store such as LocalMachine\My`}}
		}
		certs, err = certificatesFromStore(strings.TrimPrefix(store, `LocalMachine\`))
	default:
		return []Measurement{{Error: fmt.Sprintf("the certificate check has an unknown location %q", params["location"])}}
	}
	if err != nil {
		return []Measurement{{Error: err.Error()}}
	}
	selected := SelectCertificates(certs, subject)
	if len(selected) == 0 {
		return []Measurement{{Error: "no certificates found" + subjectSuffix(subject)}}
	}
	out := make([]Measurement, 0, len(selected))
	for _, cert := range selected {
		days := cert.NotAfter.Sub(now).Hours() / 24
		out = append(out, Measurement{
			Target: CertificateName(cert),
			Value:  round(days, 1),
			Detail: fmt.Sprintf("Expires %s, issued by %s", cert.NotAfter.UTC().Format("2006-01-02"), displayName(cert.Issuer.CommonName, cert.Issuer.String())),
		})
	}
	return out
}

func subjectSuffix(subject string) string {
	if subject == "" {
		return ""
	}
	return fmt.Sprintf(" with %q in the subject", subject)
}

// SelectCertificates filters on the subject and leaves out a certificate that was replaced by a newer one with the same subject
// (an auto-renewed certificate often stays in the store). The result is sorted by expiry and bounded.
func SelectCertificates(certs []*x509.Certificate, subject string) []*x509.Certificate {
	latest := map[string]*x509.Certificate{}
	for _, cert := range certs {
		if cert == nil || (subject != "" && !strings.Contains(strings.ToLower(cert.Subject.String()), strings.ToLower(subject))) {
			continue
		}
		key := cert.Subject.String()
		if current, ok := latest[key]; !ok || cert.NotAfter.After(current.NotAfter) {
			latest[key] = cert
		}
	}
	out := make([]*x509.Certificate, 0, len(latest))
	for _, cert := range latest {
		out = append(out, cert)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].NotAfter.Before(out[j].NotAfter) })
	if len(out) > maxCertificates {
		out = out[:maxCertificates]
	}
	return out
}

// CertificateName is the target of a certificate result: its common name (or subject) and the first 8 characters of its thumbprint.
func CertificateName(cert *x509.Certificate) string {
	sum := sha1.Sum(cert.Raw) // #nosec G401 -- identification only.
	thumb := strings.ToUpper(hex.EncodeToString(sum[:]))[:8]
	return fmt.Sprintf("%s (%s)", displayName(cert.Subject.CommonName, cert.Subject.String()), thumb)
}

func displayName(commonName, full string) string {
	if commonName != "" {
		return commonName
	}
	if full != "" {
		return full
	}
	return "unnamed"
}

// certificatesFromPath reads PEM or DER certificates from a file, or from the certificate files in a folder (not recursive).
func certificatesFromPath(path string) ([]*x509.Certificate, error) {
	info, err := os.Stat(path)
	if err != nil {
		return nil, fmt.Errorf("%s could not be read: %v", path, err)
	}
	files := []string{path}
	if info.IsDir() {
		entries, err := os.ReadDir(path)
		if err != nil {
			return nil, fmt.Errorf("%s could not be listed: %v", path, err)
		}
		files = files[:0]
		for _, e := range entries {
			switch strings.ToLower(filepath.Ext(e.Name())) {
			case ".pem", ".crt", ".cer", ".der":
				if !e.IsDir() {
					files = append(files, filepath.Join(path, e.Name()))
				}
			}
		}
	}
	var certs []*x509.Certificate
	for _, file := range files {
		data, err := readLimited(file, 4<<20)
		if err != nil {
			continue
		}
		certs = append(certs, ParseCertificates(data)...)
	}
	return certs, nil
}

func readLimited(path string, limit int64) ([]byte, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	buf := make([]byte, limit)
	n, err := f.Read(buf)
	if n == 0 && err != nil {
		return nil, err
	}
	return buf[:n], nil
}

// ParseCertificates reads every PEM CERTIFICATE block, or a single DER certificate when the data is not PEM.
func ParseCertificates(data []byte) []*x509.Certificate {
	var out []*x509.Certificate
	rest := data
	foundPEM := false
	for {
		var block *pem.Block
		block, rest = pem.Decode(rest)
		if block == nil {
			break
		}
		foundPEM = true
		if block.Type != "CERTIFICATE" {
			continue
		}
		if cert, err := x509.ParseCertificate(block.Bytes); err == nil {
			out = append(out, cert)
		}
	}
	if !foundPEM {
		if cert, err := x509.ParseCertificate(data); err == nil {
			out = append(out, cert)
		}
	}
	return out
}
