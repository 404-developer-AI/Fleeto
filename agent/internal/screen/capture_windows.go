//go:build windows

package screen

import (
	"unsafe"
)

// capturer copies a rectangle of the desktop the thread is attached to into a DIB section with GDI (BitBlt), and draws the mouse
// cursor into it. GDI works everywhere: the sign-in screen, UAC, RDP sessions and virtual machines without a GPU (decided 2026-09-17;
// DXGI desktop duplication follows with H.264). It must be used from one locked OS thread, and reset when that thread changes desktop.
type capturer struct {
	memDC  uintptr
	bitmap uintptr
	old    uintptr
	bits   unsafe.Pointer
	width  int
	height int
	img    Image
}

func (c *capturer) reset() {
	if c.memDC != 0 {
		if c.old != 0 {
			procSelectObject.Call(c.memDC, c.old)
		}
		procDeleteDC.Call(c.memDC)
	}
	if c.bitmap != 0 {
		procDeleteObject.Call(c.bitmap)
	}
	*c = capturer{img: c.img}
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
	// The screen DC's origin is the primary monitor's top-left corner, as virtual desktop coordinates are.
	if ok, _, err := procBitBlt.Call(c.memDC, 0, 0, uintptr(area.Width), uintptr(area.Height), screenDC, uintptr(int32(area.X)), uintptr(int32(area.Y)),
		srcCopy); ok == 0 {
		return nil, fmtCallError("BitBlt", err)
	}
	c.drawCursor(area)
	size := area.Width * area.Height * 4
	if cap(c.img.Pix) < size {
		c.img.Pix = make([]byte, size)
	}
	c.img.Pix = c.img.Pix[:size]
	copy(c.img.Pix, unsafe.Slice((*byte)(c.bits), size))
	c.img.Width, c.img.Height = area.Width, area.Height
	return &c.img, nil
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
