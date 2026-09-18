//go:build windows

package screen

import (
	"testing"
)

// The capture tests need an interactive desktop (a developer's machine); on a runner without monitors they are skipped.

func primaryMonitor(t testing.TB) Monitor {
	t.Helper()
	procSetProcessDpiAwarenessContext.Call(dpiPerMonitorV2)
	for _, m := range monitors() {
		if m.Primary {
			return m
		}
	}
	t.Skip("no monitor on this machine")
	return Monitor{}
}

func TestCaptureUsesDesktopDuplicationForAWholeMonitor(t *testing.T) {
	area := primaryMonitor(t)
	var c capturer
	defer c.reset()
	// The first image of a new duplication comes from GDI (its own first frame is black); the next ones from DXGI.
	first, err := c.grab(area)
	if err != nil {
		t.Skipf("the desktop cannot be captured here: %v", err)
	}
	if first.Width != area.Width || first.Height != area.Height {
		t.Fatalf("image %dx%d, monitor %dx%d", first.Width, first.Height, area.Width, area.Height)
	}
	if c.dup == nil {
		t.Skipf("desktop duplication is not available here (captured with %s)", c.method)
	}
	gdiLit := litPixels(first)
	img, err := c.grab(area)
	if err != nil || c.method != "dxgi" {
		t.Fatalf("second grab: method %s, error %v", c.method, err)
	}
	// Never a black screen: the image has about as much content as the one GDI captured.
	if lit := litPixels(img); lit < gdiLit/2 {
		t.Fatalf("desktop duplication gave %d lit pixels, GDI %d", lit, gdiLit)
	}
}

// litPixels counts the pixels that are not black.
func litPixels(img *Image) int {
	lit := 0
	for i := 0; i+2 < len(img.Pix); i += 4 {
		if img.Pix[i]|img.Pix[i+1]|img.Pix[i+2] != 0 {
			lit++
		}
	}
	return lit
}

func TestCaptureUsesGDIForAPartOfTheDesktop(t *testing.T) {
	area := primaryMonitor(t)
	area.Width /= 2
	var c capturer
	defer c.reset()
	if _, err := c.grab(area); err != nil {
		t.Skipf("the desktop cannot be captured here: %v", err)
	}
	if c.method != "gdi" {
		t.Fatalf("half a monitor was captured with %s, want gdi", c.method)
	}
}

func BenchmarkCapturePrimaryMonitor(b *testing.B) {
	area := primaryMonitor(b)
	var c capturer
	defer c.reset()
	if _, err := c.grab(area); err != nil {
		b.Skip(err)
	}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		if _, err := c.grab(area); err != nil {
			b.Fatal(err)
		}
	}
	b.Logf("%dx%d with %s", area.Width, area.Height, c.method)
}
