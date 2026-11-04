package patch

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// The Windows installation through the Windows Update Agent. Everything but the COM calls lives here, so it is tested with a fake
// session on every platform; install_windows.go only makes the calls.

const (
	// OperationResultCode of IDownloadResult, IInstallationResult and IUpdateInstallationResult.
	wuaResultFailed  = 4
	wuaResultAborted = 5

	// installerBusyWait is how long the agent waits for another installation of the Windows Update Agent to end.
	installerBusyWait = 5 * time.Minute
	installerBusyPoll = 15 * time.Second

	problemRestartFirst  = "Restart the endpoint first: Windows has to finish earlier updates with a restart before it installs more."
	problemInstallerBusy = "The Windows Update Agent was busy with another installation for 5 minutes, so nothing was installed. " +
		"It is tried again at the next round."
)

// wuaUpdateResult is one IUpdateDownloadResult or IUpdateInstallationResult.
type wuaUpdateResult struct {
	ResultCode     int64
	HResult        uint32
	RebootRequired bool
}

// wuaOperation is one IDownloadResult or IInstallationResult; Updates are in the order of the collection.
type wuaOperation struct {
	ResultCode     int64
	HResult        uint32
	RebootRequired bool
	Updates        []wuaUpdateResult
}

// wuaInstallSession is what an installation needs of the Windows Update Agent, on top of the search of a scan.
type wuaInstallSession interface {
	wuaSession
	// Collect puts the updates of the last Search with these ids ("UpdateID:RevisionNumber") in the collection of the installation
	// and accepts their license terms. It returns the ids it took, in the order of the collection.
	Collect(ids []string) ([]string, error)
	// Download runs IUpdateDownloader.Download for the collection.
	Download() (wuaOperation, error)
	// InstallerBusy is IUpdateInstaller.IsBusy.
	InstallerBusy() (bool, error)
	// RebootRequiredBeforeInstallation is IUpdateInstaller.RebootRequiredBeforeInstallation.
	RebootRequiredBeforeInstallation() (bool, error)
	// Install runs IUpdateInstaller.Install for the collection.
	Install() (wuaOperation, error)
}

// resultName is an OperationResultCode in words.
func resultName(code int64) string {
	switch code {
	case 0:
		return "Not started"
	case 1:
		return "In progress"
	case wuaResultSucceeded:
		return "Succeeded"
	case wuaResultSucceededWithErrors:
		return "Succeeded with errors"
	case wuaResultFailed:
		return "Failed"
	case wuaResultAborted:
		return "Aborted"
	}
	return fmt.Sprintf("Result %d", code)
}

// succeeded: a result of 2 or 3 is an installed update.
func succeeded(code int64) bool {
	return code == wuaResultSucceeded || code == wuaResultSucceededWithErrors
}

