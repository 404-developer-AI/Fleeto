//go:build windows

package checks

import (
	"context"
	"crypto/x509"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"unsafe"

	"github.com/yusufpapurcu/wmi"
	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

// ---------------------------------------------------------------------------------------------------------------------
// Pending restart
// ---------------------------------------------------------------------------------------------------------------------

func pendingReboot(context.Context) Measurement {
	var reasons []string
	if keyExists(`SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending`) {
		reasons = append(reasons, "Windows component servicing")
	}
	if keyExists(`SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired`) {
		reasons = append(reasons, "Windows Update")
	}
	if active, pending := computerNames(); active != "" && pending != "" && !strings.EqualFold(active, pending) {
		reasons = append(reasons, "computer rename")
	}
	// PendingFileRenameOperations is left out on purpose: installers and antivirus updates set it all the time, so it would
	// report a restart that nothing needs.
	if len(reasons) == 0 {
		return Measurement{Value: 1, Detail: "No restart pending"}
	}
	return Measurement{Value: 0, Detail: "Restart pending for " + strings.Join(reasons, ", ")}
}

func keyExists(path string) bool {
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, path, registry.QUERY_VALUE|registry.WOW64_64KEY)
	if err != nil {
		return false
	}
	_ = k.Close()
	return true
}

func computerNames() (string, string) {
	read := func(path string) string {
		k, err := registry.OpenKey(registry.LOCAL_MACHINE, path, registry.QUERY_VALUE)
		if err != nil {
			return ""
		}
		defer k.Close()
		v, _, _ := k.GetStringValue("ComputerName")
		return v
	}
	return read(`SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName`), read(`SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName`)
}

// ---------------------------------------------------------------------------------------------------------------------
// Certificate store
// ---------------------------------------------------------------------------------------------------------------------

func certificatesFromStore(name string) ([]*x509.Certificate, error) {
	storeName, err := windows.UTF16PtrFromString(name)
	if err != nil {
		return nil, err
	}
	store, err := windows.CertOpenStore(windows.CERT_STORE_PROV_SYSTEM, 0, 0,
		windows.CERT_SYSTEM_STORE_LOCAL_MACHINE|windows.CERT_STORE_READONLY_FLAG|windows.CERT_STORE_OPEN_EXISTING_FLAG,
		uintptr(unsafe.Pointer(storeName)))
	if err != nil {
		return nil, fmt.Errorf(`certificate store LocalMachine\%s could not be opened: %v`, name, err)
	}
	defer windows.CertCloseStore(store, 0)
	var certs []*x509.Certificate
	var ctx *windows.CertContext
	for {
		ctx, err = windows.CertEnumCertificatesInStore(store, ctx)
		if err != nil || ctx == nil {
			break
		}
		der := unsafe.Slice(ctx.EncodedCert, ctx.Length)
		if cert, err := x509.ParseCertificate(append([]byte(nil), der...)); err == nil {
			certs = append(certs, cert)
		}
	}
	return certs, nil
}

// ---------------------------------------------------------------------------------------------------------------------
// Event log
// ---------------------------------------------------------------------------------------------------------------------

var (
	wevtapi      = windows.NewLazySystemDLL("wevtapi.dll")
	procEvtQuery = wevtapi.NewProc("EvtQuery")
	procEvtNext  = wevtapi.NewProc("EvtNext")
	procEvtClose = wevtapi.NewProc("EvtClose")
)

const (
	evtQueryChannelPath    = 0x1
	errorNoMoreItems       = 259
	errorEvtChannelMissing = 15007
	maxEvents              = 10000
)

