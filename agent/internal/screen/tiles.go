package screen

import (
	"bytes"
	"encoding/binary"
	"errors"
	"image"
	"image/jpeg"
	"image/png"
)

// Tile codec (decided 2026-09-16): the image is cut into square tiles, a tile is sent only when its pixels changed, and runs of changed
// tiles in a row travel as one rectangle. A rectangle with few colors (text, windows, a desktop background of one color) is PNG so text
// stays sharp; anything else (photos, gradients, video) is JPEG. The browser draws each rectangle where it belongs.

const (
	// TileSize is the edge of a tile in pixels.
	TileSize = 64
	// maxRunTiles is how many changed tiles of a row join one rectangle.
	maxRunTiles = 8
	// paletteLimit is the most distinct colors a rectangle has to be PNG.
	paletteLimit = 64

	// FormatPNG and FormatJPEG are the tile formats in an update.
	FormatPNG  byte = 1
	FormatJPEG byte = 2

	// DefaultQuality is the JPEG quality of a normal link.
	DefaultQuality = 70
	// LowQuality is the JPEG quality when frames are large and acknowledgements slow.
	LowQuality = 45
)

// Image is a captured frame: Width x Height pixels, 4 bytes each in B, G, R, A order, rows top to bottom (a Windows DIB section).
type Image struct {
	Width  int
	Height int
	Pix    []byte
}

// Tile is one changed rectangle, encoded.
type Tile struct {
	X, Y, W, H int
	Format     byte
	Data       []byte
}

// Encoder remembers the last frame it encoded, so the next one sends only what changed.
type Encoder struct {
	prev          []byte
	width, height int
	// Quality is the JPEG quality; 0 means DefaultQuality.
	Quality int
}

// Reset forgets the last frame: the next Encode sends the whole image.
func (e *Encoder) Reset() {
	e.prev = nil
}

// Encode returns the changed rectangles of img, or all of it when full is set or the size changed.
func (e *Encoder) Encode(img *Image, full bool) ([]Tile, error) {
	if img.Width <= 0 || img.Height <= 0 || len(img.Pix) < img.Width*img.Height*4 {
		return nil, errors.New("the captured image is empty")
	}
	if e.prev == nil || e.width != img.Width || e.height != img.Height {
		full = true
	}
	quality := e.Quality
	if quality <= 0 {
		quality = DefaultQuality
	}
	var tiles []Tile
	for ty := 0; ty < img.Height; ty += TileSize {
		h := min(TileSize, img.Height-ty)
		runStart := -1
		flush := func(endX int) error {
			if runStart < 0 {
				return nil
			}
			tile, err := encodeRect(img, runStart, ty, endX-runStart, h, quality)
			if err != nil {
				return err
			}
			tiles = append(tiles, tile)
			runStart = -1
			return nil
		}
		for tx := 0; tx < img.Width; tx += TileSize {
			w := min(TileSize, img.Width-tx)
			if full || e.tileChanged(img, tx, ty, w, h) {
				if runStart < 0 {
					runStart = tx
				}
				if (tx+w-runStart)/TileSize >= maxRunTiles {
					if err := flush(tx + w); err != nil {
						return nil, err
					}
				}
				continue
			}
			if err := flush(tx); err != nil {
				return nil, err
			}
		}
		if err := flush(img.Width); err != nil {
			return nil, err
		}
	}
	size := img.Width * img.Height * 4
	if cap(e.prev) < size {
		e.prev = make([]byte, size)
	}
	e.prev = e.prev[:size]
	copy(e.prev, img.Pix[:size])
	e.width, e.height = img.Width, img.Height
	return tiles, nil
}

func (e *Encoder) tileChanged(img *Image, x, y, w, h int) bool {
	for row := y; row < y+h; row++ {
		start := (row*img.Width + x) * 4
		end := start + w*4
		if !bytes.Equal(img.Pix[start:end], e.prev[start:end]) {
			return true
		}
	}
	return false
}

