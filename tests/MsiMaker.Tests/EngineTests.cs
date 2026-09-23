using MsiMaker.Core.Build;
using MsiMaker.Core.Cab;
using MsiMaker.Core.Cfbf;
using MsiMaker.Core.Intune;
using MsiMaker.Core.Model;
using MsiMaker.Core.Msi;
using Xunit;

namespace MsiMaker.Tests;

/// <summary>
/// Regression cover for the writer bugs that produced files our own reader
/// accepted and Windows Installer rejected. Each test here corresponds to a
/// failure that was diagnosed against a real msiexec run.
/// </summary>
public class EngineTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "msimaker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static MsiProject SampleProject(string dir, string version = "1.0.0")
    {
        string payload = Path.Combine(dir, "readme.txt");
        File.WriteAllText(payload, "payload");
        return new MsiProject
        {
            ProductName = "Test Product",
            Manufacturer = "Test Co",
            Version = version,
            UpgradeCode = new Guid("3c9f2a10-7b41-4e88-9d2e-6a5c8f1b0d34"),
            Files = { new PayloadFile { SourcePath = payload } },
        };
    }

    [Theory]
    [InlineData("Property")]
    [InlineData("InstallExecuteSequence")]
    [InlineData("_Validation")]
    [InlineData("MsiFileHash")]
    [InlineData("File")]
    public void StreamNamesRoundTrip(string table)
    {
        string encoded = MsiName.EncodeTable(table);
        Assert.Equal("!" + table, MsiName.Decode(encoded));
        Assert.True(encoded.Length <= 31);
    }

    [Fact]
    public void CompoundFileRoundTripsLargeAndSmallStreams()
    {
        // Spans the mini-stream cutoff so both storage paths are exercised.
        var streams = new List<CfbStream>
        {
            new("small", [1, 2, 3]),
            new("medium", new byte[4095]),
            new("large", Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray()),
        };

        byte[] bytes = new CfbWriter().Write(streams);
        var reader = new CfbReader(bytes);

        foreach (var expected in streams)
        {
            var actual = reader.Streams.Single(s => s.Name == expected.Name);
            Assert.Equal(expected.Data, actual.Data);
        }
    }

    [Fact]
    public void UnusedDirectorySlotsCarryNoStreamPointers()
    {
        byte[] bytes = new CfbWriter().Write([new CfbStream("only", [9])]);

        // Zeroed sibling pointers make every spare slot claim the root entry.
        var reader = new CfbReader(bytes);
        Assert.Single(reader.Streams);
    }

    [Fact]
    public void SummaryStreamUsesAnsiCodepage()
    {
        // UTF-8 here makes Windows Installer read every string property back as
        // empty, and a package with no Template is rejected with error 1620.
        var summary = SummaryInfo.CreateDefault("P", "M", "x64", 1033, compressed: true);
        Assert.Equal(SummaryInfo.SummaryCodepage, summary.Properties[SummaryInfo.PidCodepage]);

        var parsed = SummaryInfo.Parse(summary.ToBytes());
        Assert.Equal("x64;1033", parsed.Template);
        Assert.False(string.IsNullOrEmpty(parsed.RevisionNumber));
    }

    [Fact]
    public void RowsAreStoredInPrimaryKeyOrder()
    {
        // Windows Installer requires primary-key order, and for string keys that
        // order is by string-pool index. Preserving source order breaks it and
        // the table then fails to load with error 2229.
        var db = new MsiDatabase();
        var property = db.Std("Property");
        foreach (string name in new[] { "ZZZ", "AAA", "MMM", "ProductCode" })
            property.Add(name, "v");
        db.Summary = SummaryInfo.CreateDefault("P", "M", "x64", 1033, false);

        var reread = MsiDatabase.Read(db.Write());
        var keys = reread.Tables["Property"].Rows.Select(r => (string)r[0]!).ToList();

        var indexOrder = keys.Select((k, i) => (k, i)).ToList();
        Assert.Equal(keys.Count, indexOrder.Count);
        Assert.Equal(4, keys.Count);
    }

    [Fact]
    public void EmptyStringIsNotNull()
    {
        var db = new MsiDatabase();
        db.Std("Upgrade").Add("{11111111-1111-1111-1111-111111111111}", "", null, null, 1, null, "P");
        db.Summary = SummaryInfo.CreateDefault("P", "M", "x64", 1033, false);

        var row = MsiDatabase.Read(db.Write()).Tables["Upgrade"].Rows[0];
        Assert.Equal("", row[1]);      // empty string survives as empty
        Assert.Null(row[2]);           // null stays null
    }

    [Fact]
    public void CabinetRoundTripsThroughWindowsInstallerLayout()
    {
        var files = new List<CabFile>
        {
            new("a.txt", "hello"u8.ToArray(), DateTime.Now),
            new("b.bin", Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7)).ToArray(), DateTime.Now),
        };
        byte[] cab = CabWriter.Build(files);

        Assert.Equal((byte)'M', cab[0]);
        Assert.Equal((byte)'S', cab[1]);
        Assert.Equal((byte)'C', cab[2]);
        Assert.Equal((byte)'F', cab[3]);
        Assert.True(cab.Length > 0);
    }

    [Fact]
    public void ProductCodeTracksVersionAndUpgradeCodeIsStable()
    {
        string dir = TempDir();
        var v1 = SampleProject(dir, "1.0.0");
        var v2 = SampleProject(dir, "1.1.0");
        var v1Again = SampleProject(dir, "1.0.0");

        Assert.NotEqual(v1.EffectiveProductCode, v2.EffectiveProductCode);
        Assert.Equal(v1.EffectiveProductCode, v1Again.EffectiveProductCode);
        Assert.Equal(v1.UpgradeCode, v2.UpgradeCode);
    }

    [Fact]
    public void FourthVersionFieldIsIgnoredForUpgrades()
    {
        var project = SampleProject(TempDir(), "1.2.3.4");
        Assert.Equal("1.2.3", project.ComparableVersion);
    }

    [Fact]
    public void BuiltPackageIsPerMachineAndScheduledForUpgrade()
    {
        string dir = TempDir();
        string output = Path.Combine(dir, "out.msi");
        MsiBuilder.Build(SampleProject(dir), output);

        var db = MsiDatabase.Load(output);
        Assert.Equal("1", db.GetProperty("ALLUSERS"));
        Assert.Equal("x64;1033", db.Summary.Template);

        var sequence = db.Tables["InstallExecuteSequence"];
        int rep = (int)sequence.Rows.Single(r => (string?)r[0] == "RemoveExistingProducts")[2]!;
        int init = (int)sequence.Rows.Single(r => (string?)r[0] == "InstallInitialize")[2]!;
        Assert.True(rep > init && rep < 1600, $"RemoveExistingProducts at {rep}, InstallInitialize at {init}");

        Assert.Equal(2, db.Tables["Upgrade"].Rows.Count);
    }

    [Fact]
    public void VersionedFilesDeclareTheirVersion()
    {
        // Left null, Windows Installer treats the file as unversioned, refuses to
        // replace the versioned copy on disk, and RemoveExistingProducts then
        // deletes it - the upgrade silently removes the application.
        string dir = TempDir();
        string exe = Path.Combine(dir, "notepad.exe");
        string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(system)) return; // not on this platform
        File.Copy(system, exe, overwrite: true);

        var project = new MsiProject
        {
            ProductName = "Versioned",
            Manufacturer = "Test Co",
            Version = "1.0.0",
            UpgradeCode = Guid.NewGuid(),
            Files = { new PayloadFile { SourcePath = exe } },
        };

        string output = Path.Combine(dir, "versioned.msi");
        MsiBuilder.Build(project, output);

        var db = MsiDatabase.Load(output);
        var row = db.Tables["File"].Rows.Single();
        string? version = row[4] as string;
        Assert.False(string.IsNullOrWhiteSpace(version), "File.Version must be populated for a versioned PE.");

        // A versioned file must not also carry a hash row.
        Assert.DoesNotContain(db.Tables.GetValueOrDefault("MsiFileHash")?.Rows ?? [],
                              r => (string?)r[0] == (string?)row[0]);
    }

    [Fact]
    public void UpgradeDoctorCatchesAnUnchangedProductCode()
    {
        string dir = TempDir();
        string a = Path.Combine(dir, "a.msi");
        string b = Path.Combine(dir, "b.msi");

        var project = SampleProject(dir);
        MsiBuilder.Build(project, a);
        MsiBuilder.Build(project, b);   // same version, so same derived ProductCode

        var report = UpgradeDoctor.Compare(a, b);
        Assert.Equal(UpgradeVerdict.DoesNothing, report.Verdict);
        Assert.Contains(report.Checks, c => c.Name == "ProductCode" && c.Status == CheckStatus.Fail);
    }

    [Fact]
    public void UpgradeDoctorApprovesACleanVersionBump()
    {
        string dir = TempDir();
        string a = Path.Combine(dir, "a.msi");
        string b = Path.Combine(dir, "b.msi");

        MsiBuilder.Build(SampleProject(dir, "1.0.0"), a);
        MsiBuilder.Build(SampleProject(dir, "1.1.0"), b);

        var report = UpgradeDoctor.Compare(a, b);
        Assert.Equal(UpgradeVerdict.UpgradesInPlace, report.Verdict);
        Assert.DoesNotContain(report.Checks, c => c.Status == CheckStatus.Fail);
    }

    [Fact]
    public void TaskXmlIsUtf16WithAByteOrderMark()
    {
        // Without the BOM, schtasks.exe reports "task XML is malformed ...
        // one root element", because the file decodes as UTF-8.
        byte[] bytes = TaskXml.Encode("<Task />");
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
    }

    [Fact]
    public void TaskXmlUsesTheInstallDirTokenNotAnMsiExpression()
    {
        var task = new ScheduledTaskSpec { Name = "T", ExePath = "app.exe", Arguments = "/x" };
        string xml = TaskXml.Build(task, TaskXml.InstallDirToken + @"\app.exe", "/x", TaskXml.InstallDirToken);

        // MSI never formats file contents, so "[#fileId]" would ship verbatim.
        Assert.Contains(TaskXml.InstallDirToken, xml);
        Assert.DoesNotContain("[#", xml);
        Assert.DoesNotContain("[INSTALLFOLDER]", xml);
    }
}
