//go:build linux

package inventory

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/shirou/gopsutil/v4/host"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// maxSoftware bounds the packages in one inventory; a Linux endpoint with a desktop has a few thousand.
const maxSoftware = 5000

// commandTimeout bounds every helper the inventory runs.
const commandTimeout = 60 * time.Second

var (
	osInfoOnce  sync.Once
	osInfoValue *agentv1.OsInfo
)

// OSInfo describes the distribution, the kernel and whether this is a server. It is read once per process: it changes with a
// reboot at most.
func OSInfo(ctx context.Context) *agentv1.OsInfo {
	osInfoOnce.Do(func() {
		release := osRelease()
		info := &agentv1.OsInfo{Platform: "linux", Architecture: goArch(), Name: release["PRETTY_NAME"], IsServer: isServer()}
		if info.Name == "" {
			info.Name = strings.TrimSpace(release["NAME"] + " " + release["VERSION_ID"])
		}
		if version := proxmoxVersion(ctx); version != "" {
			// A Proxmox VE host reports itself as Debian; name the product the technician manages.
			info.Name = strings.TrimSpace("Proxmox VE " + version + " (" + info.Name + ")")
		}
		if info.Name == "" {
			info.Name = "Linux"
		}
		if h, err := host.InfoWithContext(ctx); err == nil {
			info.Version = h.KernelVersion
		}
		osInfoValue = info
	})
	return osInfoValue
}

// osRelease reads /etc/os-release (or its fallback) into a map with the quotes removed.
func osRelease() map[string]string {
	values := map[string]string{}
	for _, path := range []string{"/etc/os-release", "/usr/lib/os-release"} {
		file, err := os.Open(path) // #nosec G304 -- fixed paths.
		if err != nil {
			continue
		}
		scanner := bufio.NewScanner(file)
		for scanner.Scan() {
			key, value, ok := strings.Cut(scanner.Text(), "=")
			if !ok {
				continue
			}
			values[strings.TrimSpace(key)] = strings.Trim(strings.TrimSpace(value), `"'`)
		}
		_ = file.Close()
		if len(values) > 0 {
			return values
		}
	}
	return values
}

// isServer: a Linux endpoint without a graphical session is a server (MD-Files/ARCHITECTURE.md §3). A display manager unit, or a
// default target that starts one, means a desktop.
func isServer() bool {
	if _, err := os.Lstat("/etc/systemd/system/display-manager.service"); err == nil {
		return false
	}
	if target, err := os.Readlink("/etc/systemd/system/default.target"); err == nil && strings.HasSuffix(target, "graphical.target") {
		return false
	}
	return true
}

// proxmoxVersion is the pve-manager version of a Proxmox VE host, or "" on any other system.
func proxmoxVersion(ctx context.Context) string {
	tool, err := exec.LookPath("pveversion")
	if err != nil {
		return ""
	}
	out, err := output(ctx, tool)
	if err != nil {
		return ""
	}
	// pveversion prints pve-manager/8.2.4/<hash> (running kernel: ...).
	_, rest, ok := strings.Cut(strings.TrimSpace(out), "pve-manager/")
	if !ok {
		return ""
	}
	version, _, _ := strings.Cut(rest, "/")
	return strings.TrimSpace(version)
}

func collectPlatform(ctx context.Context) (platformInfo, error) {
	info := platformInfo{
		Manufacturer: dmiValue("sys_vendor"),
		Model:        dmiValue("product_name"),
		SerialNumber: dmiValue("product_serial"),
	}
	if info.SerialNumber == "" {
		info.SerialNumber = dmiValue("board_serial")
	}
	if info.Model == "" {
		info.Model = dmiValue("board_name")
	}
	var errs []error
	user, err := loggedOnUser(ctx)
	if err != nil {
		errs = append(errs, err)
	}
	info.LoggedOnUser = user
	domain, err := realmDomain(ctx)
	if err != nil {
		errs = append(errs, err)
	}
	info.Domain = domain
	return info, errors.Join(errs...)
}

// dmiValue reads one DMI field. Values such as "To Be Filled By O.E.M." are left as the firmware wrote them.
func dmiValue(name string) string {
	data, err := os.ReadFile(filepath.Join("/sys/class/dmi/id", name)) // #nosec G304 -- fixed directory, fixed names.
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(data))
}

