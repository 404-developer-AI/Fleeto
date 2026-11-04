//go:build windows

package patch

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"math"
	"runtime"
	"sync/atomic"
	"time"
	"unsafe"

	"github.com/go-ole/go-ole"
	"github.com/go-ole/go-ole/oleutil"
	"golang.org/x/sys/windows/registry"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
)

// wuaBusy is set while a search of the Windows Update Agent runs. A search cannot be stopped once it started, so a scan that timed
// out leaves it running; the next scan then does not start a second one.
var wuaBusy atomic.Bool

const (
	hrDispException = 0x80020009 // DISP_E_EXCEPTION: the real code is in the EXCEPINFO.
	hrSFalse        = 0x00000001 // S_FALSE: COM was initialized on this thread already.
	// clientApplicationID names Fleeto in the log of the Windows Update Agent. It is a property of the search session only.
	clientApplicationID = "Fleeto"
)

// Scan reads the missing updates with the Windows Update Agent. It only searches: nothing is downloaded, installed or registered.
//
// The search is synchronous and runs on its own goroutine, locked to a thread initialized for COM in the multithreaded apartment.
// When ctx ends first, Scan returns a report without updates (the Manager adds the reason); the search finishes on its own and its
// result is dropped.
func Scan(ctx context.Context, requestID string, logger *slog.Logger) *agentv1.PatchScanReport {
	if !wuaBusy.CompareAndSwap(false, true) {
		return &agentv1.PatchScanReport{Engine: EngineWindowsUpdate, Problem: problemWUABusy, RequestId: requestID}
	}
	done := make(chan *agentv1.PatchScanReport, 1)
	go func() {
		report := &agentv1.PatchScanReport{Engine: EngineWindowsUpdate, Problem: problemWUAInternal}
		defer wuaBusy.Store(false)
		// done is buffered, so this never blocks, also when Scan returned already.
		defer func() { done <- report }()
		defer safego.Recover(logger, "windows update search")
		// Never unlocked: the thread ends with this goroutine, after COM was uninitialized on it.
		runtime.LockOSThread()
		if err := ole.CoInitializeEx(0, ole.COINIT_MULTITHREADED); err != nil {
			if oleCode(err) != hrSFalse {
				logger.Warn("COM could not be initialized for the Windows Update search", "error", err)
				report = &agentv1.PatchScanReport{Engine: EngineWindowsUpdate, Problem: wuaProblem(oleCode(err), "")}
				return
			}
		}
		defer ole.CoUninitialize()
		sess := &comSession{logger: logger}
		defer sess.close()
		report = scanWUA(sess, windowsRegistry{})
	}()
	var report *agentv1.PatchScanReport
	select {
	case report = <-done:
	case <-ctx.Done():
		report = &agentv1.PatchScanReport{Engine: EngineWindowsUpdate}
	}
	report.RequestId = requestID
	return report
}

// oleCode returns the HRESULT of a COM error; for DISP_E_EXCEPTION the code the Windows Update Agent raised.
func oleCode(err error) uint32 {
	var oe *ole.OleError
	if !errors.As(err, &oe) {
		return 0
	}
	code := uint32(oe.Code())
	if code == hrDispException {
		if ex, ok := oe.SubError().(ole.EXCEPINFO); ok && ex.SCODE() != 0 {
			return ex.SCODE()
		}
	}
	return code
}

func wrap(op string, err error) error {
	if err == nil {
		return nil
	}
	return &wuaError{Op: op, Code: oleCode(err), Err: err}
}

// comSession is the Windows Update Agent through IDispatch. For a scan it only reads: it never calls Download, Install or
// AddService2. An installation (install_windows.go) sets keep, so the updates of the last search stay available to install.
type comSession struct {
	logger  *slog.Logger
	session *ole.IDispatch
	// keep holds on to the updates of the last search in last.
	keep bool
	last *ole.IDispatch
	// coll is the UpdateColl of an installation, installer its IUpdateInstaller.
	coll      *ole.IDispatch
	installer *ole.IDispatch
}

