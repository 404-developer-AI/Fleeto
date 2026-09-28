package storage

import (
	"encoding/binary"
	"errors"
	"time"
	"unicode/utf16"
)

// The NTFS parser works on raw master file table records. Every function takes bytes and checks every offset, so it is
// tested on synthetic records and a corrupt record is skipped rather than trusted.

const (
	attrStandardInformation = 0x10
	attrAttributeList       = 0x20
	attrFileName            = 0x30
	attrData                = 0x80
	attrIndexAllocation     = 0xA0
	attrEnd                 = 0xFFFFFFFF

	recordInUse     = 0x0001
	recordDirectory = 0x0002

	namespaceDOS = 2

	// fixupStride is the size of the blocks protected by the update sequence array, whatever the sector size.
	fixupStride = 512

	// firstUserRecord is the first record that is not NTFS metadata; records below it are the metafiles of the root.
	firstUserRecord = 24
	// rootRecord and extendRecord are the root folder and $Extend.
	rootRecord   = 5
	extendRecord = 11
	// badClusRecord is $BadClus, whose $Bad stream spans the whole volume without using it.
	badClusRecord = 8

	recordMask = 0x0000FFFFFFFFFFFF
)

var (
	errNotRecord  = errors.New("not a file record")
	errBadFixup   = errors.New("update sequence mismatch")
	errBadRecord  = errors.New("corrupt file record")
	errBadRunList = errors.New("corrupt run list")
)

// fileRef splits a file reference into its record number and sequence number.
func fileRef(ref uint64) (record uint64, seq uint16) {
	return ref & recordMask, uint16(ref >> 48)
}

// applyFixups checks and restores the last two bytes of every 512-byte block of a record from its update sequence array.
func applyFixups(rec []byte) error {
	if len(rec) < 0x30 || string(rec[0:4]) != "FILE" {
		return errNotRecord
	}
	usaOffset := int(binary.LittleEndian.Uint16(rec[4:]))
	usaCount := int(binary.LittleEndian.Uint16(rec[6:]))
	if usaCount < 1 || usaOffset+usaCount*2 > len(rec) || (usaCount-1)*fixupStride > len(rec) {
		return errBadRecord
	}
	usn := rec[usaOffset : usaOffset+2]
	for i := 1; i < usaCount; i++ {
		end := i * fixupStride
		if rec[end-2] != usn[0] || rec[end-1] != usn[1] {
			return errBadFixup
		}
		rec[end-2] = rec[usaOffset+i*2]
		rec[end-1] = rec[usaOffset+i*2+1]
	}
	return nil
}

// record is what the scan needs from one file record.
type record struct {
	inUse     bool
	directory bool
	seq       uint16
	baseRef   uint64
	// names are the $FILE_NAME attributes that are not DOS-only names.
	names []fileName
	// dataOnDisk is the size on disk of every $DATA stream whose first segment is in this record; index allocation is the
	// size of a folder's index.
	dataOnDisk      uint64
	indexAllocation uint64
	modified        time.Time
	hasAttrList     bool
	// dataRuns are the runs of the unnamed $DATA stream in this record, with the first VCN they start at; used for $MFT only.
	dataRuns []run
	dataSize uint64
	hasData  bool
	// attrList is the resident value of $ATTRIBUTE_LIST; used for $MFT only.
	attrList []byte
}

type fileName struct {
	parent   uint64
	name     string
	modified time.Time
}

