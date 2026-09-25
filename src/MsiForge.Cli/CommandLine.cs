using MsiForge.Core.Build;
using MsiForge.Core.Diagnostics;
using MsiForge.Core.Intune;
using MsiForge.Core.Model;
using MsiForge.Core.Msi;
using MsiForge.Core.Payload;
using MsiForge.Core.Blueprints;
using MsiForge.Core.Signing;

namespace MsiForge.Cli;

/// <summary>
/// The command line, as a callable entry point rather than a Main.
///
/// The desktop app links this and dispatches to it when it is started with a
/// command, so a release is one executable that is both the GUI and the CLI.
/// Shipping two binaries invites the version skew where someone scripts with
/// last month&apos;s msiforge.exe against this month&apos;s app.
/// </summary>
public static class CommandLine
{
    /// <summary>Every verb Run accepts, so a caller can tell a command from a file path.</summary>
    public static readonly IReadOnlySet<string> Commands = new HashSet<string>(StringComparer.Ordinal)
    {
        "build", "pack", "analyze", "blueprints", "blueprint", "upgrade", "intune", "intunewin",
        "sign", "verify-signature", "doctor", "selftest", "explain", "inspect", "roundtrip",
        "mktest", "taskxml", "subset", "help", "--help", "-h",
    };

    public static int Run(string[] args)
    {

        if (args.Length == 0)
        {
            Console.WriteLine("""
                msiforge - cross-platform MSI builder

                  build     <blueprint|bundle> [out.msi] Build an MSI from a blueprint or .msiforge bundle
                  pack      <blueprint.json> [out]      Bundle a blueprint and its payload into one file
                  analyze   <installer.exe> [out.json]  Identify the installer and its silent switches
                  blueprints                           List the preloaded blueprints for awkward applications
                  blueprint <id> <out.json> [payload]  Start a blueprint from a preloaded one
                  upgrade   <old.msi> <new.msi>        Will Intune actually upgrade? Check before shipping
                  intune    <file.msi>                 Exactly what to enter in the Intune portal
                  intunewin <file.msi> [out]           Wrap an MSI as a Win32 .intunewin package
                  sign      <file.msi> [blueprint.json] Authenticode-sign a package
                  verify-signature <file.msi>          Check an existing signature
                  doctor                               What this machine can and cannot do
                  selftest                             Build and verify a package end to end
                  explain   <msiexec.log>              Say what actually went wrong in an install log
                  inspect   <file.msi>                 Show identity, upgrade and table summary
                  roundtrip <file.msi> <out.msi>       Read, rewrite and verify table fidelity
                """);
            return 0;
        }

        try
        {
            switch (args[0])
            {
                case "mktest": return MakeMinimal(args[1]);
                case "taskxml": return ShowTaskXml(args[1]);
                case "subset": return Subset(args[1], args[2], args[3..]);
                case "build": return BuildProject(args[1], args.Length > 2 ? args[2] : null);
                case "pack": return PackProject(args[1], args.Length > 2 ? args[2] : null);
                case "analyze": return Analyze(args[1], args.Length > 2 ? args[2] : null);
                case "blueprints": return ListBlueprints();
                case "blueprint":
                    if (args.Length < 3)
                    {
                        Console.Error.WriteLine(
                            "usage: msiforge blueprint <id> <out.json> [payload]\n"
                            + "  Starts a blueprint from a preloaded one. Run 'msiforge blueprints' for the list.");
                        return 2;
                    }
                    return WriteBlueprint(args[1], args[2], args.Length > 3 ? args[3] : null);
                case "upgrade": return UpgradeCheck(args[1], args[2]);
                case "intune": return ShowIntuneConfiguration(args[1]);
                case "intunewin": return MakeIntuneWin(args[1], args.Length > 2 ? args[2] : null);
                case "selftest": return MsiForge.Cli.SelfTest.Run();
                case "doctor": return MsiForge.Cli.SelfTest.Doctor();
                case "sign": return SignMsi(args[1], args.Length > 2 ? args[2] : null);
                case "verify-signature": return VerifySignature(args[1]);
                case "explain": return ExplainLog(args[1]);
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

    }

    static int Analyze(string path, string? projectOut)
    {
        var a = InstallerDetective.Analyse(path);
        Console.WriteLine($"File          {Path.GetFileName(a.Path)}  ({new FileInfo(a.Path).Length:N0} bytes)");
        Console.WriteLine($"Installer     {a.KindName}   (confidence {a.Confidence}%)");
        Console.WriteLine($"Architecture  {a.Architecture}");
        if (a.Version is not null)
        {
            Console.WriteLine($"Company       {a.Version.CompanyName ?? "-"}");
            Console.WriteLine($"Product       {a.Version.ProductName ?? "-"}");
            Console.WriteLine($"Version       file={a.Version.FileVersion ?? "-"}  product={a.Version.ProductVersion ?? "-"}");
            Console.WriteLine($"  -> MSI       {InstallerDetective.NormaliseVersion(a.Version.ProductVersion ?? a.Version.FileVersion)}  (three fields; the rest is ignored on upgrade)");
        }
        Console.WriteLine();
        Console.WriteLine($"Silent        {a.SilentArguments ?? "(unknown)"}");
        if (a.SilentArgumentsWithDirectory is not null)
            Console.WriteLine($"With dir      {a.SilentArgumentsWithDirectory}");
        if (a.UninstallHint is not null)
            Console.WriteLine($"Uninstall     {a.UninstallHint}");

        Console.WriteLine();
        foreach (var e in a.Evidence) Console.WriteLine($"  evidence: {e}");
        foreach (var w in a.Warnings) Console.WriteLine($"  warning:  {w}");

        // An installer that installs via its own MSI cannot be wrapped, so refuse
        // to write a starter project for one rather than hand back a package that
        // fails at install. The warning above says why and what to do instead.
        if (a.Kind == InstallerKind.MsiWrappedExe)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("rejected: this installer installs via its own MSI and cannot be wrapped. "
                                    + "Deploy the extracted MSI directly, as noted above.");
            return 2;
        }

        if (projectOut is not null)
        {
            var project = InstallerDetective.ToProject(a);
            project.Save(projectOut);
            Console.WriteLine();
            Console.WriteLine($"wrote starter blueprint {projectOut} (UpgradeCode {DeterministicGuid.Format(project.UpgradeCode)} - keep it stable forever)");
        }
        return 0;
    }

    /// <summary>
    /// Lists the library. The awkwardness note is printed in full rather than
    /// truncated: it is the reason the blueprint exists, and an admin scanning the
    /// list is choosing on it.
    /// </summary>
    static int ListBlueprints()
    {
        var library = BlueprintLibrary.Load();
        var blueprints = library.List();

        // The list arrives grouped by category; print a header when it changes.
        string? group = null;
        foreach (var r in blueprints)
        {
            if (r.Category != group)
            {
                group = r.Category;
                Console.WriteLine();
                Console.WriteLine(group.ToUpperInvariant());
            }
            string source = r.FromUserFolder ? " (yours)" : "";
            Console.WriteLine($"  {r.Id,-22}{source}  {r.Name} - {r.Vendor}");
            WriteWrapped(r.Awkwardness, "      ");
            Console.WriteLine();
        }

        Console.WriteLine($"{blueprints.Count} blueprints. Add your own as .json in {library.UserDirectory}");
        Console.WriteLine("  (override that folder with MSIFORGE_BLUEPRINTS; a file reusing a built-in id replaces it)");
        foreach (var p in library.Problems) Console.WriteLine($"  warning: {p}");
        return 0;
    }

    static int WriteBlueprint(string id, string output, string? payload)
    {
        var library = BlueprintLibrary.Load();
        var blueprint = library.Find(id);
        if (blueprint is null)
        {
            Console.Error.WriteLine($"No blueprint '{id}'. Run 'msiforge blueprints' for the library.");
            return 2;
        }

        var project = BlueprintLibrary.Instantiate(blueprint, payload);
        project.Save(output);

        Console.WriteLine($"wrote {output} from blueprint '{blueprint.Id}'");
        Console.WriteLine($"  ProductName    {project.ProductName}");
        Console.WriteLine($"  ProductVersion {project.Version}  (set this to the version you downloaded)");
        Console.WriteLine($"  UpgradeCode    {DeterministicGuid.Format(project.UpgradeCode)}  (stable - never change it)");
        Console.WriteLine($"  Payload        {(payload is null ? Blueprint.PayloadToken + "  (fill this in before building)" : Path.GetFullPath(payload))}");
        if (blueprint.SetupFileHint is not null) Console.WriteLine($"  Expecting      {blueprint.SetupFileHint}");
        if (blueprint.DownloadUrl is not null) Console.WriteLine($"  Download       {blueprint.DownloadUrl}");

        Console.WriteLine();
        Console.WriteLine("why this one is awkward:");
        WriteWrapped(blueprint.Awkwardness, "  ");

        if (blueprint.DownloadNotes is not null)
        {
            Console.WriteLine();
            Console.WriteLine("getting the payload:");
            WriteWrapped(blueprint.DownloadNotes, "  ");
        }

        if (blueprint.Notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("notes:");
            foreach (var note in blueprint.Notes) WriteWrapped(note, "    ", "  - ");
        }

        // Report now rather than at build time: a project that cannot build is
        // cheaper to find out about while the admin is still looking at the blueprint.
        project.ExpandFolders();
        var errors = project.Validate();
        if (errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("not buildable yet:");
            foreach (var e in errors) Console.WriteLine($"  {e}");
        }
        return 0;
    }

    /// <summary>
    /// Wraps prose to a readable width; blueprint notes are paragraphs, not labels.
    /// <paramref name="firstIndent"/> gives a bullet its hanging indent.
    /// </summary>
    static void WriteWrapped(string text, string indent, string? firstIndent = null)
    {
        const int width = 76;
        string prefix = firstIndent ?? indent;
        var line = new System.Text.StringBuilder();
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                Console.WriteLine(prefix + line);
                prefix = indent;
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) Console.WriteLine(prefix + line);
    }

