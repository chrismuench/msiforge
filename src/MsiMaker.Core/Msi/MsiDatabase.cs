using System.Buffers.Binary;
using System.Text;
using MsiMaker.Core.Cfbf;

namespace MsiMaker.Core.Msi;

/// <summary>Column type bits from the MSI <c>_Columns</c> table.</summary>
[Flags]
public enum MsiColType
{
    SizeMask = 0x00FF,
    Valid = 0x0100,
    Localizable = 0x0200,
    StringBit = 0x0400, // always set alongside String on real databases
    String = 0x0800,
    Nullable = 0x1000,
    Key = 0x2000,
}

public sealed record MsiColumn(string Name, int Type)
{
    public bool IsString => (Type & (int)MsiColType.String) != 0;
    public bool IsKey => (Type & (int)MsiColType.Key) != 0;
    public bool IsNullable => (Type & (int)MsiColType.Nullable) != 0;
    public int Size => Type & (int)MsiColType.SizeMask;

    /// <summary>Byte width of this column inside a table stream.</summary>
    public int Width(int stringRefSize) => IsString ? stringRefSize : (Size == 2 ? 2 : 4);

    public static MsiColumn Str(string name, int len, bool key = false, bool nullable = true) =>
        new(name, 0x0D00 | (len & 0xFF) | (key ? 0x2000 : 0) | (nullable ? 0x1000 : 0));

    public static MsiColumn Int16(string name, bool key = false, bool nullable = true) =>
        new(name, 0x0102 | (key ? 0x2000 : 0) | (nullable ? 0x1000 : 0));

    public static MsiColumn Int32(string name, bool key = false, bool nullable = true) =>
        new(name, 0x0104 | (key ? 0x2000 : 0) | (nullable ? 0x1000 : 0));
}

public sealed class MsiTable(string name, IReadOnlyList<MsiColumn> columns)
{
    public string Name { get; } = name;
    public IReadOnlyList<MsiColumn> Columns { get; } = columns;

    /// <summary>Row cells are <see cref="string"/>, <see cref="int"/>, or null.</summary>
    public List<object?[]> Rows { get; } = [];

    public int IndexOf(string column)
    {
        for (int i = 0; i < Columns.Count; i++)
            if (Columns[i].Name == column) return i;
        return -1;
    }

    public MsiTable Add(params object?[] values)
    {
        if (values.Length != Columns.Count)
            throw new ArgumentException($"{Name}: expected {Columns.Count} values, got {values.Length}.");
        Rows.Add(values);
        return this;
    }

    /// <summary>First row whose <paramref name="column"/> equals <paramref name="value"/>.</summary>
    public object?[]? Find(string column, string value)
    {
        int i = IndexOf(column);
        return i < 0 ? null : Rows.FirstOrDefault(r => (r[i] as string) == value);
    }
}

/// <summary>
/// A Windows Installer database: a set of tables plus the summary information
/// stream, held in a compound file. Reads and writes with no native dependency.
/// </summary>
public sealed class MsiDatabase
{
    public Dictionary<string, MsiTable> Tables { get; } = new(StringComparer.Ordinal);
    public SummaryInfo Summary { get; set; } = new();

    /// <summary>Non-table streams, e.g. the embedded cabinet (name starts with a digit or is a binary blob).</summary>
    public Dictionary<string, byte[]> Streams { get; } = new(StringComparer.Ordinal);

    public int Codepage { get; set; } = 65001;

    public MsiTable Table(string name, params MsiColumn[] columns)
    {
        if (Tables.TryGetValue(name, out var existing)) return existing;
        var t = new MsiTable(name, columns);
        Tables[name] = t;
        return t;
    }

    /// <summary>Gets or creates a standard MSI table using the harvested schema.</summary>
    public MsiTable Std(string name)
    {
        if (Tables.TryGetValue(name, out var existing)) return existing;
        if (!MsiSchema.Standard.TryGetValue(name, out var cols))
            throw new ArgumentException($"No baked-in schema for table '{name}'.");
        var t = new MsiTable(name, cols.Select(c => new MsiColumn(c.Name, c.Type)).ToArray());
        Tables[name] = t;
        return t;
    }

    public string? GetProperty(string name)
    {
        if (!Tables.TryGetValue("Property", out var p)) return null;
        return p.Find("Property", name)?[1] as string;
    }

    public void SetProperty(string name, string value)
    {
        var p = Std("Property");
        int keyIdx = p.IndexOf("Property");
        var row = p.Rows.FirstOrDefault(r => (r[keyIdx] as string) == name);
        if (row is not null) row[1] = value; else p.Add(name, value);
    }

    // ---------------------------------------------------------------- reading

    public static MsiDatabase Load(string path) => Read(File.ReadAllBytes(path));

    public static MsiDatabase Read(byte[] bytes)
    {
        var cfb = new CfbReader(bytes);
        var raw = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var s in cfb.Streams) raw[MsiName.Decode(s.Name)] = s.Data;

