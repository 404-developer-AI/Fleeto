package storage

import (
	"encoding/binary"
	"testing"
	"time"
	"unicode/utf16"
)

const testRecordBytes = 1024

// recordBuilder writes synthetic MFT records the way NTFS lays them out.
type recordBuilder struct {
	flags   uint16
	seq     uint16
	baseRef uint64
	attrs   [][]byte
}

// resident adds a resident attribute with the value at offset 0x18.
func (b *recordBuilder) resident(typ uint32, value []byte) {
	a := make([]byte, 0x18, 0x18+len(value)+8)
	binary.LittleEndian.PutUint32(a[0:], typ)
	binary.LittleEndian.PutUint32(a[0x10:], uint32(len(value)))
	binary.LittleEndian.PutUint16(a[0x14:], 0x18)
	a = append(a, value...)
	for len(a)%8 != 0 {
		a = append(a, 0)
	}
	binary.LittleEndian.PutUint32(a[4:], uint32(len(a)))
	b.attrs = append(b.attrs, a)
}

// nonResidentData adds a non-resident $DATA segment. With sparse set the header has the compressed size field.
func (b *recordBuilder) nonResidentData(typ uint32, named bool, startVCN, allocated, dataSize, compressed uint64, sparse bool, runs []byte) {
	a := make([]byte, 0x48)
	binary.LittleEndian.PutUint32(a[0:], typ)
	a[8] = 1
	if named {
		a[9] = 2
	}
	if sparse {
		binary.LittleEndian.PutUint16(a[0x0C:], 0x8000)
	}
	binary.LittleEndian.PutUint64(a[0x10:], startVCN)
	binary.LittleEndian.PutUint16(a[0x20:], 0x48)
	binary.LittleEndian.PutUint64(a[0x28:], allocated)
	binary.LittleEndian.PutUint64(a[0x30:], dataSize)
	binary.LittleEndian.PutUint64(a[0x40:], compressed)
	a = append(a, runs...)
	a = append(a, 0)
	for len(a)%8 != 0 {
		a = append(a, 0)
	}
	binary.LittleEndian.PutUint32(a[4:], uint32(len(a)))
	b.attrs = append(b.attrs, a)
}

func fileNameValue(parent uint64, name string, namespace byte) []byte {
	units := utf16.Encode([]rune(name))
	v := make([]byte, 66+len(units)*2)
	binary.LittleEndian.PutUint64(v[0:], parent)
	binary.LittleEndian.PutUint64(v[16:], toFiletime(time.Date(2026, 1, 2, 3, 4, 5, 0, time.UTC)))
	v[64] = byte(len(units))
	v[65] = namespace
	for i, u := range units {
		binary.LittleEndian.PutUint16(v[66+i*2:], u)
	}
	return v
}

func standardInformation(modified time.Time) []byte {
	v := make([]byte, 48)
	binary.LittleEndian.PutUint64(v[8:], toFiletime(modified))
	return v
}

func toFiletime(t time.Time) uint64 {
	return uint64(t.UnixNano()/100) + 116444736000000000
}

// build lays out the record with an update sequence array of 3 entries (two 512-byte blocks) and protects it.
func (b *recordBuilder) build() []byte {
	rec := make([]byte, testRecordBytes)
	copy(rec, "FILE")
	binary.LittleEndian.PutUint16(rec[4:], 0x30) // usa offset
	binary.LittleEndian.PutUint16(rec[6:], 3)    // usa count: usn + 2 blocks
	binary.LittleEndian.PutUint16(rec[0x10:], b.seq)
	binary.LittleEndian.PutUint16(rec[0x14:], 0x38)
	binary.LittleEndian.PutUint16(rec[0x16:], b.flags)
	binary.LittleEndian.PutUint64(rec[0x20:], b.baseRef)
	off := 0x38
	for _, a := range b.attrs {
		copy(rec[off:], a)
		off += len(a)
	}
	binary.LittleEndian.PutUint32(rec[off:], attrEnd)
	off += 8
	binary.LittleEndian.PutUint32(rec[0x18:], uint32(off))
	binary.LittleEndian.PutUint32(rec[0x1C:], testRecordBytes)
	// Protect: move the last two bytes of each block into the array and write the sequence number there.
	usn := []byte{0x07, 0x00}
	copy(rec[0x30:], usn)
	for i := 1; i <= 2; i++ {
		end := i * fixupStride
		copy(rec[0x30+i*2:], rec[end-2:end])
		copy(rec[end-2:], usn)
	}
	return rec
}

func ref(record uint64, seq uint16) uint64 { return uint64(seq)<<48 | record }

