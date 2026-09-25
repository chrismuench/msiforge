using System.IO.Compression;
using MsiMaker.Core.Build;
using MsiMaker.Core.Model;
using MsiMaker.Core.Msi;
using Xunit;

namespace MsiMaker.Tests;

/// <summary>
/// Cover for a round of defects found by reading the builder and the parsers
/// adversarially rather than by hitting them in normal use.
///
/// They share a shape worth naming: none of them threw. A sequence number that
/// overruns the next standard action, a string index that drifts by one, a zip
/// entry that lands in the folder next door - each produces a package or a
/// reading that looks entirely correct and is wrong. That is why they are tested
/// here rather than trusted to review.
/// </summary>
public class HardeningTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "msimaker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static MsiProject Project(string dir, string name = "Hardening")
    {
        string payload = Path.Combine(dir, "app.exe");
        File.WriteAllText(payload, "payload");
        return new MsiProject
        {
            ProductName = name,
            Manufacturer = "Test Co",
            Version = "1.0.0",
            UpgradeCode = new Guid("5e1d9c30-4a72-4b16-90c8-1f3b7e2d6a55"),
            Files = { new PayloadFile { SourcePath = payload } },
        };
    }

    private static List<(string Action, int Sequence)> ExecuteSequence(string msiPath)
    {
        var db = MsiDatabase.Load(msiPath);
        return db.Tables["InstallExecuteSequence"].Rows
            .Select(r => ((string)r[0]!, (int)r[2]!))
            .ToList();
    }

    // ---------------------------------------------------------- sequence bands

    [Fact]
    public void UninstallActionsNeverOverrunRemoveFiles()
    {
        // Twelve uninstall actions used to step from 3400 in tens, so the eleventh
        // landed on RemoveFiles (3500) and the twelfth after it - running once the
        // payload it needs has already been deleted. With IgnoreExitCode that
        // fails silently and msiexec still returns 0.
        string dir = TempDir();
        var project = Project(dir);
        for (int i = 0; i < 12; i++)
            project.Run.Add(new RunAction
            {
                Id = $"u{i:00}",
                Command = "echo hello",
                When = RunWhen.Uninstall,
                IgnoreExitCode = true,
            });

        string msi = Path.Combine(dir, "out.msi");
        MsiBuilder.Build(project, msi);

        const int removeFiles = 3500;
        var uninstallActions = ExecuteSequence(msi).Where(r => r.Action.StartsWith("ca_u")).ToList();

        Assert.Equal(12, uninstallActions.Count);
        Assert.All(uninstallActions, r => Assert.True(r.Sequence < removeFiles,
            $"{r.Action} is sequenced at {r.Sequence}, on or after RemoveFiles ({removeFiles})."));
    }

    [Fact]
    public void AnUninstallActionAndATaskDeleteDoNotShareASequenceNumber()
    {
        // Two counters both started at 3400, so ca_<id>_uninstall and
        // ca_task_delete_<name> were emitted at the same number with no defined
        // order between them.
        string dir = TempDir();
        var project = Project(dir);
        project.Run.Add(new RunAction { Id = "r", Command = "echo hello", When = RunWhen.Uninstall, IgnoreExitCode = true });
        project.Tasks.Add(new ScheduledTaskSpec { Name = "Nightly", ExePath = "app.exe", Folder = "\\Test" });

        string msi = Path.Combine(dir, "out.msi");
        MsiBuilder.Build(project, msi);

        var sequence = ExecuteSequence(msi);
        var duplicates = sequence.GroupBy(r => r.Sequence).Where(g => g.Count() > 1).ToList();

        Assert.True(duplicates.Count == 0,
            "two actions share a sequence number: "
            + string.Join("; ", duplicates.Select(g => $"{g.Key} -> {string.Join(", ", g.Select(x => x.Action))}")));
    }

    [Fact]
    public void ClosingManyProcessesDoesNotStepOverRemoveExistingProducts()
    {
        // The immediate close pass began at 1490 with only ten slots before
        // InstallInitialize (1500) and RemoveExistingProducts (1501). Past ten
        // executables the later ones were killed after the old build had already
        // been removed, defeating the only reason that pass exists.
        string dir = TempDir();
        var project = Project(dir);
        for (int i = 0; i < 14; i++) project.CloseProcesses.Add($"p{i:00}.exe");

        string msi = Path.Combine(dir, "out.msi");
        MsiBuilder.Build(project, msi);

        var sequence = ExecuteSequence(msi);
        var immediate = sequence.Where(r => r.Action.StartsWith("ca_close_") && !r.Action.StartsWith("ca_close_deferred")).ToList();

        Assert.Equal(14, immediate.Count);
        Assert.All(immediate, r => Assert.True(r.Sequence < 1500,
            $"{r.Action} is at {r.Sequence}, at or after InstallInitialize (1500)."));
    }

    [Fact]
    public void TooManyActionsIsRefusedRatherThanSilentlyMisSequenced()
    {
        string dir = TempDir();
        var project = Project(dir);
        for (int i = 0; i < 400; i++)
            project.Run.Add(new RunAction { Id = $"u{i:000}", Command = "echo hello", When = RunWhen.Uninstall, IgnoreExitCode = true });

        var error = Assert.Throws<InvalidOperationException>(
            () => MsiBuilder.Build(project, Path.Combine(dir, "out.msi")));
        Assert.Contains("sequence slots", error.Message);
    }

    // ------------------------------------------------------------ string pool

    [Fact]
    public void StringsAfterAVeryLongOneKeepTheirIdentity()
    {
        // A string over 65535 bytes occupies two pool slots but is still one
        // string and takes one index. Reading it as two shifted every later
        // string by one: the tables still parsed, every value was still a real
        // string, just the wrong one - and that reaches the upgrade check.
        string dir = TempDir();
        var project = Project(dir);
        project.Arp.Comments = new string('L', 70_000);

        string msi = Path.Combine(dir, "out.msi");
        MsiBuilder.Build(project, msi);

        var db = MsiDatabase.Load(msi);
        var upgradeRows = db.Tables["Upgrade"].Rows;
        var actionProperties = upgradeRows.Select(r => r[6] as string).ToList();

        Assert.Contains("MSIMAKERUPGRADE", actionProperties);
        Assert.Contains("MSIMAKERDOWNGRADE", actionProperties);
        Assert.Equal("1.0.0", db.GetProperty("ProductVersion"));
        Assert.Equal(new string('L', 70_000), db.GetProperty("ARPCOMMENTS"));
    }

    // -------------------------------------------------------------- sanitising

    [Theory]
    [InlineData("Acme|Corp", "AcmeCorp")]
    [InlineData("Acme:Corp", "AcmeCorp")]
    [InlineData("a/b\\c", "abc")]
    [InlineData("trailing.", "trailing")]
    public void SanitizeStripsWindowsSeparatorsWhateverTheHostIs(string input, string expected)
    {
        // Path.GetInvalidFileNameChars() answers for the machine running the
        // build; on macOS it is only '/'. The target is Windows either way, and
        // ':' and '|' are structural in the Directory table's DefaultDir column -
        // an unsanitised one silently changes the install location.
        Assert.Equal(expected, MsiProject.Sanitize(input));
    }

    // ------------------------------------------------------- command injection

    [Fact]
    public void AWildcardTaskNameIsRefused()
    {
        // schtasks /Delete /TN "*" /F is the documented delete-every-task
        // wildcard, and the uninstall and rollback actions run as SYSTEM with
        // their exit code ignored.
        string dir = TempDir();
        var project = Project(dir);
        project.Tasks.Add(new ScheduledTaskSpec { Name = "*", ExePath = "app.exe" });

        var error = Assert.Throws<InvalidOperationException>(
            () => MsiBuilder.Build(project, Path.Combine(dir, "out.msi")));
        Assert.Contains("SYSTEM", error.Message);
    }

    [Fact]
    public void AQuoteInAProcessNameIsRefused()
    {
        string dir = TempDir();
        var project = Project(dir);
        project.CloseProcesses.Add("app\".exe");

        var error = Assert.Throws<InvalidOperationException>(
            () => MsiBuilder.Build(project, Path.Combine(dir, "out.msi")));
        Assert.Contains("CloseProcesses", error.Message);
    }

    // ----------------------------------------------------------------- bundles

    [Fact]
    public void AnEntryLandingInASiblingFolderIsRefused()
    {
        // The containment check compared without a trailing separator, so an
        // entry resolving to ...\bundles\acme passed StartsWith("...\bundles\a").
        // Every bundle unpacks into that same parent, and extraction overwrites.
        string dir = TempDir();
        string bundle = Path.Combine(dir, "a.msimaker");

        using (var archive = ZipFile.Open(bundle, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("project.json");
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write("""
                    {"ProductName":"A","Manufacturer":"T","Version":"1.0.0",
                     "UpgradeCode":"5e1d9c30-4a72-4b16-90c8-1f3b7e2d6a55","Files":[]}
                    """);

            var escaping = archive.CreateEntry("payload/../../acme/payload/setup.exe");
            using (var writer = new StreamWriter(escaping.Open())) writer.Write("evil");
        }

        var error = Assert.Throws<InvalidDataException>(
            () => ProjectBundle.Open(bundle, Path.Combine(dir, "bundles", "a")));
        Assert.Contains("escapes", error.Message);
    }

    [Fact]
    public void AManifestNamingAFileOutsideTheBundleIsRefused()
    {
        // A bundle is self-contained by definition, so a payload path pointing at
        // the build machine is either a mistake or an attempt to pack up someone's
        // private key under an innocent target name.
        string dir = TempDir();
        string secret = Path.Combine(dir, "id_rsa");
        File.WriteAllText(secret, "PRIVATE KEY");

        string bundle = Path.Combine(dir, "b.msimaker");
        using (var archive = ZipFile.Open(bundle, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("project.json");
            using var writer = new StreamWriter(manifest.Open());
            writer.Write($$"""
                {"ProductName":"B","Manufacturer":"T","Version":"1.0.0",
                 "UpgradeCode":"5e1d9c30-4a72-4b16-90c8-1f3b7e2d6a55",
                 "Files":[{"SourcePath":{{System.Text.Json.JsonSerializer.Serialize(secret)}},
                           "TargetPath":"readme.txt"}]}
                """);
        }

        var error = Assert.Throws<InvalidDataException>(
            () => ProjectBundle.Open(bundle, Path.Combine(dir, "bundles", "b")));
        Assert.Contains("outside the bundle", error.Message);
    }

    // -------------------------------------------------------------- parameters

    [Fact]
    public void ParametersBecomePropertiesAndAreMarkedSecure()
    {
        string dir = TempDir();
        var project = Project(dir);
        project.Parameters.Add(new MsiParameter
        {
            Name = "INSTALLARGS",
            Default = "/quiet",
            Description = "Switches passed to the vendor installer.",
        });

        string msi = Path.Combine(dir, "out.msi");
        MsiBuilder.Build(project, msi);

        var db = MsiDatabase.Load(msi);
        Assert.Equal("/quiet", db.GetProperty("INSTALLARGS"));

        // Without this the value is silently dropped the moment the install
        // elevates, and the deferred action reads an empty string.
        Assert.Contains("INSTALLARGS", db.GetProperty("SecureCustomProperties"));
    }

    [Fact]
    public void ALowerCaseParameterNameIsRefused()
    {
        // Windows Installer ignores a command-line assignment to anything but an
        // all-upper-case property, without saying so - which would make the
        // parameter look supported and do nothing.
        string dir = TempDir();
        var project = Project(dir);
        project.Parameters.Add(new MsiParameter { Name = "InstallArgs" });

        var error = Assert.Throws<InvalidOperationException>(
            () => MsiBuilder.Build(project, Path.Combine(dir, "out.msi")));
        Assert.Contains("public property name", error.Message);
    }

    [Fact]
    public void EveryPackageCanBeAskedWhatItAccepts()
    {
        string dir = TempDir();
        var project = Project(dir);
        project.Parameters.Add(new MsiParameter { Name = "LICENSEKEY", Description = "Licence key." });

        string msi = Path.Combine(dir, "out.msi");
        MsiBuilder.Build(project, msi);

        var db = MsiDatabase.Load(msi);
        var help = db.Tables["CustomAction"].Rows.SingleOrDefault(r => (string)r[0]! == "ca_help");
        Assert.NotNull(help);

        // Type 19 shows the text and stops - no helper DLL, so nothing to compile
        // per architecture.
        Assert.Equal(19, (int)help![1]!);
        Assert.Contains("LICENSEKEY", (string)help[3]!);
        Assert.Contains("INSTALLFOLDER", (string)help[3]!);

        // Ahead of every standard action, so asking what a package accepts works
        // even on a machine it would refuse to install on.
        Assert.Contains(ExecuteSequence(msi), r => r.Action == "ca_help" && r.Sequence == 1);
    }

    // -------------------------------------------------- install folder permissions

    [Fact]
    public void AProgramDataInstallLocksTheFolderDown()
    {
        // C:\ProgramData grants Users container-inherit write and add-subdirectory,
        // and CREATOR OWNER full control over anything they create. A custom action
        // or scheduled task running as SYSTEM out of a folder there can be given a
        // DLL to load by any standard user - no race, no pre-creation.
        string dir = TempDir();
        var project = Project(dir);
        project.Root = InstallRoot.CommonAppData;
        project.Run.Add(new RunAction { Id = "go", Command = "echo hello", When = RunWhen.Install, IgnoreExitCode = true });

        string msi = Path.Combine(dir, "out.msi");
        MsiBuilder.Build(project, msi);

        var db = MsiDatabase.Load(msi);
        var lockRow = db.Tables["MsiLockPermissionsEx"].Rows.Single();

        Assert.Equal("INSTALLFOLDER", (string)lockRow[1]!);

        // Against a CreateFolder row, not the Directory row - the folder has to be
        // explicitly created by a component before it can be locked.
        Assert.Equal("CreateFolder", (string)lockRow[2]!);
        Assert.Contains("INSTALLFOLDER", db.Tables["CreateFolder"].Rows.Select(r => (string)r[0]!));

        // The leading P is the point: without it the permissive ACEs ProgramData
        // hands down are still there alongside these.
        string sddl = (string)lockRow[3]!;
        Assert.StartsWith("D:P", sddl, StringComparison.Ordinal);
        Assert.Contains(";;;BU)", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain("FA;;;BU", sddl, StringComparison.Ordinal);
    }

    [Fact]
    public void AProgramFilesInstallIsLeftAlone()
    {
        // Program Files already grants Users read and execute and nothing else, so
        // overriding its ACL would be churn with a chance of getting it wrong.
        string dir = TempDir();
        var project = Project(dir);

        string msi = Path.Combine(dir, "out.msi");
        MsiBuilder.Build(project, msi);

        var db = MsiDatabase.Load(msi);
        Assert.False(db.Tables.ContainsKey("MsiLockPermissionsEx"));
    }

    [Fact]
    public void RunningAsSystemFromAWritableFolderIsWarnedAboutWhenLockingIsRefused()
    {
        string dir = TempDir();
        var project = Project(dir);
        project.Root = InstallRoot.CommonAppData;
        project.LockInstallFolder = false;
        project.Run.Add(new RunAction { Id = "go", Command = "echo hello", When = RunWhen.Install, IgnoreExitCode = true });

        var result = MsiBuilder.Build(project, Path.Combine(dir, "out.msi"));
        Assert.Contains(result.Warnings, w => w.Contains("SYSTEM", StringComparison.Ordinal));
    }

    // ------------------------------------------------------- generated file names


    [Fact]
    public void AGeneratedTaskXmlNeverOverwritesAPayloadFile()
    {
        // The MSI keys were kept unique; the file name written to disk was not. A
        // payload file and a task XML resolving to one path in INSTALLFOLDER left
        // two components sharing a key path and the author's file replaced, with
        // every table valid and the install returning 0.
        string dir = TempDir();
        string clashing = Path.Combine(dir, "My_Task.xml");
        File.WriteAllText(clashing, "the author's own file");

        var project = Project(dir);
        project.Files.Add(new PayloadFile { SourcePath = clashing });
        project.Tasks.Add(new ScheduledTaskSpec { Name = "My Task", ExePath = "app.exe", Folder = "\\T" });
        project.Tasks.Add(new ScheduledTaskSpec { Name = "My_Task", ExePath = "app.exe", Folder = "\\T" });

        string msi = Path.Combine(dir, "out.msi");
        MsiBuilder.Build(project, msi);

        var db = MsiDatabase.Load(msi);
        var names = db.Tables["File"].Rows.Select(r => (string)r[2]!).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("My_Task.xml", names);
    }

    [Fact]
    public void AScheduledTaskFileHasItsAclPinned()
    {
        // A SYSTEM task is stored as a file under System32\\Tasks and still
        // inherits from its folder. Registering it into a folder created with
        // hostile inheritable permissions would carry them onto the task. The
        // package breaks inheritance on the task file it created - and only that
        // file, never the shared vendor folder - fixing it to SYSTEM,
        // Administrators and read-only for everyone else.
        string dir = TempDir();
        var project = Project(dir);
        project.Tasks.Add(new ScheduledTaskSpec { Name = "Nightly", ExePath = "app.exe", Folder = "\\Vendor" });

        string msi = Path.Combine(dir, "out.msi");
        MsiBuilder.Build(project, msi);

        var db = MsiDatabase.Load(msi);
        var secure = db.Tables["CustomAction"].Rows
            .SingleOrDefault(r => ((string)r[0]!).StartsWith("ca_task_secure", StringComparison.Ordinal));
        Assert.NotNull(secure);

        string command = (string)secure![3]!;
        Assert.Contains("icacls.exe", command, StringComparison.Ordinal);
        Assert.Contains("/inheritance:r", command, StringComparison.Ordinal);

        // SIDs, not names - Users and Administrators are localised.
        Assert.Contains("*S-1-5-18:(F)", command, StringComparison.Ordinal);      // SYSTEM
        Assert.Contains("*S-1-5-32-544:(F)", command, StringComparison.Ordinal);  // Administrators
        Assert.Contains("*S-1-5-32-545:(RX)", command, StringComparison.Ordinal); // Users, read only

        // It targets the task under System32\Tasks, not our installed XML copy.
        Assert.Contains("Tasks\\Vendor\\Nightly", command, StringComparison.Ordinal);

        // Deferred, no-impersonate, ignore-return: runs as SYSTEM, and a failure to
        // tighten the ACL must not fail the install - the service's own descriptor
        // is the baseline.
        int type = (int)secure[1]!;
        Assert.Equal(CaDeferredNoImpersonateIgnore, type & CaDeferredNoImpersonateIgnore);
    }

    private const int CaDeferredNoImpersonateIgnore = 64 | 1024 | 2048;

    // ------------------------------------------------- programs-list dedup

    [Fact]
    public void AWrappedInstallerHidesItsOwnProgramsEntry()
    {
        // The wrapped EXE writes its own Programs entry, so a second one from the
        // wrapper is the app listed twice - and, since the wrapper carries the
        // declared version and the EXE its real one, usually at two versions.
        string dir = TempDir();
        var project = Project(dir);
        project.Run.Add(new RunAction { Id = "install", ExePath = "app.exe", When = RunWhen.Install });

        var result = MsiBuilder.Build(project, Path.Combine(dir, "out.msi"));
        var db = MsiDatabase.Load(Path.Combine(dir, "out.msi"));

        Assert.Equal("1", db.GetProperty("ARPSYSTEMCOMPONENT"));
        Assert.Contains(result.Warnings, w => w.Contains("twice", StringComparison.Ordinal) || w.Contains("hidden", StringComparison.Ordinal));
    }

    [Fact]
    public void APackageThatInstallsItsOwnFilesStaysVisible()
    {
        // No wrapped installer means our MSI is the only thing that registers, so
        // it must appear in Programs.
        string dir = TempDir();
        var project = Project(dir);

        MsiBuilder.Build(project, Path.Combine(dir, "out.msi"));
        var db = MsiDatabase.Load(Path.Combine(dir, "out.msi"));

        Assert.Null(db.GetProperty("ARPSYSTEMCOMPONENT"));
    }

    [Fact]
    public void ShowingAWrapperInProgramsCanBeForced()
    {
        string dir = TempDir();
        var project = Project(dir);
        project.Run.Add(new RunAction { Id = "install", ExePath = "app.exe", When = RunWhen.Install });
        project.Arp.HideFromProgramsList = false;

        MsiBuilder.Build(project, Path.Combine(dir, "out.msi"));
        var db = MsiDatabase.Load(Path.Combine(dir, "out.msi"));

        Assert.Null(db.GetProperty("ARPSYSTEMCOMPONENT"));
    }
}
