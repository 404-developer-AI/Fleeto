//go:build linux

package remote

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"os/exec"
	"strings"
)

func runSystemctl(ctx context.Context, args ...string) (string, error) {
	path, err := exec.LookPath("systemctl")
	if err != nil {
		return "", errors.New("systemctl is not available on this endpoint")
	}
	cmd := exec.CommandContext(ctx, path, args...) // #nosec G204 -- systemctl with a validated unit name and fixed verbs.
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	if err := cmd.Run(); err != nil {
		detail := strings.TrimSpace(stderr.String())
		if detail == "" {
			detail = strings.TrimSpace(stdout.String())
		}
		if detail != "" {
			return stdout.String(), fmt.Errorf("%s", detail)
		}
		return stdout.String(), err
	}
	return stdout.String(), nil
}

func unit(name string) string {
	if strings.Contains(name, ".") {
		return name
	}
	return name + ".service"
}

func startService(ctx context.Context, name string) error {
	_, err := runSystemctl(ctx, "start", unit(name))
	return err
}

func stopService(ctx context.Context, name string) error {
	_, err := runSystemctl(ctx, "stop", unit(name))
	return err
}

func restartService(ctx context.Context, name string) error {
	_, err := runSystemctl(ctx, "restart", unit(name))
	return err
}

func setStartType(ctx context.Context, name, startType string) error {
	u := unit(name)
	switch startType {
	case "automatic", "automatic_delayed":
		_, err := runSystemctl(ctx, "unmask", u)
		if err != nil {
			return err
		}
		_, err = runSystemctl(ctx, "enable", u)
		return err
	case "manual":
		_, err := runSystemctl(ctx, "unmask", u)
		if err != nil {
			return err
		}
		_, err = runSystemctl(ctx, "disable", u)
		return err
	case "disabled":
		_, err := runSystemctl(ctx, "mask", u)
		return err
	default:
		return errors.New("choose automatic, manual or disabled")
	}
}

func serviceState(ctx context.Context, name string) (string, error) {
	out, err := runSystemctl(ctx, "show", unit(name), "--property=ActiveState")
	if err != nil {
		return "", err
	}
	_, value, _ := strings.Cut(strings.TrimSpace(out), "=")
	switch value {
	case "active", "reloading":
		return "running", nil
	case "activating":
		return "starting", nil
	case "deactivating":
		return "stopping", nil
	default:
		return "stopped", nil
	}
}
