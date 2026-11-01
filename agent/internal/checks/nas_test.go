package checks

import (
	"strings"
	"testing"
)

// /proc/mdstat of a Synology DS with two disks and DSM 7.1.1 (2026-11-01).
const mdstatDSM = `Personalities : [raid1]
md2 : active raid1 sda5[0] sdb5[1]
      1948683456 blocks super 1.2 [2/2] [UU]

md1 : active raid1 sda2[0] sdb2[1]
      2097088 blocks [2/2] [UU]

md0 : active raid1 sda1[0] sdb1[1]
      2490176 blocks [2/2] [UU]

unused devices: <none>
`

// A 4-bay DSM with two disks: md0 and md1 have a slot per bay; a rebuilding and a degraded data array, and a scrubbed one.
const mdstatTrouble = `Personalities : [raid1] [raid6] [raid5] [raid4]
md3 : active raid5 sdc5[2] sdb5[1] sda5[0](F)
      5850870528 blocks super 1.2 level 5, 64k chunk, algorithm 2 [3/2] [_UU]

md2 : active raid1 sdb3[1] sda3[0]
      1948683456 blocks super 1.2 [2/2] [UU]
      [=>...................]  recovery =  8.5% (166224128/1948683456) finish=167.4min speed=177352K/sec

md4 : active raid1 sdd3[1] sde3[0]
      1948683456 blocks super 1.2 [2/2] [UU]
      [==>..................]  check = 12.0% (233842048/1948683456) finish=150.0min speed=190000K/sec

md5 : inactive sdf1[0](S)
      976630464 blocks super 1.2

md1 : active raid1 sda2[0] sdb2[1]
      2097088 blocks [4/2] [UU__]

md0 : active raid1 sda1[0] sdb1[1]
      2490176 blocks [4/2] [UU__]

unused devices: <none>
`

func byTarget(ms []Measurement) map[string]Measurement {
	out := map[string]Measurement{}
	for _, m := range ms {
		out[m.Target] = m
	}
	return out
}

func TestRaidStateOfANasWithTwoHealthyDisks(t *testing.T) {
	got := byTarget(RaidMeasurements(mdstatDSM, "*", true))
	if len(got) != 1 || got["md2"].Value != RaidHealthy || got["md2"].Detail != "raid1, 2 of 2 disks" || got["md2"].Error != "" {
		t.Fatalf("DSM: %+v", got)
	}
	// Elsewhere every array counts.
	if all := RaidMeasurements(mdstatDSM, "*", false); len(all) != 3 {
		t.Fatalf("not DSM: %+v", all)
	}
}

func TestRaidStateJudgesEveryArray(t *testing.T) {
	got := byTarget(RaidMeasurements(mdstatTrouble, "*", true))
	if _, ok := got["md0"]; ok {
		t.Fatal("the system partition of DSM counts with *, so an empty bay looks degraded")
	}
	cases := map[string]struct {
		value  float64
		detail string
	}{
		"md3": {RaidDegraded, "raid5, 2 of 3 disks, 1 failed"},
		"md2": {RaidRebuilding, "raid1, 2 of 2 disks, rebuilding 8.5%"},
		"md4": {RaidHealthy, "raid1, 2 of 2 disks, data scrubbing 12.0%"},
		"md5": {RaidDegraded, "md, inactive"},
	}
	for name, want := range cases {
		if m := got[name]; m.Value != want.value || m.Detail != want.detail {
			t.Errorf("%s = %+v, want %v %q", name, m, want.value, want.detail)
		}
	}
	// A named array counts, also md0 of DSM.
	if named := RaidMeasurements(mdstatTrouble, "md0", true); len(named) != 1 || named[0].Value != RaidDegraded {
		t.Fatalf("md0 named: %+v", named)
	}
	if missing := RaidMeasurements(mdstatTrouble, "md9", true); missing[0].Error == "" {
		t.Fatalf("missing array: %+v", missing)
	}
	if bad := RaidMeasurements(mdstatTrouble, "../etc", true); !strings.Contains(bad[0].Error, "must be *") {
		t.Fatalf("bad name: %+v", bad)
	}
	if none := RaidMeasurements("Personalities : \nunused devices: <none>\n", "*", false); none[0].Error == "" {
		t.Fatalf("no arrays: %+v", none)
	}
}