// loggedOnUser is the user of the local (seated) session, like the console user on Windows. Remote shell sessions are not counted.
func loggedOnUser(ctx context.Context) (string, error) {
	tool, err := exec.LookPath("loginctl")
	if err != nil {
		return "", nil
	}
	out, err := output(ctx, tool, "list-sessions", "--no-legend")
	if err != nil {
		return "", fmt.Errorf("list the sessions: %w", err)
	}
	for line := range strings.SplitSeq(out, "\n") {
		// SESSION UID USER SEAT TTY
		fields := strings.Fields(line)
		if len(fields) >= 4 && strings.HasPrefix(fields[3], "seat") {
			return fields[2], nil
		}
	}
	return "", nil
}

// realmDomain is the Active Directory or IPA domain this endpoint is joined to, or "" when it is not joined.
func realmDomain(ctx context.Context) (string, error) {
	tool, err := exec.LookPath("realm")
	if err != nil {
		return "", nil
	}
	out, err := output(ctx, tool, "list", "--name-only")
	if err != nil {
		// realmd answers with an error when no domain is configured; that is not a problem worth reporting.
		return "", nil
	}
	for line := range strings.SplitSeq(out, "\n") {
		if domain := strings.TrimSpace(line); domain != "" {
			return domain, nil
		}
	}
	return "", nil
}

// installedSoftware reads the package database of the distribution: dpkg on Debian and Ubuntu, rpm on the RHEL family.
func installedSoftware() ([]*agentv1.SoftwareItem, error) {
	ctx, cancel := context.WithTimeout(context.Background(), commandTimeout)
	defer cancel()
	if tool, err := exec.LookPath("dpkg-query"); err == nil {
		return dpkgPackages(ctx, tool)
	}
	if tool, err := exec.LookPath("rpm"); err == nil {
		return rpmPackages(ctx, tool)
	}
	return nil, errors.New("this distribution has no package database Fleeto can read (dpkg or rpm)")
}

func dpkgPackages(ctx context.Context, tool string) ([]*agentv1.SoftwareItem, error) {
	out, err := output(ctx, tool, "-W", "-f=${db:Status-Abbrev}\\t${Package}\\t${Version}\\t${Maintainer}\\n")
	if err != nil {
		return nil, fmt.Errorf("read the dpkg database: %w", err)
	}
	return parseDpkg(out), nil
}

// parseDpkg reads the dpkg-query output: status, package, version and maintainer per line. Only installed packages ("ii") count.
func parseDpkg(out string) []*agentv1.SoftwareItem {
	var items []*agentv1.SoftwareItem
	for line := range strings.SplitSeq(out, "\n") {
		fields := strings.Split(strings.TrimRight(line, "\r"), "\t")
		if len(fields) < 4 || !strings.HasPrefix(strings.TrimSpace(fields[0]), "ii") || fields[1] == "" {
			continue
		}
		if len(items) >= maxSoftware {
			break
		}
		items = append(items, &agentv1.SoftwareItem{Name: fields[1], Version: fields[2], Publisher: publisher(fields[3])})
	}
	return items
}

func rpmPackages(ctx context.Context, tool string) ([]*agentv1.SoftwareItem, error) {
	out, err := output(ctx, tool, "-qa", "--qf", "%{NAME}\\t%{VERSION}-%{RELEASE}\\t%{VENDOR}\\t%{INSTALLTIME}\\n")
	if err != nil {
		return nil, fmt.Errorf("read the rpm database: %w", err)
	}
	return parseRpm(out), nil
}

// parseRpm reads the rpm output: name, version-release, vendor and install time in seconds per line.
func parseRpm(out string) []*agentv1.SoftwareItem {
	var items []*agentv1.SoftwareItem
	for line := range strings.SplitSeq(out, "\n") {
		fields := strings.Split(strings.TrimRight(line, "\r"), "\t")
		// rpm prints (none) for a header without a name.
		if len(fields) < 4 || fields[0] == "" || fields[0] == "(none)" {
			continue
		}
		if len(items) >= maxSoftware {
			break
		}
		item := &agentv1.SoftwareItem{Name: fields[0], Version: fields[1], Publisher: publisher(fields[2])}
		if seconds, err := strconv.ParseInt(strings.TrimSpace(fields[3]), 10, 64); err == nil && seconds > 0 {
			// The same shape as the Windows registry: YYYYMMDD.
			item.InstallDate = time.Unix(seconds, 0).UTC().Format("20060102")
		}
		items = append(items, item)
	}
	return items
}

