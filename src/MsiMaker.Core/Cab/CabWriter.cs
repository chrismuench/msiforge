using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MsiMaker.Core.Cab;

public sealed record CabFile(string Name, byte[] Data, DateTime Modified)
{
    /// <summary>MS-DOS file attributes. 0x20 is _A_ARCH, which is what real cabinets carry.</summary>
    public int Attributes { get; init; } = 0x20;
}

/// <summary>
/// Writes MS-CAB cabinets, optionally MSZIP-compressed. This is what an MSI
/// embeds when its files are compressed, and it is the other half of the format
/// work that WiX delegates to wixnative.exe. Pure managed, so it runs anywhere.
/// </summary>
public static class CabWriter
{
    private const int BlockSize = 32768;      // MSZIP hard limit per data block
    private const ushort CompressNone = 0;
    private const ushort CompressMszip = 1;

    public static byte[] Build(IReadOnlyList<CabFile> files, bool compress = true)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) throw new ArgumentException("A cabinet needs at least one file.");

        // One folder holding every file, concatenated. Folder-relative offsets
        // are what CFFILE.uoffFolderStart points at.
        var folder = new MemoryStream();
        var entries = new List<(CabFile File, uint Offset)>();
        foreach (var f in files)
        {
            entries.Add((f, (uint)folder.Length));
            folder.Write(f.Data);
        }
        byte[] folderData = folder.ToArray();

        // Split into blocks and compress each one independently. MSZIP allows a
        // decompressor to carry history between blocks, but never requires it,
        // so a fresh deflate per block is valid and much simpler to get right.
        var blocks = new List<byte[]>();
        for (int offset = 0; offset < folderData.Length; offset += BlockSize)
        {
            int len = Math.Min(BlockSize, folderData.Length - offset);
            var chunk = folderData.AsSpan(offset, len).ToArray();
            blocks.Add(compress ? Mszip(chunk) : chunk);
        }
        if (blocks.Count == 0) blocks.Add([]);

        // CFFILE entries are variable length because the name is inline.
        var fileEntries = new MemoryStream();
        foreach (var (f, off) in entries)
        {
            byte[] name = Encoding.ASCII.GetBytes(f.Name);
            WriteU32(fileEntries, (uint)f.Data.Length);
            WriteU32(fileEntries, off);
            WriteU16(fileEntries, 0);                       // iFolder: the single folder
            WriteU16(fileEntries, DosDate(f.Modified));
            WriteU16(fileEntries, DosTime(f.Modified));
            WriteU16(fileEntries, (ushort)f.Attributes);
            fileEntries.Write(name);
            fileEntries.WriteByte(0);
        }
        byte[] fileBytes = fileEntries.ToArray();

        const int headerSize = 36;
        const int folderSize = 8;
        int coffFiles = headerSize + folderSize;
        int coffCabStart = coffFiles + fileBytes.Length;

        var data = new MemoryStream();
        for (int i = 0; i < blocks.Count; i++)
        {
            int rawLen = Math.Min(BlockSize, Math.Max(0, folderData.Length - i * BlockSize));
            WriteDataBlock(data, blocks[i], rawLen);
        }
        byte[] dataBytes = data.ToArray();

        uint total = (uint)(coffCabStart + dataBytes.Length);

        var cab = new MemoryStream();
        cab.Write("MSCF"u8);
        WriteU32(cab, 0);                       // reserved1
        WriteU32(cab, total);                   // cbCabinet
        WriteU32(cab, 0);                       // reserved2
        WriteU32(cab, (uint)coffFiles);         // coffFiles
        WriteU32(cab, 0);                       // reserved3
        cab.WriteByte(3);                       // versionMinor
        cab.WriteByte(1);                       // versionMajor
        WriteU16(cab, 1);                       // cFolders
        WriteU16(cab, (ushort)files.Count);     // cFiles
        WriteU16(cab, 0);                       // flags
        WriteU16(cab, 0);                       // setID
        WriteU16(cab, 0);                       // iCabinet

        WriteU32(cab, (uint)coffCabStart);                          // CFFOLDER.coffCabStart
        WriteU16(cab, (ushort)blocks.Count);                        // CFFOLDER.cCFData
        WriteU16(cab, compress ? CompressMszip : CompressNone);     // CFFOLDER.typeCompress

        cab.Write(fileBytes);
        cab.Write(dataBytes);
        return cab.ToArray();
    }

    /// <summary>MSZIP block: the literal signature "CK" followed by raw DEFLATE.</summary>
    private static byte[] Mszip(byte[] raw)
    {
        var ms = new MemoryStream();
        ms.Write("CK"u8);
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw);

        byte[] result = ms.ToArray();
        // A block must never grow past the uncompressed size plus its signature,
        // or the decompressor's fixed 32K window can be overrun.
        return result.Length >= raw.Length + 2 ? Store(raw) : result;
    }

    /// <summary>A stored (uncompressed) DEFLATE stream, still wrapped as MSZIP.</summary>
    private static byte[] Store(byte[] raw)
    {
        var ms = new MemoryStream();
        ms.Write("CK"u8);
        int offset = 0;
        do
        {
            int len = Math.Min(65535, raw.Length - offset);
            bool last = offset + len >= raw.Length;
            ms.WriteByte((byte)(last ? 1 : 0));          // BFINAL, BTYPE=00 (stored)
            WriteU16(ms, (ushort)len);
            WriteU16(ms, (ushort)~len);
            ms.Write(raw, offset, len);
            offset += len;
        } while (offset < raw.Length);
        return ms.ToArray();
    }

    private static void WriteDataBlock(Stream output, byte[] compressed, int uncompressedLength)
    {
        Span<byte> sizes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(sizes, (ushort)compressed.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(sizes[2..], (ushort)uncompressedLength);

        uint checksum = Checksum(compressed, Checksum(sizes, 0));
        WriteU32(output, checksum);
        output.Write(sizes);
        output.Write(compressed);
    }

    /// <summary>
    /// The cabinet checksum: 32-bit little-endian words XOR-folded, with any
    /// trailing 1-3 bytes packed big-end-first into a final word.
    /// </summary>
    private static uint Checksum(ReadOnlySpan<byte> data, uint seed)
    {
        uint csum = seed;
        int whole = data.Length / 4;
        for (int i = 0; i < whole; i++)
            csum ^= BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);

        uint tail = 0;
        int rest = data.Length % 4;
        int p = whole * 4;
        switch (rest)
        {
            case 3: tail |= (uint)data[p++] << 16; goto case 2;
            case 2: tail |= (uint)data[p++] << 8; goto case 1;
            case 1: tail |= data[p]; break;
        }
        return csum ^ tail;
    }

    private static ushort DosDate(DateTime d) =>
        (ushort)(((Math.Clamp(d.Year, 1980, 2107) - 1980) << 9) | (d.Month << 5) | d.Day);

    private static ushort DosTime(DateTime d) =>
        (ushort)((d.Hour << 11) | (d.Minute << 5) | (d.Second / 2));

    private static void WriteU16(Stream s, ushort v)
    { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); s.Write(b); }

    private static void WriteU32(Stream s, uint v)
    { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
}
