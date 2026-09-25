using System.Buffers.Binary;
using System.Text;

namespace MsiMaker.Core.Msi;

/// <summary>
/// The MSI summary information stream (an OLE property set). Small, but it carries
/// two fields that decide whether an Intune upgrade works at all: the Template
/// (platform;language) and the Revision Number (package code).
/// </summary>
public sealed class SummaryInfo
{
    public const int PidCodepage = 1;
    public const int PidTitle = 2;
    public const int PidSubject = 3;
    public const int PidAuthor = 4;
    public const int PidKeywords = 5;
    public const int PidComments = 6;
    public const int PidTemplate = 7;
    public const int PidLastAuthor = 8;
    public const int PidRevNumber = 9;
    public const int PidCreated = 12;
    public const int PidLastSaved = 13;
    public const int PidPageCount = 14;  // schema version
    public const int PidWordCount = 15;  // source flags
    public const int PidCharCount = 16;
    public const int PidCreatingApp = 18;
    public const int PidSecurity = 19;

    private static readonly Guid FmtidSummaryInformation = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");

    public Dictionary<int, object> Properties { get; } = new();

    public string? Template
    {
        get => Properties.GetValueOrDefault(PidTemplate) as string;
        set => Set(PidTemplate, value);
    }

    /// <summary>The package code. Must be a fresh GUID for every build.</summary>
    public string? RevisionNumber
    {
        get => Properties.GetValueOrDefault(PidRevNumber) as string;
        set => Set(PidRevNumber, value);
    }

    public string? Subject { get => Get(PidSubject); set => Set(PidSubject, value); }
    public string? Author { get => Get(PidAuthor); set => Set(PidAuthor, value); }
    public string? Comments { get => Get(PidComments); set => Set(PidComments, value); }
    public string? Keywords { get => Get(PidKeywords); set => Set(PidKeywords, value); }
    public string? CreatingApp { get => Get(PidCreatingApp); set => Set(PidCreatingApp, value); }

    public int PageCount
    {
        get => Properties.GetValueOrDefault(PidPageCount) as int? ?? 0;
        set => Properties[PidPageCount] = value;
    }

    /// <summary>Source flags: bit 1 = compressed (files live in a cabinet).</summary>
    public int WordCount
    {
        get => Properties.GetValueOrDefault(PidWordCount) as int? ?? 0;
        set => Properties[PidWordCount] = value;
    }

    private string? Get(int pid) => Properties.GetValueOrDefault(pid) as string;

    private void Set(int pid, string? value)
    {
        if (value is null) Properties.Remove(pid);
        else Properties[pid] = value;
    }

    /// <summary>
    /// ANSI codepage for the summary stream. Windows Installer does not accept
    /// UTF-8 (65001) here even though the database string pool may use it: every
    /// string property silently reads back empty, and a package with no Template
    /// is then rejected outright with error 1620.
    /// </summary>
    public const int SummaryCodepage = 1252;

    /// <summary>Defaults that produce a valid, Intune-friendly per-machine package.</summary>
    public static SummaryInfo CreateDefault(string productName, string manufacturer, string platform, int language, bool compressed)
    {
        var si = new SummaryInfo();
        si.Properties[PidCodepage] = SummaryCodepage;
        si.Properties[PidTitle] = "Installation Database";
        si.Subject = productName;
        si.Author = manufacturer;
        si.Keywords = "Installer,MSI";
        si.Comments = $"This installer database contains the logic and data required to install {productName}.";
        si.Template = $"{platform};{language}";
        si.RevisionNumber = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
        si.Properties[PidCreated] = DateTime.UtcNow;
        si.Properties[PidLastSaved] = DateTime.UtcNow;
        si.PageCount = 500;                       // schema version: Windows Installer 4.5+
        si.WordCount = compressed ? 2 : 0;        // 2 = long file names, files in a cabinet
        si.Properties[PidSecurity] = 2;           // read-only recommended
        si.CreatingApp = "MsiMaker";
        return si;
    }

    // ---------------------------------------------------------------- parsing

    /// <summary>The largest FILETIME DateTime.FromFileTimeUtc will accept.</summary>
    private const long MaxFileTime = 2650467743999999999L;

    public static SummaryInfo Parse(byte[] data)
    {
        var si = new SummaryInfo();
        if (data.Length < 48) return si;

        int sectionOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(44));
        // Subtraction, not addition: sectionOffset + 8 overflows to a negative for
        // a value near int.MaxValue and the guard then lets it through.
        if (sectionOffset <= 0 || sectionOffset > data.Length - 8) return si;