func eventLogCheck(ctx context.Context, params map[string]string) Measurement {
	query, err := EventLogQuery(params)
	if err != nil {
		return Measurement{Error: err.Error()}
	}
	log := strings.TrimSpace(params["log"])
	window := intParam(params, "window_minutes", 60, 1, 1440)
	count, err := countEvents(ctx, log, query)
	if err != nil {
		return Measurement{Error: err.Error()}
	}
	detail := fmt.Sprintf("%d matching events in the last %d minutes", count, window)
	if count >= maxEvents {
		detail = fmt.Sprintf("%d or more matching events in the last %d minutes", maxEvents, window)
	}
	return Measurement{Value: float64(count), Detail: detail}
}

func countEvents(ctx context.Context, log, query string) (int, error) {
	logPtr, err := windows.UTF16PtrFromString(log)
	if err != nil {
		return 0, err
	}
	queryPtr, err := windows.UTF16PtrFromString(query)
	if err != nil {
		return 0, err
	}
	handle, _, callErr := procEvtQuery.Call(0, uintptr(unsafe.Pointer(logPtr)), uintptr(unsafe.Pointer(queryPtr)), evtQueryChannelPath)
	if handle == 0 {
		if errno, ok := callErr.(windows.Errno); ok && errno == errorEvtChannelMissing {
			return 0, fmt.Errorf("the event log %s does not exist on this endpoint", log)
		}
		return 0, fmt.Errorf("the event log %s could not be queried: %v", log, callErr)
	}
	defer procEvtClose.Call(handle)

	events := make([]uintptr, 64)
	count := 0
	for count < maxEvents {
		if ctx.Err() != nil {
			return count, ctx.Err()
		}
		var returned uint32
		ok, _, callErr := procEvtNext.Call(handle, uintptr(len(events)), uintptr(unsafe.Pointer(&events[0])), 5000, 0, uintptr(unsafe.Pointer(&returned)))
		if ok == 0 {
			if errno, isErrno := callErr.(windows.Errno); isErrno && errno == errorNoMoreItems {
				break
			}
			return count, fmt.Errorf("reading the event log %s failed: %v", log, callErr)
		}
		for _, event := range events[:returned] {
			procEvtClose.Call(event)
		}
		count += int(returned)
	}
	return count, nil
}

// EventLogQuery builds the XPath query of an event log check. Every value is validated first; quotes and brackets are refused.
func EventLogQuery(params map[string]string) (string, error) {
	log := strings.TrimSpace(params["log"])
	if log == "" || len(log) > 200 || strings.ContainsAny(log, `'"<>&[]`) || hasControlChars(log) {
		return "", errors.New("the event log check has no valid log parameter")
	}
	var conditions []string
	switch params["level"] {
	case "critical":
		conditions = append(conditions, "Level=1")
	case "error", "":
		conditions = append(conditions, "(Level=1 or Level=2)")
	case "warning":
		conditions = append(conditions, "(Level=1 or Level=2 or Level=3)")
	case "any":
	default:
		return "", fmt.Errorf("the event log check has an unknown level %q", params["level"])
	}
	if source := strings.TrimSpace(params["source"]); source != "" {
		if len(source) > 200 || strings.ContainsAny(source, `'"<>&[]`) || hasControlChars(source) {
			return "", errors.New("the event log check has an invalid source parameter")
		}
		conditions = append(conditions, fmt.Sprintf("Provider[@Name='%s']", source))
	}
	if ids := strings.TrimSpace(params["event_ids"]); ids != "" {
		var parts []string
		for _, id := range strings.Split(ids, ",") {
			n, err := strconv.Atoi(strings.TrimSpace(id))
			if err != nil || n < 0 || n > 65535 {
				return "", errors.New("the event log check has invalid event ids")
			}
			parts = append(parts, fmt.Sprintf("EventID=%d", n))
		}
		if len(parts) > 20 {
			return "", errors.New("the event log check has more than 20 event ids")
		}
		conditions = append(conditions, "("+strings.Join(parts, " or ")+")")
	}
	window := intParam(params, "window_minutes", 60, 1, 1440)
	conditions = append(conditions, fmt.Sprintf("TimeCreated[timediff(@SystemTime) <= %d]", int64(window)*60*1000))
	return "*[System[" + strings.Join(conditions, " and ") + "]]", nil
}