        var db = new MsiDatabase();

        if (raw.TryGetValue("\u0005SummaryInformation", out var si))
            db.Summary = SummaryInfo.Parse(si);

        if (!raw.TryGetValue("!_StringPool", out var pool) || !raw.TryGetValue("!_StringData", out var sdata))
            throw new InvalidDataException("MSI is missing its string pool.");

        var strings = ReadStringPool(pool, sdata, out int codepage, out int stringRefSize);
        db.Codepage = codepage;

        // _Columns describes every other table, and is itself fixed-shape.
        MsiColumn[] colsSchema =
        [
            MsiColumn.Str("Table", 64, key: true, nullable: false),
            MsiColumn.Int16("Number", key: true, nullable: false),
            MsiColumn.Str("Name", 64, nullable: false),
            MsiColumn.Int16("Type", nullable: false),
        ];

        var schema = new Dictionary<string, List<(int Num, string Name, int Type)>>(StringComparer.Ordinal);
        if (raw.TryGetValue("!_Columns", out var colBytes))
        {
            foreach (var row in ReadRows(colBytes, colsSchema, strings, stringRefSize))
            {
                string table = (string)row[0]!;
                int type = row[3] is int t ? t & 0xFFFF : 0;
                schema.TryAdd(table, []);
                schema[table].Add((row[1] as int? ?? 0, (string)(row[2] ?? "")!, type));
            }
            foreach (var list in schema.Values) list.Sort((a, b) => a.Num.CompareTo(b.Num));
        }

        foreach (var (tableName, cols) in schema)
        {
            var columns = cols.Select(c => new MsiColumn(c.Name, c.Type)).ToArray();
            var table = new MsiTable(tableName, columns);
            if (raw.TryGetValue("!" + tableName, out var tb))
                foreach (var row in ReadRows(tb, columns, strings, stringRefSize))
                    table.Rows.Add(row);
            db.Tables[tableName] = table;
        }

        // Anything left that is not a table stream is payload (cabinets, icons, binaries).
        foreach (var (name, data) in raw)
            if (!name.StartsWith('!') && !name.StartsWith('\u0005'))
                db.Streams[name] = data;