func TestApplyFixupsRestoresBlockEnds(t *testing.T) {
	b := &recordBuilder{flags: recordInUse, seq: 1}
	rec := b.build()
	rec[0x30+2] = 0xAB // the saved bytes of block 1
	rec[0x30+3] = 0xCD
	if err := applyFixups(rec); err != nil {
		t.Fatal(err)
	}
	if rec[510] != 0xAB || rec[511] != 0xCD {
		t.Fatalf("block end not restored: % x", rec[510:512])
	}
	torn := b.build()
	torn[1023] = 0x99
	if err := applyFixups(torn); err != errBadFixup {
		t.Fatalf("a torn record must fail, got %v", err)
	}
	if err := applyFixups(make([]byte, testRecordBytes)); err != errNotRecord {
		t.Fatalf("an empty record is not a record, got %v", err)
	}
}

func TestDecodeRunsHandlesNegativeDeltasAndSparseRuns(t *testing.T) {
	// 0x21: length 1 byte, offset 2 bytes. Run 1: 16 clusters at LCN 0x1000. Run 2: sparse 8 clusters. Run 3: 4 clusters at
	// delta -0x800 (0xF800), so LCN 0x800.
	runs, err := decodeRuns([]byte{0x21, 0x10, 0x00, 0x10, 0x01, 0x08, 0x21, 0x04, 0x00, 0xF8, 0x00})
	if err != nil {
		t.Fatal(err)
	}
	want := []run{{lcn: 0x1000, length: 16}, {length: 8, sparse: true}, {lcn: 0x800, length: 4}}
	if len(runs) != len(want) {
		t.Fatalf("runs = %+v", runs)
	}
	for i := range want {
		if runs[i] != want[i] {
			t.Fatalf("run %d = %+v, want %+v", i, runs[i], want[i])
		}
	}
	if _, err := decodeRuns([]byte{0x21, 0x10}); err == nil {
		t.Fatal("a truncated run list must fail")
	}
	if _, err := decodeRuns([]byte{0x11, 0x01, 0x80, 0x00}); err == nil {
		t.Fatal("a run before cluster 0 must fail")
	}
}

func TestVCNOffsetFollowsRuns(t *testing.T) {
	runs := []run{{lcn: 100, length: 2}, {length: 1, sparse: true}, {lcn: 50, length: 4}}
	if off, ok := vcnOffset(runs, 4096+10, 4096); !ok || off != 101*4096+10 {
		t.Fatalf("offset in the first run = %d %v", off, ok)
	}
	if _, ok := vcnOffset(runs, 2*4096, 4096); ok {
		t.Fatal("a sparse cluster has no offset")
	}
	if off, ok := vcnOffset(runs, 4*4096, 4096); !ok || off != 51*4096 {
		t.Fatalf("offset in the last run = %d %v", off, ok)
	}
	if _, ok := vcnOffset(runs, 7*4096, 4096); ok {
		t.Fatal("past the end has no offset")
	}
}

func TestParseRecordReadsNamesSizesAndTimes(t *testing.T) {
	modified := time.Date(2026, 9, 1, 12, 0, 0, 0, time.UTC)
	b := &recordBuilder{flags: recordInUse, seq: 3}
	b.resident(attrStandardInformation, standardInformation(modified))
	b.resident(attrFileName, fileNameValue(ref(rootRecord, 5), "REPORT~1.PDF", namespaceDOS))
	b.resident(attrFileName, fileNameValue(ref(rootRecord, 5), "Report 2026.pdf", 1))
	b.nonResidentData(attrData, false, 0, 8192, 5000, 0, false, []byte{0x11, 0x02, 0x10})
	b.nonResidentData(attrData, true, 0, 4096, 100, 0, false, []byte{0x11, 0x01, 0x20})
	rec := b.build()
	if err := applyFixups(rec); err != nil {
		t.Fatal(err)
	}
	r, err := parseRecord(rec)
	if err != nil {
		t.Fatal(err)
	}
	if !r.inUse || r.directory || r.seq != 3 {
		t.Fatalf("flags: %+v", r)
	}
	if len(r.names) != 1 || r.names[0].name != "Report 2026.pdf" {
		t.Fatalf("the DOS name must be left out: %+v", r.names)
	}
	if !r.modified.Equal(modified) {
		t.Fatalf("modified = %v", r.modified)
	}
	// Both streams count on disk: 8192 + 4096.
	if r.dataOnDisk != 12288 {
		t.Fatalf("on disk = %d", r.dataOnDisk)
	}
}

func TestSparseStreamCountsItsAllocatedClustersOnly(t *testing.T) {
	b := &recordBuilder{flags: recordInUse, seq: 1}
	b.resident(attrFileName, fileNameValue(ref(rootRecord, 5), "disk.vhdx", 1))
	b.nonResidentData(attrData, false, 0, 100<<30, 100<<30, 3<<30, true, []byte{0x11, 0x01, 0x10})
	rec := b.build()
	_ = applyFixups(rec)
	r, err := parseRecord(rec)
	if err != nil {
		t.Fatal(err)
	}
	if r.dataOnDisk != 3<<30 {
		t.Fatalf("a sparse file takes its allocated clusters, got %d", r.dataOnDisk)
	}
}

