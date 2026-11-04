package patch

import (
	"encoding/binary"
	"errors"
	"fmt"
	"math"
	"math/big"
	"net"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"

	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// The Windows engine (0.9.0 step 2, PATCHING.md section 4): the Windows Update Agent through COM. This file holds everything that is
// not COM, so it is tested with fakes on every platform; scan_windows.go only reads the values from the Windows Update Agent.

const (
	// EngineWindowsUpdate is the engine of a Windows scan.
	EngineWindowsUpdate = "windows_update"

	// SourceWSUS, SourceMicrosoftUpdate and SourceWindowsUpdate say where the Windows Update Agent searched.
	SourceWSUS            = "wsus"
	SourceMicrosoftUpdate = "microsoft_update"
	SourceWindowsUpdate   = "windows_update"

	// microsoftUpdateServiceID is the service id of Microsoft Update. A scan only looks whether it is registered; registering it is
	// done with the configuration in a later step, never by a scan.
	microsoftUpdateServiceID = "7971f918-a847-4430-9279-4a52d1efe18d"

	// ServerSelection of IUpdateSearcher: ssDefault follows what the endpoint has (a WSUS from a group policy, else the default
	// service), ssOthers searches the service named by ServiceID.
	serverSelectionDefault = 0
	serverSelectionOthers  = 3

	// wuaCriteria finds the software updates that are missing and not hidden. Drivers are a later option of the patch policy.
	wuaCriteria = "IsInstalled=0 and IsHidden=0 and Type='Software'"

	// wuaMaxRead bounds the updates read from one search; Finalize keeps at most MaxUpdates of them, security updates first.
	wuaMaxRead = 10000
	// wuaMaxIDs bounds the KB numbers and CVEs kept of one update.
	wuaMaxIDs = 100

	maxClassificationLen = 50
	maxSourceLen         = 20
	maxSourceServerLen   = 255

	// Group policy of Windows Update.
	wuPolicyKey   = `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate`
	wuPolicyAUKey = `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU`
	// Restart indicators, the same the "Pending restart" check reads (except a computer rename, which no update causes).
	cbsRebootPendingKey = `SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending`
	wuRebootRequiredKey = `SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired`

	// IUpdate.Type and InstallationBehavior.RebootBehavior.
	wuaTypeDriver         = 2
	wuaRebootNeverReboots = 0

	// ISearchResult.ResultCode (OperationResultCode).
	wuaResultSucceeded           = 2
	wuaResultSucceededWithErrors = 3

	// Windows names of the update classifications that the scan reports in English, whatever the language of the endpoint.
	classificationSecurityUpdates = "Security Updates"
	classificationCriticalUpdates = "Critical Updates"
	classificationUpgrades        = "Upgrades"
	classificationDrivers         = "Drivers"

	problemWUABusy = "The previous Windows Update search has not finished yet, so no new search was started. It is tried again " +
		"at the next scan; if this keeps happening, restart the Windows Update service (wuauserv)."
	problemWUAInternal = "The Windows Update Agent search failed inside the agent; the agent log has the details."
	problemWUAPartial  = "The Windows Update Agent search finished with errors, so the list may be incomplete. It is tried again " +
		"at the next scan."
)

// classificationNames maps the ids of the update classifications, which are the same on every endpoint and WSUS, to their English
// names. ICategory.Name is in the language of the endpoint.
var classificationNames = map[string]string{
	"0fa1201d-4330-4fa8-8ae9-b877473b6441": classificationSecurityUpdates,
	"e6cf1350-c01b-414d-a61f-263d14d133b4": classificationCriticalUpdates,
	"cd5ffd1e-e932-4e3a-bf74-18bf0b1bbd83": "Updates",
	"28bc880e-0592-4cbf-8f95-c79b17911d5f": "Update Rollups",
	"e0789628-ce08-4437-be74-2495b842f43b": "Definition Updates",
	"b54e7d24-7add-428f-8b75-90a396fa584f": "Feature Packs",
	"68c5b0a3-d1a6-4553-ae49-01d3a7827828": "Service Packs",
	"b4832bd8-e735-4761-8daf-37f882276dab": "Tools",
	"ebfc1fc5-71a4-4f7b-9aca-3b9a503104a0": classificationDrivers,
	"3689bdc8-b205-4af4-8d4a-a63924c5e9d5": classificationUpgrades,
}

var (
	kbPattern  = regexp.MustCompile(`^KB[0-9]{1,10}$`)
	cvePattern = regexp.MustCompile(`^CVE-[0-9]{4}-[0-9]{4,10}$`)
	guidChars  = regexp.MustCompile(`^[0-9a-fA-F-]{1,64}$`)
	hostChars  = regexp.MustCompile(`^[A-Za-z0-9._\-\[\]:]+$`)
)

// wuaCategory is one ICategory of an update.
type wuaCategory struct {
	ID   string
	Name string
	// Type is "UpdateClassification", "Product", "Company" or "ProductFamily".
	Type string
}

// wuaUpdate is what the scan reads of one IUpdate.
type wuaUpdate struct {
	UpdateID       string
	RevisionNumber int64
	Title          string
	// KBArticleIDs are the numbers without "KB", as the Windows Update Agent gives them.
	KBArticleIDs []string
	MsrcSeverity string
	// LastDeploymentChangeTime; zero when the update has none.
	LastDeploymentChangeTime time.Time
	CveIDs                   []string
	MaxDownloadSize          uint64
	IsDownloaded             bool
	IsMandatory              bool
	RebootBehavior           int64
	Type                     int64
	Categories               []wuaCategory
}

// wuaSearchResult is one ISearchResult.
type wuaSearchResult struct {
	ResultCode int64
	Updates    []wuaUpdate
}

// wuaSession is what a scan needs of the Windows Update Agent. Everything it does is read-only: it searches and reads, it never
// downloads, installs or registers a service.
type wuaSession interface {
	// MicrosoftUpdateRegistered reports whether the Microsoft Update service is registered (IUpdateServiceManager.Services).
	MicrosoftUpdateRegistered() (bool, error)
	// Search runs IUpdateSearcher.Search with the server selection and, for ssOthers, the service id.
	Search(criteria string, serverSelection int32, serviceID string) (wuaSearchResult, error)
	// RebootRequired is ISystemInformation.RebootRequired.
	RebootRequired() (bool, error)
}

// registryReader reads the local machine registry (64-bit view).
type registryReader interface {
	String(path, name string) (string, bool)
	Uint32(path, name string) (uint32, bool)
	KeyExists(path string) bool
}

// wuaError is a failed call of the Windows Update Agent with its HRESULT.
type wuaError struct {
	Op   string
	Code uint32
	Err  error
}

func (e *wuaError) Error() string {
	if e.Err != nil {
		return fmt.Sprintf("%s: 0x%08X: %v", e.Op, e.Code, e.Err)
	}
	return fmt.Sprintf("%s: 0x%08X", e.Op, e.Code)
}

func (e *wuaError) Unwrap() error { return e.Err }

// wuaSource is where a search goes.
type wuaSource struct {
	Source          string
	Server          string
	ServerSelection int32
	ServiceID       string
}

// wsusServer returns the WSUS server of the group policy: WUServer together with AU\UseWUServer = 1.
func wsusServer(reg registryReader) (string, bool) {
	use, ok := reg.Uint32(wuPolicyAUKey, "UseWUServer")
	if !ok || use != 1 {
		return "", false
	}
	server, ok := reg.String(wuPolicyKey, "WUServer")
	if !ok || strings.TrimSpace(server) == "" {
		return "", false
	}
	return server, true
}

// hostPort reduces a WUServer value to its host and port, without scheme, user information, path or query; empty when it cannot be
// read as a server name.
func hostPort(raw string) string {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return ""
	}
	if !strings.Contains(raw, "://") {
		raw = "http://" + raw
	}
	u, err := url.Parse(raw)
	if err != nil {
		return ""
	}
	host := u.Host
	if host == "" || len(host) > maxSourceServerLen || !hostChars.MatchString(host) {
		return ""
	}
	if h, port, err := net.SplitHostPort(host); err == nil {
		if h == "" {
			return ""
		}
		if n, err := strconv.Atoi(port); err != nil || n < 1 || n > 65535 {
			return ""
		}
	}
	return host
}