// ---------------------------------------------------------------------------------------------------------------------
// Security Center
// ---------------------------------------------------------------------------------------------------------------------

type antiVirusProduct struct {
	DisplayName  string
	ProductState uint32
}

type mpComputerStatus struct {
	AntivirusEnabled          bool
	RealTimeProtectionEnabled bool
	AntivirusSignatureAge     uint32
}

type netFirewallProfile struct {
	Name    string
	Enabled uint16
}

func securityCenterCheck(_ context.Context, component string) Measurement {
	switch component {
	case "firewall":
		return firewallStatus()
	case "antivirus", "":
		return antivirusStatus()
	default:
		return Measurement{Error: fmt.Sprintf("the security check has an unknown component %q", component)}
	}
}

func antivirusStatus() Measurement {
	var products []antiVirusProduct
	err := wmi.QueryNamespace("SELECT displayName, productState FROM AntiVirusProduct", &products, `root\SecurityCenter2`)
	if err == nil && len(products) > 0 {
		var parts []string
		fine := false
		for _, p := range products {
			on, current := DecodeProductState(p.ProductState)
			parts = append(parts, fmt.Sprintf("%s: %s", p.DisplayName, describeProduct(on, current)))
			fine = fine || (on && current)
		}
		value := 0.0
		if fine {
			value = 1
		}
		return Measurement{Value: value, Detail: strings.Join(parts, "; ")}
	}
	// Windows Server has no Security Center: ask Microsoft Defender directly.
	var status []mpComputerStatus
	if derr := wmi.QueryNamespace("SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureAge FROM MSFT_MpComputerStatus",
		&status, `root\Microsoft\Windows\Defender`); derr == nil && len(status) > 0 {
		s := status[0]
		current := s.AntivirusSignatureAge <= 7
		on := s.AntivirusEnabled && s.RealTimeProtectionEnabled
		value := 0.0
		if on && current {
			value = 1
		}
		return Measurement{Value: value, Detail: fmt.Sprintf("Microsoft Defender Antivirus: %s, definitions %d days old", describeProduct(on, current), s.AntivirusSignatureAge)}
	}
	if err == nil {
		return Measurement{Value: 0, Detail: "Windows Security Center reports no antivirus product"}
	}
	return Measurement{Error: "the antivirus status is not available: Windows Security Center and Microsoft Defender cannot be queried on this endpoint"}
}

// DecodeProductState reads the Security Center product state: byte 2 is the scanner state (0x10 or 0x11 on), byte 1 the
// definition state (0x00 up to date).
func DecodeProductState(state uint32) (on, upToDate bool) {
	scanner := (state >> 8) & 0xFF
	definitions := state & 0xFF
	return scanner == 0x10 || scanner == 0x11, definitions == 0x00
}

func describeProduct(on, current bool) string {
	switch {
	case on && current:
		return "on, up to date"
	case on:
		return "on, definitions out of date"
	default:
		return "off"
	}
}

func firewallStatus() Measurement {
	var profiles []netFirewallProfile
	if err := wmi.QueryNamespace("SELECT Name, Enabled FROM MSFT_NetFirewallProfile", &profiles, `root\StandardCimv2`); err != nil {
		return Measurement{Error: fmt.Sprintf("the firewall status could not be read: %v", err)}
	}
	if len(profiles) == 0 {
		return Measurement{Error: "Windows reports no firewall profiles"}
	}
	var off []string
	for _, p := range profiles {
		if p.Enabled != 1 {
			off = append(off, p.Name)
		}
	}
	if len(off) > 0 {
		return Measurement{Value: 0, Detail: "Off for the " + strings.Join(off, ", ") + " profile"}
	}
	return Measurement{Value: 1, Detail: fmt.Sprintf("On for every profile (%d)", len(profiles))}
}