func TestLaterSegmentsDoNotCountTwice(t *testing.T) {
	b := &recordBuilder{flags: recordInUse, seq: 1, baseRef: ref(40, 1)}
	b.nonResidentData(attrData, false, 1000, 0, 0, 0, false, []byte{0x11, 0x01, 0x10})
	rec := b.build()
	_ = applyFixups(rec)
	r, _ := parseRecord(rec)
	if r.dataOnDisk != 0 {
		t.Fatalf("a segment after the first carries no sizes, got %d", r.dataOnDisk)
	}
}

func TestParseRecordRejectsCorruptAttributes(t *testing.T) {
	b := &recordBuilder{flags: recordInUse, seq: 1}
	b.resident(attrFileName, fileNameValue(ref(rootRecord, 5), "a.txt", 1))
	rec := b.build()
	_ = applyFixups(rec)
	// An attribute length that runs past the used size.
	binary.LittleEndian.PutUint32(rec[0x38+4:], 0x7000)
	if _, err := parseRecord(rec); err == nil {
		t.Fatal("a corrupt attribute length must fail")
	}
}

func TestParseAttrList(t *testing.T) {
	entry := func(typ uint32, vcn, reference uint64) []byte {
		e := make([]byte, 0x20)
		binary.LittleEndian.PutUint32(e[0:], typ)
		binary.LittleEndian.PutUint16(e[4:], 0x20)
		binary.LittleEndian.PutUint64(e[8:], vcn)
		binary.LittleEndian.PutUint64(e[0x10:], reference)
		return e
	}
	list := append(entry(attrData, 0, ref(0, 1)), entry(attrData, 5000, ref(17, 1))...)
	entries, err := parseAttrList(list)
	if err != nil || len(entries) != 2 || entries[1].startVCN != 5000 {
		t.Fatalf("entries = %+v, %v", entries, err)
	}
	if n, _ := fileRef(entries[1].ref); n != 17 {
		t.Fatalf("record = %d", n)
	}
}

