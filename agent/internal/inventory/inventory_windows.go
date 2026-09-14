//go:build windows

package inventory

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"sync"

	"github.com/yusufpapurcu/wmi"
	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

type win32OperatingSystem struct {
	Caption     string
	Version     string
	BuildNumber string
	ProductType uint32
}

type win32ComputerSystem struct {
	Manufacturer string
	Model        string
	Domain       string
	PartOfDomain bool
	UserName     *string
}

type win32BIOS struct {
	SerialNumber string
}

var (
	osInfoOnce  sync.Once
	osInfoValue *agentv1.OsInfo
)

// OSInfo returns the operating system description. It is read once per process: it only changes with a reboot.
func OSInfo(context.Context) *agentv1.OsInfo {
	osInfoOnce.Do(func() {
		info := &agentv1.OsInfo{Platform: "windows", Architecture: nativeArchitecture()}
		var rows []win32OperatingSystem
		if err := wmi.Query(wmi.CreateQuery(&rows, "", "Win32_OperatingSystem"), &rows); err == nil && len(rows) > 0 {
			info.Name = strings.TrimSpace(strings.TrimPrefix(strings.TrimSpace(rows[0].Caption), "Microsoft "))
			info.Version = rows[0].Version
			// ProductType 1 is a workstation; 2 (domain controller) and 3 (server) are server editions.
			info.IsServer = rows[0].ProductType != 1
		} else {
			major, minor, build := windows.RtlGetNtVersionNumbers()
			info.Name = "Windows"
			info.Version = fmt.Sprintf("%d.%d.%d", major, minor, build)
		}
		osInfoValue = info
	})
	return osInfoValue
}

func nativeArchitecture() string {
	var processMachine, nativeMachine uint16
	if err := windows.IsWow64Process2(windows.CurrentProcess(), &processMachine, &nativeMachine); err == nil {
		switch nativeMachine {
		case 0x8664:
			return "amd64"
		case 0xAA64:
			return "arm64"
		case 0x014c:
			return "386"
		}
	}
	return goArch()
}

func collectPlatform(context.Context) (platformInfo, error) {
	var info platformInfo
	var errs []error
	var cs []win32ComputerSystem
	if err := wmi.Query(wmi.CreateQuery(&cs, "", "Win32_ComputerSystem"), &cs); err == nil && len(cs) > 0 {
		info.Manufacturer = strings.TrimSpace(cs[0].Manufacturer)
		info.Model = strings.TrimSpace(cs[0].Model)
		if cs[0].PartOfDomain {
			info.Domain = cs[0].Domain
		}
		if cs[0].UserName != nil {
			info.LoggedOnUser = *cs[0].UserName
		}
	} else if err != nil {
		errs = append(errs, fmt.Errorf("Win32_ComputerSystem: %w", err))
	}
	var bios []win32BIOS
	if err := wmi.Query(wmi.CreateQuery(&bios, "", "Win32_BIOS"), &bios); err == nil && len(bios) > 0 {
		info.SerialNumber = strings.TrimSpace(bios[0].SerialNumber)
	} else if err != nil {
		errs = append(errs, fmt.Errorf("Win32_BIOS: %w", err))
	}
	return info, errors.Join(errs...)
}

const uninstallPath = `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`

// installedSoftware reads the machine-wide Uninstall keys in the 64-bit and the 32-bit registry view.
func installedSoftware() ([]*agentv1.SoftwareItem, error) {
	var items []*agentv1.SoftwareItem
	var errs []error
	for _, view := range []uint32{registry.WOW64_64KEY, registry.WOW64_32KEY} {
		found, err := readUninstallKey(view)
		if err != nil {
			errs = append(errs, err)
		}
		items = append(items, found...)
	}
	if len(items) > 0 {
		return items, nil
	}
	return items, errors.Join(errs...)
}

func readUninstallKey(view uint32) ([]*agentv1.SoftwareItem, error) {
	root, err := registry.OpenKey(registry.LOCAL_MACHINE, uninstallPath, registry.ENUMERATE_SUB_KEYS|registry.QUERY_VALUE|view)
	if err != nil {
		return nil, fmt.Errorf("open %s: %w", uninstallPath, err)
	}
	defer root.Close()
	names, err := root.ReadSubKeyNames(-1)
	if err != nil {
		return nil, fmt.Errorf("list %s: %w", uninstallPath, err)
	}
	var items []*agentv1.SoftwareItem
	for _, name := range names {
		k, err := registry.OpenKey(root, name, registry.QUERY_VALUE|view)
		if err != nil {
			continue
		}
		if v, _, err := k.GetIntegerValue("SystemComponent"); err == nil && v == 1 {
			k.Close()
			continue
		}
		display := stringValue(k, "DisplayName")
		if display == "" {
			k.Close()
			continue
		}
		items = append(items, &agentv1.SoftwareItem{
			Name:        display,
			Version:     stringValue(k, "DisplayVersion"),
			Publisher:   stringValue(k, "Publisher"),
			InstallDate: stringValue(k, "InstallDate"),
		})
		k.Close()
	}
	return items, nil
}

func stringValue(k registry.Key, name string) string {
	v, _, err := k.GetStringValue(name)
	if err != nil {
		return ""
	}
	return strings.TrimSpace(v)
}
