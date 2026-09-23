using System.Text;

namespace MsiMaker.Core.Msi;

/// <summary>
/// MSI stream-name codec. Table and stream names are packed into the Unicode
/// private-use area so they fit the 31-character compound-file name limit.
/// Verified by round-tripping every stream of a WiX-built MSI.
/// </summary>
public static class MsiName
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._";
    private const char TableMarker = '䡀';
    private const int PairBase = 0x3800;
    private const int SingleBase = 0x4800;

    private static int ToMime(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'Z' => c - 'A' + 10,
        >= 'a' and <= 'z' => c - 'a' + 36,
        '.' => 62,
        '_' => 63,
        _ => -1,
    };

    /// <summary>Encodes a logical name. <paramref name="isTable"/> prefixes the table marker.</summary>
    public static string Encode(string name, bool isTable)
    {
        var sb = new StringBuilder();
        if (isTable) sb.Append(TableMarker);

        int i = 0;
        while (i < name.Length)
        {
            int m1 = ToMime(name[i]);
            if (m1 < 0) { sb.Append(name[i]); i++; continue; }

            int m2 = i + 1 < name.Length ? ToMime(name[i + 1]) : -1;
            if (m2 >= 0) { sb.Append((char)(PairBase + m1 + (m2 << 6))); i += 2; }
            else { sb.Append((char)(SingleBase + m1)); i += 1; }
        }

        string encoded = sb.ToString();
        if (encoded.Length > 31)
            throw new InvalidOperationException($"Encoded stream name exceeds 31 chars: {name}");
        return encoded;
    }

    /// <summary>Decodes an encoded name. Table names come back with a leading '!'.</summary>
    public static string Decode(string encoded)
    {
        var sb = new StringBuilder();
        foreach (char c in encoded)
        {
            int o = c;
            if (o == TableMarker) sb.Append('!');
            else if (o >= SingleBase && o < SingleBase + 64) sb.Append(Alphabet[o - SingleBase]);
            else if (o >= PairBase && o < SingleBase)
            {
                int v = o - PairBase;
                sb.Append(Alphabet[v & 0x3F]).Append(Alphabet[(v >> 6) & 0x3F]);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string EncodeTable(string table) => Encode(table, isTable: true);
    public static string EncodeStream(string stream) => Encode(stream, isTable: false);
}