// chooseSource decides where the search goes. A WSUS of a group policy wins and is searched with ssDefault, which follows it. Without
// one, a registered Microsoft Update service is searched explicitly, so Office, SQL Server and .NET updates are included; otherwise
// ssDefault, which is Windows Update. Nothing is registered or changed.
func chooseSource(reg registryReader, sess wuaSession) (wuaSource, error) {
	if server, ok := wsusServer(reg); ok {
		return wuaSource{Source: SourceWSUS, Server: hostPort(server), ServerSelection: serverSelectionDefault}, nil
	}
	registered, err := sess.MicrosoftUpdateRegistered()
	if err != nil {
		// The list of services could not be read: search the default service, which is what the endpoint uses itself.
		return wuaSource{Source: SourceWindowsUpdate, ServerSelection: serverSelectionDefault}, err
	}
	if registered {
		return wuaSource{Source: SourceMicrosoftUpdate, ServerSelection: serverSelectionOthers, ServiceID: microsoftUpdateServiceID}, nil
	}
	return wuaSource{Source: SourceWindowsUpdate, ServerSelection: serverSelectionDefault}, nil
}

// scanWUA runs one Windows scan against a session.
func scanWUA(sess wuaSession, reg registryReader) *agentv1.PatchScanReport {
	report := &agentv1.PatchScanReport{Engine: EngineWindowsUpdate}
	var probs problems
	// A failure to read the list of services is not a problem of the scan: it searches the default service then.
	src, _ := chooseSource(reg, sess)
	report.Source, report.SourceServer = src.Source, src.Server

	result, err := sess.Search(wuaCriteria, src.ServerSelection, src.ServiceID)
	if err != nil {
		probs.add(wuaProblem(errorCode(err), src.Source))
	} else {
		if result.ResultCode != wuaResultSucceeded && result.ResultCode != 0 {
			probs.add(problemWUAPartial)
		}
		for i, u := range result.Updates {
			if i >= wuaMaxRead {
				break
			}
			if p := mapUpdate(u); p != nil {
				report.Updates = append(report.Updates, p)
			}
		}
	}

	restart, err := sess.RebootRequired()
	report.RestartRequired = (err == nil && restart) || reg.KeyExists(cbsRebootPendingKey) || reg.KeyExists(wuRebootRequiredKey)
	report.Problem = probs.String()
	return report
}

