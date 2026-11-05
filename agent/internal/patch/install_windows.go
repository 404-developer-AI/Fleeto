//go:build windows

package patch

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"runtime"
	"strconv"
	"strings"
	"time"

	"github.com/go-ole/go-ole"
	"github.com/go-ole/go-ole/oleutil"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
)

// wuaClaimWait is how long an installation waits for a search of the Windows Update Agent (a scan) to end.
const wuaClaimWait = ScanTimeout

const problemWUAInstallInternal = "The installation through the Windows Update Agent failed inside the agent; the agent log has the details."

// InstallEngine is the engine that installs updates on this endpoint.
func InstallEngine() string { return EngineWindowsUpdate }

// Install searches, downloads and installs updates with the Windows Update Agent, on a thread of its own initialized for COM in the
// multithreaded apartment. It holds the busy guard of the scan, so no search runs next to it. It is synchronous and never stopped:
// a call of the Windows Update Agent cannot be cancelled, and ctx only keeps an installation from starting after the agent stops.
// The tool's own output goes to tool, the "fleeto-patch:" lines to lines.
func Install(ctx context.Context, job *agentv1.PatchInstallJob, opts InstallOptions, tool, lines io.Writer, logger *slog.Logger) InstallResult {
	out := newInstallOutput(tool, lines)
	done := make(chan InstallResult, 1)
	go func() {
		result := InstallResult{ExitCode: 1, Error: problemWUAInstallInternal}
		defer func() { done <- result }()
		defer safego.Recover(logger, "windows update installation")
		if !claimWUA(ctx) {
			result = failAll(out, job, "A Windows Update search did not end within 15 minutes, so nothing was installed. Restart the "+
				"Windows Update service (wuauserv) if this keeps happening.")
			return
		}
		defer wuaBusy.Store(false)
		// Never unlocked: the thread ends with this goroutine, after COM was uninitialized on it.
		runtime.LockOSThread()
		if err := ole.CoInitializeEx(0, ole.COINIT_MULTITHREADED); err != nil && oleCode(err) != hrSFalse {
			logger.Warn("COM could not be initialized for the Windows Update installation", "error", err)
			result = failAll(out, job, wuaProblem(oleCode(err), ""))
			return
		}
		defer ole.CoUninitialize()
		sess := &comSession{logger: logger, keep: true}
		defer sess.close()
		result = installWUA(ctx, sess, windowsRegistry{}, job, opts.Drivers, out, time.Sleep)
	}()
	return <-done
}

// claimWUA takes the busy guard of the Windows Update Agent, waiting for a running search to end.
func claimWUA(ctx context.Context) bool {
	deadline := time.Now().Add(wuaClaimWait)
	for !wuaBusy.CompareAndSwap(false, true) {
		if time.Now().After(deadline) || ctx.Err() != nil {
			return false
		}
		time.Sleep(time.Second)
	}
	return true
}

// identityKey reads "updateid:revision" of an IUpdate, in the form of the scan.
func identityKey(update *ole.IDispatch) (string, error) {
	identity, err := getObject(update, "Identity")
	if err != nil {
		return "", err
	}
	defer identity.Release()
	id, err := getString(identity, "UpdateID")
	if err != nil {
		return "", err
	}
	revision, err := getInt(identity, "RevisionNumber")
	if err != nil {
		return "", err
	}
	return strings.ToLower(strings.TrimSpace(id)) + ":" + strconv.FormatInt(revision, 10), nil
}

func (s *comSession) Collect(ids []string) ([]string, error) {
	if len(s.kept) == 0 {
		return nil, errors.New("no search result to install from")
	}
	wanted := map[string]bool{}
	for _, id := range ids {
		wanted[id] = true
	}
	coll, err := createObject("Microsoft.Update.UpdateColl")
	if err != nil {
		return nil, err
	}
	if s.coll != nil {
		s.coll.Release()
	}
	s.coll = coll
	var taken []string
	// The searches of the installation in order: software first, then drivers. An update is taken once.
	for _, updates := range s.kept {
		count, err := getInt(updates, "Count")
		if err != nil {
			return nil, err
		}
		for i := range min(max(count, 0), wuaMaxRead) {
			key, err := s.collectOne(coll, updates, int32(i), wanted)
			if err != nil {
				s.logger.Warn("an update could not be prepared for installation", "error", err)
				continue
			}
			if key != "" {
				taken = append(taken, key)
			}
		}
	}
	return taken, nil
}