    static int SignMsi(string msiPath, string? projectPath)
    {
        var options = projectPath is null ? new SigningOptions { Enabled = true }
                                          : MsiProject.Load(projectPath).Signing;
        options.Enabled = true;

        var result = CodeSigner.Sign(msiPath, options);
        if (result.Signed)
        {
            // The tool that actually signed, which for a PFX is the in-process
            // managed signer - naming whatever external signer happens to be
            // installed would say the opposite of what took place.
            Console.WriteLine($"signed {Path.GetFileName(msiPath)} with {result.Tool}");
            return 0;
        }

        Console.WriteLine($"NOT signed: {result.Detail}");
        Console.WriteLine($"  external signer found: {CodeSigner.FindSigner(options.SignerPath) ?? "(none)"}");
        return 1;
    }

    static int VerifySignature(string msiPath)
    {
        var result = CodeSigner.Verify(msiPath);
        Console.WriteLine(result.Signed
            ? $"signature valid ({result.Tool})"
            : $"signature NOT valid: {result.Detail}");
        return result.Signed ? 0 : 1;
    }

    static int ShowIntuneConfiguration(string msiPath)
    {
        var config = IntuneConfiguration.From(msiPath);
        Console.Write(config.Describe());
        return 0;
    }

    static int MakeIntuneWin(string msiPath, string? output)
    {
        output ??= Path.ChangeExtension(msiPath, ".intunewin");
        var r = IntuneWinPackager.PackageMsi(msiPath, output);
        Console.WriteLine($"wrote {r.Path}");
        Console.WriteLine($"  setup file  {r.SetupFile}");
        Console.WriteLine($"  payload     {r.PlainSize:N0} bytes -> {r.EncryptedSize:N0} encrypted");
        Console.WriteLine($"  detection   MSI ProductCode rule (no script needed)");
        return 0;
    }

