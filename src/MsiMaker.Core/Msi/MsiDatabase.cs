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

    private static readonly HashSet<string> InternalTables =
        new(StringComparer.Ordinal) { "_Tables", "_Columns", "_StringPool", "_StringData" };

    /// <summary>
    /// Populates _Validation for the tables currently in the database. Windows
    /// Installer reads this whenever it recreates a table, which an administrative
    /// install does for every table in the package.
    /// </summary>
    public void AddValidation()
    {
        Tables.Remove("_Validation");
        var validation = new MsiTable("_Validation",
            MsiSchema.ValidationColumns.Select(c => new MsiColumn(c.Name, c.Type)).ToArray());

        foreach (var row in MsiSchema.ValidationRows)
        {
            if (row.Length == 0 || row[0] is not string table) continue;
            if (!Tables.ContainsKey(table)) continue;
            validation.Rows.Add((object?[])row.Clone());
        }
        Tables["_Validation"] = validation;
    }

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
                // A string reference of 0, or one past the end of the pool, reads back as
        // null - which as a dictionary key throws rather than being ignored.
        if (row[0] is not string table) continue;
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

        // A string index is not a pool slot. A string longer than 65535 bytes is
        // written as two consecutive 4-byte slots - (0, lengthHigh) then
        // (lengthLow, refcount) - but it is still one string and takes one index,
        // exactly as the writer above assigns them.
        //
        // Conflating the two shifts every string after the first long one by one
        // position. Nothing throws: the tables still parse, every value is still a
        // real string from the pool, just the wrong one. That reaches the upgrade
        // check, which then answers "will this upgrade?" confidently and wrongly.
        int offset = 0;
        int index = 1;
        for (int slot = 1; slot < n && index < result.Length; slot++, index++)
        {
            int len = Word(slot * 2);
            int refs = Word(slot * 2 + 1);
            if (len == 0 && refs != 0 && slot + 1 < n)
            {
                // Cast before shifting - refs is a ushort, so refs << 16 is negative
                // for anything from 0x8000 up, and a negative length slips past the
                // clamp below straight into Encoding.GetString.
                len = (int)(((uint)refs << 16) | Word(slot * 2 + 2));
                slot++;
            }
            if (len < 0 || offset + (long)len > data.Length) len = Math.Max(0, data.Length - offset);
            result[index] = enc.GetString(data, offset, len);
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

        // Index 0 means NULL. An empty string is a different thing: it gets a real
        // pool slot of zero length. Collapsing "" to NULL corrupts non-nullable
        // columns - _Validation is full of empty cells - and the table then fails
        // to load.
        int Intern(string? s)
        {
            if (s is null) return 0;
            if (index.TryGetValue(s, out int i)) { refs[i - 1]++; return i; }
            order.Add(s);
            refs.Add(1);
            index[s] = order.Count;
            return order.Count;
        }

        // _Tables, _Columns and the string pool describe the database and are
        // emitted separately. _Validation is an ordinary table and must be listed,
        // or Windows Installer cannot recreate tables (error 2211).
        var tableNames = Tables.Keys
            .Where(t => !InternalTables.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        // Intern once per reference that will actually be emitted, walking the
        // streams in write order. The stored refcount has to match real usage:
        // Windows Installer manages the pool with it, and an under-counted
        // string makes whole tables fail to load.

        // _Tables: one reference per table name.
        foreach (var name in tableNames) Intern(name);

        // _Columns: one row per column, each naming its table and its column.
        foreach (var name in tableNames)
            foreach (var col in Tables[name].Columns)
            {
                Intern(name);
                Intern(col.Name);
            }

        // Table streams: one reference per non-null string cell.
        foreach (var name in tableNames)
        {
            var t = Tables[name];
            foreach (var row in t.Rows)
                for (int c = 0; c < t.Columns.Count; c++)
                    if (t.Columns[c].IsString) Intern(row[c] as string);
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
            if (b.Length == 0)
            {
                // len == 0 with a non-zero refcount means "long string", so an
                // empty string must store a refcount of 0.
                PoolWord(0);
                PoolWord(0);
            }
            else if (b.Length > 0xFFFF)
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
            var sortedRows = SortByPrimaryKey(t, index);
            var ms = new MemoryStream();
            for (int c = 0; c < t.Columns.Count; c++)
            {
                var col = t.Columns[c];
                foreach (var row in sortedRows)
                {
                    object? v = row[c];
                    if (col.IsString) WriteRef(ms, v is string s ? index[s] : 0, stringRefSize);
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

    /// <summary>
    /// Windows Installer requires a table's rows to be stored in primary-key
    /// order, and for string keys that order is by string-pool index, not by the
    /// text. Because indices are assigned afresh on every write, rows have to be
    /// re-sorted here; leaving the original order produces a database whose
    /// tables our own reader accepts but msi.dll refuses to load (error 2229).
    /// </summary>
    private static List<object?[]> SortByPrimaryKey(MsiTable table, Dictionary<string, int> index)
    {
        var keyColumns = new List<int>();
        for (int i = 0; i < table.Columns.Count; i++)
            if (table.Columns[i].IsKey) keyColumns.Add(i);

        if (keyColumns.Count == 0) return table.Rows;

        long SortKey(object?[] row, int column) => row[column] switch
        {
            // Index 0 is NULL, which sorts first - exactly as a zero cell does.
            string s => index.TryGetValue(s, out int i) ? i : 0,
            int v => v,
            _ => 0,
        };

        var sorted = new List<object?[]>(table.Rows);
        sorted.Sort((a, b) =>
        {
            foreach (int c in keyColumns)
            {
                int cmp = SortKey(a, c).CompareTo(SortKey(b, c));
                if (cmp != 0) return cmp;
            }
            return 0;
        });
        return sorted;
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