// smartctl 6.5 on Synology DSM 7.1.1 for a Seagate IronWolf (2026-11-01), with the information section.
const smartIronWolf = `smartctl 6.5 (build date Oct  7 2021) [x86_64-linux-4.4.180+] (local build)
Copyright (C) 2002-16, Bruce Allen, Christian Franke, www.smartmontools.org

=== START OF INFORMATION SECTION ===
Model Family:     Seagate IronWolf
Device Model:     ST2000VN004-2E4164
Serial Number:    Z52XXXXX

=== START OF READ SMART DATA SECTION ===
SMART overall-health self-assessment test result: PASSED

SMART Attributes Data Structure revision number: 10
Vendor Specific SMART Attributes with Thresholds:
ID# ATTRIBUTE_NAME                                                   FLAG     VALUE WORST THRESH TYPE      UPDATED  WHEN_FAILED RAW_VALUE
  1 Raw_Read_Error_Rate                                              0x000f   119   099   006    Pre-fail  Always       -       210211576
  3 Spin_Up_Time                                                     0x0003   096   095   000    Pre-fail  Always       -       0
  4 Start_Stop_Count                                                 0x0032   098   098   020    Old_age   Always       -       2166
  5 Reallocated_Sector_Ct                                            0x0033   100   100   010    Pre-fail  Always       -       0
  7 Seek_Error_Rate                                                  0x000f   086   060   030    Pre-fail  Always       -       428833208
  9 Power_On_Hours                                                   0x0032   049   049   000    Old_age   Always       -       44936
 10 Spin_Retry_Count                                                 0x0013   100   100   097    Pre-fail  Always       -       0
 12 Power_Cycle_Count                                                0x0032   098   098   020    Old_age   Always       -       2123
184 End-to-End_Error                                                 0x0032   100   100   099    Old_age   Always       -       0
187 Reported_Uncorrect                                               0x0032   100   100   000    Old_age   Always       -       0
188 Command_Timeout                                                  0x0032   100   100   000    Old_age   Always       -       0 0 0
189 High_Fly_Writes                                                  0x003a   001   001   000    Old_age   Always       -       267
190 Airflow_Temperature_Cel                                          0x0022   081   057   045    Old_age   Always       -       19 (Min/Max 19/21)
191 G-Sense_Error_Rate                                               0x0032   100   100   000    Old_age   Always       -       0
192 Power-Off_Retract_Count                                          0x0032   100   100   000    Old_age   Always       -       38
193 Load_Cycle_Count                                                 0x0032   099   099   000    Old_age   Always       -       2278
194 Temperature_Celsius                                              0x0022   019   043   000    Old_age   Always       -       19 (0 6 0 0 0)
197 Current_Pending_Sector                                           0x0012   100   100   000    Old_age   Always       -       0
198 Offline_Uncorrectable                                            0x0010   100   100   000    Old_age   Offline      -       0
199 UDMA_CRC_Error_Count                                             0x003e   200   200   000    Old_age   Always       -       0
`

func TestDiskHealthAndTemperatureOfAHealthyDisk(t *testing.T) {
	r := ParseSmart(smartIronWolf)
	health := HealthMeasurement("sda", r)
	if health.Value != DiskHealthy || health.Target != "sda" ||
		health.Detail != "ST2000VN004-2E4164: PASSED, 0 reallocated, 0 pending, 0 uncorrectable sectors, 44936 power-on hours" {
		t.Fatalf("health = %+v", health)
	}
	if temperature := TemperatureMeasurement("sda", r); temperature.Value != 19 || temperature.Error != "" {
		t.Fatalf("temperature = %+v", temperature)
	}
	// The serial number never reaches the detail.
	if strings.Contains(health.Detail, "Z52") {
		t.Fatal("the serial number is in the detail")
	}
}

func TestDiskHealthFindsWornAndFailingDisks(t *testing.T) {
	pending := strings.Replace(smartIronWolf, "Old_age   Always       -       0\n198", "Old_age   Always       -       8\n198", 1)
	if m := HealthMeasurement("sda", ParseSmart(pending)); m.Value != DiskWarning || !strings.Contains(m.Detail, "8 pending") {
		t.Fatalf("pending sectors: %+v", m)
	}
	failed := strings.Replace(smartIronWolf, "test result: PASSED", "test result: FAILED!", 1)
	if m := HealthMeasurement("sda", ParseSmart(failed)); m.Value != DiskFailing || !strings.Contains(m.Detail, "FAILED") {
		t.Fatalf("failed: %+v", m)
	}
	failingNow := strings.Replace(smartIronWolf, "Pre-fail  Always       -       0\n  7", "Pre-fail  Always   FAILING_NOW 2048\n  7", 1)
	if m := HealthMeasurement("sda", ParseSmart(failingNow)); m.Value != DiskFailing {
		t.Fatalf("failing now: %+v", m)
	}
}

const smartNvme = `smartctl 7.4 2023-08-01 r5530 [x86_64-linux-6.8.0] (local build)
=== START OF INFORMATION SECTION ===
Model Number:                       Samsung SSD 980 PRO 1TB
=== START OF SMART DATA SECTION ===
SMART overall-health self-assessment test result: PASSED

SMART/Health Information (NVMe Log 0x02)
Critical Warning:                   0x00
Temperature:                        41 Celsius
Percentage Used:                    3%
Power On Hours:                     2,713
Media and Data Integrity Errors:    0
`

func TestDiskHealthOfAnNvmeDisk(t *testing.T) {
	r := ParseSmart(smartNvme)
	if m := HealthMeasurement("nvme0n1", r); m.Value != DiskHealthy || m.Detail != "Samsung SSD 980 PRO 1TB: PASSED, 0 media errors, 2713 power-on hours" {
		t.Fatalf("nvme = %+v", m)
	}
	if m := TemperatureMeasurement("nvme0n1", r); m.Value != 41 {
		t.Fatalf("nvme temperature = %+v", m)
	}
	warning := strings.Replace(smartNvme, "0x00", "0x04", 1)
	if m := HealthMeasurement("nvme0n1", ParseSmart(warning)); m.Value != DiskFailing {
		t.Fatalf("critical warning: %+v", m)
	}
}

func TestADiskInStandbyIsNotReadAndAnUnreadableOneSaysWhy(t *testing.T) {
	standby := ParseSmart("smartctl 6.5\nDevice is in STANDBY mode, exit(2)\n")
	if !standby.Standby {
		t.Fatal("standby not recognised")
	}
	unreadable := HealthMeasurement("sdb", ParseSmart("smartctl 6.5\nSmartctl open device: /dev/sdb failed: No such device\n"))
	if !strings.Contains(unreadable.Error, "No such device") {
		t.Fatalf("unreadable = %+v", unreadable)
	}
	for _, bad := range []string{"../sda", "sda1", "sda;reboot", "md0", ""} {
		if DiskName.MatchString(bad) {
			t.Errorf("DiskName accepts %q", bad)
		}
	}
	for _, good := range []string{"sda", "sdab", "sata1", "nvme0n1"} {
		if !DiskName.MatchString(good) {
			t.Errorf("DiskName refuses %q", good)
		}
	}
}