    static int UpgradeCheck(string previous, string candidate)
    {
        var report = UpgradeDoctor.Compare(previous, candidate);
        Console.WriteLine($"{Path.GetFileName(previous)}  ->  {Path.GetFileName(candidate)}");
        Console.WriteLine();
        foreach (var c in report.Checks)
        {
            string mark = c.Status switch
            {
                CheckStatus.Pass => "OK  ",
                CheckStatus.Warn => "WARN",
                _ => "FAIL",
            };
            Console.WriteLine($"  [{mark}] {c.Name,-24} {c.Detail}");
        }
        Console.WriteLine();
        Console.WriteLine($"  => {report.Summary}");
        return report.Verdict == UpgradeVerdict.UpgradesInPlace ? 0 : 1;
    }

    /// <summary>Prints the Task Scheduler XML a project would register.</summary>
    static int ShowTaskXml(string projectPath)
    {
        var project = MsiProject.Load(projectPath);
        foreach (var task in project.Tasks)
        {
            Console.WriteLine($"==== {task.FullName} ====");
            // Mirror what the builder writes: an environment-variable path the
            // target expands, not an MSI formatted expression.
            string root = project.Root == MsiForge.Core.Model.InstallRoot.CommonAppData
                ? "%ProgramData%"
                : project.Platform == MsiForge.Core.Model.MsiPlatform.X86
                    ? "%ProgramFiles(x86)%"
                    : "%ProgramFiles%";
            var segments = new List<string> { root };
            if (!string.IsNullOrWhiteSpace(project.ManufacturerFolder)) segments.Add(project.ManufacturerFolder!);
            segments.Add(project.EffectiveInstallFolder);
            string installDir = string.Join("\\", segments);

            Console.WriteLine(MsiForge.Core.Build.TaskXml.Build(
                task, installDir + "\\" + task.ExePath.Replace('/', '\\'), task.Arguments, installDir));
            Console.WriteLine();
        }
        return 0;
    }