// encodeRect encodes one rectangle as PNG when it has few colors, otherwise as JPEG.
func encodeRect(img *Image, x, y, w, h, quality int) (Tile, error) {
	rgba := image.NewRGBA(image.Rect(0, 0, w, h))
	colors := make(map[uint32]struct{}, paletteLimit+1)
	for row := 0; row < h; row++ {
		src := img.Pix[((y+row)*img.Width+x)*4 : ((y+row)*img.Width+x+w)*4]
		dst := rgba.Pix[row*rgba.Stride : row*rgba.Stride+w*4]
		for i := 0; i < len(src); i += 4 {
			b, g, r := src[i], src[i+1], src[i+2]
			dst[i], dst[i+1], dst[i+2], dst[i+3] = r, g, b, 0xFF
			if len(colors) <= paletteLimit {
				colors[uint32(r)<<16|uint32(g)<<8|uint32(b)] = struct{}{}
			}
		}
	}
	var buf bytes.Buffer
	tile := Tile{X: x, Y: y, W: w, H: h}
	if len(colors) <= paletteLimit {
		tile.Format = FormatPNG
		encoder := png.Encoder{CompressionLevel: png.BestSpeed}
		if err := encoder.Encode(&buf, rgba); err != nil {
			return Tile{}, err
		}
	} else {
		tile.Format = FormatJPEG
		if err := jpeg.Encode(&buf, rgba, &jpeg.Options{Quality: quality}); err != nil {
			return Tile{}, err
		}
	}
	tile.Data = buf.Bytes()
	return tile, nil
}

// Update layout (FrameUpdate, binary, big-endian):
//
//	type byte (0x18) | frame uint32 | flags uint8 (1: last update of the frame) | count uint16 |
//	count x (x uint16 | y uint16 | w uint16 | h uint16 | format uint8 | length uint32 | data)
const (
	updateHeaderBytes = 1 + 4 + 1 + 2
	tileHeaderBytes   = 2 + 2 + 2 + 2 + 1 + 4
	// FlagLast marks the last update of a frame: the browser acknowledges the frame after drawing it.
	FlagLast byte = 1
)

// Updates packs the tiles of one frame into FrameUpdate frames of at most MaxFrameBytes each. A frame without tiles still gets one
// (empty, last) update, so the browser acknowledges it.
func Updates(frame uint32, tiles []Tile) ([][]byte, error) {
	var out [][]byte
	var current []byte
	count := 0
	start := func() {
		current = make([]byte, updateHeaderBytes, 64*1024)
		current[0] = FrameUpdate
		binary.BigEndian.PutUint32(current[1:], frame)
		count = 0
	}
	finish := func() {
		binary.BigEndian.PutUint16(current[6:], uint16(count))
		out = append(out, current)
	}
	start()
	for _, t := range tiles {
		size := tileHeaderBytes + len(t.Data)
		if updateHeaderBytes+size > MaxFrameBytes {
			return nil, errors.New("an encoded tile is larger than a frame")
		}
		if len(current)+size > MaxFrameBytes || count == 0xFFFF {
			finish()
			start()
		}
		var header [tileHeaderBytes]byte
		binary.BigEndian.PutUint16(header[0:], uint16(t.X))
		binary.BigEndian.PutUint16(header[2:], uint16(t.Y))
		binary.BigEndian.PutUint16(header[4:], uint16(t.W))
		binary.BigEndian.PutUint16(header[6:], uint16(t.H))
		header[8] = t.Format
		binary.BigEndian.PutUint32(header[9:], uint32(len(t.Data)))
		current = append(current, header[:]...)
		current = append(current, t.Data...)
		count++
	}
	finish()
	out[len(out)-1][5] = FlagLast
	return out, nil
}

// ParseUpdate reads a FrameUpdate (for tests and tools).
func ParseUpdate(update []byte) (frame uint32, last bool, tiles []Tile, err error) {
	if len(update) < updateHeaderBytes || update[0] != FrameUpdate {
		return 0, false, nil, errors.New("not an update")
	}
	frame = binary.BigEndian.Uint32(update[1:])
	last = update[5]&FlagLast != 0
	count := int(binary.BigEndian.Uint16(update[6:]))
	rest := update[updateHeaderBytes:]
	for i := 0; i < count; i++ {
		if len(rest) < tileHeaderBytes {
			return 0, false, nil, errors.New("a tile header is cut off")
		}
		t := Tile{
			X: int(binary.BigEndian.Uint16(rest[0:])), Y: int(binary.BigEndian.Uint16(rest[2:])),
			W: int(binary.BigEndian.Uint16(rest[4:])), H: int(binary.BigEndian.Uint16(rest[6:])), Format: rest[8],
		}
		n := int(binary.BigEndian.Uint32(rest[9:]))
		rest = rest[tileHeaderBytes:]
		if len(rest) < n {
			return 0, false, nil, errors.New("tile data is cut off")
		}
		t.Data = rest[:n]
		rest = rest[n:]
		tiles = append(tiles, t)
	}
	return frame, last, tiles, nil
}
