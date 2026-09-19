package screen

import (
	"net/url"
	"path"
	"strings"
)

// Files on an X11 clipboard (0.3.0 step 6): file managers offer copied files as text/uri-list (RFC 2483: one URI a line, CRLF, "#" starts
// a comment) and GNOME's Files also as x-special/gnome-copied-files ("copy" or "cut", then one URI a line). Only local file URIs count.

// fileURI turns an absolute path into a file URI with every byte that needs it percent-encoded.
func fileURI(p string) string {
	u := url.URL{Scheme: "file", Path: p}
	return u.String()
}

// uriList is the text/uri-list of paths.
func uriList(paths []string) string {
	var b strings.Builder
	for _, p := range paths {
		b.WriteString(fileURI(p))
		b.WriteString("\r\n")
	}
	return b.String()
}

// gnomeCopiedFiles is the x-special/gnome-copied-files of paths, as a copy (pasting never moves them away).
func gnomeCopiedFiles(paths []string) string {
	lines := make([]string, 0, len(paths)+1)
	lines = append(lines, "copy")
	for _, p := range paths {
		lines = append(lines, fileURI(p))
	}
	return strings.Join(lines, "\n")
}

// pathsFromURIList reads the local file paths of a text/uri-list or x-special/gnome-copied-files; anything else (another scheme, a
// remote host, a relative path) is left out.
func pathsFromURIList(text string) []string {
	var out []string
	for _, line := range strings.Split(text, "\n") {
		line = strings.TrimSpace(strings.TrimSuffix(line, "\r"))
		if line == "" || strings.HasPrefix(line, "#") || line == "copy" || line == "cut" {
			continue
		}
		u, err := url.Parse(line)
		if err != nil || u.Scheme != "file" || (u.Host != "" && u.Host != "localhost") || !strings.HasPrefix(u.Path, "/") {
			continue
		}
		clean := path.Clean(u.Path)
		if strings.ContainsRune(clean, 0) {
			continue
		}
		out = append(out, clean)
		if len(out) >= MaxCopiedFiles {
			break
		}
	}
	return out
}