    /// <summary>
    /// Rewrites an MSI keeping only the named tables, so a reader disagreement can
    /// be bisected down to the table that triggers it.
    /// </summary>
    static int Subset(string input, string output, string[] keep)
    {
        var db = MsiDatabase.Load(input);
        var wanted = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        foreach (var name in db.Tables.Keys.ToList())
            if (!wanted.Contains(name)) db.Tables.Remove(name);
        db.Streams.Clear();
        db.Save(output);
        Console.WriteLine($"{Path.GetFileName(output)}: {db.Tables.Count} tables "
                          + $"({string.Join(", ", db.Tables.Keys.OrderBy(k => k, StringComparer.Ordinal))})");
        return 0;
    }

    /// <summary>
    /// Smallest database our writer can emit: one table, the string pool and the
    /// summary stream. Used to reproduce reader disagreements in a file small
    /// enough to read by hand.
    /// </summary>
    static int MakeMinimal(string output)
    {
        var db = new MsiDatabase();
        db.SetProperty("ProductName", "Min");
        db.SetProperty("ProductVersion", "1.0.0");
        db.SetProperty("ProductCode", "{11111111-1111-1111-1111-111111111111}");
        db.SetProperty("Manufacturer", "M");
        db.Summary = SummaryInfo.CreateDefault("Min", "M", "x64", 1033, compressed: false);
        db.Save(output);
        Console.WriteLine($"wrote {output} ({new FileInfo(output).Length:N0} bytes), "
                          + $"tables: {string.Join(", ", db.Tables.Keys)}");
        return 0;
    }

