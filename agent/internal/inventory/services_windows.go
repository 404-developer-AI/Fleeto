//go:build windows

package inventory

import (
	"fmt"
	"unsafe"

	"golang.org/x/sys/windows"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// services lists the Win32 services with their start type and state. It asks only for the rights it needs, so it also works in
// the non-elevated development mode (services whose configuration cannot be read keep an empty start type).
func services() ([]*agentv1.ServiceItem, error) {
	scm, err := windows.OpenSCManager(nil, nil, windows.SC_MANAGER_CONNECT|windows.SC_MANAGER_ENUMERATE_SERVICE)
	if err != nil {
		return nil, fmt.Errorf("open the service manager: %w", err)
	}
	defer windows.CloseServiceHandle(scm)

	var needed, returned, resume uint32
	var buf []byte
	for {
		var ptr *byte
		if len(buf) > 0 {
			ptr = &buf[0]
		}
		err = windows.EnumServicesStatusEx(scm, windows.SC_ENUM_PROCESS_INFO, windows.SERVICE_WIN32, windows.SERVICE_STATE_ALL,
			ptr, uint32(len(buf)), &needed, &returned, &resume, nil)
		if err == nil {
			break
		}
		if err != windows.ERROR_MORE_DATA || needed <= uint32(len(buf)) {
			return nil, fmt.Errorf("list services: %w", err)
		}
		buf = make([]byte, needed)
		resume = 0
	}
	if returned == 0 {
		return nil, nil
	}
	entries := unsafe.Slice((*windows.ENUM_SERVICE_STATUS_PROCESS)(unsafe.Pointer(&buf[0])), returned)
	out := make([]*agentv1.ServiceItem, 0, min(len(entries), maxServices))
	for _, e := range entries {
		if len(out) >= maxServices {
			break
		}
		name := windows.UTF16PtrToString(e.ServiceName)
		out = append(out, &agentv1.ServiceItem{
			Name:        name,
			DisplayName: windows.UTF16PtrToString(e.DisplayName),
			StartType:   startType(scm, name),
			State:       serviceState(e.ServiceStatusProcess.CurrentState),
		})
	}
	return out, nil
}

func startType(scm windows.Handle, name string) string {
	namePtr, err := windows.UTF16PtrFromString(name)
	if err != nil {
		return ""
	}
	h, err := windows.OpenService(scm, namePtr, windows.SERVICE_QUERY_CONFIG)
	if err != nil {
		return ""
	}
	defer windows.CloseServiceHandle(h)
	var needed uint32
	_ = windows.QueryServiceConfig(h, nil, 0, &needed)
	if needed == 0 {
		return ""
	}
	buf := make([]byte, needed)
	config := (*windows.QUERY_SERVICE_CONFIG)(unsafe.Pointer(&buf[0]))
	if err := windows.QueryServiceConfig(h, config, needed, &needed); err != nil {
		return ""
	}
	switch config.StartType {
	case windows.SERVICE_AUTO_START, windows.SERVICE_BOOT_START, windows.SERVICE_SYSTEM_START:
		if delayedAutoStart(h) {
			return "automatic_delayed"
		}
		return "automatic"
	case windows.SERVICE_DEMAND_START:
		return "manual"
	case windows.SERVICE_DISABLED:
		return "disabled"
	default:
		return ""
	}
}

func delayedAutoStart(h windows.Handle) bool {
	var info windows.SERVICE_DELAYED_AUTO_START_INFO
	var needed uint32
	err := windows.QueryServiceConfig2(h, windows.SERVICE_CONFIG_DELAYED_AUTO_START_INFO,
		(*byte)(unsafe.Pointer(&info)), uint32(unsafe.Sizeof(info)), &needed)
	return err == nil && info.IsDelayedAutoStartUp != 0
}

func serviceState(state uint32) string {
	switch state {
	case windows.SERVICE_RUNNING:
		return "running"
	case windows.SERVICE_STOPPED:
		return "stopped"
	case windows.SERVICE_START_PENDING:
		return "starting"
	case windows.SERVICE_STOP_PENDING:
		return "stopping"
	case windows.SERVICE_PAUSED, windows.SERVICE_PAUSE_PENDING, windows.SERVICE_CONTINUE_PENDING:
		return "paused"
	default:
		return ""
	}
}
