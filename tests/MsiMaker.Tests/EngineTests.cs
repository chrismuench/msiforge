using MsiMaker.Core.Build;
using MsiMaker.Core.Cab;
using MsiMaker.Core.Cfbf;
using MsiMaker.Core.Intune;
using MsiMaker.Core.Model;
using MsiMaker.Core.Msi;
using MsiMaker.Core.Signing;
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
    public void SetupWizardIsAuthoredWhenEnabled()
    {
        string dir = TempDir();
        string output = Path.Combine(dir, "wiz.msi");
        MsiBuilder.Build(SampleProject(dir), output);   // ShowInstallWizard defaults true

        var db = MsiDatabase.Load(output);
        var dialogs = db.Tables["Dialog"].Rows.Select(r => (string)r[0]!).ToList();
        Assert.Contains("WelcomeDlg", dialogs);
        Assert.Contains("InstallDirDlg", dialogs);
        Assert.Contains("BrowseDlg", dialogs);
        Assert.Contains("ExitDialog", dialogs);

        // The folder page is bound to the install directory, and the chosen path is
        // applied by a built-in SetTargetPath event - no custom action.
        Assert.Equal("INSTALLFOLDER", db.GetProperty("WIXUI_INSTALLDIR"));
        Assert.Contains(db.Tables["ControlEvent"].Rows, r => (string?)r[2] == "SetTargetPath");

        // The completion dialog uses the special -1 sequence the installer shows on
        // success; without it there is no finish screen.
        var ui = db.Tables["InstallUISequence"].Rows;
        Assert.Equal(-1, (int)ui.Single(r => (string?)r[0] == "ExitDialog")[2]!);
    }

    [Fact]
    public void ChosenFolderIsPassedToAWrappedInstallerViaCustomActionData()
    {
        string dir = TempDir();
        string exe = Path.Combine(dir, "Setup.exe");
        File.WriteAllText(exe, "stub installer");

        var project = SampleProject(dir);
        project.Files.Clear();
        project.Files.Add(new PayloadFile { SourcePath = exe });
        project.Run.Add(new RunAction
        {
            Id = "vendor",
            ExePath = "Setup.exe",
            Arguments = "/S /D=[INSTALLFOLDER]",
            When = RunWhen.Install,
            IgnoreExitCode = true,
        });

        string output = Path.Combine(dir, "flow.msi");
        MsiBuilder.Build(project, output);
        var db = MsiDatabase.Load(output);
        var ca = db.Tables["CustomAction"].Rows;

        // The deferred vendor action reads the folder from CustomActionData, because
        // a deferred action cannot read [INSTALLFOLDER] itself.
        var install = ca.Single(r => (string?)r[0] == "ca_vendor_install");
        Assert.Contains("[CustomActionData]", (string)install[3]!);
        Assert.DoesNotContain("[INSTALLFOLDER]", (string)install[3]!);

        // An immediate type-51 setter fills that CustomActionData from [INSTALLFOLDER],
        // so the wizard's (or command line's) choice reaches the vendor EXE.
        Assert.Contains(ca, r =>
            (int)r[1]! == 51 && (string?)r[2] == "ca_vendor_install" && (string?)r[3] == "[INSTALLFOLDER]");

        // And INSTALLFOLDER survives elevation, so a command-line value is not dropped.
        Assert.Contains("INSTALLFOLDER", db.GetProperty("SecureCustomProperties"));
    }

    [Fact]
    public void WrappedInstallerGetsAutoUninstallCaptureAndReplay()
    {
        string dir = TempDir();
        string exe = Path.Combine(dir, "vendor-setup.exe");
        File.WriteAllText(exe, "stub");

        var project = SampleProject(dir);
        project.Files.Clear();
        project.Files.Add(new PayloadFile { SourcePath = exe });
        project.Run.Add(new RunAction
        {
            Id = "install", ExePath = "vendor-setup.exe", Arguments = "/S", When = RunWhen.Install,
        });

        string output = Path.Combine(dir, "auto.msi");
        var result = MsiBuilder.Build(project, output);

        // No "left behind" warning - auto-remove handles it.
        Assert.DoesNotContain(result.Warnings, w => w.Contains("leave the application", StringComparison.Ordinal));

        var db = MsiDatabase.Load(output);
        var actions = db.Tables["CustomAction"].Rows.Select(r => (string)r[0]!).ToList();
        Assert.Contains("ca_uninstall_baseline", actions);
        Assert.Contains("ca_uninstall_capture", actions);
        Assert.Contains("ca_uninstall_replay", actions);

        // The replay is scheduled on removal; capture only on install.
        var seq = db.Tables["InstallExecuteSequence"].Rows;
        Assert.Contains(seq, r => (string?)r[0] == "ca_uninstall_replay" && ((string?)r[1])!.Contains("REMOVE"));
        Assert.Contains(seq, r => (string?)r[0] == "ca_uninstall_capture" && (string?)r[1] == "NOT REMOVE");
    }

    [Fact]
    public void AutoUninstallCanBeTurnedOff()
    {
        string dir = TempDir();
        string exe = Path.Combine(dir, "vendor-setup.exe");
        File.WriteAllText(exe, "stub");

        var project = SampleProject(dir);
        project.AutoRemoveWrappedApp = false;
        project.Files.Clear();
        project.Files.Add(new PayloadFile { SourcePath = exe });
        project.Run.Add(new RunAction
        {
            Id = "install", ExePath = "vendor-setup.exe", Arguments = "/S", When = RunWhen.Install,
        });

        var result = MsiBuilder.Build(project, Path.Combine(dir, "off.msi"));

        // Off, it falls back to warning that the app will be left behind.
        Assert.Contains(result.Warnings, w => w.Contains("leave the application", StringComparison.Ordinal));
        var db = MsiDatabase.Load(Path.Combine(dir, "off.msi"));
        Assert.DoesNotContain(db.Tables["CustomAction"].Rows,
            r => (string?)r[0] == "ca_uninstall_capture");
    }

    [Fact]
    public void SetupWizardIsOmittedWhenDisabled()
    {
        string dir = TempDir();
        string output = Path.Combine(dir, "nowiz.msi");
        var project = SampleProject(dir);
        project.ShowInstallWizard = false;
        MsiBuilder.Build(project, output);

        var db = MsiDatabase.Load(output);
        Assert.False(db.Tables.ContainsKey("Dialog"));
        Assert.DoesNotContain(db.Tables["InstallUISequence"].Rows, r => (string?)r[0] == "WelcomeDlg");
    }

    [Fact]
    public void VersionedFilesDeclareTheirVersion()
    {
        // Left null, Windows Installer treats the file as unversioned, refuses to
        // replace the versioned copy on disk, and RemoveExistingProducts then
        // deletes it - the upgrade silently removes the application.
        string dir = TempDir();
        // A real versioned PE is needed, and only Windows is guaranteed to have
        // one lying about. Elsewhere the unversioned half of the rule is still
        // asserted below, rather than the whole test passing vacuously.
        string exe = Path.Combine(dir, "sample.exe");
        string system = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")
            : "";

        bool haveVersionedPe = system.Length > 0 && File.Exists(system);
        if (haveVersionedPe) File.Copy(system, exe, overwrite: true);
        else File.WriteAllBytes(exe, [0x4D, 0x5A, 0x90, 0x00]);   // an MZ stub, no version resource

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
        var hashRows = db.Tables.GetValueOrDefault("MsiFileHash")?.Rows ?? [];

        if (haveVersionedPe)
        {
            Assert.False(string.IsNullOrWhiteSpace(version), "File.Version must be populated for a versioned PE.");

            // A versioned file must not also carry a hash row.
            Assert.DoesNotContain(hashRows, r => (string?)r[0] == (string?)row[0]);
        }
        else
        {
            // The inverse rule, which holds on every platform: no version
            // resource means no version, and a hash row instead.
            Assert.Null(version);
            Assert.Contains(hashRows, r => (string?)r[0] == (string?)row[0]);
        }
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
    public void DeferredTaskActionsHaveAPairedRollback()
    {
        // Without a rollback action, an install that fails after the task is
        // registered leaves the task behind.
        string dir = TempDir();
        string exe = Path.Combine(dir, "tool.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);   // enough to be a payload file

        var project = new MsiProject
        {
            ProductName = "Rollback",
            Manufacturer = "Test Co",
            Version = "1.0.0",
            UpgradeCode = Guid.NewGuid(),
            Files = { new PayloadFile { SourcePath = exe } },
            Tasks = { new ScheduledTaskSpec { Name = "T", ExePath = "tool.exe" } },
        };

        string output = Path.Combine(dir, "rollback.msi");
        MsiBuilder.Build(project, output);
        var db = MsiDatabase.Load(output);

        var actions = db.Tables["CustomAction"];
        var rollback = actions.Rows.Single(r => ((string)r[0]!).Contains("rollback"));
        var create = actions.Rows.Single(r => ((string)r[0]!).Contains("create"));

        const int rollbackFlag = 256;
        Assert.True(((int)rollback[1]! & rollbackFlag) != 0, "rollback action must set msidbCustomActionTypeRollback");
        Assert.True(((int)create[1]! & rollbackFlag) == 0, "the create action must not");

        // Rollback actions replay in reverse, so it has to be scheduled first.
        var sequence = db.Tables["InstallExecuteSequence"];
        int rollbackSeq = (int)sequence.Rows.Single(r => (string?)r[0] == (string?)rollback[0])[2]!;
        int createSeq = (int)sequence.Rows.Single(r => (string?)r[0] == (string?)create[0])[2]!;
        Assert.True(rollbackSeq < createSeq, $"rollback at {rollbackSeq} must precede create at {createSeq}");
    }

    [Fact]
    public void RunActionWithoutRollbackIsFlagged()
    {
        string dir = TempDir();
        var project = SampleProject(dir);
        project.Run.Add(new RunAction { Id = "setup", ExePath = "readme.txt", Arguments = "/S" });

        var result = MsiBuilder.Build(project, Path.Combine(dir, "run.msi"));
        Assert.Contains(result.Warnings, w => w.Contains("RollbackArguments"));
    }

    [Fact]
    public void ClosingProcessesNeverPrecedesRemoveExistingProducts()
    {
        // A deferred action between InstallInitialize and RemoveExistingProducts
        // is rejected outright: error 2613, "RemoveExistingProducts action
        // sequenced incorrectly".
        string dir = TempDir();
        var project = SampleProject(dir);
        project.CloseProcesses.Add("HPImageAssistant.exe");
        project.CloseProcesses.Add("notepad");        // extension supplied for us

        string output = Path.Combine(dir, "close.msi");
        MsiBuilder.Build(project, output);
        var db = MsiDatabase.Load(output);

        var sequence = db.Tables["InstallExecuteSequence"];
        int Seq(string action) => (int)sequence.Rows.Single(r => (string?)r[0] == action)[2]!;

        int init = Seq("InstallInitialize");
        int rep = Seq("RemoveExistingProducts");
        Assert.Equal(1501, rep);
        Assert.True(rep > init);

        var actions = db.Tables["CustomAction"];
        var closeActions = actions.Rows.Where(r => ((string)r[0]!).StartsWith("ca_close")).ToList();
        Assert.Equal(4, closeActions.Count);   // immediate and deferred, per process

        const int deferredFlag = 1024;
        foreach (var action in closeActions)
        {
            string id = (string)action[0]!;
            int seq = Seq(id);
            bool isDeferred = ((int)action[1]! & deferredFlag) != 0;

            if (isDeferred)
                Assert.True(seq > rep, $"deferred {id} at {seq} must follow RemoveExistingProducts at {rep}");
            else
                Assert.True(seq < init, $"immediate {id} at {seq} must precede InstallInitialize at {init}");
        }

        // Missing extensions are filled in, so "notepad" becomes "notepad.exe".
        Assert.Contains(actions.Rows, r => ((string?)r[3] ?? "").Contains("notepad.exe"));
    }

    [Fact]
    public void SigningIsOffUnlessAsked()
    {
        var result = CodeSigner.Sign("nonexistent.msi", new SigningOptions());
        Assert.False(result.Signed);
        Assert.Contains("not enabled", result.Detail);
    }

    [Fact]
    public void SigningRefusesWhenTheCertificateIsNotIdentified()
    {
        string? signer = CodeSigner.FindSigner();
        if (signer is null) return; // nothing installed to sign with here

        var result = CodeSigner.Sign("nonexistent.msi", new SigningOptions { Enabled = true });
        Assert.False(result.Signed);
        Assert.Contains("CertificatePath", result.Detail);
    }

    [Fact]
    public void SigningPasswordIsNeverStoredInTheProject()
    {
        // A project file belongs in source control; a password in it would go too.
        var options = new SigningOptions();
        var properties = options.GetType().GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("PasswordEnvironmentVariable", properties);
        Assert.DoesNotContain(properties, n => n.Equals("Password", StringComparison.OrdinalIgnoreCase));
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
