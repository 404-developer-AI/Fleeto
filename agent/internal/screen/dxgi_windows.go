//go:build windows

package screen

import (
	"errors"
	"fmt"
	"syscall"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// DXGI desktop duplication (0.3.0 step 5): the GPU hands over the image of one monitor as it composed it, in a few milliseconds where
// GDI (BitBlt) needs tens of them for a large screen. It serves one whole monitor; "All monitors", a rotated monitor, an RDP session and
// a desktop DXGI refuses keep GDI. A duplication ends when the desktop switches (sign-in screen, UAC) or the display mode changes; it is
// then made again, with GDI in between.

var (
	dxgi                      = windows.NewLazySystemDLL("dxgi.dll")
	d3d11                     = windows.NewLazySystemDLL("d3d11.dll")
	procCreateDXGIFactory1    = dxgi.NewProc("CreateDXGIFactory1")
	procD3D11CreateDevice     = d3d11.NewProc("D3D11CreateDevice")
	iidIDXGIFactory1          = mustGUID("{770aae78-f26f-4dba-a829-253c83d1b387}")
	iidIDXGIOutput1           = mustGUID("{00cddea8-939b-4b83-a340-a685226666cc}")
	iidID3D11Texture2D        = mustGUID("{6f15aaf2-d208-4e89-9ab4-489535d34f9c}")
	errDuplicationUnavailable = errors.New("desktop duplication is not available for this area")
	// errNoImageYet: a new duplication has no image of the desktop until something is drawn (its first frame is black); the caller
	// captures with GDI once and seeds it.
	errNoImageYet = errors.New("desktop duplication has no image yet")
)

const (
	d3dDriverTypeUnknown = 0
	d3d11SDKVersion      = 7
	d3d11UsageStaging    = 3
	d3d11CPUAccessRead   = 0x20000
	d3d11MapRead         = 1
	dxgiRotationIdentity = 1
	dxgiRotationNone     = 0

	hrDXGINotFound    = 0x887A0002
	hrDXGIWaitTimeout = 0x887A0027

	// Vtable slots.
	slotFactoryEnumAdapters1 = 12
	slotAdapterEnumOutputs   = 7
	slotOutputGetDesc        = 7
	slotOutputDuplicate      = 22
	slotDuplAcquireNextFrame = 8
	slotDuplReleaseFrame     = 14
	slotDeviceCreateTexture  = 5
	slotContextMap           = 14
	slotContextUnmap         = 15
	slotContextCopyResource  = 47
	slotTextureGetDesc       = 10

	// firstFrameWait is how long a new duplication may take for its first image, in milliseconds.
	firstFrameWait = 50
	// duplicationRetry is how long GDI is used after desktop duplication failed, before it is tried again.
	duplicationRetry = 5 * time.Second
)

type dxgiOutputDesc struct {
	DeviceName [32]uint16
	Left       int32
	Top        int32
	Right      int32
	Bottom     int32
	Attached   int32
	Rotation   uint32
	Monitor    uintptr
}

type dxgiFrameInfo struct {
	LastPresentTime     int64
	LastMouseUpdateTime int64
	AccumulatedFrames   uint32
	RectsCoalesced      int32
	ProtectedContent    int32
	PointerX            int32
	PointerY            int32
	PointerVisible      int32
	TotalMetadataSize   uint32
	PointerShapeSize    uint32
}

type texture2DDesc struct {
	Width, Height, MipLevels, ArraySize, Format uint32
	SampleCount, SampleQuality                  uint32
	Usage, BindFlags, CPUAccessFlags, MiscFlags uint32
}

type mappedSubresource struct {
	Data       uintptr
	RowPitch   uint32
	DepthPitch uint32
}

// duplication is desktop duplication of one monitor, with the last image it gave (without the mouse cursor).
type duplication struct {
	area    Monitor
	device  uintptr
	context uintptr
	dupl    uintptr
	staging uintptr
	clean   []byte
	// have says whether clean holds an image yet.
	have bool
}

// openDuplication duplicates the monitor that covers exactly this area; errDuplicationUnavailable when none does.
func openDuplication(area Monitor) (*duplication, error) {
	if err := dxgi.Load(); err != nil {
		return nil, errDuplicationUnavailable
	}
	if err := d3d11.Load(); err != nil {
		return nil, errDuplicationUnavailable
	}
	var factory uintptr
	if hr, _, _ := procCreateDXGIFactory1.Call(uintptr(unsafe.Pointer(&iidIDXGIFactory1)), uintptr(unsafe.Pointer(&factory))); failed(hr) {
		return nil, hrError("CreateDXGIFactory1", hr)
	}
	defer comRelease(factory)
	for a := uintptr(0); ; a++ {
		var adapter uintptr
		hr, _, _ := syscall.SyscallN(method(factory, slotFactoryEnumAdapters1), factory, a, uintptr(unsafe.Pointer(&adapter)))
		if uint32(hr) == hrDXGINotFound {
			return nil, errDuplicationUnavailable
		}
		if failed(hr) {
			return nil, hrError("EnumAdapters1", hr)
		}
		d, err := duplicateOnAdapter(adapter, area)
		comRelease(adapter)
		if d != nil || (err != nil && !errors.Is(err, errDuplicationUnavailable)) {
			return d, err
		}
	}
}

func duplicateOnAdapter(adapter uintptr, area Monitor) (*duplication, error) {
	for o := uintptr(0); ; o++ {
		var output uintptr
		hr, _, _ := syscall.SyscallN(method(adapter, slotAdapterEnumOutputs), adapter, o, uintptr(unsafe.Pointer(&output)))
		if uint32(hr) == hrDXGINotFound {
			return nil, errDuplicationUnavailable
		}
		if failed(hr) {
			return nil, hrError("EnumOutputs", hr)
		}
		var desc dxgiOutputDesc
		syscall.SyscallN(method(output, slotOutputGetDesc), output, uintptr(unsafe.Pointer(&desc)))
		matches := desc.Attached != 0 && int(desc.Left) == area.X && int(desc.Top) == area.Y &&
			int(desc.Right-desc.Left) == area.Width && int(desc.Bottom-desc.Top) == area.Height
		if !matches {
			comRelease(output)
			continue
		}
		if desc.Rotation != dxgiRotationIdentity && desc.Rotation != dxgiRotationNone {
			comRelease(output)
			return nil, errDuplicationUnavailable
		}
		d, err := duplicateOutput(adapter, output, area)
		comRelease(output)
		return d, err
	}
}

func duplicateOutput(adapter, output uintptr, area Monitor) (*duplication, error) {
	output1 := queryInterface(output, &iidIDXGIOutput1)
	if output1 == 0 {
		return nil, errDuplicationUnavailable
	}
	defer comRelease(output1)
	d := &duplication{area: area}
	var level uint32
	hr, _, _ := procD3D11CreateDevice.Call(adapter, d3dDriverTypeUnknown, 0, 0, 0, 0, d3d11SDKVersion, uintptr(unsafe.Pointer(&d.device)),
		uintptr(unsafe.Pointer(&level)), uintptr(unsafe.Pointer(&d.context)))
	if failed(hr) {
		return nil, hrError("D3D11CreateDevice", hr)
	}
	hr, _, _ = syscall.SyscallN(method(output1, slotOutputDuplicate), output1, d.device, uintptr(unsafe.Pointer(&d.dupl)))
	if failed(hr) {
		d.close()
		// An RDP session, a protected desktop or too many duplications: GDI serves this area.
		return nil, fmt.Errorf("%w (DuplicateOutput HRESULT %s)", errDuplicationUnavailable, hresult(hr))
	}
	return d, nil
}

// grab writes the monitor's image into dst (B, G, R, A, rows top to bottom, width x 4 bytes a row). With no new image since the last
// grab, the last one is used again.
func (d *duplication) grab(dst []byte) error {
	wait := uintptr(0)
	if !d.have {
		wait = firstFrameWait
	}
	var info dxgiFrameInfo
	var resource uintptr
	hr, _, _ := syscall.SyscallN(method(d.dupl, slotDuplAcquireNextFrame), d.dupl, wait, uintptr(unsafe.Pointer(&info)),
		uintptr(unsafe.Pointer(&resource)))
	switch {
	case uint32(hr) == hrDXGIWaitTimeout:
	case failed(hr):
		// Access lost (a desktop switch, a mode change): the caller makes a new duplication.
		return hrError("AcquireNextFrame", hr)
	default:
		// A frame without a present carries only a mouse move, and the first frame of a new duplication is black: neither is an image.
		err := d.copyFrame(resource, info.LastPresentTime != 0)
		comRelease(resource)
		syscall.SyscallN(method(d.dupl, slotDuplReleaseFrame), d.dupl)
		if err != nil {
			return err
		}
	}
	if !d.have {
		return errNoImageYet
	}
	copy(dst, d.clean)
	return nil
}

// seed gives a new duplication the image GDI captured, until the desktop draws something new.
func (d *duplication) seed(image []byte) {
	d.clean = append(d.clean[:0], image...)
	d.have = true
}

// copyFrame copies a new desktop image through the CPU-readable staging texture. A frame with only a mouse move carries no new image.
func (d *duplication) copyFrame(resource uintptr, image bool) error {
	if !image {
		return nil
	}
	texture := queryInterface(resource, &iidID3D11Texture2D)
	if texture == 0 {
		return errors.New("the desktop image is not a texture")
	}
	defer comRelease(texture)
	if d.staging == 0 {
		var desc texture2DDesc
		syscall.SyscallN(method(texture, slotTextureGetDesc), texture, uintptr(unsafe.Pointer(&desc)))
		if int(desc.Width) != d.area.Width || int(desc.Height) != d.area.Height {
			return errors.New("the desktop image does not have the monitor's size")
		}
		desc.MipLevels, desc.ArraySize, desc.SampleCount, desc.SampleQuality = 1, 1, 1, 0
		desc.Usage, desc.BindFlags, desc.CPUAccessFlags, desc.MiscFlags = d3d11UsageStaging, 0, d3d11CPUAccessRead, 0
		if hr, _, _ := syscall.SyscallN(method(d.device, slotDeviceCreateTexture), d.device, uintptr(unsafe.Pointer(&desc)), 0,
			uintptr(unsafe.Pointer(&d.staging))); failed(hr) {
			return hrError("CreateTexture2D", hr)
		}
	}
	syscall.SyscallN(method(d.context, slotContextCopyResource), d.context, d.staging, texture)
	var mapped mappedSubresource
	if hr, _, _ := syscall.SyscallN(method(d.context, slotContextMap), d.context, d.staging, 0, d3d11MapRead, 0,
		uintptr(unsafe.Pointer(&mapped))); failed(hr) {
		return hrError("Map", hr)
	}
	defer syscall.SyscallN(method(d.context, slotContextUnmap), d.context, d.staging, 0)
	row := d.area.Width * 4
	size := row * d.area.Height
	if cap(d.clean) < size {
		d.clean = make([]byte, size)
	}
	d.clean = d.clean[:size]
	pitch := int(mapped.RowPitch)
	if pitch < row {
		return errors.New("the desktop image rows are shorter than the monitor")
	}
	source := unsafe.Slice((*byte)(globalPointer(mapped.Data)), pitch*(d.area.Height-1)+row)
	for y := 0; y < d.area.Height; y++ {
		copy(d.clean[y*row:(y+1)*row], source[y*pitch:y*pitch+row])
	}
	d.have = true
	return nil
}

func (d *duplication) close() {
	comRelease(d.staging)
	comRelease(d.dupl)
	comRelease(d.context)
	comRelease(d.device)
	*d = duplication{}
}
