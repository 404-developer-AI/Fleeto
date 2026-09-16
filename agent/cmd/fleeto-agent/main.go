// Command fleeto-agent is the Fleeto agent: one static binary that installs itself, enrolls, and runs as a service.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/signal"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/agent"
	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/logging"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/service"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/version"
)

// Exit codes.
const (
	exitOK      = 0
	exitError   = 1
	exitUsage   = 2
	exitRevoked = 3
)

const usage = `Fleeto Agent

Usage:
  fleeto-agent install --server <host:port> --token <fet_...> --ca-fingerprint <sha256 hex>
  fleeto-agent uninstall
  fleeto-agent status [--state-dir <dir>]
  fleeto-agent version [--short]
  fleeto-agent run                      (started by the service manager)
  fleeto-agent run --foreground --state-dir <dir> [--key-store file|cng|tpm]
                     [--server <host:port> --token <fet_...> --ca-fingerprint <sha256 hex>]

Copy the install command from the site page in Fleeto. install and uninstall need administrator rights (root on Linux).
run --foreground is for development: it needs no administrator rights and keeps its key in the state directory.
`

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}

func run(args []string, stdout, stderr io.Writer) int {
	if len(args) == 0 {
		if service.IsService() {
			return runService(stderr)
		}
		fmt.Fprint(stderr, usage)
		return exitUsage
	}
	switch args[0] {
	case "install":
		return cmdInstall(args[1:], stdout, stderr)
	case "uninstall":
		return cmdUninstall(args[1:], stdout, stderr)
	case "status":
		return cmdStatus(args[1:], stdout, stderr)
	case "version", "--version", "-v":
		if len(args) > 1 && args[1] == "--short" {
			// Read by the watchdog before it installs a binary: only the version, nothing else.
			fmt.Fprintln(stdout, version.Version)
			return exitOK
		}
		return cmdVersion(stdout)
	case "run":
		return cmdRun(args[1:], stderr)
	case "help", "--help", "-h", "/?":
		fmt.Fprint(stdout, usage)
		return exitOK
	default:
		fmt.Fprintf(stderr, "Unknown command %q.\n\n%s", args[0], usage)
		return exitUsage
	}
}

func newFlagSet(name string, stderr io.Writer) *flag.FlagSet {
	fs := flag.NewFlagSet(name, flag.ContinueOnError)
	fs.SetOutput(stderr)
	fs.Usage = func() { fmt.Fprint(stderr, usage) }
	return fs
}

func fail(stderr io.Writer, err error) int {
	fmt.Fprintf(stderr, "Error: %v\n", err)
	return exitError
}

func cmdInstall(args []string, stdout, stderr io.Writer) int {
	fs := newFlagSet("install", stderr)
	server := fs.String("server", "", "gateway host:port")
	token := fs.String("token", "", "enrollment token")
	fingerprint := fs.String("ca-fingerprint", "", "SHA-256 fingerprint of the instance CA certificate")
	if err := fs.Parse(args); err != nil {
		return exitUsage
	}
	if *server == "" || *token == "" || *fingerprint == "" || fs.NArg() > 0 {
		fmt.Fprintln(stderr, "Error: install needs --server, --token and --ca-fingerprint; copy the install command from the site page.")
		return exitUsage
	}
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	err := service.Install(ctx, service.InstallOptions{Server: *server, Token: *token, CAFingerprint: *fingerprint}, stdout)
	if err != nil {
		return fail(stderr, err)
	}
	return exitOK
}

func cmdUninstall(args []string, stdout, stderr io.Writer) int {
	fs := newFlagSet("uninstall", stderr)
	if err := fs.Parse(args); err != nil || fs.NArg() > 0 {
		return exitUsage
	}
	if err := service.Uninstall(stdout); err != nil {
		return fail(stderr, err)
	}
	fmt.Fprintln(stdout, "The Fleeto Agent is uninstalled.")
	return exitOK
}

