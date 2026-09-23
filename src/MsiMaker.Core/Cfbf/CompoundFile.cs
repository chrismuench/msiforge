using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace MsiMaker.Core.Cfbf;

/// <summary>
/// Compound File Binary Format (MS-CFB) constants. This is the container an MSI
/// lives in. Pure managed, no native dependencies, so it behaves identically on
/// Windows and macOS.
/// </summary>
public static class Cfb
{
    public const uint Free = 0xFFFFFFFF;
    public const uint EndOfChain = 0xFFFFFFFE;
    public const uint FatSect = 0xFFFFFFFD;
    public const uint DifSect = 0xFFFFFFFC;
    public const uint MaxRegSect = 0xFFFFFFFA;
    public const uint NoStream = 0xFFFFFFFF;

    internal static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
}

public enum CfbEntryType : byte { Empty = 0, Storage = 1, Stream = 2, Root = 5 }

public sealed record CfbStream(string Name, byte[] Data);

public sealed class CfbReader
{
    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly byte[] _miniStream;

    public IReadOnlyList<CfbStream> Streams { get; }
    public Guid RootClsid { get; }

    public CfbReader(byte[] data)
    {
        _data = data;
        if (data.Length < 512 || !data.AsSpan(0, 8).SequenceEqual(Cfb.Signature))
            throw new InvalidDataException("Not a compound file (bad signature) - this is not an MSI.");

        _sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x1E));
        _miniSectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x20));
        uint numFatSectors = ReadU32(0x2C);
        uint dirStart = ReadU32(0x30);
        _miniCutoff = ReadU32(0x38);
        uint miniFatStart = ReadU32(0x3C);
        uint difatStart = ReadU32(0x44);
        uint numDifatSectors = ReadU32(0x48);

        // DIFAT: 109 entries live in the header, the remainder in a chain of DIFAT sectors.
        var fatSectors = new List<uint>();
        for (int i = 0; i < 109 && fatSectors.Count < numFatSectors; i++)
        {
            uint s = ReadU32(0x4C + i * 4);
            if (s <= Cfb.MaxRegSect) fatSectors.Add(s);
        }
        uint difat = difatStart;
        int perDifat = _sectorSize / 4 - 1;
        for (uint i = 0; i < numDifatSectors && difat <= Cfb.MaxRegSect; i++)
        {
            int off = SectorOffset(difat);
            for (int j = 0; j < perDifat && fatSectors.Count < numFatSectors; j++)
            {
                uint s = ReadU32(off + j * 4);
                if (s <= Cfb.MaxRegSect) fatSectors.Add(s);
            }
            difat = ReadU32(off + perDifat * 4);
        }

        _fat = new uint[fatSectors.Count * (_sectorSize / 4)];
        for (int i = 0; i < fatSectors.Count; i++)
        {
            int off = SectorOffset(fatSectors[i]);
            for (int j = 0; j < _sectorSize / 4; j++)
                _fat[i * (_sectorSize / 4) + j] = ReadU32(off + j * 4);
        }

        byte[] miniFatBytes = ReadChain(miniFatStart);
        _miniFat = new uint[miniFatBytes.Length / 4];
        for (int i = 0; i < _miniFat.Length; i++)
            _miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i * 4));

        // The root entry carries the mini stream, so it has to be resolved before any small stream.
        byte[] dir = ReadChain(dirStart);
        int entryCount = dir.Length / 128;
        _miniStream = [];
        RootClsid = Guid.Empty;
        for (int i = 0; i < entryCount; i++)
        {
            if ((CfbEntryType)dir[i * 128 + 66] != CfbEntryType.Root) continue;
            RootClsid = new Guid(dir.AsSpan(i * 128 + 80, 16));
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(i * 128 + 116));
            long size = BinaryPrimitives.ReadInt64LittleEndian(dir.AsSpan(i * 128 + 120));
            _miniStream = ReadChain(start, size);
            break;
        }

        var streams = new List<CfbStream>();
        for (int i = 0; i < entryCount; i++)
        {
            var span = dir.AsSpan(i * 128, 128);
            if ((CfbEntryType)span[66] != CfbEntryType.Stream) continue;
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(span[64..]);
            if (nameLen < 2) continue;
            string name = Encoding.Unicode.GetString(span[..(nameLen - 2)]);
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(span[116..]);
            long size = BinaryPrimitives.ReadInt64LittleEndian(span[120..]);
            streams.Add(new CfbStream(name, size < _miniCutoff ? ReadMiniChain(start, size) : ReadChain(start, size)));
        }
        Streams = streams;
    }

    public static CfbReader FromFile(string path) => new(File.ReadAllBytes(path));

    private uint ReadU32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset));

    private int SectorOffset(uint sector) => (int)((sector + 1) * (uint)_sectorSize);

    private byte[] ReadChain(uint start, long size = -1)
    {
        var ms = new MemoryStream();
        uint s = start;
        var seen = new HashSet<uint>();
        while (s <= Cfb.MaxRegSect && seen.Add(s))
        {
            int off = SectorOffset(s);
            if (off < 0 || off + _sectorSize > _data.Length) break;
            ms.Write(_data, off, _sectorSize);
            s = s < _fat.Length ? _fat[s] : Cfb.EndOfChain;
        }
        byte[] b = ms.ToArray();
        return size >= 0 && size < b.Length ? b[..(int)size] : b;
    }

    private byte[] ReadMiniChain(uint start, long size)
    {
        var ms = new MemoryStream();
        uint s = start;
        var seen = new HashSet<uint>();
        while (s <= Cfb.MaxRegSect && seen.Add(s))
        {
            int off = (int)s * _miniSectorSize;
            if (off + _miniSectorSize > _miniStream.Length) break;
            ms.Write(_miniStream, off, _miniSectorSize);
            s = s < _miniFat.Length ? _miniFat[s] : Cfb.EndOfChain;
        }
        byte[] b = ms.ToArray();
        return size < b.Length ? b[..(int)size] : b;
    }
}