// collectOne adds update index of a search result to the collection when it is wanted, after accepting its license terms. It
// returns the id it took, or empty.
func (s *comSession) collectOne(coll, updates *ole.IDispatch, index int32, wanted map[string]bool) (string, error) {
	update, err := getObject(updates, "Item", index)
	if err != nil {
		return "", err
	}
	defer update.Release()
	key, err := identityKey(update)
	if err != nil || !wanted[key] {
		return "", nil
	}
	delete(wanted, key)
	if accepted, err := getBool(update, "EulaAccepted"); err != nil || !accepted {
		v, err := oleutil.CallMethod(update, "AcceptEula")
		if err != nil {
			return "", wrap("AcceptEula", err)
		}
		_ = v.Clear()
	}
	v, err := oleutil.CallMethod(coll, "Add", update)
	if err != nil {
		return "", wrap("Add", err)
	}
	_ = v.Clear()
	return key, nil
}

// putUpdates sets the Updates property of a downloader or installer to the collection; it is a property put by reference.
func (s *comSession) putUpdates(obj *ole.IDispatch) error {
	v, err := oleutil.PutPropertyRef(obj, "Updates", s.coll)
	if v != nil {
		_ = v.Clear()
	}
	if err == nil {
		return nil
	}
	v, err = oleutil.PutProperty(obj, "Updates", s.coll)
	if v != nil {
		_ = v.Clear()
	}
	return wrap("Updates", err)
}

// readOperation reads an IDownloadResult or IInstallationResult for n updates.
func readOperation(result *ole.IDispatch, n int, reboot bool) wuaOperation {
	var op wuaOperation
	op.ResultCode, _ = getInt(result, "ResultCode")
	if hr, err := getInt(result, "HResult"); err == nil {
		op.HResult = uint32(hr)
	}
	if reboot {
		op.RebootRequired, _ = getBool(result, "RebootRequired")
	}
	for i := range n {
		var r wuaUpdateResult
		if item, err := callObject(result, "GetUpdateResult", int32(i)); err == nil {
			r.ResultCode, _ = getInt(item, "ResultCode")
			if hr, err := getInt(item, "HResult"); err == nil {
				r.HResult = uint32(hr)
			}
			if reboot {
				r.RebootRequired, _ = getBool(item, "RebootRequired")
			}
			item.Release()
		}
		op.Updates = append(op.Updates, r)
	}
	return op
}

func (s *comSession) collCount() int {
	n, err := getInt(s.coll, "Count")
	if err != nil {
		return 0
	}
	return int(min(max(n, 0), wuaMaxRead))
}

func (s *comSession) Download() (wuaOperation, error) {
	if s.coll == nil || s.session == nil {
		return wuaOperation{}, errors.New("nothing to download")
	}
	downloader, err := callObject(s.session, "CreateUpdateDownloader")
	if err != nil {
		return wuaOperation{}, err
	}
	defer downloader.Release()
	_ = putProperty(downloader, "ClientApplicationID", clientApplicationID)
	if err := s.putUpdates(downloader); err != nil {
		return wuaOperation{}, err
	}
	result, err := callObject(downloader, "Download")
	if err != nil {
		return wuaOperation{}, err
	}
	defer result.Release()
	return readOperation(result, s.collCount(), false), nil
}

func (s *comSession) ensureInstaller() error {
	if s.installer != nil {
		return nil
	}
	if s.session == nil {
		return errors.New("no Windows Update session")
	}
	installer, err := callObject(s.session, "CreateUpdateInstaller")
	if err != nil {
		return err
	}
	s.installer = installer
	_ = putProperty(installer, "ClientApplicationID", clientApplicationID)
	return nil
}

func (s *comSession) InstallerBusy() (bool, error) {
	if err := s.ensureInstaller(); err != nil {
		return false, err
	}
	return getBool(s.installer, "IsBusy")
}

func (s *comSession) RebootRequiredBeforeInstallation() (bool, error) {
	if err := s.ensureInstaller(); err != nil {
		return false, err
	}
	return getBool(s.installer, "RebootRequiredBeforeInstallation")
}

func (s *comSession) Install() (wuaOperation, error) {
	if s.coll == nil {
		return wuaOperation{}, errors.New("nothing to install")
	}
	if err := s.ensureInstaller(); err != nil {
		return wuaOperation{}, err
	}
	if err := s.putUpdates(s.installer); err != nil {
		return wuaOperation{}, err
	}
	result, err := callObject(s.installer, "Install")
	if err != nil {
		return wuaOperation{}, err
	}
	defer result.Release()
	return readOperation(result, s.collCount(), true), nil
}
