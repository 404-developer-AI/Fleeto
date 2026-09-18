//go:build windows

package screen

import (
	"errors"
	"time"
	"unsafe"
)

// capturer copies a rectangle of the desktop the thread is attached to into a DIB section, and draws the mouse cursor into it. A whole
// monitor comes from DXGI desktop duplication where the GPU offers it (0.3.0 step 5); everything else, and whatever duplication refuses,
// from GDI (BitBlt), which works everywhere: the sign-in screen, UAC, RDP sessions and virtual machines without a GPU (decided
// 2026-09-17). It must be used from one locked OS thread, and reset when that thread changes desktop.
type capturer struct {
	memDC  uintptr
	bitmap uintptr
	old    uintptr
	bits   unsafe.Pointer
	width  int
	height int
	img    Image

	dup *duplication
	// dupRetry is when desktop duplication may be tried again after it failed.
	dupRetry time.Time
	// Method says how the last image was captured: "dxgi" or "gdi".
	method string
}

func (c *capturer) reset() {
	if c.dup != nil {
		c.dup.close()
	}
	if c.memDC != 0 {
		if c.old != 0 {
			procSelectObject.Call(c.memDC, c.old)
		}
		procDeleteDC.Call(c.memDC)
	}
	if c.bitmap != 0 {
		procDeleteObject.Call(c.bitmap)
	}
	*c = capturer{img: c.img, dupRetry: c.dupRetry}
}

func (c *capturer) prepare(screenDC uintptr, width, height int) error {
	if c.memDC != 0 && c.width == width && c.height == height {
		return nil
	}
	c.reset()
	memDC, _, err := procCreateCompatibleDC.Call(screenDC)
	if memDC == 0 {
		return fmtCallError("CreateCompatibleDC", err)
	}
	header := bitmapInfoHeader{Width: int32(width), Height: -int32(height), Planes: 1, BitCount: 32, Compression: biRGB}
	header.Size = uint32(unsafe.Sizeof(header))
	var bits unsafe.Pointer
	bitmap, _, err := procCreateDIBSection.Call(screenDC, uintptr(unsafe.Pointer(&header)), dibRGBColors, uintptr(unsafe.Pointer(&bits)), 0, 0)
	if bitmap == 0 || bits == nil {
		procDeleteDC.Call(memDC)
		return fmtCallError("CreateDIBSection", err)
	}
	old, _, _ := procSelectObject.Call(memDC, bitmap)
	c.memDC, c.bitmap, c.old, c.bits, c.width, c.height = memDC, bitmap, old, bits, width, height
	return nil
}

// grab captures the rectangle (x, y, width, height) in virtual desktop pixels.
func (c *capturer) grab(area Monitor) (*Image, error) {
	screenDC, _, err := procGetDC.Call(0)
	if screenDC == 0 {
		return nil, fmtCallError("GetDC", err)
	}
	defer procReleaseDC.Call(0, screenDC)
	if err := c.prepare(screenDC, area.Width, area.Height); err != nil {
		return nil, err
	}
	size := area.Width * area.Height * 4
	if c.duplicate(area, unsafe.Slice((*byte)(c.bits), size)) {
		c.method = "dxgi"
		return c.finish(area), nil
	}
	c.method = "gdi"
	// The screen DC's origin is the primary monitor's top-left corner, as virtual desktop coordinates are.
	if ok, _, err := procBitBlt.Call(c.memDC, 0, 0, uintptr(area.Width), uintptr(area.Height), screenDC, uintptr(int32(area.X)), uintptr(int32(area.Y)),
		srcCopy); ok == 0 {
		return nil, fmtCallError("BitBlt", err)
	}
	if c.dup != nil {
		// A new duplication waits for its first image: this one, before the cursor is drawn into it.
		c.dup.seed(unsafe.Slice((*byte)(c.bits), size))
	}
	return c.finish(area), nil
}

// duplicate fills the DIB section from desktop duplication, and reports whether it did.
func (c *capturer) duplicate(area Monitor, bits []byte) bool {
	if c.dup != nil && c.dup.area != area {
		c.dup.close()
		c.dup = nil
	}
	if c.dup == nil {
		if time.Now().Before(c.dupRetry) {
			return false
		}
		dup, err := openDuplication(area)
		if err != nil {
			c.dupRetry = time.Now().Add(duplicationRetry)
			return false
		}
		c.dup = dup
	}
	err := c.dup.grab(bits)
	if errors.Is(err, errNoImageYet) {
		return false
	}
	if err != nil {
		// Most often a desktop switch or a display change: GDI now, a new duplication soon.
		c.dup.close()
		c.dup = nil
		c.dupRetry = time.Now().Add(time.Second)
		return false
	}
	return true
}

// finish draws the cursor and copies the DIB section out.
func (c *capturer) finish(area Monitor) *Image {
	c.drawCursor(area)
	size := area.Width * area.Height * 4
	if cap(c.img.Pix) < size {
		c.img.Pix = make([]byte, size)
	}
	c.img.Pix = c.img.Pix[:size]
	copy(c.img.Pix, unsafe.Slice((*byte)(c.bits), size))
	c.img.Width, c.img.Height = area.Width, area.Height
	return &c.img
}

// drawCursor paints the endpoint's mouse cursor into the captured image, so the technician sees where it is and what it looks like.
func (c *capturer) drawCursor(area Monitor) {
	info := cursorInfo{}
	info.Size = uint32(unsafe.Sizeof(info))
	if ok, _, _ := procGetCursorInfo.Call(uintptr(unsafe.Pointer(&info))); ok == 0 || info.Flags&cursorShowing == 0 || info.Cursor == 0 {
		return
	}
	icon := iconInfo{}
	if ok, _, _ := procGetIconInfo.Call(uintptr(info.Cursor), uintptr(unsafe.Pointer(&icon))); ok == 0 {
		return
	}
	if icon.Mask != 0 {
		procDeleteObject.Call(uintptr(icon.Mask))
	}
	if icon.Color != 0 {
		procDeleteObject.Call(uintptr(icon.Color))
	}
	x := int(info.Position.X) - int(icon.XHotspot) - area.X
	y := int(info.Position.Y) - int(icon.YHotspot) - area.Y
	procDrawIconEx.Call(c.memDC, uintptr(int32(x)), uintptr(int32(y)), uintptr(info.Cursor), 0, 0, 0, 0, diNormal)
}