// collect runs a set of records through the collector and returns the result.
func collect(t *testing.T, records map[uint64][]byte) (Result, *mftCollector) {
	t.Helper()
	tr := newTree(`C:\`, `\`)
	c := newMFTCollector(tr)
	for n := uint64(0); n < 200; n++ {
		if rec, ok := records[n]; ok {
			c.add(n, rec)
		}
	}
	c.finish()
	return tr.result(), c
}

func dirRecord(seq uint16, parent uint64, name string) []byte {
	b := &recordBuilder{flags: recordInUse | recordDirectory, seq: seq}
	b.resident(attrFileName, fileNameValue(parent, name, 3))
	b.nonResidentData(attrIndexAllocation, true, 0, 4096, 4096, 0, false, []byte{0x11, 0x01, 0x30})
	return b.build()
}

func fileRecord(seq uint16, parent uint64, name string, onDisk uint64) []byte {
	b := &recordBuilder{flags: recordInUse, seq: seq}
	b.resident(attrStandardInformation, standardInformation(time.Date(2026, 9, 1, 0, 0, 0, 0, time.UTC)))
	b.resident(attrFileName, fileNameValue(parent, name, 1))
	b.nonResidentData(attrData, false, 0, onDisk, onDisk, 0, false, []byte{0x11, 0x01, 0x40})
	return b.build()
}

func TestCollectorBuildsTheTreeFromRecords(t *testing.T) {
	root := ref(rootRecord, rootRecord)
	records := map[uint64][]byte{
		// $MFT itself: counted on the root, never listed.
		0:  fileRecord(1, root, "$MFT", 1<<20),
		8:  fileRecord(8, root, "$BadClus", 1<<40),
		11: dirRecord(11, root, "$Extend"),
		30: dirRecord(1, root, "Users"),
		31: dirRecord(1, ref(30, 1), "Public"),
		// A file whose parent comes later in the table.
		32: fileRecord(1, ref(33, 2), "big.iso", 5<<20),
		33: dirRecord(2, ref(31, 1), "Downloads"),
		34: fileRecord(1, ref(11, 11), "$UsnJrnl", 50<<20),
		35: fileRecord(1, ref(30, 1), "notes.txt", 4096),
	}
	res, c := collect(t, records)
	if c.corrupt != 0 {
		t.Fatalf("corrupt = %d", c.corrupt)
	}
	byPath := map[string]Folder{}
	for _, f := range res.Folders {
		byPath[f.Path] = f
	}
	if res.Folders[0].Path != `C:\` {
		t.Fatalf("the root comes first: %+v", res.Folders[0])
	}
	if _, listed := byPath[`C:\$Extend`]; listed {
		t.Fatal("$Extend must not be listed")
	}
	downloads, ok := byPath[`C:\Users\Public\Downloads`]
	if !ok || downloads.SizeBytes != 5<<20+4096 || downloads.FileCount != 1 {
		t.Fatalf("Downloads = %+v (%v)", downloads, ok)
	}
	users := byPath[`C:\Users`]
	if users.SizeBytes != 5<<20+4096*3+4096 || users.FileCount != 2 || users.FolderCount != 2 {
		t.Fatalf("Users = %+v", users)
	}
	// Root: $MFT, $Extend with its journal, the index of the root folders and Users; $BadClus never.
	wantRoot := uint64(1<<20) + (50<<20 + 4096) + users.SizeBytes
	if res.Folders[0].SizeBytes != wantRoot {
		t.Fatalf("root = %d, want %d", res.Folders[0].SizeBytes, wantRoot)
	}
	if len(res.Files) != 2 || res.Files[0].Path != `C:\Users\Public\Downloads\big.iso` || res.Files[1].Path != `C:\Users\notes.txt` {
		t.Fatalf("files = %+v", res.Files)
	}
	if res.Files[0].ModifiedAt.IsZero() {
		t.Fatal("a file keeps its modification time")
	}
}

func TestCollectorCountsHardLinksOnceAndMergesExtensionRecords(t *testing.T) {
	root := ref(rootRecord, rootRecord)
	// A file with two names (hard links) in two folders.
	linked := &recordBuilder{flags: recordInUse, seq: 1}
	linked.resident(attrFileName, fileNameValue(ref(40, 1), "a.dll", 1))
	linked.resident(attrFileName, fileNameValue(ref(41, 1), "a.dll", 1))
	linked.nonResidentData(attrData, false, 0, 1<<20, 1<<20, 0, false, []byte{0x11, 0x01, 0x10})
	// A fragmented file: the base record has the attribute list and the name, the extension record has the data.
	base := &recordBuilder{flags: recordInUse, seq: 4}
	base.resident(attrAttributeList, make([]byte, 0x20))
	base.resident(attrFileName, fileNameValue(ref(40, 1), "fragmented.bin", 1))
	ext := &recordBuilder{flags: recordInUse, seq: 1, baseRef: ref(50, 4)}
	ext.nonResidentData(attrData, false, 0, 7<<20, 7<<20, 0, false, []byte{0x11, 0x01, 0x10})
	records := map[uint64][]byte{
		40: dirRecord(1, root, "A"),
		41: dirRecord(1, root, "B"),
		42: linked.build(),
		// The extension record comes before its base.
		45: ext.build(),
		50: base.build(),
	}
	res, _ := collect(t, records)
	byPath := map[string]Folder{}
	for _, f := range res.Folders {
		byPath[f.Path] = f
	}
	if a := byPath[`C:\A`]; a.SizeBytes != 1<<20+7<<20+4096 || a.FileCount != 2 {
		t.Fatalf("A = %+v", a)
	}
	if b := byPath[`C:\B`]; b.SizeBytes != 4096 || b.FileCount != 0 {
		t.Fatalf("the second name of a hard link must not count again: B = %+v", b)
	}
	if res.Files[0].Path != `C:\A\fragmented.bin` || res.Files[0].SizeBytes != 7<<20 {
		t.Fatalf("files = %+v", res.Files)
	}
}

func TestCollectorAttachesFilesWithAStaleParentToTheRoot(t *testing.T) {
	root := ref(rootRecord, rootRecord)
	records := map[uint64][]byte{
		40: dirRecord(2, root, "Current"),
		// Points at sequence 1 of record 40, which was reused.
		41: fileRecord(1, ref(40, 1), "orphan.dat", 1<<20),
	}
	res, _ := collect(t, records)
	byPath := map[string]Folder{}
	for _, f := range res.Folders {
		byPath[f.Path] = f
	}
	if byPath[`C:\Current`].SizeBytes != 4096 {
		t.Fatalf("the reused folder must not get the orphan: %+v", byPath[`C:\Current`])
	}
	if res.Folders[0].SizeBytes != 1<<20+4096 {
		t.Fatalf("the orphan still counts on the volume: %+v", res.Folders[0])
	}
}

func TestCollectorCountsCorruptRecords(t *testing.T) {
	good := fileRecord(1, ref(rootRecord, rootRecord), "ok.txt", 4096)
	torn := fileRecord(1, ref(rootRecord, rootRecord), "torn.txt", 4096)
	torn[1023] ^= 0xFF
	_, c := collect(t, map[uint64][]byte{30: good, 31: torn, 32: make([]byte, testRecordBytes)})
	if c.corrupt != 1 || c.records != 1 {
		t.Fatalf("corrupt = %d, records = %d", c.corrupt, c.records)
	}
}