func (s *comSession) close() {
	for _, obj := range []**ole.IDispatch{&s.installer, &s.coll, &s.last, &s.session} {
		if *obj != nil {
			(*obj).Release()
			*obj = nil
		}
	}
}

func createObject(progID string) (*ole.IDispatch, error) {
	unknown, err := oleutil.CreateObject(progID)
	if err != nil {
		return nil, wrap("create "+progID, err)
	}
	defer unknown.Release()
	disp, err := unknown.QueryInterface(ole.IID_IDispatch)
	if err != nil {
		return nil, wrap("query "+progID, err)
	}
	return disp, nil
}

func (s *comSession) MicrosoftUpdateRegistered() (bool, error) {
	manager, err := createObject("Microsoft.Update.ServiceManager")
	if err != nil {
		return false, err
	}
	defer manager.Release()
	services, err := getObject(manager, "Services")
	if err != nil {
		return false, err
	}
	defer services.Release()
	count, err := getInt(services, "Count")
	if err != nil {
		return false, err
	}
	for i := range count {
		found, err := func() (bool, error) {
			service, err := getObject(services, "Item", int32(i))
			if err != nil {
				return false, err
			}
			defer service.Release()
			id, err := getString(service, "ServiceID")
			if err != nil {
				return false, err
			}
			return equalFoldASCII(id, microsoftUpdateServiceID), nil
		}()
		if err != nil {
			return false, err
		}
		if found {
			return true, nil
		}
	}
	return false, nil
}

func (s *comSession) RebootRequired() (bool, error) {
	info, err := createObject("Microsoft.Update.SystemInfo")
	if err != nil {
		return false, err
	}
	defer info.Release()
	return getBool(info, "RebootRequired")
}

func (s *comSession) Search(criteria string, serverSelection int32, serviceID string) (wuaSearchResult, error) {
	var result wuaSearchResult
	if s.session == nil {
		session, err := createObject("Microsoft.Update.Session")
		if err != nil {
			return result, err
		}
		s.session = session
		if err := putProperty(session, "ClientApplicationID", clientApplicationID); err != nil {
			s.logger.Debug("the client application id of the Windows Update session could not be set", "error", err)
		}
	}
	searcher, err := callObject(s.session, "CreateUpdateSearcher")
	if err != nil {
		return result, err
	}
	defer searcher.Release()
	if err := putProperty(searcher, "ServerSelection", serverSelection); err != nil {
		return result, err
	}
	if serviceID != "" {
		if err := putProperty(searcher, "ServiceID", serviceID); err != nil {
			return result, err
		}
	}
	searchResult, err := callObject(searcher, "Search", criteria)
	if err != nil {
		return result, err
	}
	defer searchResult.Release()
	if code, err := getInt(searchResult, "ResultCode"); err == nil {
		result.ResultCode = code
	}
	updates, err := getObject(searchResult, "Updates")
	if err != nil {
		return result, err
	}
	if s.keep {
		if s.last != nil {
			s.last.Release()
		}
		s.last = updates
	} else {
		defer updates.Release()
	}
	count, err := getInt(updates, "Count")
	if err != nil {
		return result, err
	}
	count = min(max(count, 0), wuaMaxRead)
	for i := range count {
		u, err := readUpdate(updates, int32(i))
		if err != nil {
			// One update that cannot be read does not lose the others.
			s.logger.Debug("an update of the Windows Update search could not be read", "error", err)
			result.ResultCode = wuaResultSucceededWithErrors
			continue
		}
		result.Updates = append(result.Updates, u)
	}
	return result, nil
}