        return db;
    }

    private static string[] ReadStringPool(byte[] pool, byte[] data, out int codepage, out int stringRefSize)
    {
        int n = pool.Length / 4;
        ushort Word(int i) => BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(i * 2));

        codepage = Word(0) | ((Word(1) & ~0x8000) << 16);
        stringRefSize = (Word(1) & 0x8000) != 0 ? 3 : 2;
        if (codepage == 0) codepage = 1252;

        var enc = codepage == 65001 ? Encoding.UTF8 : Encoding.Latin1;
        var result = new string[Math.Max(n, 1)];
        result[0] = string.Empty;

        int offset = 0;
        for (int i = 1; i < n; i++)
        {
            int len = Word(i * 2);
            int refs = Word(i * 2 + 1);
            if (len == 0 && refs != 0 && i + 1 < n)
            {
                // Long string: the real length spills into the following pool slot.
                len = (refs << 16) | Word(i * 2 + 2);
                i++;
            }
            if (offset + len > data.Length) len = Math.Max(0, data.Length - offset);
            result[i] = enc.GetString(data, offset, len);
            offset += len;
        }
        return result;
    }

    private static List<object?[]> ReadRows(byte[] stream, IReadOnlyList<MsiColumn> columns, string[] strings, int sref)
    {
        int rowLen = columns.Sum(c => c.Width(sref));
        var rows = new List<object?[]>();
        if (rowLen == 0) return rows;
        int count = stream.Length / rowLen;
        for (int i = 0; i < count; i++) rows.Add(new object?[columns.Count]);

        int offset = 0;
        for (int c = 0; c < columns.Count; c++)
        {
            var col = columns[c];
            int w = col.Width(sref);
            for (int r = 0; r < count; r++)
            {
                int o = offset + r * w;
                uint v = w switch
                {
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(o)),
                    3 => (uint)(stream[o] | (stream[o + 1] << 8) | (stream[o + 2] << 16)),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(o)),
                };
                if (col.IsString) rows[r][c] = v == 0 ? null : (v < strings.Length ? strings[v] : null);
                else if (v == 0) rows[r][c] = null;
                // Integers carry a sign bias so that a stored 0 can mean NULL.
                else if (w == 2) rows[r][c] = (int)v - 0x8000;
                else rows[r][c] = unchecked((int)(v ^ 0x80000000));
            }
            offset += count * w;
        }
        return rows;
    }

    // ---------------------------------------------------------------- writing

    public void Save(string path) => File.WriteAllBytes(path, Write());

    public byte[] Write()
    {
        var enc = Codepage == 65001 ? Encoding.UTF8 : Encoding.Latin1;

        // Pass 1: intern every string, counting references so the pool matches usage.
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        var refs = new List<int>();

        int Intern(string? s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (index.TryGetValue(s, out int i)) { refs[i - 1]++; return i; }
            order.Add(s);
            refs.Add(1);
            index[s] = order.Count;
            return order.Count;
        }

        var tableNames = Tables.Keys.Where(t => !t.StartsWith('_')).OrderBy(t => t, StringComparer.Ordinal).ToList();

        foreach (var name in tableNames)
        {
            Intern(name);
            var t = Tables[name];
            foreach (var col in t.Columns) Intern(col.Name);
            foreach (var row in t.Rows)
                for (int c = 0; c < t.Columns.Count; c++)
                    if (t.Columns[c].IsString) Intern(row[c] as string);
        }

        // _Tables and _Columns reference table and column names a second time.
        foreach (var name in tableNames)
        {
            Intern(name);
            foreach (var col in Tables[name].Columns) Intern(col.Name);
        }

        int stringRefSize = order.Count >= 0xFFFF ? 3 : 2;

        // Pass 2: emit the pool and data streams.
        var poolMs = new MemoryStream();
        void PoolWord(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); poolMs.Write(b); }

        PoolWord(Codepage & 0xFFFF);
        PoolWord(((Codepage >> 16) & 0x7FFF) | (stringRefSize == 3 ? 0x8000 : 0));

        var dataMs = new MemoryStream();
        for (int i = 0; i < order.Count; i++)
        {
            byte[] b = enc.GetBytes(order[i]);
            if (b.Length > 0xFFFF)
            {
                PoolWord(0);
                PoolWord(b.Length >> 16);
                PoolWord(b.Length & 0xFFFF);
                PoolWord(Math.Min(refs[i], 0xFFFF));
            }
            else
            {
                PoolWord(b.Length);
                PoolWord(Math.Min(refs[i], 0xFFFF));
            }
            dataMs.Write(b);
        }

        var streams = new List<CfbStream>
        {
            new(MsiName.EncodeTable("_StringPool"), poolMs.ToArray()),
            new(MsiName.EncodeTable("_StringData"), dataMs.ToArray()),
        };

        // _Tables: one string-ref column.
        var tablesMs = new MemoryStream();
        foreach (var name in tableNames) WriteRef(tablesMs, index[name], stringRefSize);
        streams.Add(new CfbStream(MsiName.EncodeTable("_Tables"), tablesMs.ToArray()));

        // _Columns is column-major like every other table: all Table refs, all
        // Numbers, all Names, all Types.
        var colRows = new List<(int Table, int Number, int Name, int Type)>();
        foreach (var name in tableNames)
        {
            var t = Tables[name];
            for (int c = 0; c < t.Columns.Count; c++)
                colRows.Add((index[name], c + 1, index[t.Columns[c].Name], t.Columns[c].Type));
        }
        // Number and Type are ordinary int16 columns, so they carry the same
        // +0x8000 sign bias as any other integer cell.
        var colMs = new MemoryStream();
        foreach (var r in colRows) WriteRef(colMs, r.Table, stringRefSize);
        foreach (var r in colRows) WriteInt16(colMs, r.Number + 0x8000);
        foreach (var r in colRows) WriteRef(colMs, r.Name, stringRefSize);
        foreach (var r in colRows) WriteInt16(colMs, r.Type + 0x8000);
        streams.Add(new CfbStream(MsiName.EncodeTable("_Columns"), colMs.ToArray()));

        // Each data table, column-major.
        foreach (var name in tableNames)
        {
            var t = Tables[name];
            var ms = new MemoryStream();
            for (int c = 0; c < t.Columns.Count; c++)
            {
                var col = t.Columns[c];
                foreach (var row in t.Rows)
                {
                    object? v = row[c];
                    if (col.IsString) WriteRef(ms, v is string s && s.Length > 0 ? index[s] : 0, stringRefSize);
                    else if (col.Width(stringRefSize) == 2) WriteInt16(ms, v is null ? (short)0 : (short)((int)v + 0x8000));
                    else WriteInt32(ms, v is null ? 0 : unchecked((int)((uint)(int)v ^ 0x80000000)));
                }
            }
            streams.Add(new CfbStream(MsiName.EncodeTable(name), ms.ToArray()));
        }

        foreach (var (name, data) in Streams)
            streams.Add(new CfbStream(MsiName.EncodeStream(name), data));

        streams.Add(new CfbStream("\u0005SummaryInformation", Summary.ToBytes()));

        return new CfbWriter(sectorSize: 4096).Write(streams);
    }

    private static void WriteRef(Stream s, int value, int size)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        s.Write(b[..size]);
    }

    private static void WriteInt16(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, unchecked((ushort)value));
        s.Write(b);
    }

    private static void WriteInt32(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        s.Write(b);
    }
}
