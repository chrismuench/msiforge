using System.Buffers.Binary;
using System.Text;

namespace MsiForge.Core.Payload;

public enum PeArchitecture { Unknown, X86, X64, Arm64 }

/// <summary>Contents of a VS_VERSIONINFO resource.</summary>
public sealed class PeVersionInfo
{
    /// <summary>Numeric file version from VS_FIXEDFILEINFO, the reliable one.</summary>
    public string? FileVersion { get; set; }

    /// <summary>
    /// The raw numeric a.b.c.d from VS_FIXEDFILEINFO, never overridden by the
    /// string table. The MSI File.Version column must be strictly numeric, and
    /// string-table versions routinely are not ("25.01", "5.3.7.802+sha").
    /// </summary>
    public string? FixedFileVersion { get; set; }

    public string? ProductVersion { get; set; }

    /// <summary>String-table values: CompanyName, ProductName, FileDescription and friends.</summary>
    public Dictionary<string, string> Strings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? CompanyName => Strings.GetValueOrDefault("CompanyName");
    public string? ProductName => Strings.GetValueOrDefault("ProductName");
    public string? FileDescription => Strings.GetValueOrDefault("FileDescription");
    public string? OriginalFilename => Strings.GetValueOrDefault("OriginalFilename");
}

/// <summary>
/// Minimal PE reader: architecture, section names and the version resource.
/// Pure managed so payload inspection works on macOS as well as Windows, where
/// the usual answer would be a Win32 API.
/// </summary>
public sealed class PeFile
{
    private readonly byte[] _data;

    public PeArchitecture Architecture { get; private set; } = PeArchitecture.Unknown;
    public IReadOnlyList<string> SectionNames { get; private set; } = [];
    public PeVersionInfo? Version { get; private set; }
    public bool IsValid { get; private set; }

    private readonly List<(uint Rva, uint Size, uint Offset)> _sections = [];
    private uint _resourceRva;

    public PeFile(byte[] data)
    {
        _data = data;
        try { Parse(); } catch { IsValid = false; }
    }

    public static PeFile FromFile(string path) => new(File.ReadAllBytes(path));