// readUpdate reads one IUpdate of a collection.
func readUpdate(updates *ole.IDispatch, index int32) (wuaUpdate, error) {
	var u wuaUpdate
	update, err := getObject(updates, "Item", index)
	if err != nil {
		return u, err
	}
	defer update.Release()

	identity, err := getObject(update, "Identity")
	if err != nil {
		return u, err
	}
	u.UpdateID, err = getString(identity, "UpdateID")
	if err == nil {
		u.RevisionNumber, err = getInt(identity, "RevisionNumber")
	}
	identity.Release()
	if err != nil {
		return u, err
	}
	if u.Title, err = getString(update, "Title"); err != nil {
		return u, err
	}
	// The rest is optional: a value that cannot be read stays empty.
	u.KBArticleIDs, _ = getStrings(update, "KBArticleIDs")
	u.CveIDs, _ = getStrings(update, "CveIDs")
	u.MsrcSeverity, _ = getString(update, "MsrcSeverity")
	u.LastDeploymentChangeTime = getDate(update, "LastDeploymentChangeTime")
	u.MaxDownloadSize = getSize(update, "MaxDownloadSize")
	u.IsDownloaded, _ = getBool(update, "IsDownloaded")
	u.IsMandatory, _ = getBool(update, "IsMandatory")
	u.Type, _ = getInt(update, "Type")
	if behavior, err := getObject(update, "InstallationBehavior"); err == nil {
		if rb, err := getInt(behavior, "RebootBehavior"); err == nil {
			u.RebootBehavior = rb
		} else {
			// Unknown: assume it may need a restart rather than promise it does not.
			u.RebootBehavior = 2
		}
		behavior.Release()
	} else {
		u.RebootBehavior = 2
	}
	u.Categories = readCategories(update)
	return u, nil
}

func readCategories(update *ole.IDispatch) []wuaCategory {
	categories, err := getObject(update, "Categories")
	if err != nil {
		return nil
	}
	defer categories.Release()
	count, err := getInt(categories, "Count")
	if err != nil {
		return nil
	}
	var out []wuaCategory
	for i := range min(max(count, 0), 100) {
		category, err := getObject(categories, "Item", int32(i))
		if err != nil {
			continue
		}
		var c wuaCategory
		c.ID, _ = getString(category, "CategoryID")
		c.Name, _ = getString(category, "Name")
		c.Type, _ = getString(category, "Type")
		category.Release()
		out = append(out, c)
	}
	return out
}

// --- IDispatch helpers. Every VARIANT is cleared; an object is returned with its reference, which the caller releases. ---

func getVariant(disp *ole.IDispatch, name string, params ...any) (*ole.VARIANT, error) {
	v, err := oleutil.GetProperty(disp, name, params...)
	if err != nil {
		if v != nil {
			_ = v.Clear()
		}
		return nil, wrap(name, err)
	}
	return v, nil
}

func getObject(disp *ole.IDispatch, name string, params ...any) (*ole.IDispatch, error) {
	v, err := getVariant(disp, name, params...)
	if err != nil {
		return nil, err
	}
	return variantObject(v, name)
}

func callObject(disp *ole.IDispatch, name string, params ...any) (*ole.IDispatch, error) {
	v, err := oleutil.CallMethod(disp, name, params...)
	if err != nil {
		if v != nil {
			_ = v.Clear()
		}
		return nil, wrap(name, err)
	}
	return variantObject(v, name)
}

// variantObject takes the IDispatch out of a VARIANT; the reference the VARIANT held passes to the caller.
func variantObject(v *ole.VARIANT, name string) (*ole.IDispatch, error) {
	obj := v.ToIDispatch()
	if obj == nil {
		_ = v.Clear()
		return nil, &wuaError{Op: name, Err: fmt.Errorf("%s is not an object (VT %d)", name, v.VT)}
	}
	return obj, nil
}

func putProperty(disp *ole.IDispatch, name string, value any) error {
	v, err := oleutil.PutProperty(disp, name, value)
	if v != nil {
		_ = v.Clear()
	}
	return wrap(name, err)
}

func getString(disp *ole.IDispatch, name string) (string, error) {
	v, err := getVariant(disp, name)
	if err != nil {
		return "", err
	}
	defer v.Clear()
	if v.VT == ole.VT_BSTR {
		return v.ToString(), nil
	}
	return "", nil
}

