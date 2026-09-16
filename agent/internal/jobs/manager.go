package jobs

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
)

const (
	jobFile        = "job.pb"
	startedFile    = "started"
	accountFile    = "account"
	startedAcked   = "started.acked"
	completionFile = "completion.pb"
	completionAck  = "completion.acked"
	seenFile       = "seen.json"
	// keepFor is how long unacknowledged output stays on disk.
	keepFor = 7 * 24 * time.Hour
	// maxJobDirs bounds the spool, so a flood of refused jobs cannot fill the disk.
	maxJobDirs = 1000
)

// Options configures a Manager.
type Options struct {
	// Dir is the spool directory, inside the protected state directory.
	Dir    string
	Access platform.Access
	Logger *slog.Logger
	Now    func() time.Time
	// Trust returns the pinned signing key and addressing.
	Trust func() signedconfig.Trust
	// Managed reports whether the applied configuration is managed.
	Managed func() bool
	// MaxConcurrent jobs run at the same time; more wait. Default 4.
	MaxConcurrent int
	// FlushInterval sends partial output chunks while a job runs. Default 1 second.
	FlushInterval time.Duration
}

// Message is a job message waiting for its acknowledgement. Key identifies it within one session.
type Message struct {
	Key string
	Msg *agentv1.AgentMessage
}

// Manager accepts, runs and reports jobs. Its state lives on disk: a crash or restart never runs a job twice and never loses
// output that the gateway did not acknowledge.
type Manager struct {
	opts   Options
	ctx    context.Context
	cancel context.CancelFunc
	sem    chan struct{}
	notify chan struct{}
	wg     sync.WaitGroup

	mu   sync.Mutex
	seen map[string]int64
}

// NewManager opens the spool, answers jobs that were interrupted by a stop and starts jobs that never started.
func NewManager(opts Options) (*Manager, error) {
	if opts.Now == nil {
		opts.Now = time.Now
	}
	if opts.MaxConcurrent <= 0 {
		opts.MaxConcurrent = 4
	}
	if opts.FlushInterval <= 0 {
		opts.FlushInterval = time.Second
	}
	if err := platform.EnsureProtectedDir(opts.Dir, opts.Access); err != nil {
		return nil, err
	}
	ctx, cancel := context.WithCancel(context.Background())
	m := &Manager{
		opts:   opts,
		ctx:    ctx,
		cancel: cancel,
		sem:    make(chan struct{}, opts.MaxConcurrent),
		notify: make(chan struct{}, 1),
		seen:   map[string]int64{},
	}
	if data, err := os.ReadFile(filepath.Join(opts.Dir, seenFile)); err == nil {
		_ = json.Unmarshal(data, &m.seen)
	}
	m.recover()
	return m, nil
}

// Notify signals that there are new messages to send.
func (m *Manager) Notify() <-chan struct{} { return m.notify }

// Close stops running jobs (they report as interrupted) and waits for them.
func (m *Manager) Close() {
	m.cancel()
	m.wg.Wait()
}

func (m *Manager) signal() {
	select {
	case m.notify <- struct{}{}:
	default:
	}
}

func (m *Manager) dir(id string) string { return filepath.Join(m.opts.Dir, strings.ToLower(id)) }

// Accept verifies a delivered job and starts it, or answers a refusal. A job id that was seen before is ignored: the gateway
// delivers a queued job again after every reconnect.
func (m *Manager) Accept(sj *agentv1.SignedJob) {
	id := PeekID(sj)
	if id == "" {
		m.opts.Logger.Warn("ignored a job that cannot be read")
		return
	}
	m.mu.Lock()
	_, known := m.seen[id]
	m.mu.Unlock()
	if known {
		return
	}
	if _, err := os.Stat(m.dir(id)); err == nil {
		return
	}

	payload, err := Verify(sj, m.opts.Trust(), m.opts.Managed(), m.opts.Now())
	if err != nil {
		m.opts.Logger.Warn("refused a job", "jobId", id, "reason", err)
		m.remember(id, m.opts.Now())
		m.complete(id, &agentv1.JobCompletion{JobId: id, Result: agentv1.JobResult_JOB_RESULT_REFUSED, Error: err.Error()})
		return
	}

	data, _ := proto.Marshal(sj)
	if err := platform.EnsureProtectedDir(m.dir(id), m.opts.Access); err != nil {
		m.opts.Logger.Error("could not store a job; it is not run", "jobId", id, "error", err)
		return
	}
	if err := platform.WriteFileAtomic(filepath.Join(m.dir(id), jobFile), data, m.opts.Access); err != nil {
		m.opts.Logger.Error("could not store a job; it is not run", "jobId", id, "error", err)
		_ = os.RemoveAll(m.dir(id))
		return
	}
	m.remember(id, payload.GetValidUntil().AsTime().Add(ClockTolerance))
	m.opts.Logger.Info("accepted a job", "jobId", id, "script", payload.GetScript().GetName(), "version", payload.GetScript().GetVersion(),
		"initiatedBy", payload.GetInitiatedBy())
	m.start(id, payload)
}