        int propCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(sectionOffset + 4));
        int codepage = 65001;

        // Codepage governs how the LPSTR values decode, so resolve it up front.
        for (int i = 0; i < propCount; i++)
        {
            int e = sectionOffset + 8 + i * 8;
            if (e + 8 > data.Length) break;
            if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(e)) != PidCodepage) continue;
            int o = sectionOffset + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(e + 4));
            if (o >= 0 && o <= data.Length - 6) codepage = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o + 4));
            break;
        }
        var enc = codepage == 65001 ? Encoding.UTF8 : Encoding.Latin1;

        for (int i = 0; i < propCount; i++)
        {
            int e = sectionOffset + 8 + i * 8;
            if (e + 8 > data.Length) break;
            int pid = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(e));
            // The per-property offset is a signed value read from the file, so it
            // needs a floor as well as a ceiling.
            int off = sectionOffset + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(e + 4));
            if (off < 0 || off > data.Length - 4) continue;

            int type = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off));
            switch (type)
            {
                case 2 when off + 6 <= data.Length: // VT_I2
                    // The codepage occupies the full unsigned range (65001 stores
                    // as 0xFDE9), so it must not be read back as a signed short.
                    si.Properties[pid] = pid == PidCodepage
                        ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 4))
                        : (int)BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off + 4));
                    break;
                case 3 when off + 8 <= data.Length: // VT_I4
                    si.Properties[pid] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 4));
                    break;
                case 30 when off + 8 <= data.Length: // VT_LPSTR
                {
                    int len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 4));
                    if (len > 0 && len <= data.Length - off - 8)
                    {
                        int trimmed = len;
                        while (trimmed > 0 && data[off + 8 + trimmed - 1] == 0) trimmed--;
                        si.Properties[pid] = enc.GetString(data, off + 8, trimmed);
                    }
                    else si.Properties[pid] = string.Empty;
                    break;
                }
                case 64 when off + 12 <= data.Length: // VT_FILETIME
                {
                    // FromFileTimeUtc throws outside the representable range, and the
                    // value is whatever the file says.
                    long ft = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(off + 4));
                    if (ft > 0 && ft <= MaxFileTime) si.Properties[pid] = DateTime.FromFileTimeUtc(ft);
                    break;
                }
            }
        }
        return si;
    }

    // ---------------------------------------------------------------- writing

    public byte[] ToBytes()
    {
        int codepage = Properties.GetValueOrDefault(PidCodepage) as int? ?? SummaryCodepage;
        if (codepage < 0) codepage &= 0xFFFF;
        var enc = codepage == 65001 ? Encoding.UTF8 : Encoding.Latin1;

        // Property id 1 (codepage) must sort first within the section.
        var ordered = Properties.OrderBy(p => p.Key == PidCodepage ? -1 : p.Key).ToList();

        var values = new MemoryStream();
        var offsets = new List<(int Pid, int Offset)>();

        foreach (var (pid, value) in ordered)
        {
            while (values.Length % 4 != 0) values.WriteByte(0);
            offsets.Add((pid, (int)values.Length));

            switch (value)
            {
                case int i when pid == PidCodepage:
                    WriteI32(values, 2); // VT_I2
                    WriteI16(values, (short)i);
                    break;
                case int i:
                    WriteI32(values, 3); // VT_I4
                    WriteI32(values, i);
                    break;
                case string s:
                {
                    WriteI32(values, 30); // VT_LPSTR
                    byte[] b = enc.GetBytes(s);
                    WriteI32(values, b.Length + 1);
                    values.Write(b);
                    values.WriteByte(0);
                    break;
                }
                case DateTime dt:
                    WriteI32(values, 64); // VT_FILETIME
                    WriteI64(values, dt.ToFileTimeUtc());
                    break;
                default:
                    WriteI32(values, 1); // VT_NULL
                    break;
            }
        }
        while (values.Length % 4 != 0) values.WriteByte(0);

        byte[] valueBytes = values.ToArray();
        int headerSize = 8 + offsets.Count * 8;
        int sectionSize = headerSize + valueBytes.Length;

        var ms = new MemoryStream();
        WriteI16(ms, unchecked((short)0xFFFE));   // byte order
        WriteI16(ms, 0);                          // format version
        WriteI32(ms, 0x000A0000);                 // OS version
        ms.Write(Guid.Empty.ToByteArray());       // CLSID
        WriteI32(ms, 1);                          // one property set
        ms.Write(FmtidSummaryInformation.ToByteArray());
        WriteI32(ms, 48);                         // offset to section

        WriteI32(ms, sectionSize);
        WriteI32(ms, offsets.Count);
        foreach (var (pid, off) in offsets)
        {
            WriteI32(ms, pid);
            WriteI32(ms, headerSize + off);
        }
        ms.Write(valueBytes);
        return ms.ToArray();
    }

    private static void WriteI16(Stream s, short v)
    { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, v); s.Write(b); }

    private static void WriteI32(Stream s, int v)
    { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); s.Write(b); }

    private static void WriteI64(Stream s, long v)
    { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); s.Write(b); }
}