    static int PackProject(string projectPath, string? output)
    {
        var project = MsiProject.Load(projectPath);
        output ??= Path.ChangeExtension(projectPath, ProjectBundle.Extension);

        var info = ProjectBundle.Save(project, output);
        Console.WriteLine($"packed {info.Path}");
        Console.WriteLine($"  {info.FileCount:N0} payload file(s), {info.PayloadBytes:N0} bytes");
        Console.WriteLine($"  bundle {info.PackedBytes:N0} bytes");
        Console.WriteLine("  self-contained: version this one file and the package is reproducible.");
        return 0;
    }

    static int BuildProject(string projectPath, string? outputPath)
    {
        var project = ProjectBundle.LoadAny(projectPath);

        // MsiProject.Load already resolves relative payload paths against the
        // project file's folder.
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;

        outputPath ??= Path.Combine(baseDir, "out",
            $"{MsiProject.Sanitize(project.ProductName)}-{project.ComparableVersion}.msi");

        var result = MsiBuilder.Build(project, outputPath);

        Console.WriteLine($"built {result.MsiPath}");
        Console.WriteLine($"  ProductName    {project.ProductName}");
        Console.WriteLine($"  ProductVersion {result.Version}");
        Console.WriteLine($"  ProductCode    {DeterministicGuid.Format(result.ProductCode)}  (derived from version)");
        Console.WriteLine($"  UpgradeCode    {DeterministicGuid.Format(result.UpgradeCode)}  (stable)");
        Console.WriteLine($"  PackageCode    {result.PackageCode}");
        Console.WriteLine($"  Size           {result.SizeBytes:N0} bytes");
        foreach (var w in result.Warnings) Console.WriteLine($"  warning: {w}");

        // Signing is last: any edit to the database would invalidate the signature.
        if (project.Signing.Enabled)
        {
            var signed = CodeSigner.Sign(result.MsiPath, project.Signing);
            Console.WriteLine(signed.Signed
                ? $"  Signed         {signed.Tool}"
                : $"  SIGNING FAILED {signed.Detail}");
            if (!signed.Signed) return 1;
        }
        return 0;
    }

    static int ExplainLog(string logPath)
    {
        var findings = InstallLogReader.Read(logPath);
        string? action = InstallLogReader.FailingAction(logPath);

        Console.WriteLine($"{Path.GetFileName(logPath)}  ({new FileInfo(logPath).Length:N0} bytes)");
        if (action is not null) Console.WriteLine($"failing action: {action}");

        if (findings.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Nothing recognised. Search the log for 'Return value 3' and read the lines above it.");
            return 0;
        }

        foreach (var f in findings)
        {
            Console.WriteLine();
            Console.WriteLine($"[{f.Code}] {f.Summary}   (line {f.LineNumber})");
            Console.WriteLine();
            WriteWrapped(f.Meaning, "    ");
            Console.WriteLine();
            Console.WriteLine("  fix");
            WriteWrapped(f.Fix, "    ");
            Console.WriteLine();
            Console.WriteLine($"  log: {f.Evidence}");
        }
        return 0;
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

        // Compare every table the reader surfaced, _Validation included: Windows
        // Installer consults it when loading tables, so corruption there breaks
        // otherwise-valid tables.
        var expected = original.Tables.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();
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

            // Row order is a storage detail - rows are written in primary-key order,
            // which depends on freshly assigned string indices - so compare as sets.
            static string RowKey(object?[] row) =>
                string.Join('\u001f', row.Select(v => v?.ToString() ?? "\u0000"));

            var wantRows = want.Rows.Select(RowKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var gotRows = got.Rows.Select(RowKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
            for (int r = 0; r < wantRows.Count; r++)
                if (wantRows[r] != gotRows[r])
                    Fail($"{name}: row {wantRows[r].Replace('\u001f', '|')} -> {gotRows[r].Replace('\u001f', '|')}");
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
            ? $"  OK - {expected.Count} tables, {original.Tables.Sum(t => t.Value.Rows.Count)} rows, "
              + $"{original.Streams.Count} streams identical"
            : $"  {problems} mismatch(es)");
        return problems == 0 ? 0 : 1;
    }

}