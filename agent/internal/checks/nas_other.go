//go:build !linux

package checks

import "context"

const nasLinuxOnly = "this check runs on Linux endpoints only"

func raidState(string) []Measurement { return []Measurement{{Error: nasLinuxOnly}} }

func diskHealth(context.Context, string) []Measurement { return []Measurement{{Error: nasLinuxOnly}} }

func diskTemperature(context.Context, string) []Measurement {
	return []Measurement{{Error: nasLinuxOnly}}
}