func cmdVersion(stdout io.Writer) int {
	fmt.Fprintf(stdout, "fleeto-agent %s (%s/%s)\n", version.Version, runtime.GOOS, runtime.GOARCH)
	keys, err := version.ParseReleasePublicKeys(version.ReleasePublicKeys)
	switch {
	case err != nil:
		fmt.Fprintf(stdout, "Release public keys: invalid (%v)\n", err)
	case len(keys) == 0:
		fmt.Fprintln(stdout, "Release public keys: none (development build)")
	default:
		ids := make([]string, 0, len(keys))
		for _, k := range keys {
			ids = append(ids, version.KeyID(k))
		}
		fmt.Fprintf(stdout, "Release public keys: %s\n", strings.Join(ids, ", "))
	}
	return exitOK
}

func cmdStatus(args []string, stdout, stderr io.Writer) int {
	fs := newFlagSet("status", stderr)
	stateDir := fs.String("state-dir", "", "state directory (default: the service state directory)")
	if err := fs.Parse(args); err != nil || fs.NArg() > 0 {
		return exitUsage
	}
	dir := *stateDir
	if dir == "" {
		dir = platform.DefaultStateDir()
	}
	fmt.Fprintf(stdout, "Fleeto Agent %s\n", version.Version)
	if *stateDir == "" {
		if svcState, err := service.State(); err == nil {
			row(stdout, "Service", svcState)
		} else if !errors.Is(err, service.ErrUnsupported) {
			row(stdout, "Service", "unknown ("+err.Error()+")")
		}
	}
	row(stdout, "State directory", dir)

	st, err := state.NewStore(dir, platform.AccessCurrentUser).Load()
	switch {
	case errors.Is(err, state.ErrNotEnrolled):
		row(stdout, "Enrollment", "not enrolled")
		return exitOK
	case errors.Is(err, os.ErrPermission):
		fmt.Fprintf(stderr, "Error: the agent state is readable by administrators only: %s\n", platform.ElevationHint)
		return exitError
	case err != nil:
		return fail(stderr, err)
	}
	if st.Revoked {
		row(stdout, "Enrollment", "revoked: enroll again")
		if st.RevokedReason != "" {
			row(stdout, "Revocation reason", st.RevokedReason)
		}
	} else {
		row(stdout, "Enrollment", "enrolled")
	}
	row(stdout, "Endpoint id", st.EndpointID)
	row(stdout, "Instance id", st.InstanceID)
	row(stdout, "Server", st.Server)
	row(stdout, "Enrolled", st.EnrolledAt.UTC().Format(time.RFC3339))
	if cert, err := st.Certificate(); err == nil {
		expiry := cert.NotAfter.UTC().Format(time.RFC3339)
		lifetime := cert.NotAfter.Sub(cert.NotBefore)
		renewal := cert.NotBefore.Add(lifetime * 2 / 3).UTC().Format(time.RFC3339)
		if time.Now().After(cert.NotAfter) {
			expiry += " (expired: enroll again)"
		}
		row(stdout, "Certificate expires", expiry)
		row(stdout, "Renewal from", renewal)
	} else {
		row(stdout, "Certificate", "unreadable ("+err.Error()+")")
	}
	row(stdout, "Key store", describeKey(st.Key))
	row(stdout, "Signing key id", st.SigningKeyID)
	if st.AppliedConfigVersion == 0 {
		row(stdout, "Configuration", "none received yet")
	} else {
		row(stdout, "Configuration", fmt.Sprintf("version %d", st.AppliedConfigVersion))
	}
	return exitOK
}

func describeKey(ref state.KeyRef) string {
	switch ref.Kind {
	case keystore.KindFile:
		return "file " + ref.File
	case keystore.KindCNG:
		scope := "user"
		if ref.Machine {
			scope = "machine"
		}
		return fmt.Sprintf("CNG %s key in %s", scope, ref.Provider)
	case keystore.KindTPM:
		return "TPM 2.0, key blob " + ref.File
	default:
		return ref.Kind
	}
}

func row(w io.Writer, label, value string) {
	fmt.Fprintf(w, "  %-20s %s\n", label+":", value)
}