// errorCode returns the HRESULT of an error of the session; 0 when it has none.
func errorCode(err error) uint32 {
	var we *wuaError
	if errors.As(err, &we) {
		return we.Code
	}
	return 0
}

// mapUpdate turns one update of the Windows Update Agent into a PatchUpdate; nil when it has no identity.
func mapUpdate(u wuaUpdate) *agentv1.PatchUpdate {
	id := strings.ToLower(strings.TrimSpace(u.UpdateID))
	if !guidChars.MatchString(id) || u.RevisionNumber < 0 {
		return nil
	}
	classification, feature := classificationOf(u.Categories)
	p := &agentv1.PatchUpdate{
		Id:              id + ":" + strconv.FormatInt(u.RevisionNumber, 10),
		Name:            u.Title,
		Classification:  classification,
		Severity:        strings.TrimSpace(u.MsrcSeverity),
		KbNumbers:       kbNumbers(u.KBArticleIDs),
		Cves:            cveIDs(u.CveIDs),
		SizeBytes:       u.MaxDownloadSize,
		Downloaded:      u.IsDownloaded,
		RestartRequired: u.RebootBehavior != wuaRebootNeverReboots,
		Mandatory:       u.IsMandatory,
		IsDriver:        u.Type == wuaTypeDriver || classification == classificationDrivers,
		IsFeatureUpdate: feature,
	}
	if !u.LastDeploymentChangeTime.IsZero() {
		p.ReleasedAt = timestamppb.New(u.LastDeploymentChangeTime.UTC())
	}
	return p
}

// classificationOf returns the English name of the first category of type UpdateClassification, and whether it is an upgrade of
// Windows itself (a feature update).
func classificationOf(categories []wuaCategory) (string, bool) {
	for _, c := range categories {
		if !strings.EqualFold(c.Type, "UpdateClassification") {
			continue
		}
		name, ok := classificationNames[strings.ToLower(strings.TrimSpace(c.ID))]
		if !ok {
			name = strings.TrimSpace(c.Name)
		}
		return name, name == classificationUpgrades
	}
	return "", false
}

// kbNumbers prefixes the KB article ids with "KB" and keeps the valid ones, once each.
func kbNumbers(ids []string) []string {
	var out []string
	for _, id := range ids {
		id = strings.ToUpper(strings.TrimSpace(id))
		if !strings.HasPrefix(id, "KB") {
			id = "KB" + id
		}
		if kbPattern.MatchString(id) && !containsString(out, id) && len(out) < wuaMaxIDs {
			out = append(out, id)
		}
	}
	return out
}

// cveIDs keeps the valid CVE identifiers, uppercase, once each.
func cveIDs(ids []string) []string {
	var out []string
	for _, id := range ids {
		id = strings.ToUpper(strings.TrimSpace(id))
		if cvePattern.MatchString(id) && !containsString(out, id) && len(out) < wuaMaxIDs {
			out = append(out, id)
		}
	}
	return out
}