// parseRecord reads a record whose fixups were applied.
func parseRecord(rec []byte) (record, error) {
	var r record
	if len(rec) < 0x30 || string(rec[0:4]) != "FILE" {
		return r, errNotRecord
	}
	flags := binary.LittleEndian.Uint16(rec[0x16:])
	r.inUse = flags&recordInUse != 0
	r.directory = flags&recordDirectory != 0
	r.seq = binary.LittleEndian.Uint16(rec[0x10:])
	r.baseRef = binary.LittleEndian.Uint64(rec[0x20:])
	if !r.inUse {
		return r, nil
	}
	used := int(binary.LittleEndian.Uint32(rec[0x18:]))
	if used > len(rec) || used < 0x30 {
		return r, errBadRecord
	}
	off := int(binary.LittleEndian.Uint16(rec[0x14:]))
	for {
		if off+8 > used {
			return r, errBadRecord
		}
		typ := binary.LittleEndian.Uint32(rec[off:])
		if typ == attrEnd {
			return r, nil
		}
		length := int(binary.LittleEndian.Uint32(rec[off+4:]))
		if length < 0x18 || off+length > used {
			return r, errBadRecord
		}
		if err := r.parseAttribute(rec[off:off+length], typ); err != nil {
			return r, err
		}
		off += length
	}
}

func (r *record) parseAttribute(a []byte, typ uint32) error {
	nonResident := a[8] != 0
	nameLength := int(a[9])
	switch typ {
	case attrStandardInformation:
		if value, ok := residentValue(a); ok && len(value) >= 16 {
			r.modified = filetime(binary.LittleEndian.Uint64(value[8:]))
		}
	case attrAttributeList:
		r.hasAttrList = true
		if value, ok := residentValue(a); ok {
			r.attrList = value
		}
	case attrFileName:
		value, ok := residentValue(a)
		if !ok || len(value) < 66 {
			return errBadRecord
		}
		length := int(value[64])
		if 66+length*2 > len(value) {
			return errBadRecord
		}
		if value[65] == namespaceDOS {
			return nil
		}
		units := make([]uint16, length)
		for i := range units {
			units[i] = binary.LittleEndian.Uint16(value[66+i*2:])
		}
		r.names = append(r.names, fileName{
			parent:   binary.LittleEndian.Uint64(value[0:]),
			name:     string(utf16.Decode(units)),
			modified: filetime(binary.LittleEndian.Uint64(value[16:])),
		})
	case attrData, attrIndexAllocation:
		if !nonResident {
			// Resident data lives inside the record, which is counted as part of $MFT.
			if typ == attrData && nameLength == 0 {
				r.hasData = true
				if value, ok := residentValue(a); ok {
					r.dataSize = uint64(len(value))
				}
			}
			return nil
		}
		if len(a) < 0x40 {
			return errBadRecord
		}
		startVCN := binary.LittleEndian.Uint64(a[0x10:])
		if typ == attrData && nameLength == 0 {
			runOffset := int(binary.LittleEndian.Uint16(a[0x20:]))
			if runOffset > len(a) {
				return errBadRecord
			}
			runs, err := decodeRuns(a[runOffset:])
			if err != nil {
				return err
			}
			r.hasData = true
			r.dataRuns = append(r.dataRuns, runs...)
			if startVCN == 0 {
				r.dataSize = binary.LittleEndian.Uint64(a[0x30:])
			}
		}
		// Only the first segment of a stream carries its sizes; later segments in extension records carry zeros.
		if startVCN != 0 {
			return nil
		}
		onDisk := nonResidentOnDisk(a)
		if typ == attrIndexAllocation {
			r.indexAllocation += onDisk
		} else {
			r.dataOnDisk += onDisk
		}
	}
	return nil
}

// nonResidentOnDisk is the allocated size of a non-resident attribute, or its total allocated size when it is compressed or
// sparse (the field only exists then, which the run list offset shows).
func nonResidentOnDisk(a []byte) uint64 {
	flags := binary.LittleEndian.Uint16(a[0x0C:])
	runOffset := int(binary.LittleEndian.Uint16(a[0x20:]))
	const compressed, sparse = 0x0001, 0x8000
	if flags&(compressed|sparse) != 0 && runOffset >= 0x48 && len(a) >= 0x48 {
		return binary.LittleEndian.Uint64(a[0x40:])
	}
	return binary.LittleEndian.Uint64(a[0x28:])
}