// publisher is the maintainer or vendor without its email address, which is personal data the inventory does not need.
func publisher(value string) string {
	name, _, _ := strings.Cut(value, "<")
	name = strings.TrimSpace(name)
	if name == "(none)" {
		return ""
	}
	return name
}

// services lists the systemd services with their start type and state, so they can be picked in the check dialog.
func services() ([]*agentv1.ServiceItem, error) {
	tool, err := exec.LookPath("systemctl")
	if err != nil {
		return nil, nil
	}
	ctx, cancel := context.WithTimeout(context.Background(), commandTimeout)
	defer cancel()
	units, err := output(ctx, tool, "list-units", "--type=service", "--all", "--no-legend", "--plain", "--no-pager")
	if err != nil {
		return nil, fmt.Errorf("list the systemd services: %w", err)
	}
	return parseUnits(units, unitFileStates(ctx, tool)), nil
}

// parseUnits reads the systemctl list-units output and gives every service its start type.
func parseUnits(units string, startTypes map[string]string) []*agentv1.ServiceItem {
	var items []*agentv1.ServiceItem
	for line := range strings.SplitSeq(units, "\n") {
		// UNIT LOAD ACTIVE SUB DESCRIPTION
		fields := strings.Fields(strings.TrimSpace(line))
		if len(fields) < 4 || !strings.HasSuffix(fields[0], ".service") {
			continue
		}
		if len(items) >= maxServices {
			break
		}
		unit := fields[0]
		name := strings.TrimSuffix(unit, ".service")
		description := ""
		if len(fields) > 4 {
			description = strings.TrimSpace(strings.Join(fields[4:], " "))
		}
		items = append(items, &agentv1.ServiceItem{
			Name:        name,
			DisplayName: description,
			StartType:   startTypes[unit],
			State:       unitState(fields[2], fields[3]),
		})
	}
	return items
}

// unitFileStates maps a unit to the start type vocabulary Fleeto uses for every platform.
func unitFileStates(ctx context.Context, tool string) map[string]string {
	states := map[string]string{}
	out, err := output(ctx, tool, "list-unit-files", "--type=service", "--no-legend", "--plain", "--no-pager")
	if err != nil {
		return states
	}
	for line := range strings.SplitSeq(out, "\n") {
		fields := strings.Fields(strings.TrimSpace(line))
		if len(fields) < 2 {
			continue
		}
		states[fields[0]] = startTypeOf(fields[1])
	}
	return states
}

func startTypeOf(unitFileState string) string {
	switch unitFileState {
	case "enabled", "enabled-runtime":
		return "automatic"
	// A static, generated or indirect unit cannot be enabled, but the system starts it when something needs it.
	case "static", "generated", "indirect", "alias":
		return "automatic"
	case "disabled":
		return "manual"
	case "masked", "masked-runtime":
		return "disabled"
	default:
		return ""
	}
}

func unitState(active, sub string) string {
	switch active {
	case "active":
		if sub == "exited" || sub == "dead" {
			// A oneshot unit that finished: nothing runs any more.
			return "stopped"
		}
		return "running"
	case "activating":
		return "starting"
	case "deactivating":
		return "stopping"
	default:
		return "stopped"
	}
}

// output runs a command and returns its standard output.
func output(ctx context.Context, name string, args ...string) (string, error) {
	if _, ok := ctx.Deadline(); !ok {
		var cancel context.CancelFunc
		ctx, cancel = context.WithTimeout(ctx, commandTimeout)
		defer cancel()
	}
	out, err := exec.CommandContext(ctx, name, args...).Output() // #nosec G204 -- tools found with LookPath and arguments built here.
	return string(out), err
}

// action1AgentID is Windows-only for now: Action1 keeps its agent id in a configuration file under /var/opt/action1/
// whose name Action1 does not document. Patch management does not cover Linux endpoints yet, and the file is read here
// once it has been seen on a real endpoint.
func action1AgentID() string {
	return ""
}
