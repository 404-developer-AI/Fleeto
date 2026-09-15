// Package inventory collects hardware, operating system and software inventory of the endpoint.
package inventory

import (
	"cmp"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"log/slog"
	"net"
	"os"
	"runtime"
	"slices"
	"strings"
	"time"

	"github.com/shirou/gopsutil/v4/cpu"
	"github.com/shirou/gopsutil/v4/host"
	"github.com/shirou/gopsutil/v4/mem"
	psnet "github.com/shirou/gopsutil/v4/net"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/checks"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// Hostname returns the host name, or "unknown".
func Hostname() string {
	name, err := os.Hostname()
	if err != nil || name == "" {
		return "unknown"
	}
	return name
}

// Collect gathers the full inventory. Parts that fail are logged and left empty: a partial inventory is better than
// none, and the next run tries again.
func Collect(ctx context.Context, logger *slog.Logger) *agentv1.Inventory {
	inv := &agentv1.Inventory{Hostname: Hostname(), Os: OSInfo(ctx)}

	platform, err := collectPlatform(ctx)
	if err != nil {
		logger.Warn("inventory: system information is incomplete", "error", err)
	}
	inv.Manufacturer = platform.Manufacturer
	inv.Model = platform.Model
	inv.SerialNumber = platform.SerialNumber
	inv.Domain = platform.Domain
	inv.LoggedOnUser = platform.LoggedOnUser

	if infos, err := cpu.InfoWithContext(ctx); err == nil && len(infos) > 0 {
		inv.CpuModel = strings.TrimSpace(infos[0].ModelName)
	} else if err != nil {
		logger.Warn("inventory: CPU model could not be read", "error", err)
	}
	if n, err := cpu.CountsWithContext(ctx, false); err == nil {
		inv.CpuCores = uint32(n)
	}
	if n, err := cpu.CountsWithContext(ctx, true); err == nil {
		inv.CpuLogicalProcessors = uint32(n)
	}
	if vm, err := mem.VirtualMemoryWithContext(ctx); err == nil {
		inv.MemoryTotalBytes = vm.Total
	}
	if boot, err := host.BootTimeWithContext(ctx); err == nil {
		inv.BootTime = timestamppb.New(time.Unix(int64(boot), 0))
	}
	if drives, err := checks.FixedDrives(); err == nil {
		for _, d := range drives {
			inv.Disks = append(inv.Disks, &agentv1.Disk{Mount: d.Name, Filesystem: d.Filesystem, TotalBytes: d.Total, FreeBytes: d.Free})
		}
	} else {
		logger.Warn("inventory: disks could not be listed", "error", err)
	}
	if nics, err := psnet.InterfacesWithContext(ctx); err == nil {
		inv.NetworkInterfaces = networkInterfaces(nics)
	} else {
		logger.Warn("inventory: network interfaces could not be listed", "error", err)
	}
	software, err := installedSoftware()
	if err != nil {
		logger.Warn("inventory: installed software could not be read", "error", err)
	}
	inv.Software = software
	if svc, err := services(); err == nil {
		inv.Services = svc
	} else {
		logger.Warn("inventory: services could not be listed", "error", err)
	}

	Normalize(inv)
	return inv
}

func networkInterfaces(nics psnet.InterfaceStatList) []*agentv1.NetworkInterface {
	var out []*agentv1.NetworkInterface
	for _, nic := range nics {
		if slices.Contains(nic.Flags, "loopback") || !slices.Contains(nic.Flags, "up") {
			continue
		}
		item := &agentv1.NetworkInterface{Name: nic.Name, MacAddress: strings.ToUpper(nic.HardwareAddr)}
		for _, a := range nic.Addrs {
			addr := a.Addr
			if ip, _, err := net.ParseCIDR(addr); err == nil {
				addr = ip.String()
			}
			item.IpAddresses = append(item.IpAddresses, addr)
		}
		if item.MacAddress == "" && len(item.IpAddresses) == 0 {
			continue
		}
		out = append(out, item)
	}
	return out
}

// Normalize sorts and deduplicates lists so the serialization, and therefore the hash, is deterministic.
func Normalize(inv *agentv1.Inventory) {
	slices.SortFunc(inv.Software, func(a, b *agentv1.SoftwareItem) int {
		return cmp.Or(
			cmp.Compare(strings.ToLower(a.GetName()), strings.ToLower(b.GetName())),
			cmp.Compare(a.GetName(), b.GetName()),
			cmp.Compare(a.GetVersion(), b.GetVersion()),
			cmp.Compare(a.GetPublisher(), b.GetPublisher()),
			cmp.Compare(a.GetInstallDate(), b.GetInstallDate()),
		)
	})
	inv.Software = slices.CompactFunc(inv.Software, func(a, b *agentv1.SoftwareItem) bool {
		return a.GetName() == b.GetName() && a.GetVersion() == b.GetVersion() && a.GetPublisher() == b.GetPublisher()
	})
	slices.SortFunc(inv.Services, func(a, b *agentv1.ServiceItem) int { return cmp.Compare(a.GetName(), b.GetName()) })
	inv.Services = slices.CompactFunc(inv.Services, func(a, b *agentv1.ServiceItem) bool { return a.GetName() == b.GetName() })
	slices.SortFunc(inv.Disks, func(a, b *agentv1.Disk) int { return cmp.Compare(a.GetMount(), b.GetMount()) })
	for _, nic := range inv.NetworkInterfaces {
		slices.Sort(nic.IpAddresses)
	}
	slices.SortFunc(inv.NetworkInterfaces, func(a, b *agentv1.NetworkInterface) int {
		return cmp.Or(cmp.Compare(a.GetName(), b.GetName()), cmp.Compare(a.GetMacAddress(), b.GetMacAddress()))
	})
}

// Hash returns the hex SHA-256 over the deterministic serialization of the normalized inventory.
func Hash(inv *agentv1.Inventory) (string, error) {
	clone := proto.Clone(inv).(*agentv1.Inventory)
	Normalize(clone)
	data, err := proto.MarshalOptions{Deterministic: true}.Marshal(clone)
	if err != nil {
		return "", fmt.Errorf("serialize inventory: %w", err)
	}
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:]), nil
}

// platformInfo is the part of the inventory that needs OS-specific APIs.
type platformInfo struct {
	Manufacturer string
	Model        string
	SerialNumber string
	Domain       string
	LoggedOnUser string
}

func goArch() string {
	return runtime.GOARCH
}