    private void Parse()
    {
        if (_data.Length < 0x40 || _data[0] != 'M' || _data[1] != 'Z') return;

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(0x3C));
        if (peOffset <= 0 || peOffset + 24 > _data.Length) return;
        if (BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(peOffset)) != 0x00004550) return; // "PE\0\0"

        IsValid = true;

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(peOffset + 4));
        Architecture = machine switch
        {
            0x014C => PeArchitecture.X86,
            0x8664 => PeArchitecture.X64,
            0xAA64 => PeArchitecture.Arm64,
            _ => PeArchitecture.Unknown,
        };

        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(peOffset + 6));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(peOffset + 20));
        int optionalOffset = peOffset + 24;
        if (optionalOffset + optionalSize > _data.Length) return;

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(optionalOffset));
        // The data-directory array sits after the fixed part, whose size differs
        // between PE32 and PE32+.
        int dirOffset = optionalOffset + (magic == 0x20B ? 112 : 96);
        if (dirOffset + 8 * 3 <= _data.Length)
            _resourceRva = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(dirOffset + 8 * 2));

        var names = new List<string>();
        int sectionOffset = optionalOffset + optionalSize;
        for (int i = 0; i < sectionCount && sectionOffset + 40 <= _data.Length; i++, sectionOffset += 40)
        {
            string name = Encoding.ASCII.GetString(_data, sectionOffset, 8).TrimEnd('\0');
            names.Add(name);
            uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(sectionOffset + 12));
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(sectionOffset + 16));
            uint rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(sectionOffset + 20));
            _sections.Add((virtualAddress, rawSize, rawOffset));
        }
        SectionNames = names;

        if (_resourceRva != 0) Version = ReadVersionResource();
    }

    private int RvaToOffset(uint rva)
    {
        foreach (var (sectionRva, size, offset) in _sections)
            if (rva >= sectionRva && rva < sectionRva + size)
                return (int)(offset + (rva - sectionRva));
        return -1;
    }

    /// <summary>Walks the resource tree to RT_VERSION (type 16) and parses it.</summary>
    private PeVersionInfo? ReadVersionResource()
    {
        int root = RvaToOffset(_resourceRva);
        if (root < 0) return null;

        // Three levels: type -> name -> language, and only then a data entry.
        int typeEntry = FindResourceEntry(root, root, 16);
        if (typeEntry < 0) return null;
        int nameEntry = FirstChildEntry(root, typeEntry);
        if (nameEntry < 0) return null;
        int langEntry = FirstChildEntry(root, nameEntry);
        if (langEntry < 0) return null;
        int dataEntry = ResolveToData(root, langEntry);
        if (dataEntry < 0) return null;

        // A data entry: RVA, size, codepage, reserved.
        if (dataEntry + 8 > _data.Length) return null;
        uint dataRva = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(dataEntry));
        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(dataEntry + 4));
        int dataOffset = RvaToOffset(dataRva);
        if (dataOffset < 0 || dataOffset + dataSize > _data.Length || dataSize < 6) return null;

        return ParseVersionInfo(_data.AsSpan(dataOffset, (int)dataSize));
    }

    /// <summary>Finds a resource directory entry with the given integer id.</summary>
    private int FindResourceEntry(int root, int dir, uint id)
    {
        if (dir + 16 > _data.Length) return -1;
        int named = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dir + 12));
        int ided = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dir + 14));
        int entry = dir + 16 + named * 8;
        for (int i = 0; i < ided; i++, entry += 8)
        {
            if (entry + 8 > _data.Length) break;
            if (BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(entry)) == id) return entry;
        }
        return -1;
    }

    /// <summary>Follows a directory entry into its subdirectory and returns that
    /// subdirectory's first entry.</summary>
    private int FirstChildEntry(int root, int entry)
    {
        if (entry + 8 > _data.Length) return -1;
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(entry + 4));
        if ((offset & 0x80000000) == 0) return -1; // a leaf, not a directory
        int target = root + (int)(offset & 0x7FFFFFFF);
        if (target + 16 > _data.Length) return -1;

        int named = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(target + 12));
        int ided = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(target + 14));
        if (named + ided == 0) return -1;
        return target + 16; // first entry of the subdirectory
    }

    /// <summary>Follows a leaf entry to the data entry it points at.</summary>
    private int ResolveToData(int root, int entry)
    {
        if (entry + 8 > _data.Length) return -1;
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(entry + 4));
        if ((offset & 0x80000000) != 0) return -1; // still a directory
        return root + (int)offset;
    }

    private static PeVersionInfo? ParseVersionInfo(ReadOnlySpan<byte> span)
    {
        var info = new PeVersionInfo();

        // VS_FIXEDFILEINFO is located by its signature rather than by walking the
        // nested key structure, which is fiddly and varies between linkers.
        for (int i = 0; i + 52 <= span.Length; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(span[i..]) != 0xFEEF04BD) continue;
            uint fileMs = BinaryPrimitives.ReadUInt32LittleEndian(span[(i + 8)..]);
            uint fileLs = BinaryPrimitives.ReadUInt32LittleEndian(span[(i + 12)..]);
            uint prodMs = BinaryPrimitives.ReadUInt32LittleEndian(span[(i + 16)..]);
            uint prodLs = BinaryPrimitives.ReadUInt32LittleEndian(span[(i + 20)..]);
            info.FileVersion = $"{fileMs >> 16}.{fileMs & 0xFFFF}.{fileLs >> 16}.{fileLs & 0xFFFF}";
            info.FixedFileVersion = info.FileVersion;
            info.ProductVersion = $"{prodMs >> 16}.{prodMs & 0xFFFF}.{prodLs >> 16}.{prodLs & 0xFFFF}";
            break;
        }

        // String table entries are UTF-16 key/value pairs; scanning for the known
        // keys is far more robust than walking the padded block structure.
        string text = Encoding.Unicode.GetString(span);
        foreach (string key in new[]
        {
            "CompanyName", "ProductName", "FileDescription", "FileVersion",
            "ProductVersion", "OriginalFilename", "InternalName", "LegalCopyright",
        })
        {
            int k = text.IndexOf(key, StringComparison.Ordinal);
            if (k < 0) continue;
            int p = k + key.Length;
            while (p < text.Length && text[p] == '\0') p++;      // padding to the value
            int end = text.IndexOf('\0', p);
            if (end < 0) end = text.Length;
            string value = text[p..end].Trim();
            if (value.Length > 0) info.Strings[key] = value;
        }

        // The string table is what Windows itself reports and what a vendor
        // actually publishes; VS_FIXEDFILEINFO is often left at a build-tool
        // default, and some self-extractors carry more than one version
        // resource. Prefer the string table and fall back to the fixed block.
        if (info.Strings.TryGetValue("ProductVersion", out string? productString) && HasDigit(productString))
            info.ProductVersion = productString;
        if (info.Strings.TryGetValue("FileVersion", out string? fileString) && HasDigit(fileString))
            info.FileVersion = fileString;

        return info.FileVersion is null && info.Strings.Count == 0 ? null : info;

        static bool HasDigit(string s) => s.Any(char.IsAsciiDigit);
    }

    /// <summary>True if the raw bytes contain this ASCII marker.</summary>
    public bool ContainsAscii(string marker)
    {
        byte[] needle = Encoding.ASCII.GetBytes(marker);
        return IndexOf(_data, needle) >= 0;
    }

    /// <summary>True if the raw bytes contain this UTF-16 marker.</summary>
    public bool ContainsUnicode(string marker)
    {
        byte[] needle = Encoding.Unicode.GetBytes(marker);
        return IndexOf(_data, needle) >= 0;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        byte first = needle[0];
        int limit = haystack.Length - needle.Length;
        for (int i = 0; i <= limit; i++)
        {
            if (haystack[i] != first) continue;
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }
}