func containsString(list []string, s string) bool {
	for _, v := range list {
		if v == s {
			return true
		}
	}
	return false
}

// oleDate converts an OLE Automation date (days since 1899-12-30, the fraction is the time of day) to a time in UTC; the Windows
// Update Agent gives LastDeploymentChangeTime in UTC. The zero date, NaN and dates outside 1970 to 9999 are reported as missing.
func oleDate(days float64) (time.Time, bool) {
	if math.IsNaN(days) || math.IsInf(days, 0) || days <= 25569 || days >= 2958466 {
		return time.Time{}, false
	}
	whole := math.Floor(days)
	ms := math.Round((days - whole) * 24 * 60 * 60 * 1000)
	epoch := time.Date(1899, 12, 30, 0, 0, 0, 0, time.UTC)
	return epoch.AddDate(0, 0, int(whole)).Add(time.Duration(ms) * time.Millisecond), true
}

// decimalToUint64 reads a DECIMAL as the 16 bytes of its VARIANT (wReserved, scale, sign, Hi32, Lo64, little-endian), as the
// Windows Update Agent gives MaxDownloadSize. A negative value, an invalid scale or one above the range of uint64 is 0.
func decimalToUint64(raw [16]byte) uint64 {
	scale, sign := raw[2], raw[3]
	hi := binary.LittleEndian.Uint32(raw[4:8])
	lo := binary.LittleEndian.Uint64(raw[8:16])
	if scale > 28 || (sign&0x80 != 0 && (hi != 0 || lo != 0)) {
		return 0
	}
	v := new(big.Int).Lsh(new(big.Int).SetUint64(uint64(hi)), 64)
	v.Or(v, new(big.Int).SetUint64(lo))
	if scale > 0 {
		v.Quo(v, new(big.Int).Exp(big.NewInt(10), big.NewInt(int64(scale)), nil))
	}
	if !v.IsUint64() {
		return 0
	}
	return v.Uint64()
}

// sizeFromValue converts a size the Windows Update Agent gave as another variant type (VT_I8, VT_R8, VT_BSTR and so on); 0 when
// it cannot.
func sizeFromValue(v any) uint64 {
	switch n := v.(type) {
	case int8:
		return uint64(max(n, 0))
	case int16:
		return uint64(max(n, 0))
	case int32:
		return uint64(max(n, 0))
	case int64:
		return uint64(max(n, 0))
	case int:
		return uint64(max(n, 0))
	case uint8:
		return uint64(n)
	case uint16:
		return uint64(n)
	case uint32:
		return uint64(n)
	case uint64:
		return n
	case uint:
		return uint64(n)
	case float32:
		return sizeFromValue(float64(n))
	case float64:
		if math.IsNaN(n) || n <= 0 || n >= math.MaxUint64 {
			return 0
		}
		return uint64(n)
	case string:
		u, err := strconv.ParseUint(strings.TrimSpace(n), 10, 64)
		if err != nil {
			return 0
		}
		return u
	}
	return 0
}

// The HRESULTs a search commonly fails with (Windows Update error codes, and Win32 and WinINet errors as HRESULTs).
const (
	hrServiceDisabled       = 0x80070422 // ERROR_SERVICE_DISABLED
	hrAccessDenied          = 0x80070005 // E_ACCESSDENIED
	hrClassNotRegistered    = 0x80040154 // REGDB_E_CLASSNOTREG
	hrAUNoService           = 0x8024A000 // WU_E_AU_NOSERVICE
	hrServiceStop           = 0x8024001E // WU_E_SERVICE_STOP
	hrNoConnection          = 0x8024001F // WU_E_NO_CONNECTION
	hrWUDisabled            = 0x8024002E // WU_E_WU_DISABLED
	hrCallCancelled         = 0x8024000B // WU_E_CALL_CANCELLED
	hrRedirectorPolicy      = 0x8024500C // WU_E_REDIRECTOR_CONNECT_POLICY
	hrNameNotResolved       = 0x8024402C // WU_E_PT_WINHTTP_NAME_NOT_RESOLVED
	hrEndpointUnreachable   = 0x80240438 // WU_E_PT_ENDPOINT_UNREACHABLE
	hrRequestTimeout        = 0x8024401C // WU_E_PT_HTTP_STATUS_REQUEST_TIMEOUT
	hrGatewayTimeout        = 0x80244023 // WU_E_PT_HTTP_STATUS_GATEWAY_TIMEOUT
	hrDenied                = 0x80244017 // WU_E_PT_HTTP_STATUS_DENIED (401)
	hrForbidden             = 0x80244018 // WU_E_PT_HTTP_STATUS_FORBIDDEN (403)
	hrNotFound              = 0x80244019 // WU_E_PT_HTTP_STATUS_NOT_FOUND (404)
	hrProxyAuth             = 0x8024401B // WU_E_PT_HTTP_STATUS_PROXY_AUTH_REQ (407)
	hrServerError           = 0x8024401F // WU_E_PT_HTTP_STATUS_SERVER_ERROR (500)
	hrBadGateway            = 0x80244021 // WU_E_PT_HTTP_STATUS_BAD_GATEWAY (502)
	hrServiceUnavailable    = 0x80244022 // WU_E_PT_HTTP_STATUS_SERVICE_UNAVAIL (503)
	hrSoapFault             = 0x80244007 // WU_E_PT_SOAPCLIENT_SOAPFAULT
	hrInetTimeout           = 0x80072EE2 // ERROR_INTERNET_TIMEOUT
	hrInetNameNotResolved   = 0x80072EE7 // ERROR_INTERNET_NAME_NOT_RESOLVED
	hrInetCannotConnect     = 0x80072EFD // ERROR_INTERNET_CANNOT_CONNECT
	hrInetConnectionAborted = 0x80072EFE // ERROR_INTERNET_CONNECTION_ABORTED
	hrInetConnectionReset   = 0x80072EFF // ERROR_INTERNET_CONNECTION_RESET
	hrInetSecureFailure     = 0x80072F8F // ERROR_INTERNET_SECURE_FAILURE
)