// installWUA runs one installation against a session. wait sleeps between the looks at a busy installer.
func installWUA(ctx context.Context, sess wuaInstallSession, reg registryReader, job *agentv1.PatchInstallJob, out *installOutput,
	wait func(time.Duration)) InstallResult {
	out.say("Searching for missing updates with the Windows Update Agent.")
	report, failed := scanWUAChecked(sess, reg)
	if report.GetProblem() != "" {
		out.say("The search reported: %s", report.GetProblem())
	}
	if failed {
		return failAll(out, job, "The search before the installation failed, so nothing was installed: "+report.GetProblem())
	}
	if ctx.Err() != nil {
		return failAll(out, job, "The agent stopped before the installation started; nothing was installed.")
	}
	sel := Select(job, report)
	targets, invalid := validWindowsTargets(sel.Targets)
	if len(targets) == 0 {
		if len(invalid) == 0 {
			out.say("Nothing to install: no selected update is missing.")
		}
		out.report(invalid, nil, sel.Outcomes)
		return finish(invalid, nil, "", report.GetRestartRequired())
	}

	ids := make([]string, len(targets))
	for i, t := range targets {
		ids[i] = t.ID
	}
	all := append(append([]Target(nil), targets...), invalid...)
	taken, err := sess.Collect(ids)
	if err != nil {
		reason := "The updates could not be prepared for installation: " + err.Error()
		out.say("%s", reason)
		out.report(all, nil, sel.Outcomes)
		return finish(all, nil, reason, report.GetRestartRequired())
	}
	byID := map[string]Target{}
	for _, t := range targets {
		byID[t.ID] = t
	}
	if len(taken) == 0 {
		reason := "None of the selected updates was found again in the search."
		out.say("%s", reason)
		out.report(all, nil, sel.Outcomes)
		return finish(all, nil, reason, report.GetRestartRequired())
	}

	out.say("Downloading %d updates.", len(taken))
	download, err := sess.Download()
	if err != nil {
		reason := "The updates could not be downloaded: " + err.Error()
		out.say("%s", reason)
		out.report(all, nil, sel.Outcomes)
		return finish(all, nil, reason, report.GetRestartRequired())
	}
	for i, r := range download.Updates {
		if i < len(taken) && !succeeded(r.ResultCode) {
			out.say("Download of %s: %s (0x%08X)", describe(byID[taken[i]]), resultName(r.ResultCode), r.HResult)
		}
	}
	out.say("Download: %s.", resultName(download.ResultCode))

	busySince := 0 * time.Second
	for {
		busy, err := sess.InstallerBusy()
		if err != nil || !busy {
			break
		}
		if busySince >= installerBusyWait {
			out.say("%s", problemInstallerBusy)
			out.report(all, nil, sel.Outcomes)
			return finish(all, nil, problemInstallerBusy, report.GetRestartRequired())
		}
		if busySince == 0 {
			out.say("The Windows Update Agent is busy with another installation; waiting up to 5 minutes.")
		}
		wait(installerBusyPoll)
		busySince += installerBusyPoll
	}
	if first, err := sess.RebootRequiredBeforeInstallation(); err == nil && first {
		out.say("%s", problemRestartFirst)
		out.report(all, nil, sel.Outcomes)
		return finish(all, nil, problemRestartFirst, true)
	}
	if ctx.Err() != nil {
		out.say("The agent stopped before the installation started; the updates were downloaded, nothing was installed.")
		out.report(all, nil, sel.Outcomes)
		return finish(all, nil, "The agent stopped before the installation started.", report.GetRestartRequired())
	}

	out.say("Installing %d updates.", len(taken))
	install, err := sess.Install()
	installed := map[string]bool{}
	var toolError string
	if err != nil {
		toolError = "The installation failed: " + err.Error()
		out.say("%s", toolError)
	} else {
		for i, id := range taken {
			t := byID[id]
			if i >= len(install.Updates) {
				out.say("%s — no result", describe(t))
				continue
			}
			r := install.Updates[i]
			if succeeded(r.ResultCode) {
				installed[id] = true
				out.say("Installed %s — %s", describe(t), resultName(r.ResultCode))
			} else {
				out.say("Not installed %s — %s (0x%08X)", describe(t), resultName(r.ResultCode), r.HResult)
			}
		}
		out.say("Installation: %s.", resultName(install.ResultCode))
	}
	restart := install.RebootRequired || restartPending(sess, reg)
	out.report(all, installed, sel.Outcomes)
	return finish(all, installed, toolError, restart)
}

// validWindowsTargets keeps the targets with an update identity of the scan's form.
func validWindowsTargets(targets []Target) (valid, invalid []Target) {
	for _, t := range targets {
		if validUpdateID(EngineWindowsUpdate, t.ID) {
			valid = append(valid, t)
		} else {
			invalid = append(invalid, t)
		}
	}
	return valid, invalid
}

// describe names an update in the output: its KB number and title.
func describe(t Target) string {
	title := cut(t.Title, maxTitleLen)
	if t.KB != "" && !strings.Contains(title, t.KB) {
		return t.KB + " " + title
	}
	if title == "" {
		return t.ID
	}
	return title
}
