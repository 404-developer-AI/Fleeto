//go:build windows

package storage

import (
	"context"
	"errors"
	"fmt"
	"unsafe"

	"golang.org/x/sys/windows"
)

// mftChunkBytes is how much of the master file table one read takes.
const mftChunkBytes = 4 << 20

// ntfsVolumeData is the start of NTFS_VOLUME_DATA_BUFFER.
type ntfsVolumeData struct {
	VolumeSerialNumber           int64
	NumberSectors                int64
	TotalClusters                int64
	FreeClusters                 int64
	TotalReserved                int64
	BytesPerSector               uint32
	BytesPerCluster              uint32
	BytesPerFileRecordSegment    uint32
	ClustersPerFileRecordSegment uint32
	MftValidDataLength           int64
	MftStartLcn                  int64
	Mft2StartLcn                 int64
	MftZoneStart                 int64
	MftZoneEnd                   int64
}

// volumeReader reads the raw volume at cluster-aligned offsets.
type volumeReader struct {
	h            windows.Handle
	clusterBytes uint64
}

func (v *volumeReader) readAt(buf []byte, offset uint64) error {
	var ov windows.Overlapped
	ov.Offset = uint32(offset)
	ov.OffsetHigh = uint32(offset >> 32)
	var n uint32
	if err := windows.ReadFile(v.h, buf, &n, &ov); err != nil {
		return err
	}
	if int(n) != len(buf) {
		return fmt.Errorf("short read of %d of %d bytes", n, len(buf))
	}
	return nil
}

// scanMFT reads the master file table of an NTFS volume ("C:") into t. It needs administrator rights.
func scanMFT(ctx context.Context, volume string, t *tree) (incomplete string, err error) {
	path, err := windows.UTF16PtrFromString(`\\.\` + volume)
	if err != nil {
		return "", err
	}
	h, err := windows.CreateFile(path, windows.GENERIC_READ, windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE|windows.FILE_SHARE_DELETE,
		nil, windows.OPEN_EXISTING, 0, 0)
	if err != nil {
		return "", fmt.Errorf("open the volume: %w", err)
	}
	defer windows.CloseHandle(h)

	var data ntfsVolumeData
	var returned uint32
	if err := windows.DeviceIoControl(h, windows.FSCTL_GET_NTFS_VOLUME_DATA, nil, 0, (*byte)(unsafe.Pointer(&data)),
		uint32(unsafe.Sizeof(data)), &returned, nil); err != nil {
		return "", fmt.Errorf("read the NTFS volume data: %w", err)
	}
	recordBytes := uint64(data.BytesPerFileRecordSegment)
	clusterBytes := uint64(data.BytesPerCluster)
	if recordBytes < 1024 || recordBytes > 65536 || clusterBytes == 0 || data.MftStartLcn <= 0 || data.MftValidDataLength <= 0 {
		return "", errors.New("the NTFS volume data is not usable")
	}
	v := &volumeReader{h: h, clusterBytes: clusterBytes}

	runs, err := mftRuns(v, uint64(data.MftStartLcn)*clusterBytes, recordBytes)
	if err != nil {
		return "", err
	}
	total := uint64(data.MftValidDataLength) / recordBytes
	c := newMFTCollector(t)
	chunk := make([]byte, alignUp(mftChunkBytes, clusterBytes))
	var number uint64
	for _, r := range runs {
		if number >= total {
			break
		}
		runBytes := r.length * clusterBytes
		for done := uint64(0); done < runBytes && number < total; {
			if err := ctx.Err(); err != nil {
				c.finish()
				return "", err
			}
			n := min(uint64(len(chunk)), runBytes-done)
			buf := chunk[:n]
			if r.sparse {
				// A sparse part of $MFT holds no records.
				number += n / recordBytes
				done += n
				continue
			}
			if err := v.readAt(buf, uint64(r.lcn)*clusterBytes+done); err != nil {
				c.finish()
				return "", fmt.Errorf("read the master file table: %w", err)
			}
			for off := uint64(0); off+recordBytes <= n && number < total; off += recordBytes {
				c.add(number, buf[off:off+recordBytes])
				number++
			}
			done += n
		}
	}
	c.finish()
	if c.corrupt > 0 && c.corrupt*1000 > c.records {
		incomplete = fmt.Sprintf("%d records of the master file table could not be read", c.corrupt)
	}
	return incomplete, nil
}

// mftRuns reads record 0 ($MFT) and returns the runs of its data, including segments in extension records when the table is
// so fragmented that it needs an attribute list.
func mftRuns(v *volumeReader, mftOffset, recordBytes uint64) ([]run, error) {
	readRecord := func(runs []run, number uint64) ([]byte, error) {
		offset, ok := vcnOffset(runs, number*recordBytes, v.clusterBytes)
		if !ok {
			return nil, fmt.Errorf("record %d of the master file table is outside its known extents", number)
		}
		return v.readRecord(offset, recordBytes)
	}
	rec, err := v.readRecord(mftOffset, recordBytes)
	if err != nil {
		return nil, fmt.Errorf("read the first record of the master file table: %w", err)
	}
	if err := applyFixups(rec); err != nil {
		return nil, fmt.Errorf("the first record of the master file table: %w", err)
	}
	r, err := parseRecord(rec)
	if err != nil || !r.inUse || len(r.dataRuns) == 0 {
		return nil, errors.New("the first record of the master file table has no data runs")
	}
	runs := r.dataRuns
	if !r.hasAttrList {
		return runs, nil
	}
	entries, err := parseAttrList(r.attrList)
	if err != nil {
		return nil, fmt.Errorf("the attribute list of the master file table: %w", err)
	}
	for _, e := range entries {
		number, _ := fileRef(e.ref)
		if e.typ != attrData || e.named || number == 0 {
			continue
		}
		ext, err := readRecord(runs, number)
		if err != nil {
			return nil, err
		}
		if err := applyFixups(ext); err != nil {
			return nil, err
		}
		er, err := parseRecord(ext)
		if err != nil {
			return nil, err
		}
		runs = append(runs, er.dataRuns...)
	}
	return runs, nil
}

func (v *volumeReader) readRecord(offset, recordBytes uint64) ([]byte, error) {
	// Reads of a raw volume must be sector aligned; a cluster is.
	start := offset / v.clusterBytes * v.clusterBytes
	buf := make([]byte, alignUp(offset-start+recordBytes, v.clusterBytes))
	if err := v.readAt(buf, start); err != nil {
		return nil, err
	}
	return buf[offset-start : offset-start+recordBytes], nil
}