// wuaProblem states in words why a search failed, with the next step, for the Patches tab.
func wuaProblem(code uint32, source string) string {
	where := "Windows Update"
	switch source {
	case SourceWSUS:
		where = "The WSUS server"
	case SourceMicrosoftUpdate:
		where = "Microsoft Update"
	}
	switch code {
	case hrNameNotResolved, hrInetNameNotResolved, hrInetCannotConnect, hrInetConnectionAborted, hrInetConnectionReset,
		hrEndpointUnreachable, hrNoConnection:
		return fmt.Sprintf("%s could not be reached (0x%08X). Check the network, DNS and proxy of the endpoint; the scan is tried "+
			"again at the next interval.", where, code)
	case hrInetTimeout, hrRequestTimeout, hrGatewayTimeout:
		return fmt.Sprintf("%s did not answer in time (0x%08X). Check the network and proxy of the endpoint; the scan is tried "+
			"again at the next interval.", where, code)
	case hrInetSecureFailure:
		return fmt.Sprintf("No secure connection to %s could be made (0x%08X). Check the clock of the endpoint and whether a "+
			"proxy inspects TLS.", where, code)
	case hrServiceDisabled, hrAUNoService, hrServiceStop:
		return fmt.Sprintf("The Windows Update service (wuauserv) is disabled or not running (0x%08X). Set its start type to "+
			"Manual or Automatic and scan again.", code)
	case hrWUDisabled, hrRedirectorPolicy:
		return fmt.Sprintf("A policy of the endpoint does not allow it to search Windows Update (0x%08X). Check the group "+
			"policies under Windows Update, such as \"Remove access to use all Windows Update features\".", code)
	case hrClassNotRegistered:
		return fmt.Sprintf("The Windows Update Agent is not available on this endpoint (0x%08X). Repair Windows Update "+
			"components and scan again.", code)
	case hrAccessDenied:
		return fmt.Sprintf("The Windows Update Agent refused the search (0x%08X). The agent must run as SYSTEM; reinstall the "+
			"agent if it does not.", code)
	case hrDenied, hrForbidden, hrProxyAuth:
		return fmt.Sprintf("%s refused the connection (0x%08X). Check the proxy settings of the endpoint and, for a WSUS, its "+
			"permissions.", where, code)
	case hrNotFound:
		return fmt.Sprintf("%s does not answer at the configured address (0x%08X). Check the WUServer group policy of the "+
			"endpoint.", where, code)
	case hrServerError, hrBadGateway, hrServiceUnavailable, hrSoapFault:
		return fmt.Sprintf("%s answered with an error (0x%08X). For a WSUS, check that its service and database run; the scan "+
			"is tried again at the next interval.", where, code)
	case hrCallCancelled:
		return fmt.Sprintf("The Windows Update search was cancelled (0x%08X), for example because Windows shut down. It is "+
			"tried again at the next interval.", code)
	case 0:
		return problemWUAInternal
	}
	return fmt.Sprintf("The Windows Update Agent search failed (0x%08X). Look the code up in the Windows Update error "+
		"reference; the scan is tried again at the next interval.", code)
}