func residentValue(a []byte) ([]byte, bool) {
	if a[8] != 0 || len(a) < 0x18 {
		return nil, false
	}
	length := int(binary.LittleEndian.Uint32(a[0x10:]))
	offset := int(binary.LittleEndian.Uint16(a[0x14:]))
	if offset+length > len(a) || offset < 0x18 {
		return nil, false
	}
	return a[offset : offset+length], true
}

// run is one extent of a non-resident stream. A sparse run has no clusters on disk.
type run struct {
	lcn    int64
	length uint64
	sparse bool
}

// decodeRuns decodes a run list: a header byte with the size of the length (low nibble) and of the signed LCN delta (high
// nibble), then both little-endian; no delta means a sparse run, and a zero header ends the list.
func decodeRuns(b []byte) ([]run, error) {
	var runs []run
	var lcn int64
	for i := 0; i < len(b); {
		header := b[i]
		if header == 0 {
			return runs, nil
		}
		lenSize := int(header & 0x0F)
		offSize := int(header >> 4)
		if lenSize == 0 || lenSize > 8 || offSize > 8 || i+1+lenSize+offSize > len(b) {
			return nil, errBadRunList
		}
		var length uint64
		for k := lenSize - 1; k >= 0; k-- {
			length = length<<8 | uint64(b[i+1+k])
		}
		if offSize == 0 {
			runs = append(runs, run{length: length, sparse: true})
		} else {
			var delta int64
			for k := offSize - 1; k >= 0; k-- {
				delta = delta<<8 | int64(b[i+1+lenSize+k])
			}
			// Sign-extend.
			shift := uint(64 - 8*offSize)
			delta = delta << shift >> shift
			lcn += delta
			if lcn < 0 {
				return nil, errBadRunList
			}
			runs = append(runs, run{lcn: lcn, length: length})
		}
		i += 1 + lenSize + offSize
	}
	return nil, errBadRunList
}

// attrListEntry is one entry of an $ATTRIBUTE_LIST: where a segment of an attribute lives.
type attrListEntry struct {
	typ      uint32
	startVCN uint64
	ref      uint64
	named    bool
}

func parseAttrList(b []byte) ([]attrListEntry, error) {
	var out []attrListEntry
	for off := 0; off+0x1A <= len(b); {
		length := int(binary.LittleEndian.Uint16(b[off+4:]))
		if length < 0x1A || off+length > len(b) {
			return nil, errBadRecord
		}
		out = append(out, attrListEntry{
			typ:      binary.LittleEndian.Uint32(b[off:]),
			named:    b[off+6] != 0,
			startVCN: binary.LittleEndian.Uint64(b[off+8:]),
			ref:      binary.LittleEndian.Uint64(b[off+0x10:]),
		})
		off += length
	}
	return out, nil
}

// filetime converts a Windows FILETIME (100 ns since 1601) to a time; zero stays zero.
func filetime(ft uint64) time.Time {
	if ft == 0 {
		return time.Time{}
	}
	const epochDiff = 116444736000000000 // 1601 to 1970 in 100 ns
	if ft < epochDiff {
		return time.Time{}
	}
	d := ft - epochDiff
	return time.Unix(int64(d/10000000), int64(d%10000000)*100).UTC()
}

// mftCollector turns parsed records into a tree. Folders are keyed by their full reference (record and sequence number), so a
// file whose parent reference is stale ends up below a folder that is never filled, and is attached to the root.
type mftCollector struct {
	t *tree
	// dirs maps a folder's full reference to its index in the tree.
	dirs map[uint64]int32
	// pending holds files whose attributes are spread over extension records; they are completed after the last record.
	pending map[uint64]*pendingFile
	// filled marks folders whose own record was read.
	filled  map[int32]bool
	corrupt int
	records int
}

type pendingFile struct {
	directory bool
	isBase    bool
	seq       uint16
	names     []fileName
	onDisk    uint64
	modified  time.Time
}

func newMFTCollector(t *tree) *mftCollector {
	c := &mftCollector{t: t, dirs: map[uint64]int32{}, pending: map[uint64]*pendingFile{}, filled: map[int32]bool{}}
	return c
}

