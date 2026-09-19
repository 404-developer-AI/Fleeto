package screen

import "time"

// Timing of the helper, the same on Windows and Linux.
const (
	// captureInterval is the shortest time between two captures (at most 25 frames a second).
	captureInterval = 40 * time.Millisecond
	// idlePoll is how often the helper looks for a desktop switch while nothing is shown.
	idlePoll = 250 * time.Millisecond
	// ackTimeout is how long the helper waits for the browser to draw a frame before it sends the next one anyway, as a full frame.
	ackTimeout = 10 * time.Second
	// monitorRefresh is how often the monitor list is read again.
	monitorRefresh = 2 * time.Second
	// largeFrameBytes and slowAck switch to the lower JPEG quality.
	largeFrameBytes = 400 * 1024
	slowAck         = 300 * time.Millisecond
)
