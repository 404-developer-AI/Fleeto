//go:build windows

package screen

import (
	"errors"
	"fmt"
	"runtime"
	"syscall"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// H.264 encoding with Media Foundation (0.3.0 step 5). The agent is built without cgo, so the COM interfaces are called through their
// vtables. The hardware encoder of the GPU is tried first (an asynchronous transform, driven by its events); when there is none, or it
// fails, the Microsoft H.264 encoder that ships with Windows (a synchronous transform). Windows Server without the Media Foundation
// feature and Windows N editions have neither: remote control then sends tiles.

var (
	mfplat                   = windows.NewLazySystemDLL("mfplat.dll")
	procMFStartup            = mfplat.NewProc("MFStartup")
	procMFShutdown           = mfplat.NewProc("MFShutdown")
	procMFCreateMediaType    = mfplat.NewProc("MFCreateMediaType")
	procMFCreateSample       = mfplat.NewProc("MFCreateSample")
	procMFCreateMemoryBuffer = mfplat.NewProc("MFCreateMemoryBuffer")
	procMFTEnumEx            = mfplat.NewProc("MFTEnumEx")
	procCoInitializeEx       = ole32.NewProc("CoInitializeEx")
	procCoUninitialize       = ole32.NewProc("CoUninitialize")
	procCoCreateInstance     = ole32.NewProc("CoCreateInstance")
	procCoTaskMemFree        = ole32.NewProc("CoTaskMemFree")
)

func mustGUID(s string) windows.GUID {
	g, err := windows.GUIDFromString(s)
	if err != nil {
		panic(err)
	}
	return g
}

var (
	iidIMFTransform            = mustGUID("{bf94c121-5b05-4e6f-8000-ba598961414d}")
	iidIMFMediaEventGenerator  = mustGUID("{2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d}")
	iidICodecAPI               = mustGUID("{901db4c7-31ce-41a2-85dc-8fa0bf41b8da}")
	clsidMSH264Encoder         = mustGUID("{6ca50344-051a-4ded-9779-a43305165e35}")
	mftCategoryVideoEncoder    = mustGUID("{f79eac7d-e545-4387-bdee-d647d7bde42a}")
	mfMediaTypeVideo           = mustGUID("{73646976-0000-0010-8000-00aa00389b71}")
	mfVideoFormatH264          = mustGUID("{34363248-0000-0010-8000-00aa00389b71}")
	mfVideoFormatNV12          = mustGUID("{3231564e-0000-0010-8000-00aa00389b71}")
	mfMTMajorType              = mustGUID("{48eba18e-f8c9-4687-bf11-0a74c9f96a8f}")
	mfMTSubtype                = mustGUID("{f7e34c9a-42e8-4714-b74b-cb29d72c35e5}")
	mfMTFrameSize              = mustGUID("{1652c33d-d6b2-4012-b834-72030849a37d}")
	mfMTFrameRate              = mustGUID("{c459a2e8-3d2c-4e44-b132-fee5156c7bb0}")
	mfMTPixelAspectRatio       = mustGUID("{c6376a1e-8d0a-4027-be45-6d9a0ad39bb6}")
	mfMTInterlaceMode          = mustGUID("{e2724bb8-e676-4806-b4b2-a8d6efb44ccd}")
	mfMTAvgBitrate             = mustGUID("{20332624-fb0d-4d9e-bd0d-cbf6786c102e}")
	mfMTMpeg2Profile           = mustGUID("{ad76a80b-2d5c-4e0b-b375-64e520137036}")
	mfMTYUVMatrix              = mustGUID("{3e23d450-2c75-4d25-a00e-b91670d12327}")
	mfMTVideoNominalRange      = mustGUID("{c21b8ee5-b956-4071-8daf-325edf5cab11}")
	mfMTMpegSequenceHeader     = mustGUID("{3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3}")
	mfTransformAsync           = mustGUID("{f81a699a-649a-497d-8c73-29f8fed6ad7a}")
	mfTransformAsyncUnlock     = mustGUID("{e5666d6b-3422-4eb6-a421-da7db1f8e207}")
	mfLowLatency               = mustGUID("{9c27891a-ed7a-40e1-88e8-b22727a024ee}")
	codecAPIRateControlMode    = mustGUID("{1c0608e9-370c-4710-8a58-cb6181c42423}")
	codecAPIMeanBitRate        = mustGUID("{f7222374-2144-4815-b550-a37f8e12ee52}")
	codecAPIGOPSize            = mustGUID("{95f31b26-95a4-41aa-9303-246a7fc6eef1}")
	codecAPIBPictureCount      = mustGUID("{8d390aac-dc5c-4200-b57f-814d04babab2}")
	codecAPIQualityVsSpeed     = mustGUID("{98332df8-03cd-476b-89fa-3f9e442dec9f}")
	codecAPIForceKeyFrame      = mustGUID("{398c1b98-8353-475a-9ef2-8f265d260345}")
	codecAPILowLatencyMode     = mfLowLatency
	mftRegisterTypeInfoNV12    = mftRegisterTypeInfo{Major: mfMediaTypeVideo, Sub: mfVideoFormatNV12}
	mftRegisterTypeInfoH264Out = mftRegisterTypeInfo{Major: mfMediaTypeVideo, Sub: mfVideoFormatH264}
)

const (
	mfVersion         = 0x00020070
	mfStartupLite     = 1
	coinitMultithread = 0
	clsctxInproc      = 1

	mftEnumFlagSyncMFT       = 0x01
	mftEnumFlagAsyncMFT      = 0x02
	mftEnumFlagHardware      = 0x04
	mftEnumFlagSortAndFilter = 0x40

	mftMessageCommandFlush         = 0x00000000
	mftMessageNotifyBeginStreaming = 0x10000000
	mftMessageNotifyEndStreaming   = 0x10000001
	mftMessageNotifyStartOfStream  = 0x10000003

	mftOutputStreamProvidesSamples   = 0x100
	mftOutputStreamCanProvideSamples = 0x200

	mfEventFlagNoWait     = 1
	meTransformNeedInput  = 601
	meTransformHaveOutput = 602

	hrNeedMoreInput     = 0xC00D6D72
	hrStreamChange      = 0xC00D6D61
	hrNotAccepting      = 0xC00D36B5
	hrNoEventsAvailable = 0xC00D3E80

	mfVideoInterlaceProgressive = 2
	mfTransferMatrixBT709       = 1
	mfNominalRange16235         = 2
	rateControlCBR              = 0
	profileMain                 = 77
	profileBaseline             = 66
	profileHigh                 = 100

	vtBool = 11
	vtUI4  = 19

	// asyncWait bounds how long a hardware encoder may take for one frame before it is given up.
	asyncWait = 2 * time.Second
)

// Vtable slots (IUnknown first).
const (
	slotQueryInterface = 0
	slotRelease        = 2

	// IMFAttributes, also the start of IMFMediaType, IMFActivate, IMFSample and IMFMediaEvent.
	slotGetUINT32   = 7
	slotGetBlobSize = 14
	slotGetBlob     = 15
	slotSetUINT32   = 21
	slotSetUINT64   = 22
	slotSetGUID     = 24

	// IMFActivate.
	slotActivateObject = 33
	slotShutdownObject = 34

	// IMFMediaEvent.
	slotEventGetType = 33

	// IMFSample.
	slotSetSampleTime       = 36
	slotSetSampleDuration   = 38
	slotConvertToContiguous = 41
	slotAddBuffer           = 42

	// IMFMediaBuffer.
	slotBufferLock             = 3
	slotBufferUnlock           = 4
	slotBufferSetCurrentLength = 6

	// IMFTransform.
	slotGetOutputStreamInfo    = 7
	slotGetAttributes          = 8
	slotGetOutputAvailableType = 14
	slotSetInputType           = 15
	slotSetOutputType          = 16
	slotGetOutputCurrentType   = 18
	slotProcessMessage         = 23
	slotProcessInput           = 24
	slotProcessOutput          = 25

	// IMFMediaEventGenerator.
	slotGetEvent = 3

	// ICodecAPI.
	slotCodecSetValue = 9
)

type mftRegisterTypeInfo struct {
	Major windows.GUID
	Sub   windows.GUID
}

type mftOutputStreamInfo struct {
	Flags     uint32
	Size      uint32
	Alignment uint32
}

type mftOutputDataBuffer struct {
	StreamID uint32
	_        uint32
	Sample   uintptr
	Status   uint32
	_        uint32
	Events   uintptr
}

// variant is a VARIANT as ICodecAPI takes it (24 bytes on 64-bit Windows).
type variant struct {
	VT  uint16
	_   [3]uint16
	Val uint64
	_   uint64
}

// method returns the function in a slot of a COM object's vtable.
func method(object uintptr, slot uintptr) uintptr {
	vtable := *(*uintptr)(globalPointer(object))
	return *(*uintptr)(globalPointer(vtable + slot*unsafe.Sizeof(uintptr(0))))
}

func comRelease(object uintptr) {
	if object != 0 {
		syscall.SyscallN(method(object, slotRelease), object)
	}
}

func failed(hr uintptr) bool { return int32(uint32(hr)) < 0 }

func hrError(step string, hr uintptr) error {
	return fmt.Errorf("%s failed (HRESULT %s)", step, hresult(hr))
}

func setGUID(attrs uintptr, key *windows.GUID, value *windows.GUID) uintptr {
	hr, _, _ := syscall.SyscallN(method(attrs, slotSetGUID), attrs, uintptr(unsafe.Pointer(key)), uintptr(unsafe.Pointer(value)))
	return hr
}

func setUINT32(attrs uintptr, key *windows.GUID, value uint32) uintptr {
	hr, _, _ := syscall.SyscallN(method(attrs, slotSetUINT32), attrs, uintptr(unsafe.Pointer(key)), uintptr(value))
	return hr
}

func setUINT64(attrs uintptr, key *windows.GUID, value uint64) uintptr {
	hr, _, _ := syscall.SyscallN(method(attrs, slotSetUINT64), attrs, uintptr(unsafe.Pointer(key)), uintptr(value))
	return hr
}

func getUINT32(attrs uintptr, key *windows.GUID) (uint32, bool) {
	var value uint32
	hr, _, _ := syscall.SyscallN(method(attrs, slotGetUINT32), attrs, uintptr(unsafe.Pointer(key)), uintptr(unsafe.Pointer(&value)))
	return value, !failed(hr)
}

func getBlob(attrs uintptr, key *windows.GUID) []byte {
	var size uint32
	hr, _, _ := syscall.SyscallN(method(attrs, slotGetBlobSize), attrs, uintptr(unsafe.Pointer(key)), uintptr(unsafe.Pointer(&size)))
	if failed(hr) || size == 0 || size > 64*1024 {
		return nil
	}
	blob := make([]byte, size)
	hr, _, _ = syscall.SyscallN(method(attrs, slotGetBlob), attrs, uintptr(unsafe.Pointer(key)), uintptr(unsafe.Pointer(&blob[0])),
		uintptr(size), 0)
	if failed(hr) {
		return nil
	}
	return blob
}

func queryInterface(object uintptr, iid *windows.GUID) uintptr {
	var out uintptr
	hr, _, _ := syscall.SyscallN(method(object, slotQueryInterface), object, uintptr(unsafe.Pointer(iid)), uintptr(unsafe.Pointer(&out)))
	if failed(hr) {
		return 0
	}
	return out
}

// mfThread runs Media Foundation on one OS thread of its own: COM (multithreaded apartment) and MFStartup belong to a thread, and the
// helper's capture thread must stay free of anything that could keep it from following the input desktop.
type mfThread struct {
	calls chan func()
	done  chan struct{}
}

// startMF starts the Media Foundation thread, or says why Media Foundation cannot be used on this endpoint.
func startMF() (*mfThread, error) {
	if err := mfplat.Load(); err != nil {
		return nil, errors.New("Media Foundation is not installed")
	}
	for _, proc := range []*windows.LazyProc{procMFStartup, procMFShutdown, procMFCreateMediaType, procMFCreateSample, procMFCreateMemoryBuffer,
		procMFTEnumEx, procCoInitializeEx, procCoUninitialize, procCoCreateInstance, procCoTaskMemFree} {
		if err := proc.Find(); err != nil {
			return nil, fmt.Errorf("Media Foundation is incomplete: %w", err)
		}
	}
	t := &mfThread{calls: make(chan func()), done: make(chan struct{})}
	started := make(chan error, 1)
	go func() {
		runtime.LockOSThread()
		defer runtime.UnlockOSThread()
		defer close(t.done)
		if hr, _, _ := procCoInitializeEx.Call(0, coinitMultithread); failed(hr) {
			started <- hrError("CoInitializeEx", hr)
			return
		}
		defer procCoUninitialize.Call()
		if hr, _, _ := procMFStartup.Call(mfVersion, mfStartupLite); failed(hr) {
			started <- hrError("MFStartup", hr)
			return
		}
		defer procMFShutdown.Call()
		started <- nil
		for call := range t.calls {
			call()
		}
	}()
	if err := <-started; err != nil {
		return nil, err
	}
	return t, nil
}

// do runs a call on the Media Foundation thread and waits for it.
func (t *mfThread) do(call func()) {
	finished := make(chan struct{})
	t.calls <- func() {
		defer close(finished)
		call()
	}
	<-finished
}

// stop ends the thread; every encoder on it must be closed first.
func (t *mfThread) stop() {
	close(t.calls)
	<-t.done
}

// mfEncoder is one H.264 encoder transform. All its methods run on the Media Foundation thread.
type mfEncoder struct {
	kind      string // "hardware" or "software"
	name      string
	activate  uintptr
	transform uintptr
	codecAPI  uintptr
	events    uintptr
	width     int
	height    int
	output    mftOutputStreamInfo
	header    []byte
	needInput int
}

// openH264 opens an encoder for frames of width x height (even) at a bit rate: the hardware encoder first unless it is excluded, then
// the Microsoft software encoder. It returns why neither could be opened.
func openH264(width, height, bitrate int, hardware bool) (*mfEncoder, error) {
	var reasons []string
	if hardware {
		enc, err := openHardware(width, height, bitrate)
		if err == nil {
			return enc, nil
		}
		reasons = append(reasons, "hardware: "+err.Error())
	}
	enc, err := openSoftware(width, height, bitrate)
	if err == nil {
		return enc, nil
	}
	reasons = append(reasons, "software: "+err.Error())
	return nil, fmt.Errorf("no H.264 encoder could be opened (%s)", joinReasons(reasons))
}

func joinReasons(reasons []string) string {
	out := ""
	for i, r := range reasons {
		if i > 0 {
			out += "; "
		}
		out += r
	}
	return out
}

func openHardware(width, height, bitrate int) (*mfEncoder, error) {
	input, output := mftRegisterTypeInfoNV12, mftRegisterTypeInfoH264Out
	var activates uintptr
	var count uint32
	hr, _, _ := procMFTEnumEx.Call(uintptr(unsafe.Pointer(&mftCategoryVideoEncoder)), mftEnumFlagHardware|mftEnumFlagSortAndFilter,
		uintptr(unsafe.Pointer(&input)), uintptr(unsafe.Pointer(&output)), uintptr(unsafe.Pointer(&activates)), uintptr(unsafe.Pointer(&count)))
	if failed(hr) {
		return nil, hrError("MFTEnumEx", hr)
	}
	if count == 0 || activates == 0 {
		if activates != 0 {
			procCoTaskMemFree.Call(activates)
		}
		return nil, errors.New("this endpoint has no hardware H.264 encoder")
	}
	list := unsafe.Slice((*uintptr)(globalPointer(activates)), count)
	var chosen *mfEncoder
	var lastErr error
	for i, activate := range list {
		if chosen == nil {
			var transform uintptr
			hr, _, _ := syscall.SyscallN(method(activate, slotActivateObject), activate, uintptr(unsafe.Pointer(&iidIMFTransform)),
				uintptr(unsafe.Pointer(&transform)))
			if failed(hr) {
				lastErr = hrError("ActivateObject", hr)
			} else {
				enc := &mfEncoder{kind: "hardware", name: fmt.Sprintf("hardware encoder %d", i+1), activate: activate, transform: transform,
					width: width, height: height}
				if err := enc.configure(bitrate, true); err != nil {
					lastErr = err
					enc.close()
					continue
				}
				chosen = enc
				continue // this activate is kept by the encoder; the rest are released
			}
		}
		comRelease(activate)
	}
	procCoTaskMemFree.Call(activates)
	if chosen == nil {
		if lastErr == nil {
			lastErr = errors.New("no hardware encoder accepted the format")
		}
		return nil, lastErr
	}
	return chosen, nil
}

func openSoftware(width, height, bitrate int) (*mfEncoder, error) {
	var transform uintptr
	hr, _, _ := procCoCreateInstance.Call(uintptr(unsafe.Pointer(&clsidMSH264Encoder)), 0, clsctxInproc,
		uintptr(unsafe.Pointer(&iidIMFTransform)), uintptr(unsafe.Pointer(&transform)))
	if failed(hr) {
		return nil, fmt.Errorf("the Microsoft H.264 encoder is not installed (HRESULT %s)", hresult(hr))
	}
	enc := &mfEncoder{kind: "software", name: "Microsoft H.264 encoder", transform: transform, width: width, height: height}
	if err := enc.configure(bitrate, false); err != nil {
		enc.close()
		return nil, err
	}
	return enc, nil
}

// configure sets the encoder up for low latency: no B pictures, constant bit rate, a key frame only when asked (and after a long GOP).
func (e *mfEncoder) configure(bitrate int, hardware bool) error {
	t := e.transform
	var attrs uintptr
	if hr, _, _ := syscall.SyscallN(method(t, slotGetAttributes), t, uintptr(unsafe.Pointer(&attrs))); !failed(hr) && attrs != 0 {
		if async, ok := getUINT32(attrs, &mfTransformAsync); ok && async != 0 {
			if hr := setUINT32(attrs, &mfTransformAsyncUnlock, 1); failed(hr) {
				comRelease(attrs)
				return hrError("unlocking the asynchronous encoder", hr)
			}
			e.events = queryInterface(t, &iidIMFMediaEventGenerator)
			if e.events == 0 {
				comRelease(attrs)
				return errors.New("the asynchronous encoder has no events")
			}
		}
		setUINT32(attrs, &mfLowLatency, 1)
		comRelease(attrs)
	} else if hardware {
		return hrError("GetAttributes", hr)
	}

	e.codecAPI = queryInterface(t, &iidICodecAPI)
	if e.codecAPI != 0 {
		e.setCodecValue(&codecAPILowLatencyMode, variant{VT: vtBool, Val: 0xFFFF})
		e.setCodecValue(&codecAPIRateControlMode, variant{VT: vtUI4, Val: rateControlCBR})
		e.setCodecValue(&codecAPIMeanBitRate, variant{VT: vtUI4, Val: uint64(bitrate)})
		e.setCodecValue(&codecAPIBPictureCount, variant{VT: vtUI4, Val: 0})
		e.setCodecValue(&codecAPIGOPSize, variant{VT: vtUI4, Val: 3000})
		if !hardware {
			// Speed over the last bit of quality: the software encoder has one frame time to spend, on a busy endpoint too.
			e.setCodecValue(&codecAPIQualityVsSpeed, variant{VT: vtUI4, Val: 30})
		}
	}

	// Encoders take the output type first. Main profile without B pictures decodes in every browser; baseline and high are tried when an
	// encoder refuses main.
	var setErr error
	for _, profile := range []uint32{profileMain, profileBaseline, profileHigh} {
		if setErr = e.setOutputType(bitrate, profile); setErr == nil {
			break
		}
	}
	if setErr != nil {
		return setErr
	}
	if err := e.setInputType(); err != nil {
		return err
	}
	if hr, _, _ := syscall.SyscallN(method(t, slotGetOutputStreamInfo), t, 0, uintptr(unsafe.Pointer(&e.output))); failed(hr) {
		return hrError("GetOutputStreamInfo", hr)
	}
	e.readHeader()
	syscall.SyscallN(method(t, slotProcessMessage), t, mftMessageCommandFlush, 0)
	if hr, _, _ := syscall.SyscallN(method(t, slotProcessMessage), t, mftMessageNotifyBeginStreaming, 0); failed(hr) {
		return hrError("starting the encoder", hr)
	}
	if hr, _, _ := syscall.SyscallN(method(t, slotProcessMessage), t, mftMessageNotifyStartOfStream, 0); failed(hr) {
		return hrError("starting the stream", hr)
	}
	return nil
}

func (e *mfEncoder) setCodecValue(key *windows.GUID, value variant) bool {
	if e.codecAPI == 0 {
		return false
	}
	v := value
	hr, _, _ := syscall.SyscallN(method(e.codecAPI, slotCodecSetValue), e.codecAPI, uintptr(unsafe.Pointer(key)), uintptr(unsafe.Pointer(&v)))
	return !failed(hr)
}

func newMediaType() (uintptr, error) {
	var mt uintptr
	if hr, _, _ := procMFCreateMediaType.Call(uintptr(unsafe.Pointer(&mt))); failed(hr) {
		return 0, hrError("MFCreateMediaType", hr)
	}
	return mt, nil
}

func (e *mfEncoder) setOutputType(bitrate int, profile uint32) error {
	mt, err := newMediaType()
	if err != nil {
		return err
	}
	defer comRelease(mt)
	setGUID(mt, &mfMTMajorType, &mfMediaTypeVideo)
	setGUID(mt, &mfMTSubtype, &mfVideoFormatH264)
	setUINT32(mt, &mfMTAvgBitrate, uint32(bitrate))
	setUINT64(mt, &mfMTFrameSize, uint64(e.width)<<32|uint64(e.height))
	setUINT64(mt, &mfMTFrameRate, uint64(videoFrameRate)<<32|1)
	setUINT64(mt, &mfMTPixelAspectRatio, 1<<32|1)
	setUINT32(mt, &mfMTInterlaceMode, mfVideoInterlaceProgressive)
	setUINT32(mt, &mfMTMpeg2Profile, profile)
	if hr, _, _ := syscall.SyscallN(method(e.transform, slotSetOutputType), e.transform, 0, mt, 0); failed(hr) {
		return hrError(fmt.Sprintf("setting the H.264 output (%d x %d, profile %d)", e.width, e.height, profile), hr)
	}
	return nil
}

func (e *mfEncoder) setInputType() error {
	mt, err := newMediaType()
	if err != nil {
		return err
	}
	defer comRelease(mt)
	setGUID(mt, &mfMTMajorType, &mfMediaTypeVideo)
	setGUID(mt, &mfMTSubtype, &mfVideoFormatNV12)
	setUINT64(mt, &mfMTFrameSize, uint64(e.width)<<32|uint64(e.height))
	setUINT64(mt, &mfMTFrameRate, uint64(videoFrameRate)<<32|1)
	setUINT64(mt, &mfMTPixelAspectRatio, 1<<32|1)
	setUINT32(mt, &mfMTInterlaceMode, mfVideoInterlaceProgressive)
	setUINT32(mt, &mfMTYUVMatrix, mfTransferMatrixBT709)
	setUINT32(mt, &mfMTVideoNominalRange, mfNominalRange16235)
	if hr, _, _ := syscall.SyscallN(method(e.transform, slotSetInputType), e.transform, 0, mt, 0); failed(hr) {
		return hrError(fmt.Sprintf("setting the NV12 input (%d x %d)", e.width, e.height), hr)
	}
	return nil
}

// readHeader keeps the SPS and PPS of the current output type, to put in front of a key frame that comes without them.
func (e *mfEncoder) readHeader() {
	var mt uintptr
	if hr, _, _ := syscall.SyscallN(method(e.transform, slotGetOutputCurrentType), e.transform, 0, uintptr(unsafe.Pointer(&mt))); failed(hr) || mt == 0 {
		return
	}
	defer comRelease(mt)
	if header := annexB(getBlob(mt, &mfMTMpegSequenceHeader)); len(header) > 0 {
		e.header = header
	}
}

// setBitrate changes the bit rate while the encoder runs; an encoder that cannot keeps its bit rate.
func (e *mfEncoder) setBitrate(bitrate int) {
	e.setCodecValue(&codecAPIMeanBitRate, variant{VT: vtUI4, Val: uint64(bitrate)})
}

// encode encodes one NV12 frame and returns its access unit (Annex B). key asks for a key frame; the caller checks that it got one.
func (e *mfEncoder) encode(nv12 []byte, key bool, at time.Duration) ([]byte, error) {
	if len(nv12) != e.width*e.height*3/2 {
		return nil, errors.New("the frame does not match the encoder's size")
	}
	if key {
		e.setCodecValue(&codecAPIForceKeyFrame, variant{VT: vtUI4, Val: 1})
	}
	sample, err := newSample(nv12, at)
	if err != nil {
		return nil, err
	}
	defer comRelease(sample)
	if e.events != 0 {
		return e.encodeAsync(sample)
	}
	return e.encodeSync(sample)
}

func newSample(data []byte, at time.Duration) (uintptr, error) {
	var buffer uintptr
	if hr, _, _ := procMFCreateMemoryBuffer.Call(uintptr(len(data)), uintptr(unsafe.Pointer(&buffer))); failed(hr) {
		return 0, hrError("MFCreateMemoryBuffer", hr)
	}
	defer comRelease(buffer)
	var pointer uintptr
	var maxLength, current uint32
	if hr, _, _ := syscall.SyscallN(method(buffer, slotBufferLock), buffer, uintptr(unsafe.Pointer(&pointer)), uintptr(unsafe.Pointer(&maxLength)),
		uintptr(unsafe.Pointer(&current))); failed(hr) {
		return 0, hrError("locking the input buffer", hr)
	}
	if int(maxLength) < len(data) {
		syscall.SyscallN(method(buffer, slotBufferUnlock), buffer)
		return 0, errors.New("the input buffer is too small")
	}
	copy(unsafe.Slice((*byte)(globalPointer(pointer)), len(data)), data)
	syscall.SyscallN(method(buffer, slotBufferUnlock), buffer)
	syscall.SyscallN(method(buffer, slotBufferSetCurrentLength), buffer, uintptr(len(data)))

	var sample uintptr
	if hr, _, _ := procMFCreateSample.Call(uintptr(unsafe.Pointer(&sample))); failed(hr) {
		return 0, hrError("MFCreateSample", hr)
	}
	if hr, _, _ := syscall.SyscallN(method(sample, slotAddBuffer), sample, buffer); failed(hr) {
		comRelease(sample)
		return 0, hrError("adding the input buffer", hr)
	}
	// Media Foundation counts time in units of 100 nanoseconds.
	syscall.SyscallN(method(sample, slotSetSampleTime), sample, uintptr(at/100))
	syscall.SyscallN(method(sample, slotSetSampleDuration), sample, uintptr(time.Second/videoFrameRate/100))
	return sample, nil
}

func (e *mfEncoder) encodeSync(sample uintptr) ([]byte, error) {
	t := e.transform
	hr, _, _ := syscall.SyscallN(method(t, slotProcessInput), t, 0, sample, 0)
	if uint32(hr) == hrNotAccepting {
		// Output is waiting from before: take it out, then the input fits.
		if _, err := e.drainOutput(); err != nil {
			return nil, err
		}
		hr, _, _ = syscall.SyscallN(method(t, slotProcessInput), t, 0, sample, 0)
	}
	if failed(hr) {
		return nil, hrError("ProcessInput", hr)
	}
	data, err := e.drainOutput()
	if err != nil {
		return nil, err
	}
	if len(data) == 0 {
		return nil, errors.New("the encoder kept the frame back (it does not encode with low latency)")
	}
	return data, nil
}

// drainOutput takes every access unit the encoder has ready.
func (e *mfEncoder) drainOutput() ([]byte, error) {
	var out []byte
	for i := 0; i < 8; i++ {
		data, more, err := e.processOutput()
		if err != nil {
			return nil, err
		}
		out = append(out, data...)
		if !more {
			break
		}
	}
	return out, nil
}

// processOutput takes one output sample; more is false when the encoder needs input first.
func (e *mfEncoder) processOutput() (data []byte, more bool, err error) {
	t := e.transform
	buffer := mftOutputDataBuffer{}
	if e.output.Flags&(mftOutputStreamProvidesSamples|mftOutputStreamCanProvideSamples) == 0 {
		size := max(int(e.output.Size), e.width*e.height)
		var mediaBuffer uintptr
		if hr, _, _ := procMFCreateMemoryBuffer.Call(uintptr(size), uintptr(unsafe.Pointer(&mediaBuffer))); failed(hr) {
			return nil, false, hrError("MFCreateMemoryBuffer", hr)
		}
		var sample uintptr
		if hr, _, _ := procMFCreateSample.Call(uintptr(unsafe.Pointer(&sample))); failed(hr) {
			comRelease(mediaBuffer)
			return nil, false, hrError("MFCreateSample", hr)
		}
		syscall.SyscallN(method(sample, slotAddBuffer), sample, mediaBuffer)
		comRelease(mediaBuffer)
		buffer.Sample = sample
	}
	var status uint32
	hr, _, _ := syscall.SyscallN(method(t, slotProcessOutput), t, 0, 1, uintptr(unsafe.Pointer(&buffer)), uintptr(unsafe.Pointer(&status)))
	defer comRelease(buffer.Events)
	defer comRelease(buffer.Sample)
	switch {
	case uint32(hr) == hrNeedMoreInput:
		return nil, false, nil
	case uint32(hr) == hrStreamChange:
		// The encoder settled its output type (hardware encoders do this before the first frame): take it as it is.
		var mt uintptr
		if hr, _, _ := syscall.SyscallN(method(t, slotGetOutputAvailableType), t, 0, 0, uintptr(unsafe.Pointer(&mt))); failed(hr) {
			return nil, false, hrError("GetOutputAvailableType", hr)
		}
		hr, _, _ := syscall.SyscallN(method(t, slotSetOutputType), t, 0, mt, 0)
		comRelease(mt)
		if failed(hr) {
			return nil, false, hrError("SetOutputType after a stream change", hr)
		}
		syscall.SyscallN(method(t, slotGetOutputStreamInfo), t, 0, uintptr(unsafe.Pointer(&e.output)))
		e.readHeader()
		return nil, true, nil
	case failed(hr):
		return nil, false, hrError("ProcessOutput", hr)
	}
	if buffer.Sample == 0 {
		return nil, true, nil
	}
	data, err = sampleBytes(buffer.Sample)
	return data, true, err
}

func sampleBytes(sample uintptr) ([]byte, error) {
	var buffer uintptr
	if hr, _, _ := syscall.SyscallN(method(sample, slotConvertToContiguous), sample, uintptr(unsafe.Pointer(&buffer))); failed(hr) {
		return nil, hrError("ConvertToContiguousBuffer", hr)
	}
	defer comRelease(buffer)
	var pointer uintptr
	var maxLength, current uint32
	if hr, _, _ := syscall.SyscallN(method(buffer, slotBufferLock), buffer, uintptr(unsafe.Pointer(&pointer)), uintptr(unsafe.Pointer(&maxLength)),
		uintptr(unsafe.Pointer(&current))); failed(hr) {
		return nil, hrError("locking the output buffer", hr)
	}
	data := make([]byte, current)
	if current > 0 {
		copy(data, unsafe.Slice((*byte)(globalPointer(pointer)), current))
	}
	syscall.SyscallN(method(buffer, slotBufferUnlock), buffer)
	return data, nil
}

// encodeAsync feeds a hardware encoder by its events: input when it asks for it, output when it has some.
func (e *mfEncoder) encodeAsync(sample uintptr) ([]byte, error) {
	deadline := time.Now().Add(asyncWait)
	fed := false
	for {
		if !fed && e.needInput > 0 {
			hr, _, _ := syscall.SyscallN(method(e.transform, slotProcessInput), e.transform, 0, sample, 0)
			if failed(hr) {
				return nil, hrError("ProcessInput", hr)
			}
			e.needInput--
			fed = true
		}
		event, err := e.nextEvent(deadline)
		if err != nil {
			return nil, err
		}
		switch event {
		case meTransformNeedInput:
			e.needInput++
		case meTransformHaveOutput:
			data, _, err := e.processOutput()
			if err != nil {
				return nil, err
			}
			if fed && len(data) > 0 {
				return data, nil
			}
		}
	}
}

// nextEvent waits for the next event of an asynchronous encoder, until the deadline.
func (e *mfEncoder) nextEvent(deadline time.Time) (uint32, error) {
	for {
		var event uintptr
		hr, _, _ := syscall.SyscallN(method(e.events, slotGetEvent), e.events, mfEventFlagNoWait, uintptr(unsafe.Pointer(&event)))
		if uint32(hr) == hrNoEventsAvailable {
			if time.Now().After(deadline) {
				return 0, errors.New("the hardware encoder did not deliver the frame in time")
			}
			time.Sleep(time.Millisecond)
			continue
		}
		if failed(hr) {
			return 0, hrError("GetEvent", hr)
		}
		var kind uint32
		syscall.SyscallN(method(event, slotEventGetType), event, uintptr(unsafe.Pointer(&kind)))
		comRelease(event)
		return kind, nil
	}
}

// close releases the encoder. A hardware encoder is shut down through its activation object, which ends its worker threads.
func (e *mfEncoder) close() {
	if e.transform != 0 {
		syscall.SyscallN(method(e.transform, slotProcessMessage), e.transform, mftMessageNotifyEndStreaming, 0)
	}
	comRelease(e.codecAPI)
	comRelease(e.events)
	comRelease(e.transform)
	if e.activate != 0 {
		syscall.SyscallN(method(e.activate, slotShutdownObject), e.activate)
		comRelease(e.activate)
	}
	*e = mfEncoder{}
}