// dirIndex returns the tree index of a folder reference, creating a placeholder below the root when it is new.
func (c *mftCollector) dirIndex(ref uint64) int32 {
	record, _ := fileRef(ref)
	if record == rootRecord {
		return 0
	}
	if i, ok := c.dirs[ref]; ok {
		return i
	}
	i := c.t.addDir(0, "")
	c.dirs[ref] = i
	return i
}

// add takes one record by its number.
func (c *mftCollector) add(number uint64, rec []byte) {
	if err := applyFixups(rec); err != nil {
		if err != errNotRecord {
			c.corrupt++
		}
		return
	}
	r, err := parseRecord(rec)
	if err != nil {
		c.corrupt++
		return
	}
	if !r.inUse {
		return
	}
	c.records++
	baseRecord, _ := fileRef(r.baseRef)
	if baseRecord != 0 {
		// An extension record: its attributes belong to the base record.
		p := c.pendingFor(baseRecord)
		p.names = append(p.names, r.names...)
		p.onDisk += r.dataOnDisk + r.indexAllocation
		return
	}
	if number == badClusRecord {
		return
	}
	if r.hasAttrList {
		p := c.pendingFor(number)
		p.isBase = true
		p.directory = r.directory
		p.seq = r.seq
		p.names = append(r.names, p.names...)
		p.onDisk += r.dataOnDisk + r.indexAllocation
		p.modified = r.modified
		return
	}
	c.place(number, r.seq, r.directory, r.names, r.dataOnDisk+r.indexAllocation, r.modified)
}

func (c *mftCollector) pendingFor(number uint64) *pendingFile {
	p := c.pending[number]
	if p == nil {
		p = &pendingFile{}
		c.pending[number] = p
	}
	return p
}

// place puts a complete file or folder in the tree. A file with several names (hard links) is counted once, under its first.
func (c *mftCollector) place(number uint64, seq uint16, directory bool, names []fileName, onDisk uint64, modified time.Time) {
	if number < firstUserRecord && number != rootRecord && number != extendRecord {
		// A metafile ($MFT, $LogFile, ...): counted on the root, never listed.
		c.t.dirs[0].size += onDisk
		return
	}
	if number == rootRecord {
		c.t.dirs[0].size += onDisk
		return
	}
	if len(names) == 0 {
		// No usable name (only a DOS name, or corrupt): count the space on the root.
		c.t.dirs[0].size += onDisk
		return
	}
	name := names[0]
	if modified.IsZero() {
		modified = name.modified
	}
	parent := c.dirIndex(name.parent)
	if directory {
		self := c.dirIndex(uint64(seq)<<48 | number)
		d := &c.t.dirs[self]
		d.parent = parent
		d.name = name.name
		d.size += onDisk
		d.hidden = number == extendRecord
		c.filled[self] = true
		return
	}
	c.t.addFile(parent, name.name, onDisk, modified)
}

// finish completes the files spread over extension records and attaches folders that were never read to the root.
func (c *mftCollector) finish() {
	for number, p := range c.pending {
		if !p.isBase {
			// Extension records without a base record in use: count their space on the root.
			c.t.dirs[0].size += p.onDisk
			continue
		}
		c.place(number, p.seq, p.directory, p.names, p.onDisk, p.modified)
	}
	c.pending = nil
	for _, i := range c.dirs {
		if !c.filled[i] {
			d := &c.t.dirs[i]
			d.parent = 0
			if d.name == "" {
				d.name = "(unknown folder)"
			}
		}
	}
}

// vcnOffset maps a byte offset in the stream to a byte offset on the volume.
func vcnOffset(runs []run, streamOffset, clusterBytes uint64) (uint64, bool) {
	vcn := streamOffset / clusterBytes
	for _, r := range runs {
		if vcn < r.length {
			if r.sparse {
				return 0, false
			}
			return (uint64(r.lcn)+vcn)*clusterBytes + streamOffset%clusterBytes, true
		}
		vcn -= r.length
	}
	return 0, false
}

func alignUp(n, to uint64) uint64 { return (n + to - 1) / to * to }