// remember records a job id until a moment; ids are kept a week past it, longer than any job can be delivered again.
func (m *Manager) remember(id string, until time.Time) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.seen[id] = until.Unix()
	now := m.opts.Now().Unix()
	for key, expires := range m.seen {
		if expires < now-int64(keepFor/time.Second) {
			delete(m.seen, key)
		}
	}
	data, _ := json.Marshal(m.seen)
	if err := platform.WriteFileAtomic(filepath.Join(m.opts.Dir, seenFile), data, m.opts.Access); err != nil {
		m.opts.Logger.Error("could not store the list of seen jobs", "error", err)
	}
}

func (m *Manager) start(id string, payload *agentv1.JobPayload) {
	m.wg.Add(1)
	safego.Go(m.opts.Logger, "job "+id, func() {
		defer m.wg.Done()
		select {
		case m.sem <- struct{}{}:
		case <-m.ctx.Done():
			return
		}
		defer func() { <-m.sem }()
		if m.opts.Now().After(payload.GetValidUntil().AsTime().Add(ClockTolerance)) {
			m.complete(id, &agentv1.JobCompletion{JobId: id, Result: agentv1.JobResult_JOB_RESULT_REFUSED,
				Error: "the job expired while it waited for other jobs to finish"})
			return
		}
		m.run(id, payload)
	})
}

// complete stores the completion and signals the session.
func (m *Manager) complete(id string, completion *agentv1.JobCompletion) {
	completion.JobId = id
	if completion.GetFinishedAt() == nil {
		completion.FinishedAt = timestamppb.New(m.opts.Now())
	}
	if completion.Stdout == nil {
		completion.Stdout = &agentv1.JobStreamSummary{}
	}
	if completion.Stderr == nil {
		completion.Stderr = &agentv1.JobStreamSummary{}
	}
	if err := m.ensureDir(id); err != nil {
		m.opts.Logger.Error("could not store a job result", "jobId", id, "error", err)
		return
	}
	data, _ := proto.Marshal(completion)
	if err := platform.WriteFileAtomic(filepath.Join(m.dir(id), completionFile), data, m.opts.Access); err != nil {
		m.opts.Logger.Error("could not store a job result", "jobId", id, "error", err)
		return
	}
	m.signal()
}

func (m *Manager) ensureDir(id string) error {
	if _, err := os.Stat(m.dir(id)); err == nil {
		return nil
	}
	entries, _ := os.ReadDir(m.opts.Dir)
	if len(entries) > maxJobDirs {
		return errors.New("too many job results are waiting to be sent")
	}
	return platform.EnsureProtectedDir(m.dir(id), m.opts.Access)
}

// recover handles the spool after a start: an interrupted job reports so, a job that never started runs now, old leftovers go.
func (m *Manager) recover() {
	entries, err := os.ReadDir(m.opts.Dir)
	if err != nil {
		return
	}
	now := m.opts.Now()
	for _, entry := range entries {
		if !entry.IsDir() || !validID(entry.Name()) {
			continue
		}
		id := entry.Name()
		dir := m.dir(id)
		if info, err := entry.Info(); err == nil && now.Sub(info.ModTime()) > keepFor {
			_ = os.RemoveAll(dir)
			continue
		}
		if exists(filepath.Join(dir, completionFile)) {
			m.cleanupIfDone(id)
			continue
		}
		if exists(filepath.Join(dir, startedFile)) {
			m.opts.Logger.Warn("a job was running when the agent stopped; it is reported as interrupted and not run again", "jobId", id)
			m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_INTERRUPTED,
				Error: "The agent stopped while the job ran. The job is not run again."})
			continue
		}
		data, err := os.ReadFile(filepath.Join(dir, jobFile))
		if err != nil {
			_ = os.RemoveAll(dir)
			continue
		}
		var sj agentv1.SignedJob
		if proto.Unmarshal(data, &sj) != nil {
			_ = os.RemoveAll(dir)
			continue
		}
		payload, err := Verify(&sj, m.opts.Trust(), m.opts.Managed(), now)
		if err != nil {
			m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_REFUSED, Error: err.Error()})
			continue
		}
		m.start(id, payload)
	}
}