func cmdRun(args []string, stderr io.Writer) int {
	fs := newFlagSet("run", stderr)
	foreground := fs.Bool("foreground", false, "run in the console (development)")
	stateDir := fs.String("state-dir", "", "state directory")
	keyStore := fs.String("key-store", keystore.KindFile, "key store: file, cng (Windows) or tpm (Linux)")
	server := fs.String("server", "", "gateway host:port, to enroll when not enrolled")
	token := fs.String("token", "", "enrollment token, to enroll when not enrolled")
	fingerprint := fs.String("ca-fingerprint", "", "instance CA fingerprint, to enroll when not enrolled")
	verbose := fs.Bool("verbose", false, "debug logging")
	if err := fs.Parse(args); err != nil || fs.NArg() > 0 {
		return exitUsage
	}
	if !*foreground {
		if service.IsService() {
			return runService(stderr)
		}
		fmt.Fprintln(stderr, "Error: 'run' is started by the service manager; use 'run --foreground --state-dir <dir>' in a console.")
		return exitUsage
	}
	if *stateDir == "" {
		fmt.Fprintln(stderr, "Error: run --foreground needs --state-dir, for example --state-dir .\\agent-dev")
		return exitUsage
	}
	if *keyStore != keystore.KindFile && *keyStore != keystore.KindCNG && *keyStore != keystore.KindTPM {
		fmt.Fprintln(stderr, "Error: --key-store must be file, cng (Windows) or tpm (Linux)")
		return exitUsage
	}
	dir, err := filepath.Abs(*stateDir)
	if err != nil {
		return fail(stderr, err)
	}
	access := platform.AccessCurrentUser
	if err := platform.EnsureProtectedDir(dir, access); err != nil {
		return fail(stderr, err)
	}
	level := slog.LevelInfo
	if *verbose {
		level = slog.LevelDebug
	}
	logger, closer, err := logging.New(filepath.Join(dir, "logs"), true, level)
	if err != nil {
		return fail(stderr, err)
	}
	defer closer.Close()

	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()

	store := state.NewStore(dir, access)
	st, err := store.Load()
	needsEnrollment := errors.Is(err, state.ErrNotEnrolled) || (err == nil && st.Revoked)
	if err != nil && !errors.Is(err, state.ErrNotEnrolled) {
		return fail(stderr, err)
	}
	enrollArgs := *server != "" || *token != "" || *fingerprint != ""
	switch {
	case needsEnrollment && !enrollArgs:
		if st != nil && st.Revoked {
			fmt.Fprintln(stderr, "Error: the agent is revoked: enroll again with --server, --token and --ca-fingerprint.")
		} else {
			fmt.Fprintln(stderr, "Error: the agent is not enrolled: pass --server, --token and --ca-fingerprint.")
		}
		return exitError
	case needsEnrollment:
		// A cng key in foreground mode is a per-user key, so no administrator rights are needed; a tpm key needs access to the TPM device.
		_, err := agent.Enroll(ctx, agent.EnrollParams{
			StateDir: dir, Access: access, Key: state.KeyRef{Kind: *keyStore},
			Server: *server, Token: *token, CAFingerprint: *fingerprint, Logger: logger,
		})
		if err != nil {
			return fail(stderr, fmt.Errorf("enrollment failed: %w", err))
		}
	case enrollArgs:
		logger.Info("already enrolled; the enrollment arguments are ignored", "endpointId", st.EndpointID)
	}

	a, err := agent.New(agent.Options{Store: store, Logger: logger})
	if err != nil {
		return fail(stderr, err)
	}
	defer a.Close()
	logger.Info("agent starting in the foreground", "version", version.Version, "stateDir", dir)
	if err := a.Run(ctx); err != nil {
		if errors.Is(err, agent.ErrRevoked) {
			fmt.Fprintln(stderr, "The agent was revoked by the Fleeto instance: enroll again.")
			return exitRevoked
		}
		return fail(stderr, err)
	}
	logger.Info("agent stopped")
	return exitOK
}

func runService(stderr io.Writer) int {
	if err := service.Run(); err != nil {
		fmt.Fprintf(stderr, "Error: %v\n", err)
		return exitError
	}
	return exitOK
}
