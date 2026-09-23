using MsiMaker.Core.Msi;

if (args.Length == 0)
{
    Console.WriteLine("""
        msimaker - cross-platform MSI builder

          inspect   <file.msi>              Show identity, upgrade and table summary
          roundtrip <file.msi> <out.msi>    Read, rewrite and verify table fidelity
        """);
    return 0;
}

try
{
    switch (args[0])
    {
        case "inspect": return Inspect(args[1]);
        case "roundtrip": return RoundTrip(args[1], args[2]);
        default:
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static int Inspect(string path)
{
    var db = MsiDatabase.Load(path);
    Console.WriteLine($"File            {Path.GetFileName(path)}  ({new FileInfo(path).Length:N0} bytes)");
    Console.WriteLine($"ProductName     {db.GetProperty("ProductName")}");
    Console.WriteLine($"ProductVersion  {db.GetProperty("ProductVersion")}");
    Console.WriteLine($"ProductCode     {db.GetProperty("ProductCode")}");
    Console.WriteLine($"UpgradeCode     {db.GetProperty("UpgradeCode")}");
    Console.WriteLine($"Manufacturer    {db.GetProperty("Manufacturer")}");
    Console.WriteLine($"ALLUSERS        {db.GetProperty("ALLUSERS") ?? "(per-user!)"}");
    Console.WriteLine($"Template        {db.Summary.Template}");
    Console.WriteLine($"PackageCode     {db.Summary.RevisionNumber}");
    Console.WriteLine($"SourceFlags     {db.Summary.WordCount}  (bit1 = cabinet)");

    if (db.Tables.TryGetValue("Upgrade", out var up) && up.Rows.Count > 0)
    {
        Console.WriteLine("\nUpgrade table");
        foreach (var r in up.Rows)
            Console.WriteLine($"  code={r[0]} min={r[1] ?? "-"} max={r[2] ?? "-"} attr={r[4]} prop={r[6]}");
    }

    if (db.Tables.TryGetValue("InstallExecuteSequence", out var seq))
    {
        var rep = seq.Rows.FirstOrDefault(r => (r[0] as string) == "RemoveExistingProducts");
        var ini = seq.Rows.FirstOrDefault(r => (r[0] as string) == "InstallInitialize");
        Console.WriteLine($"\nRemoveExistingProducts  seq={rep?[2]?.ToString() ?? "ABSENT"}"
                          + $"   (InstallInitialize={ini?[2]?.ToString() ?? "?"})");
    }

    Console.WriteLine($"\nTables ({db.Tables.Count}):");
    foreach (var (name, t) in db.Tables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {name,-26} {t.Columns.Count,2} cols  {t.Rows.Count,4} rows");
    if (db.Streams.Count > 0)
        Console.WriteLine($"\nStreams: {string.Join(", ", db.Streams.Select(s => $"{s.Key} ({s.Value.Length:N0}b)"))}");
    return 0;
}

static int RoundTrip(string input, string output)
{
    var original = MsiDatabase.Load(input);
    File.WriteAllBytes(output, original.Write());
    var reread = MsiDatabase.Load(output);

    int problems = 0;
    void Fail(string msg) { Console.WriteLine($"  MISMATCH {msg}"); problems++; }

    Console.WriteLine($"round-trip {Path.GetFileName(input)} -> {Path.GetFileName(output)}");
    Console.WriteLine($"  size {new FileInfo(input).Length:N0} -> {new FileInfo(output).Length:N0} bytes");

    // _Validation is intentionally not reproduced; it is advisory metadata.
    var expected = original.Tables.Keys.Where(t => !t.StartsWith('_')).OrderBy(t => t, StringComparer.Ordinal).ToList();
    foreach (var name in expected)
    {
        if (!reread.Tables.TryGetValue(name, out var got)) { Fail($"table {name} missing"); continue; }
        var want = original.Tables[name];
        if (got.Columns.Count != want.Columns.Count) { Fail($"{name} column count {want.Columns.Count} -> {got.Columns.Count}"); continue; }
        for (int c = 0; c < want.Columns.Count; c++)
        {
            if (want.Columns[c].Name != got.Columns[c].Name) Fail($"{name}.{c} name {want.Columns[c].Name} -> {got.Columns[c].Name}");
            if (want.Columns[c].Type != got.Columns[c].Type) Fail($"{name}.{want.Columns[c].Name} type 0x{want.Columns[c].Type:X4} -> 0x{got.Columns[c].Type:X4}");
        }
        if (got.Rows.Count != want.Rows.Count) { Fail($"{name} row count {want.Rows.Count} -> {got.Rows.Count}"); continue; }
        for (int r = 0; r < want.Rows.Count; r++)
            for (int c = 0; c < want.Columns.Count; c++)
            {
                object? a = want.Rows[r][c], b = got.Rows[r][c];
                if (!Equals(a, b)) Fail($"{name}[{r}].{want.Columns[c].Name}: {a ?? "null"} -> {b ?? "null"}");
            }
    }

    foreach (var (name, data) in original.Streams)
    {
        if (!reread.Streams.TryGetValue(name, out var got)) { Fail($"stream {name} missing"); continue; }
        if (!data.AsSpan().SequenceEqual(got)) Fail($"stream {name} differs ({data.Length} vs {got.Length} bytes)");
    }

    if (original.Summary.Template != reread.Summary.Template) Fail("summary Template");
    if (original.Summary.RevisionNumber != reread.Summary.RevisionNumber) Fail("summary RevisionNumber");
    if (original.Summary.WordCount != reread.Summary.WordCount) Fail("summary WordCount");

    Console.WriteLine(problems == 0
        ? $"  OK - {expected.Count} tables, {original.Tables.Where(t => !t.Key.StartsWith('_')).Sum(t => t.Value.Rows.Count)} rows, "
          + $"{original.Streams.Count} streams identical"
        : $"  {problems} mismatch(es)");
    return problems == 0 ? 0 : 1;
}