// Pending returns every unacknowledged message, oldest job first: started, output in order, completion. skip leaves out
// messages already sent in this session without reading their data.
func (m *Manager) Pending(skip func(key string) bool) []Message {
	entries, err := os.ReadDir(m.opts.Dir)
	if err != nil {
		return nil
	}
	type jobDir struct {
		id  string
		mod time.Time
	}
	var dirs []jobDir
	for _, entry := range entries {
		if entry.IsDir() && validID(entry.Name()) {
			info, _ := entry.Info()
			var mod time.Time
			if info != nil {
				mod = info.ModTime()
			}
			dirs = append(dirs, jobDir{entry.Name(), mod})
		}
	}
	sort.Slice(dirs, func(i, j int) bool { return dirs[i].mod.Before(dirs[j].mod) })

	var messages []Message
	for _, d := range dirs {
		dir := m.dir(d.id)
		if exists(filepath.Join(dir, startedFile)) && !exists(filepath.Join(dir, startedAcked)) {
			key := "started:" + d.id
			if !skip(key) {
				startedAt := m.opts.Now()
				if data, err := os.ReadFile(filepath.Join(dir, startedFile)); err == nil {
					if t, err := time.Parse(time.RFC3339Nano, strings.TrimSpace(string(data))); err == nil {
						startedAt = t
					}
				}
				var account string
				if data, err := os.ReadFile(filepath.Join(dir, accountFile)); err == nil {
					account = strings.TrimSpace(string(data))
				}
				messages = append(messages, Message{key, &agentv1.AgentMessage{Body: &agentv1.AgentMessage_JobStarted{
					JobStarted: &agentv1.JobStarted{JobId: d.id, StartedAt: timestamppb.New(startedAt), RunAsAccount: account}}}})
			}
		}
		chunks, _ := filepath.Glob(filepath.Join(dir, "out-*.chunk"))
		sort.Strings(chunks)
		for _, path := range chunks {
			stream, sequence, ok := parseChunkName(filepath.Base(path))
			if !ok {
				continue
			}
			key := fmt.Sprintf("output:%s:%d:%d", d.id, stream, sequence)
			if skip(key) {
				continue
			}
			data, err := os.ReadFile(path)
			if err != nil || len(data) == 0 {
				continue
			}
			messages = append(messages, Message{key, &agentv1.AgentMessage{Body: &agentv1.AgentMessage_JobOutput{
				JobOutput: &agentv1.JobOutput{JobId: d.id, Stream: stream, Sequence: sequence, Data: data}}}})
		}
		if exists(filepath.Join(dir, completionFile)) && !exists(filepath.Join(dir, completionAck)) {
			key := "completion:" + d.id
			if skip(key) {
				continue
			}
			data, err := os.ReadFile(filepath.Join(dir, completionFile))
			var completion agentv1.JobCompletion
			if err != nil || proto.Unmarshal(data, &completion) != nil {
				continue
			}
			messages = append(messages, Message{key, &agentv1.AgentMessage{Body: &agentv1.AgentMessage_JobCompletion{JobCompletion: &completion}}})
		}
	}
	return messages
}

// Ack removes an acknowledged message from disk.
func (m *Manager) Ack(ack *agentv1.JobAck) {
	id := strings.ToLower(ack.GetJobId())
	if !validID(id) {
		return
	}
	dir := m.dir(id)
	if !exists(dir) {
		return
	}
	switch ack.GetKind() {
	case agentv1.JobAckKind_JOB_ACK_KIND_STARTED:
		_ = platform.WriteFileAtomic(filepath.Join(dir, startedAcked), nil, m.opts.Access)
	case agentv1.JobAckKind_JOB_ACK_KIND_OUTPUT:
		_ = os.Remove(filepath.Join(dir, chunkName(ack.GetStream(), ack.GetSequence())))
	case agentv1.JobAckKind_JOB_ACK_KIND_COMPLETION:
		_ = platform.WriteFileAtomic(filepath.Join(dir, completionAck), nil, m.opts.Access)
	}
	m.cleanupIfDone(id)
}

// cleanupIfDone removes a job directory once its completion and all of its output are acknowledged.
func (m *Manager) cleanupIfDone(id string) {
	dir := m.dir(id)
	if !exists(filepath.Join(dir, completionAck)) {
		return
	}
	if chunks, _ := filepath.Glob(filepath.Join(dir, "out-*.chunk")); len(chunks) > 0 {
		return
	}
	if exists(filepath.Join(dir, startedFile)) && !exists(filepath.Join(dir, startedAcked)) {
		return
	}
	if err := os.RemoveAll(dir); err != nil {
		m.opts.Logger.Warn("could not remove a finished job", "jobId", id, "error", err)
	}
}

func chunkName(stream agentv1.JobStream, sequence uint64) string {
	return fmt.Sprintf("out-%d-%08d.chunk", int32(stream), sequence)
}

func parseChunkName(name string) (agentv1.JobStream, uint64, bool) {
	var stream int32
	var sequence uint64
	if _, err := fmt.Sscanf(name, "out-%d-%d.chunk", &stream, &sequence); err != nil {
		return 0, 0, false
	}
	s := agentv1.JobStream(stream)
	if s != agentv1.JobStream_JOB_STREAM_STDOUT && s != agentv1.JobStream_JOB_STREAM_STDERR {
		return 0, 0, false
	}
	return s, sequence, true
}

func exists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}
