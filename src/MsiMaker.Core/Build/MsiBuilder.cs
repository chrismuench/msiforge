using System.Security.Cryptography;
using System.Text;
using MsiMaker.Core.Cab;
using MsiMaker.Core.Model;
using MsiMaker.Core.Msi;
using MsiMaker.Core.Payload;

namespace MsiMaker.Core.Build;

public sealed record BuildResult(
    string MsiPath,
    Guid ProductCode,
    Guid UpgradeCode,
    string Version,
    string PackageCode,
    long SizeBytes,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a project into an MSI. Everything Intune needs for a clean in-place
/// upgrade is produced here by construction rather than left to the author:
/// a derived ProductCode, a stable UpgradeCode, stable component GUIDs, a
/// correct Upgrade table, and RemoveExistingProducts scheduled right after
/// InstallInitialize.
/// </summary>
public static class MsiBuilder
{
    // Custom action type bits, confirmed against WiX output (3154 = 18|64|1024|2048).
    /// <summary>
    /// msidbCustomActionTypeTextData + Error: shows the Target text and ends the
    /// install. No helper DLL, which matters - a binary custom action would have to
    /// be compiled per architecture and would undo the point of a managed builder.
    /// </summary>
    private const int CaShowTextAndStop = 19;

    private const int CaExeFromFile = 18;
    private const int CaExeInDirectory = 34;
    private const int CaIgnoreReturn = 64;

    /// <summary>
    /// msidbCustomActionTypeRollback. A rollback action runs only when the
    /// install fails, and must be scheduled immediately ahead of the deferred
    /// action it undoes - rollback actions execute in reverse order.
    /// </summary>
    private const int CaRollback = 256;

    private const int CaDeferred = 1024;
    private const int CaNoImpersonate = 2048;

    private const int ComponentIs64Bit = 256;
    private const int ComponentRegistryKeyPath = 4;
    private const int FileVital = 512;

    /// <summary>
    /// Hands out sequence numbers from the gap between two standard actions, and
    /// refuses rather than overrunning into the next one.
    ///
    /// Overrunning is silent, which is why this is a type and not an int. An
    /// uninstall action that spills past RemoveFiles (3500) runs after the payload
    /// it needs has already been deleted: with IgnoreExitCode it fails invisibly
    /// and msiexec still returns 0, and without it the uninstall returns 1603 and
    /// rolls back, leaving a product its own package can no longer remove.
    /// </summary>
    private sealed class SequenceBand(string purpose, int first, int limit)
    {
        private readonly int _first = first;
        private int _next = first;

        public int Take(int count = 1)
        {
            if (_next + count > limit)
                throw new InvalidOperationException(
                    $"Too many {purpose} actions: sequence slots {_first}-{limit - 1} are full. "
                    + "Windows Installer orders actions by number, and there is no room left before "
                    + "the next standard action, so another one here would run at the wrong point in "
                    + "the install.");

            int value = _next;
            _next += count;
            return value;
        }
    }

    /// <summary>Set this on the command line to be told what the package accepts.</summary>
    private const string HelpProperty = "HELP";
    private const string HelpActionId = "ca_help";

    /// <summary>
    /// The text the HELP dialog shows: what this package is, and every value it
    /// will accept on the command line, with defaults.
    /// </summary>
    private static string HelpText(MsiProject project, bool is64)
    {
        var text = new StringBuilder();
        text.AppendLine($"{project.ProductName} {project.ComparableVersion}");
        text.AppendLine();
        text.AppendLine("Install:    msiexec /i \"" + Sanitised(project) + ".msi\" /qn");
        text.AppendLine("Uninstall:  msiexec /x \"" + Sanitised(project) + ".msi\" /qn");
        text.AppendLine();
        text.AppendLine("Values you can set on the command line, as NAME=value:");
        text.AppendLine();
        text.AppendLine("  INSTALLFOLDER   Where to install. Default:");
        text.AppendLine($"                  {TargetInstallDirectory(project, is64)}");

        foreach (var parameter in project.Parameters)
        {
            text.AppendLine();
            text.AppendLine($"  {parameter.Name}");
            if (!string.IsNullOrWhiteSpace(parameter.Description))
                text.AppendLine($"                  {parameter.Description}");
            text.AppendLine($"                  Default: {(string.IsNullOrEmpty(parameter.Default)
                ? "(empty)" : parameter.Default)}");
        }

        text.AppendLine();
        text.AppendLine("Quoting: msiexec needs NAME=\"a value with spaces\" quoted as shown,");
        text.AppendLine("with the quotes around the value only, not around the whole pair.");
        return text.ToString();
    }

    private static string Sanitised(MsiProject project) => MsiProject.Sanitize(project.ProductName);

    /// <summary>
    /// SYSTEM and Administrators keep full control, everyone else gets read and
    /// execute, and the leading "P" stops anything being inherited from the
    /// parent - which is the entire point. Without it the permissive ACEs that
    /// C:\ProgramData hands down are still there alongside these.
    ///
    /// 0x1200a9 is FILE_GENERIC_READ | FILE_GENERIC_EXECUTE. Written numerically
    /// because the SDDL shorthand for it varies with the object type, and an SDDL
    /// string Windows Installer cannot parse fails the install at CreateFolders.
    /// </summary>
    private const string LockedFolderSddl =
        "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    private const string DowngradeProperty = "MSIMAKERDOWNGRADE";
    private const string UpgradeProperty = "MSIMAKERUPGRADE";

    public static BuildResult Build(MsiProject project, string outputPath)
    {
        project.ExpandFolders();

        var errors = project.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException("Project is not valid:\n  " + string.Join("\n  ", errors));

        var warnings = new List<string>();
        var db = new MsiDatabase();

        bool is64 = project.Platform is MsiPlatform.X64 or MsiPlatform.Arm64;
        string platform = project.Platform switch
        {
            MsiPlatform.X86 => "Intel",
            MsiPlatform.X64 => "x64",
            MsiPlatform.Arm64 => "Arm64",
            _ => "x64",
        };

        Guid productCode = project.EffectiveProductCode;
        string version = project.ComparableVersion;

        if ((project.Version ?? "").Split('.').Length > 3)
            warnings.Add($"Version '{project.Version}' has a fourth field. Windows Installer ignores it; "
                         + $"upgrades are decided on '{version}' alone.");

        // ------------------------------------------------------------ identity
        db.SetProperty("ProductName", project.ProductName);
        db.SetProperty("ProductVersion", version);
        db.SetProperty("ProductCode", DeterministicGuid.Format(productCode));
        db.SetProperty("UpgradeCode", DeterministicGuid.Format(project.UpgradeCode));
        db.SetProperty("Manufacturer", project.Manufacturer);
        db.SetProperty("ProductLanguage", project.Language.ToString());
        db.SetProperty("ALLUSERS", "1");                       // per-machine; Intune installs as SYSTEM
        db.SetProperty("MSIINSTALLPERUSER", "0");
        // A public property set on the command line is dropped when the install
        // elevates, unless it is listed here. That failure is silent: the property
        // is simply empty by the time the deferred action reads it.
        var secure = new List<string> { DowngradeProperty, UpgradeProperty, HelpProperty };
        foreach (var parameter in project.Parameters)
        {
            if (!string.IsNullOrEmpty(parameter.Default))
                db.SetProperty(parameter.Name, parameter.Default!);
            secure.Add(parameter.Name);
        }

        db.SetProperty("SecureCustomProperties", string.Join(";", secure));

        var arp = project.Arp;
        if (!string.IsNullOrWhiteSpace(arp.HelpLink)) db.SetProperty("ARPHELPLINK", arp.HelpLink!);
        if (!string.IsNullOrWhiteSpace(arp.AboutUrl)) db.SetProperty("ARPURLINFOABOUT", arp.AboutUrl!);
        if (!string.IsNullOrWhiteSpace(arp.Contact)) db.SetProperty("ARPCONTACT", arp.Contact!);
        if (!string.IsNullOrWhiteSpace(arp.Comments)) db.SetProperty("ARPCOMMENTS", arp.Comments!);
        if (arp.NoModify) db.SetProperty("ARPNOMODIFY", "1");
        if (arp.NoRepair) db.SetProperty("ARPNOREPAIR", "1");
        if (arp.NoRemove) db.SetProperty("ARPNOREMOVE", "1");

        // A package that runs a vendor's own installer EXE is a wrapper: the EXE
        // writes its own Programs entry, so a second one from the wrapper is the
        // app listed twice, usually at two different versions. Hide the wrapper by
        // default in that case; a package that installs its own files is the only
        // installer there is, so it stays visible.
        var installerRuns = project.Run
            .Where(r => r.When is RunWhen.Install or RunWhen.Both && !string.IsNullOrWhiteSpace(r.ExePath))
            .ToList();
        bool wrapsAnInstaller = installerRuns.Count > 0;

        // A wrapped installer decides where it lands from its own command line, not
        // from the Install folder here. If the arguments name no directory, the
        // location is whatever the vendor defaults to - which is the surprise when
        // someone removes the /D= or /DIR= switch and nothing asks where to install.
        foreach (var run in installerRuns)
            if (!InstallerDetective.MentionsInstallDirectory(run.Arguments) && !run.UseInstallerDefaultLocation)
                warnings.Add($"Run action '{run.Id}' launches an installer with no install-directory "
                             + "switch and no decision to use the installer's default, so where it "
                             + "installs has not been settled. Choose a location when building, or add "
                             + "the vendor's directory switch (for example /D=, /DIR=, INSTALLDIR= or "
                             + "APPDIR=) with the path you want.");
        bool hide = arp.HideFromProgramsList ?? wrapsAnInstaller;
        if (hide)
        {
            db.SetProperty("ARPSYSTEMCOMPONENT", "1");
            if (arp.HideFromProgramsList is null)
                warnings.Add("This package runs a vendor installer that registers its own Programs entry, "
                             + "so the wrapper is hidden from Programs to avoid listing the app twice. It "
                             + "is still tracked and removable by ProductCode. Set HideFromProgramsList "
                             + "false to show it anyway.");
        }

        // ------------------------------------------------------------- upgrade
        // Row 1 removes every earlier version. Row 2 only detects a newer one so
        // the launch condition can refuse the downgrade with a clear message.
        var upgrade = db.Std("Upgrade");
        upgrade.Add(DeterministicGuid.Format(project.UpgradeCode), null, version, null, 513, null, UpgradeProperty);
        upgrade.Add(DeterministicGuid.Format(project.UpgradeCode), version, null, null, 2, null, DowngradeProperty);

        db.Std("LaunchCondition").Add(
            $"NOT {DowngradeProperty}",
            $"A newer version of {project.ProductName} is already installed. Setup will now exit.");

        // msiexec keeps /? and /help for itself, so a package cannot be asked for
        // help with a switch - a property is the only channel it leaves open.
        db.Std("CustomAction").Add(HelpActionId, CaShowTextAndStop, null, HelpText(project, is64), null);

        // ----------------------------------------------------------- directories
        var directory = db.Std("Directory");
        directory.Add("TARGETDIR", null, "SourceDir");

        string rootId = project.Root switch
        {
            InstallRoot.ProgramFiles => is64 ? "ProgramFiles64Folder" : "ProgramFilesFolder",
            InstallRoot.ProgramFilesX86 => "ProgramFilesFolder",
            InstallRoot.CommonAppData => "CommonAppDataFolder",
            InstallRoot.Windows => "WindowsFolder",
            _ => "ProgramFiles64Folder",
        };
        directory.Add(rootId, "TARGETDIR", ".");

        string parent = rootId;
        if (!string.IsNullOrWhiteSpace(project.ManufacturerFolder))
        {
            directory.Add("MFRFOLDER", parent, DefaultDir(MsiProject.Sanitize(project.ManufacturerFolder!)));
            parent = "MFRFOLDER";
        }
        directory.Add("INSTALLFOLDER", parent, DefaultDir(project.EffectiveInstallFolder));

        // Sub-folders implied by payload target paths.
        var dirIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [""] = "INSTALLFOLDER" };
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "TARGETDIR", rootId, "MFRFOLDER", "INSTALLFOLDER" };

        string DirectoryFor(string relativeDir)
        {
            relativeDir = relativeDir.Trim('/');
            if (dirIds.TryGetValue(relativeDir, out string? existing)) return existing;

            int slash = relativeDir.LastIndexOf('/');
            string parentDir = slash < 0 ? "" : relativeDir[..slash];
            string leaf = slash < 0 ? relativeDir : relativeDir[(slash + 1)..];

            string parentId = DirectoryFor(parentDir);
            string id = UniqueId("d_" + relativeDir.Replace('/', '_'), identifiers);
            directory.Add(id, parentId, DefaultDir(leaf));
            dirIds[relativeDir] = id;
            return id;
        }

        // Shortcut destinations need their own well-known directory rows.
        foreach (var shortcutDir in project.Shortcuts.Select(s => s.Directory).Distinct(StringComparer.Ordinal))
            if (identifiers.Add(shortcutDir)) directory.Add(shortcutDir, "TARGETDIR", ".");

        // ------------------------------------------------- components and files
        var component = db.Std("Component");
        var file = db.Std("File");
        var featureComponents = db.Std("FeatureComponents");
        var fileHash = db.Std("MsiFileHash");

        int componentAttributes = is64 ? ComponentIs64Bit : 0;
        var cabFiles = new List<CabFile>();
        var fileIdByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var componentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int sequence = 0;

        foreach (var payload in project.Files)
        {
            string target = payload.EffectiveTarget;
            int slash = target.LastIndexOf('/');
            string relativeDir = slash < 0 ? "" : target[..slash];
            string fileName = slash < 0 ? target : target[(slash + 1)..];
            string dirId = DirectoryFor(relativeDir);

            sequence++;
            string fileId = UniqueId("f_" + fileName, identifiers);
            string componentId = UniqueId("c_" + fileName, componentIds);

            // One file per component, with a component code derived from the
            // product and the target path, so it is identical in every version.
            Guid componentGuid = DeterministicGuid.Create(project.UpgradeCode, "Component/" + target);

            component.Add(componentId, DeterministicGuid.Format(componentGuid), dirId,
                          componentAttributes, null, fileId);
            featureComponents.Add("MainFeature", componentId);

            byte[] bytes = File.ReadAllBytes(payload.SourcePath);

            // A versioned file MUST declare its version here. Left null, Windows
            // Installer treats it as unversioned, refuses to replace the versioned
            // copy already on disk ("Disallowing installation of component ...
            // since the same component with higher versioned keyfile exists"), and
            // RemoveExistingProducts then deletes the old copy - so the upgrade
            // silently removes the application.
            string? fileVersion = ReadFileVersion(bytes);
            file.Add(fileId, componentId, fileName, bytes.Length, fileVersion, null, FileVital, sequence);

            // MsiFileHash applies only to unversioned files; for a versioned file
            // the version is what drives replacement, and a hash row is invalid.
            if (fileVersion is null)
            {
                // MD5 is not a security choice here. The MsiFileHash table is
                // defined by the MSI format as a 128-bit MD5 stored as four 32-bit
                // integers, and Windows Installer compares it to decide whether a
                // file needs replacing. Any other algorithm produces a table
                // Windows Installer will not accept.
#pragma warning disable CA5351
                byte[] md5 = MD5.HashData(bytes);
#pragma warning restore CA5351
                fileHash.Add(fileId, 0,
                    BitConverter.ToInt32(md5, 0), BitConverter.ToInt32(md5, 4),
                    BitConverter.ToInt32(md5, 8), BitConverter.ToInt32(md5, 12));
            }

            cabFiles.Add(new CabFile(fileId, bytes, File.GetLastWriteTime(payload.SourcePath)));
            fileIdByTarget[target] = fileId;
        }

        // Registry values live in their own component so they survive file changes.
        if (project.Registry.Count > 0)
        {
            var registry = db.Std("Registry");
            string regComponentId = UniqueId("c_registry", componentIds);
            string? keyPath = null;
            int index = 0;

            foreach (var value in project.Registry)
            {
                string rowId = UniqueId($"r_{++index}", identifiers);
                keyPath ??= rowId;
                registry.Add(rowId, RegistryRoot(value.Root), value.Key, value.Name,
                             EncodeRegistryValue(value), regComponentId);
            }

            Guid regGuid = DeterministicGuid.Create(project.UpgradeCode, "Component/registry");
            component.Add(regComponentId, DeterministicGuid.Format(regGuid), "INSTALLFOLDER",
                          componentAttributes | ComponentRegistryKeyPath, null, keyPath);
            featureComponents.Add("MainFeature", regComponentId);
        }

        if (project.Shortcuts.Count > 0)
        {
            var shortcut = db.Std("Shortcut");
            int index = 0;
            foreach (var s in project.Shortcuts)
            {
                string targetPath = s.TargetPath.Replace('\\', '/').TrimStart('/');
                if (!fileIdByTarget.TryGetValue(targetPath, out string? targetFileId))
                {
                    warnings.Add($"Shortcut '{s.Name}' targets '{s.TargetPath}', which is not in the payload; skipped.");
                    continue;
                }
                string componentForShortcut = (string)component.Rows
                    .First(r => (string?)r[5] == targetFileId)[0]!;

                shortcut.Add(UniqueId($"sc_{++index}", identifiers), s.Directory, s.Name,
                             componentForShortcut, $"[#{targetFileId}]", s.Arguments,
                             s.Description, null, null, null, null, "INSTALLFOLDER",
                             null, null, null, null);
            }
        }

        // --------------------------------------------------- scheduled tasks
        // The task XML is generated here and shipped as an ordinary payload file,
        // so it is versioned with the package and removed on uninstall.
        var taskFiles = new List<(ScheduledTaskSpec Task, string FileId)>();
        foreach (var task in project.Tasks)
        {
            string exeTarget = task.ExePath.Replace('\\', '/').TrimStart('/');
            if (!fileIdByTarget.TryGetValue(exeTarget, out string? exeFileId))
            {
                warnings.Add($"Scheduled task '{task.Name}' runs '{task.ExePath}', which is not in the payload; skipped.");
                continue;
            }

            // Task Scheduler expands environment variables in Command and
            // WorkingDirectory, so the install path goes straight into the XML.
            // That removes any need to rewrite the file on the device, and with
            // it the PowerShell registrar - which matters because PowerShell is
            // the thing most often restricted by AppLocker, WDAC or Constrained
            // Language Mode on a managed endpoint.
            string windowsRelative = exeTarget.Replace('/', '\\');
            string installDirectory = TargetInstallDirectory(project, is64);
            string xml = TaskXml.Build(task,
                installDirectory + "\\" + windowsRelative,
                task.Arguments,
                installDirectory);
            byte[] xmlBytes = TaskXml.Encode(xml);

            sequence++;
            // The MSI keys are made unique below, but the file *name* is what
            // lands on disk, and two task XMLs - or a task XML and a payload file
            // of the same name - would be written to one path in INSTALLFOLDER.
            // Windows Installer accepts that without complaint: the tables load,
            // the install returns 0, and the author's own file has been replaced
            // by generated XML, with two components claiming a key path neither
            // of them owns.
            string taskFileName = UniqueTarget(
                MsiProject.Sanitize(task.Name).Replace(' ', '_') + ".xml", fileIdByTarget);
            string taskFileId = UniqueId("f_task_" + taskFileName, identifiers);
            string taskComponentId = UniqueId("c_task_" + taskFileName, componentIds);
            Guid taskGuid = DeterministicGuid.Create(project.UpgradeCode, "Component/task/" + task.FullName);

            component.Add(taskComponentId, DeterministicGuid.Format(taskGuid), "INSTALLFOLDER",
                          componentAttributes, null, taskFileId);
            featureComponents.Add("MainFeature", taskComponentId);
            file.Add(taskFileId, taskComponentId, taskFileName, xmlBytes.Length, null, null, FileVital, sequence);
            fileIdByTarget[taskFileName] = taskFileId;

#pragma warning disable CA5351 // MsiFileHash is MD5 by definition; see the note above.
            byte[] taskMd5 = MD5.HashData(xmlBytes);
#pragma warning restore CA5351
            fileHash.Add(taskFileId, 0,
                BitConverter.ToInt32(taskMd5, 0), BitConverter.ToInt32(taskMd5, 4),
                BitConverter.ToInt32(taskMd5, 8), BitConverter.ToInt32(taskMd5, 12));

            cabFiles.Add(new CabFile(taskFileId, xmlBytes, DateTime.Now));
            taskFiles.Add((task, taskFileId));
        }

        // SystemFolder is the 32-bit system directory (SysWOW64 on x64); the
        // native one is System64Folder. Added on demand, since a package with no
        // tasks and no shell commands has no use for it.
        string systemDirId = is64 ? "System64Folder" : "SystemFolder";

        void EnsureSystemDirectory()
        {
            if (identifiers.Add(systemDirId)) directory.Add(systemDirId, "TARGETDIR", ".");
        }

        if (taskFiles.Count > 0) EnsureSystemDirectory();

        // -------------------------------------------------------- run actions
        var installSequence = db.Std("InstallExecuteSequence");

        // Closing applications has to happen before anything touches a file,
        // which means before RemoveExistingProducts removes the old build. The
        // only free slot after InstallInitialize (1500) is 1501, so when there
        // are processes to close, RemoveExistingProducts moves to 1502.
        var processesToClose = project.CloseProcesses
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : p + ".exe")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // RemoveExistingProducts must be the first thing in the execution script,
        // so nothing deferred may precede it: Windows Installer rejects that with
        // error 2613, "RemoveExistingProducts action sequenced incorrectly".
        // Closing applications therefore happens twice, and both are cheap:
        //   - immediately, before InstallInitialize, which frees files ahead of
        //     the old build being removed during an upgrade;
        //   - deferred, after RemoveExistingProducts, which runs elevated and so
        //     still works when the immediate pass could not.
        // Bounded bands rather than a bare counter: with more than ten executables
        // the old fixed start of 1490 stepped straight over InstallInitialize
        // (1500) and RemoveExistingProducts (1501), so the later processes were
        // killed after the old build had already been removed - which is the one
        // thing the immediate pass exists to prevent, and it raised no error.
        var immediateClose = new SequenceBand("close-process", 1410, 1500);
        var deferredClose = new SequenceBand("deferred close-process", 1550, 1600);

        foreach (var (action, condition, seq) in MsiSchema.Sequences["InstallExecuteSequence"])
            installSequence.Add(action, condition, seq);
        foreach (var table in new[] { "InstallUISequence", "AdminExecuteSequence", "AdminUISequence", "AdvtExecuteSequence" })
        {
            var t = db.Std(table);
            foreach (var (action, condition, seq) in MsiSchema.Sequences[table]) t.Add(action, condition, seq);
        }

        // Ahead of every standard action, including LaunchConditions: asking what
        // the package accepts should work even when the package would refuse to
        // install on this machine. In the UI sequence it is a dialog; under /qn
        // there is nobody to show it to, so it goes to the log instead.
        installSequence.Add(HelpActionId, HelpProperty, 1);
        db.Std("InstallUISequence").Add(HelpActionId, HelpProperty, 1);

        // A setup wizard for anyone who runs the package by hand: Welcome, a
        // destination-folder page, ready, progress and a completion screen. Silent
        // installs (/qn) never see it. Off is honoured for a package meant only to
        // be pushed silently.
        if (project.ShowInstallWizard)
            MsiInstallerUi.Emit(db, offerFolderChoice: true);

        if (processesToClose.Count > 0)
        {
            EnsureSystemDirectory();
            var closeActions = db.Std("CustomAction");
            foreach (string image in processesToClose)
            {
                // No condition: files must be free on install, upgrade and
                // uninstall alike. The return code is ignored because taskkill
                // reports failure when the process simply is not running.
                string command = $"taskkill.exe /F /T /IM \"{image}\"";

                string immediate = UniqueId($"ca_close_{image}", identifiers);
                closeActions.Add(immediate,
                    CaExeInDirectory | CaIgnoreReturn,
                    systemDirId, command, null);
                installSequence.Add(immediate, null, immediateClose.Take());

                string deferred = UniqueId($"ca_close_deferred_{image}", identifiers);
                closeActions.Add(deferred,
                    CaExeInDirectory | CaDeferred | CaNoImpersonate | CaIgnoreReturn,
                    systemDirId, command, null);
                installSequence.Add(deferred, null, deferredClose.Take());
            }
        }

        // Run actions and scheduled tasks compete for the same two windows, so
        // each window is one counter shared between them. Two counters that both
        // started at 3400 is how ca_<id>_uninstall and ca_task_delete_<name> came
        // to share a sequence number, with undefined relative order.
        var afterInstallFiles = new SequenceBand("install-time", 4010, 4500);
        var beforeRemoveFiles = new SequenceBand("uninstall-time", 3310, 3500);

        if (project.Run.Count > 0)
        {
            var customAction = db.Std("CustomAction");

            foreach (var run in project.Run)
            {
                // Two shapes: an executable this package installs (resolved
                // through the File table), or a shell command for something it
                // did not - typically a vendor uninstaller left behind by a
                // wrapped installer, which the MSI otherwise cannot remove.
                bool isCommand = !string.IsNullOrWhiteSpace(run.Command);
                string source;
                string target;
                int baseType;

                // The undo may be a different program from the action - a wrapped
                // installer is undone by the vendor's uninstaller - so the
                // rollback gets its own source and type.
                bool rollbackIsCommand = !string.IsNullOrWhiteSpace(run.RollbackCommand);
                string rollbackSource;
                string rollbackTarget;
                int rollbackBaseType;

                if (isCommand)
                {
                    EnsureSystemDirectory();
                    source = systemDirId;
                    target = "cmd.exe /c " + run.Command;
                    baseType = CaExeInDirectory;
                }
                else
                {
                    string exePath = run.ExePath!.Replace('\\', '/').TrimStart('/');
                    if (!fileIdByTarget.TryGetValue(exePath, out string? exeFileId))
                    {
                        warnings.Add($"Run action '{run.Id}' targets '{run.ExePath}', which is not in the payload; skipped.");
                        continue;
                    }
                    source = exeFileId;
                    target = run.Arguments;
                    baseType = CaExeFromFile;
                }

                if (rollbackIsCommand)
                {
                    EnsureSystemDirectory();
                    rollbackSource = systemDirId;
                    rollbackTarget = "cmd.exe /c " + run.RollbackCommand;
                    rollbackBaseType = CaExeInDirectory;
                }
                else
                {
                    rollbackSource = source;
                    rollbackTarget = isCommand
                        ? "cmd.exe /c " + run.RollbackArguments
                        : run.RollbackArguments ?? "";
                    rollbackBaseType = baseType;
                }

                int type = baseType | CaDeferred | CaNoImpersonate | (run.IgnoreExitCode ? CaIgnoreReturn : 0);

                if (run.When is RunWhen.Install or RunWhen.Both)
                {
                    string condition = Combine(run.Condition, "NOT REMOVE")!;

                    // Two adjacent slots so the rollback immediately precedes the
                    // action it undoes, whatever follows.
                    int slot = afterInstallFiles.Take(run.HasRollback ? 2 : 1);

                    if (run.HasRollback)
                    {
                        string rollbackId = UniqueId($"ca_{run.Id}_rollback", identifiers);
                        customAction.Add(rollbackId,
                            rollbackBaseType | CaRollback | CaDeferred | CaNoImpersonate | CaIgnoreReturn,
                            rollbackSource, rollbackTarget, null);
                        installSequence.Add(rollbackId, condition, slot++);
                    }
                    else
                    {
                        warnings.Add($"Run action '{run.Id}' has no rollback. If the install fails "
                                     + "after it runs, whatever it did is left behind. Set "
                                     + "RollbackCommand or RollbackArguments.");
                    }

                    string id = UniqueId($"ca_{run.Id}_install", identifiers);
                    customAction.Add(id, type, source, target, null);
                    installSequence.Add(id, condition, slot);
                }
                if (run.When is RunWhen.Uninstall or RunWhen.Both)
                {
                    string id = UniqueId($"ca_{run.Id}_uninstall", identifiers);
                    customAction.Add(id, type, source, target, null);
                    installSequence.Add(id, Combine(run.Condition, "REMOVE~=\"ALL\""),
                                        beforeRemoveFiles.Take());
                }
            }
        }

        if (taskFiles.Count > 0)
        {
            var customAction = db.Std("CustomAction");

            foreach (var (task, taskFileId) in taskFiles)
            {
                // Rollback first: if the install fails after the task is
                // registered, the task must not survive. It sits immediately
                // ahead of the create action, since rollback replays backwards.
                string undo = UniqueId($"ca_task_rollback_{task.Name}", identifiers);
                customAction.Add(undo,
                    CaExeInDirectory | CaRollback | CaDeferred | CaNoImpersonate | CaIgnoreReturn,
                    systemDirId,
                    $"schtasks.exe /Delete /TN \"{task.FullName}\" /F",
                    null);
                int slot = afterInstallFiles.Take(3);
                installSequence.Add(undo, "NOT REMOVE", slot++);

                string create = UniqueId($"ca_task_create_{task.Name}", identifiers);
                customAction.Add(create,
                    CaExeInDirectory | CaDeferred | CaNoImpersonate,
                    systemDirId,
                    $"schtasks.exe /Create /TN \"{task.FullName}\" /XML \"[#{taskFileId}]\" /F",
                    null);
                installSequence.Add(create, "NOT REMOVE", slot++);

                // Pin the ACL on the task object we just registered. A SYSTEM task
                // is stored as a file under %SystemRoot%\System32\Tasks, and the
                // Task Scheduler service already gives it a sensible descriptor -
                // but the file still inherits from its folder, so a folder created
                // with hostile inheritable permissions (an attacker who got there
                // first, say) could reach the task. Breaking inheritance and fixing
                // it to SYSTEM, Administrators and read-only for everyone else
                // removes that path, and Task Scheduler still reads and runs the
                // task afterwards. SIDs, not names, because Users/Administrators
                // are localised. Only the file we created is touched, never the
                // shared vendor folder. Best-effort: a failure here should not fail
                // the install, since the service's own descriptor is the baseline.
                //
                // [System64Folder]/[SystemFolder] and [# ] are resolved when the
                // deferred script is written, so the path is baked in by run time.
                string secureTask = UniqueId($"ca_task_secure_{task.Name}", identifiers);
                customAction.Add(secureTask,
                    CaExeInDirectory | CaDeferred | CaNoImpersonate | CaIgnoreReturn,
                    systemDirId,
                    $"icacls.exe \"[{systemDirId}]Tasks{task.FullName}\" /inheritance:r "
                    + "/grant:r *S-1-5-18:(F) *S-1-5-32-544:(F) *S-1-5-32-545:(RX)",
                    null);
                installSequence.Add(secureTask, "NOT REMOVE", slot);

                // Deleting is allowed to fail: the task may already be gone.
                //
                // The task's folder is deliberately left behind. schtasks has no
                // command that removes one, and a folder named for the vendor is
                // shared: on a machine with the vendor's own software installed,
                // "\HP" holds their tasks too. An empty folder is untidy; removing
                // someone else's tasks is not.
                string delete = UniqueId($"ca_task_delete_{task.Name}", identifiers);
                customAction.Add(delete,
                    CaExeInDirectory | CaDeferred | CaNoImpersonate | CaIgnoreReturn,
                    systemDirId,
                    $"schtasks.exe /Delete /TN \"{task.FullName}\" /F",
                    null);
                installSequence.Add(delete, "REMOVE~=\"ALL\"", beforeRemoveFiles.Take());
            }
        }

        // ------------------------------------------------------ folder permissions
        //
        // A custom action running as SYSTEM, and a scheduled task running as
        // SYSTEM, both execute a file out of the install folder. Under
        // %ProgramFiles% that is fine - Users get read and execute there and
        // nothing else. Under C:\ProgramData it is not: the default ACL grants
        // Users container-inherit write and add-subdirectory, and CREATOR OWNER
        // full control, so a standard user can drop a DLL beside the executable
        // and have SYSTEM load it. No race and no pre-creation needed.
        if (project.ShouldLockInstallFolder)
        {
            // MsiLockPermissionsEx applies to a CreateFolder row, not to the
            // Directory row, so the folder has to be explicitly created by a
            // component before it can be locked.
            string lockComponentId = UniqueId("c_installfolder", componentIds);
            Guid lockGuid = DeterministicGuid.Create(project.UpgradeCode, "Component/installfolder");

            component.Add(lockComponentId, DeterministicGuid.Format(lockGuid), "INSTALLFOLDER",
                          componentAttributes, null, null);
            featureComponents.Add("MainFeature", lockComponentId);
            db.Std("CreateFolder").Add("INSTALLFOLDER", lockComponentId);
            db.Std("MsiLockPermissionsEx").Add(
                UniqueId("lock_installfolder", identifiers),
                "INSTALLFOLDER", "CreateFolder", LockedFolderSddl, null);

            if (project.RootIsUserWritable && project.LockInstallFolder is null)
                warnings.Add("Installing under ProgramData, where the default permissions let any user "
                             + "write. The install folder is locked to SYSTEM, Administrators and "
                             + "read-only for everyone else. Set LockInstallFolder false if the "
                             + "application needs to write there itself.");
        }
        else if (project.RootIsUserWritable && (project.Run.Count > 0 || project.Tasks.Count > 0))
        {
            warnings.Add("This package runs a program as SYSTEM from a folder under ProgramData, which "
                         + "ordinary users can write to, and LockInstallFolder is set false. A user who "
                         + "puts a DLL next to that program gets it loaded as SYSTEM. Remove "
                         + "LockInstallFolder, or install under ProgramFiles instead.");
        }

        // ------------------------------------------------------ feature and media
        db.Std("Feature").Add("MainFeature", null, project.ProductName, null, 2, 1, null, 0);

        string cabName = "payload.cab";
        if (cabFiles.Count > 0)
        {
            byte[] cab = CabWriter.Build(cabFiles);
            db.Streams[cabName] = cab;
            // '#' marks the cabinet as an embedded stream, which is what makes the
            // MSI a single self-contained file - a hard requirement for Intune.
            db.Std("Media").Add(1, sequence, null, "#" + cabName, null, null);
        }
        else
        {
            db.Std("Media").Add(1, 0, null, null, null, null);
        }

        // Without this, Programs and Features shows a blank size. Windows expects
        // kilobytes.
        long payloadBytes = cabFiles.Sum(f => (long)f.Data.Length);
        if (payloadBytes > 0)
            db.SetProperty("ARPSIZE", Math.Max(1, payloadBytes / 1024).ToString());

        // Must come last: it only describes the tables that ended up present.
        db.AddValidation();

        // ------------------------------------------------------------- summary
        db.Summary = SummaryInfo.CreateDefault(project.ProductName, project.Manufacturer,
                                               platform, project.Language, compressed: cabFiles.Count > 0);
        string packageCode = db.Summary.RevisionNumber!;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        db.Save(outputPath);

        return new BuildResult(outputPath, productCode, project.UpgradeCode, version, packageCode,
                               new FileInfo(outputPath).Length, warnings);
    }

    /// <summary>
    /// The version Windows Installer should compare, or null for a file that
    /// genuinely carries no version resource.
    /// </summary>
    private static string? ReadFileVersion(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') return null;

        var pe = new PeFile(bytes);
        string? version = pe.Version?.FixedFileVersion;
        if (string.IsNullOrWhiteSpace(version)) return null;

        // A file whose version resource reads 0.0.0.0 is effectively unversioned.
        var parts = version.Split('.');
        return parts.All(p => int.TryParse(p, out int v) && v == 0) ? null : version;
    }

    /// <summary>
    /// The install directory as the target machine will see it, written with the
    /// environment variables Task Scheduler expands. Resolving it on the build
    /// machine would bake in this machine's drive layout instead of the one the
    /// package lands on.
    /// </summary>
    private static string TargetInstallDirectory(MsiProject project, bool is64)
    {
        string root = project.Root switch
        {
            InstallRoot.ProgramFiles => is64 ? "%ProgramFiles%" : "%ProgramFiles(x86)%",
            InstallRoot.ProgramFilesX86 => "%ProgramFiles(x86)%",
            InstallRoot.CommonAppData => "%ProgramData%",
            InstallRoot.Windows => "%SystemRoot%",
            _ => "%ProgramFiles%",
        };

        var parts = new List<string> { root };
        if (!string.IsNullOrWhiteSpace(project.ManufacturerFolder)) parts.Add(MsiProject.Sanitize(project.ManufacturerFolder!));
        parts.Add(project.EffectiveInstallFolder);
        return string.Join("\\", parts);
    }

    private static string? Combine(string? custom, string standard) =>
        string.IsNullOrWhiteSpace(custom) ? standard : $"({custom}) AND ({standard})";

    /// <summary>
    /// Directory.DefaultDir. Long names are fine because the package declares long
    /// file names in its summary information.
    /// </summary>
    private static string DefaultDir(string name) => name;

    private static int RegistryRoot(string root) => root.ToUpperInvariant() switch
    {
        "HKCR" or "HKEY_CLASSES_ROOT" => 0,
        "HKCU" or "HKEY_CURRENT_USER" => 1,
        "HKLM" or "HKEY_LOCAL_MACHINE" => 2,
        "HKU" or "HKEY_USERS" => 3,
        _ => 2,
    };

    /// <summary>Registry values carry their type as a prefix inside the Value column.</summary>
    private static string EncodeRegistryValue(RegistryValue value) => value.Type.ToLowerInvariant() switch
    {
        "integer" or "int" or "dword" => "#" + value.Value,
        "expandable" or "expandstring" => "#%" + value.Value,
        _ => value.Value,
    };

    /// <summary>
    /// MSI identifiers allow letters, digits, underscore and period, and must not
    /// start with a digit. Collisions get a short hash suffix.
    /// </summary>
    /// <summary>
    /// A file name that no other file in the install folder already uses. Unlike
    /// UniqueId this guards a path on disk, not a table key - the two are
    /// different namespaces and only the first was being kept distinct.
    /// </summary>
    private static string UniqueTarget(string name, Dictionary<string, string> taken)
    {
        if (!taken.ContainsKey(name)) return name;

        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem}_{n}{extension}";
            if (!taken.ContainsKey(candidate)) return candidate;
        }
    }

    private static string UniqueId(string seed, HashSet<string> taken)
    {
        var sb = new StringBuilder();
        foreach (char c in seed)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' ? c : '_');

        string candidate = sb.ToString();
        if (candidate.Length == 0 || !(char.IsAsciiLetter(candidate[0]) || candidate[0] == '_'))
            candidate = "_" + candidate;
        if (candidate.Length > 60) candidate = candidate[..60];

        if (taken.Add(candidate)) return candidate;

        for (int i = 2; ; i++)
        {
            string next = $"{candidate}_{i}";
            if (taken.Add(next)) return next;
        }
    }
}