func getInt(disp *ole.IDispatch, name string) (int64, error) {
	v, err := getVariant(disp, name)
	if err != nil {
		return 0, err
	}
	defer v.Clear()
	switch n := v.Value().(type) {
	case int8:
		return int64(n), nil
	case int16:
		return int64(n), nil
	case int32:
		return int64(n), nil
	case int64:
		return n, nil
	case int:
		return int64(n), nil
	case uint8:
		return int64(n), nil
	case uint16:
		return int64(n), nil
	case uint32:
		return int64(n), nil
	case uint64:
		if n > math.MaxInt64 {
			return 0, &wuaError{Op: name, Err: fmt.Errorf("out of range")}
		}
		return int64(n), nil
	case uint:
		return int64(n), nil
	}
	return 0, &wuaError{Op: name, Err: fmt.Errorf("%s is not a number (VT %d)", name, v.VT)}
}

func getBool(disp *ole.IDispatch, name string) (bool, error) {
	v, err := getVariant(disp, name)
	if err != nil {
		return false, err
	}
	defer v.Clear()
	if v.VT != ole.VT_BOOL {
		return false, &wuaError{Op: name, Err: fmt.Errorf("%s is not a boolean (VT %d)", name, v.VT)}
	}
	return v.Val != 0, nil
}

// getDate reads a VT_DATE in pure Go; zero when it is missing or not a date.
func getDate(disp *ole.IDispatch, name string) (t time.Time) {
	v, err := getVariant(disp, name)
	if err != nil {
		return t
	}
	defer v.Clear()
	if v.VT != ole.VT_DATE {
		return t
	}
	t, _ = oleDate(math.Float64frombits(uint64(v.Val)))
	return t
}

// The DECIMAL of a VT_DECIMAL fills the first 16 bytes of the VARIANT, its reserved word overlapping vt.
var _ [unsafe.Sizeof(ole.VARIANT{}) - 16]byte

// getSize reads MaxDownloadSize, a DECIMAL that may also come as another number type; 0 when it cannot be read.
func getSize(disp *ole.IDispatch, name string) uint64 {
	v, err := getVariant(disp, name)
	if err != nil {
		return 0
	}
	defer v.Clear()
	if v.VT == ole.VT_DECIMAL {
		return decimalToUint64(*(*[16]byte)(unsafe.Pointer(v)))
	}
	if v.VT == ole.VT_DATE {
		return 0
	}
	return sizeFromValue(v.Value())
}

// getStrings reads an IStringCollection.
func getStrings(disp *ole.IDispatch, name string) ([]string, error) {
	coll, err := getObject(disp, name)
	if err != nil {
		return nil, err
	}
	defer coll.Release()
	count, err := getInt(coll, "Count")
	if err != nil {
		return nil, err
	}
	var out []string
	for i := range min(max(count, 0), wuaMaxIDs) {
		v, err := getVariant(coll, "Item", int32(i))
		if err != nil {
			return out, err
		}
		if v.VT == ole.VT_BSTR {
			out = append(out, v.ToString())
		}
		_ = v.Clear()
	}
	return out, nil
}

func equalFoldASCII(a, b string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range len(a) {
		x, y := a[i]|0x20, b[i]|0x20
		if x != y {
			return false
		}
	}
	return true
}

// windowsRegistry reads the 64-bit view of HKEY_LOCAL_MACHINE.
type windowsRegistry struct{}

func (windowsRegistry) String(path, name string) (string, bool) {
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, path, registry.QUERY_VALUE|registry.WOW64_64KEY)
	if err != nil {
		return "", false
	}
	defer k.Close()
	v, _, err := k.GetStringValue(name)
	return v, err == nil
}

func (windowsRegistry) Uint32(path, name string) (uint32, bool) {
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, path, registry.QUERY_VALUE|registry.WOW64_64KEY)
	if err != nil {
		return 0, false
	}
	defer k.Close()
	v, _, err := k.GetIntegerValue(name)
	if err != nil || v > math.MaxUint32 {
		return 0, false
	}
	return uint32(v), true
}

func (windowsRegistry) KeyExists(path string) bool {
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, path, registry.QUERY_VALUE|registry.WOW64_64KEY)
	if err != nil {
		return false
	}
	_ = k.Close()
	return true
}
