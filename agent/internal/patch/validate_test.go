package patch

import (
	"fmt"
	"strings"
	"testing"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func validLinuxJob() *agentv1.PatchInstallJob {
	return &agentv1.PatchInstallJob{
		Scope:                agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED,
		Updates:              []*agentv1.PatchInstallUpdate{{Id: "libssl3:amd64", Title: "libssl3", Version: "3.0.2-0ubuntu1.12"}},
		Restart:              agentv1.PatchRestart_PATCH_RESTART_IF_REQUIRED,
		RestartNoticeSeconds: 600,
	}
}

func validWindowsJob() *agentv1.PatchInstallJob {
	return &agentv1.PatchInstallJob{
		Scope:   agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING,
		Filter:  &agentv1.PatchInstallFilter{Classifications: []string{"Security Updates"}, ExcludedNames: []string{"Preview"}},
		Restart: agentv1.PatchRestart_PATCH_RESTART_NEVER,
	}
}

func TestValidateAcceptsWellFormedJobs(t *testing.T) {
	for engine, job := range map[string]*agentv1.PatchInstallJob{EngineApt: validLinuxJob(), EngineWindowsUpdate: validWindowsJob()} {
		if err := ValidateInstallJob(job, engine); err != nil {
			t.Errorf("%s: %v", engine, err)
		}
	}
	rpm := validLinuxJob()
	rpm.Updates[0] = &agentv1.PatchInstallUpdate{Id: "NetworkManager_x:x86_64", Version: "1:1.46.0-26.el9_5"}
	if err := ValidateInstallJob(rpm, EngineDnf); err != nil {
		t.Errorf("an rpm name with uppercase and _ is valid for dnf: %v", err)
	}
	win := validWindowsJob()
	win.Scope = agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED
	win.Updates = []*agentv1.PatchInstallUpdate{{Id: "0b6a4e2c-1d3f-4a5b-8c7d-9e0f1a2b3c4d:201", Title: "KB5031354"}}
	if err := ValidateInstallJob(win, EngineWindowsUpdate); err != nil {
		t.Errorf("a named Windows update is valid: %v", err)
	}
	restricted := validWindowsJob()
	restricted.Filter.RestrictToIds = true
	restricted.Filter.AllowedIds = []string{"0b6a4e2c-1d3f-4a5b-8c7d-9e0f1a2b3c4d:201"}
	if err := ValidateInstallJob(restricted, EngineWindowsUpdate); err != nil {
		t.Errorf("a filter restricted to allowed ids is valid: %v", err)
	}
	restricted.Filter.AllowedIds = nil
	if err := ValidateInstallJob(restricted, EngineWindowsUpdate); err != nil {
		t.Errorf("a filter restricted to no ids is valid (it installs nothing): %v", err)
	}
}

func TestValidateRefusesEveryBadBody(t *testing.T) {
	type change func(j *agentv1.PatchInstallJob)
	many := make([]*agentv1.PatchInstallUpdate, MaxSpecifiedUpdates+1)
	for i := range many {
		many[i] = &agentv1.PatchInstallUpdate{Id: fmt.Sprintf("pkg%d:amd64", i), Version: "1.0"}
	}
	excluded := make([]string, MaxExcludedNames+1)
	for i := range excluded {
		excluded[i] = fmt.Sprintf("pkg%d", i)
	}
	cases := []struct {
		name   string
		engine string
		base   func() *agentv1.PatchInstallJob
		change change
	}{
		{"no scope", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.Scope = 0 }},
		{"no updates named", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.Updates = nil }},
		{"too many updates", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.Updates = many }},
		{"updates with everything missing", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) {
			j.Scope = agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING
		}},
		{"an option as a name", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.Updates[0].Id = "-oAPT::x=y:amd64" }},
		{"a shell character in a name", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.Updates[0].Id = "libssl3;reboot:amd64" }},
		{"uppercase in a dpkg name", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.Updates[0].Id = "LibSSL:amd64" }},
		{"no architecture", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.Updates[0].Id = "libssl3" }},
		{"no version on Linux", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.Updates[0].Version = "" }},
		{"a space in a version", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.Updates[0].Version = "1.0 -y" }},
		{"a slash in a version", EngineDnf, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.Updates[0].Version = "1.0/../x" }},
		{"a version too long", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.Updates[0].Version = strings.Repeat("1", 101) }},
		{"a control character in a title", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.Updates[0].Title = "x\nfleeto-patch: installed y" }},
		{"a Linux id on Windows", EngineWindowsUpdate, validWindowsJob, func(j *agentv1.PatchInstallJob) {
			j.Scope = agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED
			j.Updates = []*agentv1.PatchInstallUpdate{{Id: "libssl3:amd64"}}
		}},
		{"an uppercase GUID", EngineWindowsUpdate, validWindowsJob, func(j *agentv1.PatchInstallJob) {
			j.Scope = agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED
			j.Updates = []*agentv1.PatchInstallUpdate{{Id: "0B6A4E2C-1D3F-4A5B-8C7D-9E0F1A2B3C4D:201"}}
		}},
		{"a version on Windows", EngineWindowsUpdate, validWindowsJob, func(j *agentv1.PatchInstallJob) {
			j.Scope = agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED
			j.Updates = []*agentv1.PatchInstallUpdate{{Id: "0b6a4e2c-1d3f-4a5b-8c7d-9e0f1a2b3c4d:201", Version: "1"}}
		}},
		{"too many excluded names", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) {
			j.Filter = &agentv1.PatchInstallFilter{ExcludedNames: excluded}
		}},
		{"an excluded option", EngineDnf, validLinuxJob, func(j *agentv1.PatchInstallJob) {
			j.Updates[0].Id = "openssl:x86_64"
			j.Filter = &agentv1.PatchInstallFilter{ExcludedNames: []string{"--nogpgcheck"}}
		}},
		{"an excluded glob", EngineDnf, validLinuxJob, func(j *agentv1.PatchInstallJob) {
			j.Updates[0].Id = "openssl:x86_64"
			j.Filter = &agentv1.PatchInstallFilter{ExcludedNames: []string{"kernel*"}}
		}},
		{"too many allowed ids", EngineWindowsUpdate, validWindowsJob, func(j *agentv1.PatchInstallJob) {
			j.Filter.RestrictToIds = true
			j.Filter.AllowedIds = make([]string, MaxAllowedIDs+1)
			for i := range j.Filter.AllowedIds {
				j.Filter.AllowedIds[i] = fmt.Sprintf("0b6a4e2c-1d3f-4a5b-8c7d-9e0f1a2b3c4d:%d", i)
			}
		}},
		{"an allowed id of another form", EngineWindowsUpdate, validWindowsJob, func(j *agentv1.PatchInstallJob) {
			j.Filter.RestrictToIds = true
			j.Filter.AllowedIds = []string{"libssl3:amd64"}
		}},
		{"an empty excluded title", EngineWindowsUpdate, validWindowsJob, func(j *agentv1.PatchInstallJob) { j.Filter.ExcludedNames = []string{" "} }},
		{"no restart choice", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.Restart = 0 }},
		{"a notice too short", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.RestartNoticeSeconds = 59 }},
		{"no notice", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.RestartNoticeSeconds = 0 }},
		{"a notice too long", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.RestartNoticeSeconds = 86401 }},
		{"a message too long", EngineApt, validLinuxJob, func(j *agentv1.PatchInstallJob) { j.RestartMessage = strings.Repeat("x", 2001) }},
		{"no engine", "", validLinuxJob, func(*agentv1.PatchInstallJob) {}},
	}
	for _, c := range cases {
		job := proto.Clone(c.base()).(*agentv1.PatchInstallJob)
		c.change(job)
		if err := ValidateInstallJob(job, c.engine); err == nil {
			t.Errorf("%s must be refused", c.name)
		}
	}
	if err := ValidateInstallJob(nil, EngineApt); err == nil {
		t.Error("a job without a body must be refused")
	}
}
