package inventory

import (
	"bufio"
	"strconv"
	"strings"
)

// DSMVersionFile is where Synology DSM keeps its version; DSM has no /etc/os-release.
const DSMVersionFile = "/etc.defaults/VERSION"

// DSMName returns the name of Synology DSM from the content of /etc.defaults/VERSION, in the way DSM names itself, e.g.
// "Synology DSM 7.1.1-42962 Update 8", or "" when the content is not that of DSM. Exported for tests.
func DSMName(content string) string {
	values := map[string]string{}
	scanner := bufio.NewScanner(strings.NewReader(content))
	for scanner.Scan() {
		key, value, ok := strings.Cut(scanner.Text(), "=")
		if ok {
			values[strings.TrimSpace(key)] = strings.Trim(strings.TrimSpace(value), `"'`)
		}
	}
	version := values["productversion"]
	if version == "" {
		major, minor := values["majorversion"], values["minorversion"]
		if major == "" {
			return ""
		}
		version = major
		if minor != "" {
			version += "." + minor
		}
	}
	name := "Synology DSM " + version
	if build := values["buildnumber"]; build != "" {
		name += "-" + build
	}
	if update, err := strconv.Atoi(values["smallfixnumber"]); err == nil && update > 0 {
		name += " Update " + strconv.Itoa(update)
	}
	return name
}
