//go:build windows

package patch

import (
	"errors"
	"fmt"
	"log/slog"
	"runtime"
	"sync/atomic"
	"time"

	"github.com/go-ole/go-ole"
	"github.com/go-ole/go-ole/oleutil"
	"golang.org/x/sys/windows/registry"

	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
)

const (
	// muMarkerPath and muMarkerValue are the marker of the registration, next to the marker of internal/winupdate.
	muMarkerPath  = `SOFTWARE\Fleeto`
	muMarkerValue = "MicrosoftUpdateRegistered"
	// muAddFlags is asfAllowPendingRegistration | asfAllowOnlineRegistration | asfRegisterServiceWithAU: register now when the
	// service can be reached, otherwise when it can, and let Automatic Updates use it.
	muAddFlags = 7
	// muTimeout bounds one look at the registration; an online registration can take a while, and a COM call cannot be stopped.
	muTimeout = 2 * time.Minute
)

// muBusy is set while a look at the registration runs, so a call that outlived muTimeout is never joined by a second one.
var muBusy atomic.Bool

// ApplyMicrosoftUpdate registers or removes the Microsoft Update service as the configuration asks and returns the outcome and
// whether it changed anything. want is microsoft_update of a managed configuration of an endpoint Fleeto patches (false otherwise,
// also for an agent-only endpoint). It is idempotent and cheap when nothing is asked and nothing is the agent's, so the agent calls it
// with every inventory, like winupdate.Apply.
func ApplyMicrosoftUpdate(want bool, logger *slog.Logger) (string, bool, error) {
	if logger == nil {
		logger = slog.Default()
	}
	if !muBusy.CompareAndSwap(false, true) {
		return "", false, errors.New("an earlier look at the Microsoft Update registration has not finished yet")
	}
	type result struct {
		outcome string
		changed bool
		err     error
	}
	done := make(chan result, 1)
	go func() {
		r := result{err: errors.New("the Microsoft Update registration failed inside the agent")}
		defer muBusy.Store(false)
		// done is buffered, so this never blocks, also when ApplyMicrosoftUpdate returned already.
		defer func() { done <- r }()
		defer safego.Recover(logger, "microsoft update registration")
		// Never unlocked: the thread ends with this goroutine, after COM was uninitialized on it.
		runtime.LockOSThread()
		if err := ole.CoInitializeEx(0, ole.COINIT_MULTITHREADED); err != nil && oleCode(err) != hrSFalse {
			r = result{err: fmt.Errorf("initialize COM: %w", err)}
			return
		}
		defer ole.CoUninitialize()
		sys := &muWindows{logger: logger}
		defer sys.close()
		outcome, changed, err := applyMicrosoftUpdate(sys, want)
		r = result{outcome, changed, err}
	}()
	select {
	case r := <-done:
		return r.outcome, r.changed, r.err
	case <-time.After(muTimeout):
		return "", false, errors.New("the Windows Update Agent did not answer within 2 minutes; the registration is looked at again " +
			"with the next inventory")
	}
}

// RestoreMicrosoftUpdate removes the registration the agent made, for the uninstall of the agent. It runs before winupdate.Restore,
// which deletes the registry key of the markers.
func RestoreMicrosoftUpdate(logger *slog.Logger) error {
	_, _, err := ApplyMicrosoftUpdate(false, logger)
	return err
}

// muWindows is the Windows Update Agent and the registry. It holds the service manager for one look.
type muWindows struct {
	logger  *slog.Logger
	manager *ole.IDispatch
}

func (m *muWindows) close() {
	if m.manager != nil {
		m.manager.Release()
		m.manager = nil
	}
}

func (m *muWindows) serviceManager() (*ole.IDispatch, error) {
	if m.manager == nil {
		manager, err := createObject("Microsoft.Update.ServiceManager")
		if err != nil {
			return nil, err
		}
		if err := putProperty(manager, "ClientApplicationID", clientApplicationID); err != nil {
			m.logger.Debug("the client application id of the Windows Update service manager could not be set", "error", err)
		}
		m.manager = manager
	}
	return m.manager, nil
}

func (m *muWindows) WSUS() bool {
	_, ok := wsusServer(windowsRegistry{})
	return ok
}

func (m *muWindows) Registered() (bool, error) {
	// The same enumeration of IUpdateServiceManager.Services the scan uses.
	return (&comSession{logger: m.logger}).MicrosoftUpdateRegistered()
}

func (m *muWindows) Add() error {
	manager, err := m.serviceManager()
	if err != nil {
		return err
	}
	v, err := oleutil.CallMethod(manager, "AddService2", microsoftUpdateServiceID, int32(muAddFlags), "")
	if v != nil {
		_ = v.Clear()
	}
	return wrap("AddService2", err)
}

func (m *muWindows) Remove() error {
	manager, err := m.serviceManager()
	if err != nil {
		return err
	}
	v, err := oleutil.CallMethod(manager, "RemoveService", microsoftUpdateServiceID)
	if v != nil {
		_ = v.Clear()
	}
	return wrap("RemoveService", err)
}

func (m *muWindows) Ours() (bool, error) {
	key, err := registry.OpenKey(registry.LOCAL_MACHINE, muMarkerPath, registry.QUERY_VALUE|registry.WOW64_64KEY)
	if errors.Is(err, registry.ErrNotExist) {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	defer key.Close()
	value, _, err := key.GetIntegerValue(muMarkerValue)
	switch {
	case errors.Is(err, registry.ErrNotExist), errors.Is(err, registry.ErrUnexpectedType):
		return false, nil
	case err != nil:
		return false, err
	}
	return value == 1, nil
}

func (m *muWindows) SetOurs() error {
	key, _, err := registry.CreateKey(registry.LOCAL_MACHINE, muMarkerPath, registry.SET_VALUE|registry.WOW64_64KEY)
	if err != nil {
		return err
	}
	defer key.Close()
	return key.SetDWordValue(muMarkerValue, 1)
}

func (m *muWindows) ClearOurs() error {
	key, err := registry.OpenKey(registry.LOCAL_MACHINE, muMarkerPath, registry.SET_VALUE|registry.WOW64_64KEY)
	if errors.Is(err, registry.ErrNotExist) {
		return nil
	}
	if err != nil {
		return err
	}
	defer key.Close()
	if err := key.DeleteValue(muMarkerValue); err != nil && !errors.Is(err, registry.ErrNotExist) {
		return err
	}
	return nil
}
