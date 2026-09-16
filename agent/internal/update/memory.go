package update

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sync"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
)

// MemoryFileName remembers failed versions and retry times across restarts, in the installer's state directory.
const MemoryFileName = "update-state.json"

// Memory is what an installer remembers per component: a version that was rolled back is never tried again (a newer release is), and
// after a failure the next attempt waits.
type Memory struct {
	path   string
	access platform.Access
	mu     sync.Mutex
	data   memoryData
}

type memoryData struct {
	RolledBack  map[string]string    `json:"rolledBack,omitempty"`
	NextAttempt map[string]time.Time `json:"nextAttempt,omitempty"`
}

// OpenMemory loads the memory of dir; a missing or damaged file starts empty.
func OpenMemory(dir string, access platform.Access) *Memory {
	m := &Memory{path: filepath.Join(dir, MemoryFileName), access: access}
	if data, err := os.ReadFile(m.path); err == nil && len(data) < 64*1024 {
		_ = json.Unmarshal(data, &m.data)
	}
	return m
}

// RolledBack reports whether version of component was rolled back before.
func (m *Memory) RolledBack(component, version string) bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.data.RolledBack[component] == version
}

// MayAttempt reports whether the retry wait of component has passed.
func (m *Memory) MayAttempt(component string, now time.Time) bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	return !now.Before(m.data.NextAttempt[component])
}

// NextAttempt is the earliest next attempt for component; the zero time when there is no wait.
func (m *Memory) NextAttempt(component string) time.Time {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.data.NextAttempt[component]
}

// RecordRollback remembers a rolled back version.
func (m *Memory) RecordRollback(component, version string) {
	m.update(func(d *memoryData) {
		if d.RolledBack == nil {
			d.RolledBack = map[string]string{}
		}
		d.RolledBack[component] = version
	})
}

// RetryAt sets the earliest next attempt for component.
func (m *Memory) RetryAt(component string, at time.Time) {
	m.update(func(d *memoryData) {
		if d.NextAttempt == nil {
			d.NextAttempt = map[string]time.Time{}
		}
		d.NextAttempt[component] = at.UTC()
	})
}

// Clear forgets the retry wait of component after a successful installation.
func (m *Memory) Clear(component string) {
	m.update(func(d *memoryData) { delete(d.NextAttempt, component) })
}

func (m *Memory) update(fn func(*memoryData)) {
	m.mu.Lock()
	defer m.mu.Unlock()
	fn(&m.data)
	if data, err := json.Marshal(m.data); err == nil {
		_ = platform.WriteFileAtomic(m.path, data, m.access)
	}
}