public sealed class CfbWriter
{
    private readonly int _sectorSize;
    private const int MiniSectorSize = 64;

    /// <summary>
    /// Streams smaller than this live in the mini stream. 4096 is the standard
    /// value; it is settable so the mini-stream path can be taken out of the
    /// picture when diagnosing a reader that disagrees with us.
    /// </summary>
    public uint MiniCutoff { get; set; } = 4096;

    /// <summary>CLSID that marks the compound file as a Windows Installer database.</summary>
    public Guid RootClsid { get; set; } = new("000C1084-0000-0000-C000-000000000046");

    public CfbWriter(int sectorSize = 4096)
    {
        if (sectorSize != 512 && sectorSize != 4096) throw new ArgumentException("Sector size must be 512 or 4096.");
        _sectorSize = sectorSize;
    }

    private sealed class Entry
    {
        public required string Name;
        public required CfbEntryType Type;
        public byte[] Data = [];
        public uint Start = Cfb.EndOfChain;
        public long Size;
        public uint Left = Cfb.NoStream, Right = Cfb.NoStream, Child = Cfb.NoStream;
        public int Index;
        public int Depth;
        public byte Color = Black;
    }

    private const byte Red = 0;
    private const byte Black = 1;

    public byte[] Write(IEnumerable<CfbStream> streams)
    {
        var entries = new List<Entry> { new() { Name = "Root Entry", Type = CfbEntryType.Root } };
        foreach (var s in streams)
            entries.Add(new Entry { Name = s.Name, Type = CfbEntryType.Stream, Data = s.Data, Size = s.Data.Length });

        var big = entries.Where(e => e.Type == CfbEntryType.Stream && e.Data.Length >= MiniCutoff).ToList();
        var small = entries.Where(e => e.Type == CfbEntryType.Stream && e.Data.Length < MiniCutoff).ToList();

        // Mini stream: small streams concatenated, each padded up to a 64-byte boundary.
        var miniMs = new MemoryStream();
        var miniFat = new List<uint>();
        foreach (var e in small)
        {
            if (e.Data.Length == 0) { e.Start = Cfb.EndOfChain; continue; }
            int sectors = (e.Data.Length + MiniSectorSize - 1) / MiniSectorSize;
            e.Start = (uint)(miniMs.Length / MiniSectorSize);
            for (int i = 0; i < sectors; i++)
                miniFat.Add(i == sectors - 1 ? Cfb.EndOfChain : e.Start + (uint)i + 1);
            miniMs.Write(e.Data);
            int pad = sectors * MiniSectorSize - e.Data.Length;
            if (pad > 0) miniMs.Write(new byte[pad]);
        }
        byte[] miniStream = miniMs.ToArray();

        byte[] miniFatBytes = miniFat.Count == 0 ? [] : new byte[Align(miniFat.Count * 4, _sectorSize)];
        miniFatBytes.AsSpan().Fill(0xFF);
        for (int i = 0; i < miniFat.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(miniFatBytes.AsSpan(i * 4), miniFat[i]);

        BuildTree(entries);
        byte[] dirBytes = new byte[Align(Math.Max(entries.Count, 4) * 128, _sectorSize)];

        // Allocate regular sectors: big streams, then mini stream, mini FAT, directory.
        var fat = new List<uint>();
        var payload = new List<byte[]>();
        uint Allocate(byte[] data)
        {
            if (data.Length == 0) return Cfb.EndOfChain;
            int sectors = (data.Length + _sectorSize - 1) / _sectorSize;
            uint start = (uint)fat.Count;
            for (int i = 0; i < sectors; i++) fat.Add(i == sectors - 1 ? Cfb.EndOfChain : start + (uint)i + 1);
            byte[] padded = new byte[sectors * _sectorSize];
            data.CopyTo(padded, 0);
            payload.Add(padded);
            return start;
        }

        foreach (var e in big) e.Start = Allocate(e.Data);
        uint miniStart = Allocate(miniStream);
        uint miniFatStart = Allocate(miniFatBytes);
        uint dirStart = Allocate(dirBytes);
        int dirPayloadIndex = payload.Count - 1;
        int miniFatCount = miniFatBytes.Length / _sectorSize;
        int dirCount = dirBytes.Length / _sectorSize;

        entries[0].Start = miniStart;
        entries[0].Size = miniStream.Length;

        // FAT and DIFAT sizing is self-referential (the FAT must describe its own
        // sectors), so iterate to a fixed point rather than guessing.
        int perSector = _sectorSize / 4;
        int dataSectors = fat.Count;
        int fatCount = 0, difatCount = 0;
        for (int i = 0; i < 32; i++)
        {
            int nf = Math.Max(1, (int)Math.Ceiling((dataSectors + fatCount + difatCount) / (double)perSector));
            int nd = nf <= 109 ? 0 : (int)Math.Ceiling((nf - 109) / (double)(perSector - 1));
            if (nf == fatCount && nd == difatCount) break;
            fatCount = nf; difatCount = nd;
        }

        uint fatStart = (uint)dataSectors;
        uint difatStart = fatStart + (uint)fatCount;
        for (int i = 0; i < fatCount; i++) fat.Add(Cfb.FatSect);
        for (int i = 0; i < difatCount; i++) fat.Add(Cfb.DifSect);

        byte[] fatBytes = new byte[fatCount * _sectorSize];
        fatBytes.AsSpan().Fill(0xFF);
        for (int i = 0; i < fat.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(fatBytes.AsSpan(i * 4), fat[i]);

        for (int i = 0; i < entries.Count; i++)
            WriteDirEntry(dirBytes.AsSpan(i * 128, 128), entries[i], i == 0);

        // Spare slots must carry NOSTREAM sibling and child pointers. Left at
        // zero they each claim the root entry as their left, right and child,
        // which readers that scan the whole directory array reject.
        for (int i = entries.Count; i < dirBytes.Length / 128; i++)
        {
            var span = dirBytes.AsSpan(i * 128, 128);
            span.Clear();
            span[66] = (byte)CfbEntryType.Empty;
            span[67] = 1; // black
            BinaryPrimitives.WriteUInt32LittleEndian(span[68..], Cfb.NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(span[72..], Cfb.NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(span[76..], Cfb.NoStream);
        }
        payload[dirPayloadIndex] = dirBytes;

        var outMs = new MemoryStream();
        outMs.Write(new byte[_sectorSize]); // header sector, filled in below
        foreach (var p in payload) outMs.Write(p);
        outMs.Write(fatBytes);

        if (difatCount > 0)
        {
            byte[] difatBytes = new byte[difatCount * _sectorSize];
            difatBytes.AsSpan().Fill(0xFF);
            for (int i = 0; i < difatCount; i++)
            {
                for (int j = 0; j < perSector - 1; j++)
                {
                    int fatIdx = 109 + i * (perSector - 1) + j;
                    if (fatIdx < fatCount)
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            difatBytes.AsSpan(i * _sectorSize + j * 4), fatStart + (uint)fatIdx);
                }
                uint next = i == difatCount - 1 ? Cfb.EndOfChain : difatStart + (uint)i + 1;
                BinaryPrimitives.WriteUInt32LittleEndian(
                    difatBytes.AsSpan(i * _sectorSize + (perSector - 1) * 4), next);
            }
            outMs.Write(difatBytes);
        }

        byte[] result = outMs.ToArray();
        WriteHeader(result, fatCount, dirStart, dirCount, miniFatStart, miniFatCount, difatStart, difatCount, fatStart);
        return result;
    }

    private void WriteHeader(byte[] buf, int fatCount, uint dirStart, int dirCount,
        uint miniFatStart, int miniFatCount, uint difatStart, int difatCount, uint fatStart)
    {
        var h = buf.AsSpan(0, _sectorSize);
        h.Clear();
        Cfb.Signature.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h[0x18..], 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(h[0x1A..], (ushort)(_sectorSize == 512 ? 3 : 4));
        BinaryPrimitives.WriteUInt16LittleEndian(h[0x1C..], 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(h[0x1E..], (ushort)BitOperations.Log2((uint)_sectorSize));
        BinaryPrimitives.WriteUInt16LittleEndian(h[0x20..], 6);
        // Directory sector count is only meaningful for v4 files.
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x28..], _sectorSize == 512 ? 0u : (uint)dirCount);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x2C..], (uint)fatCount);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x30..], dirStart);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x38..], MiniCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x3C..], miniFatCount > 0 ? miniFatStart : Cfb.EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x40..], (uint)miniFatCount);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x44..], difatCount > 0 ? difatStart : Cfb.EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(h[0x48..], (uint)difatCount);
        for (int i = 0; i < 109; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(h[(0x4C + i * 4)..], i < fatCount ? fatStart + (uint)i : Cfb.Free);
    }

    private void WriteDirEntry(Span<byte> span, Entry e, bool isRoot)
    {
        span.Clear();
        byte[] name = Encoding.Unicode.GetBytes(e.Name);
        if (name.Length > 62) throw new InvalidOperationException($"Stream name too long: {e.Name}");
        name.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[64..], (ushort)(name.Length + 2));
        span[66] = (byte)e.Type;
        span[67] = isRoot ? Black : e.Color;
        BinaryPrimitives.WriteUInt32LittleEndian(span[68..], e.Left);
        BinaryPrimitives.WriteUInt32LittleEndian(span[72..], e.Right);
        BinaryPrimitives.WriteUInt32LittleEndian(span[76..], e.Child);
        if (isRoot) RootClsid.ToByteArray().CopyTo(span[80..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[116..], e.Start);
        BinaryPrimitives.WriteInt64LittleEndian(span[120..], e.Size);
    }

    /// <summary>
    /// The directory is a red-black tree keyed by (name length, uppercase ordinal).
    /// Building it balanced from the sorted list gives the ordering; colouring only
    /// the deepest level red gives a valid black-height, since every root-to-leaf
    /// path then crosses the same number of black nodes.
    /// </summary>
    private static void BuildTree(List<Entry> entries)
    {
        for (int i = 0; i < entries.Count; i++) entries[i].Index = i;
        var children = entries.Skip(1).ToList();
        children.Sort(static (a, b) =>
        {
            int c = a.Name.Length.CompareTo(b.Name.Length);
            return c != 0 ? c : string.CompareOrdinal(a.Name.ToUpperInvariant(), b.Name.ToUpperInvariant());
        });

        int maxDepth = 0;
        entries[0].Child = Build(children, 0, children.Count - 1, 0, ref maxDepth);

        foreach (var e in children)
            e.Color = maxDepth > 0 && e.Depth == maxDepth ? Red : Black;

        static uint Build(List<Entry> list, int lo, int hi, int depth, ref int maxDepth)
        {
            if (lo > hi) return Cfb.NoStream;
            int mid = (lo + hi) / 2;
            list[mid].Depth = depth;
            if (depth > maxDepth) maxDepth = depth;
            list[mid].Left = Build(list, lo, mid - 1, depth + 1, ref maxDepth);
            list[mid].Right = Build(list, mid + 1, hi, depth + 1, ref maxDepth);
            return (uint)list[mid].Index;
        }
    }

    private static int Align(int value, int boundary) =>
        value == 0 ? 0 : (value + boundary - 1) / boundary * boundary;
}
