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
        db.SetProperty("SecureCustomProperties", $"{DowngradeProperty};{UpgradeProperty}");

        var arp = project.Arp;
        if (!string.IsNullOrWhiteSpace(arp.HelpLink)) db.SetProperty("ARPHELPLINK", arp.HelpLink!);
        if (!string.IsNullOrWhiteSpace(arp.AboutUrl)) db.SetProperty("ARPURLINFOABOUT", arp.AboutUrl!);
        if (!string.IsNullOrWhiteSpace(arp.Contact)) db.SetProperty("ARPCONTACT", arp.Contact!);
        if (!string.IsNullOrWhiteSpace(arp.Comments)) db.SetProperty("ARPCOMMENTS", arp.Comments!);
        if (arp.NoModify) db.SetProperty("ARPNOMODIFY", "1");
        if (arp.NoRepair) db.SetProperty("ARPNOREPAIR", "1");
        if (arp.NoRemove) db.SetProperty("ARPNOREMOVE", "1");

        // ------------------------------------------------------------- upgrade
        // Row 1 removes every earlier version. Row 2 only detects a newer one so
        // the launch condition can refuse the downgrade with a clear message.
        var upgrade = db.Std("Upgrade");
        upgrade.Add(DeterministicGuid.Format(project.UpgradeCode), null, version, null, 513, null, UpgradeProperty);
        upgrade.Add(DeterministicGuid.Format(project.UpgradeCode), version, null, null, 2, null, DowngradeProperty);

        db.Std("LaunchCondition").Add(
            $"NOT {DowngradeProperty}",
            $"A newer version of {project.ProductName} is already installed. Setup will now exit.");

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
            directory.Add("MFRFOLDER", parent, DefaultDir(project.ManufacturerFolder!));
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
                byte[] md5 = MD5.HashData(bytes);
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

            // The install directory is a token here, not "[#fileId]": MSI formats
            // its own fields but never the contents of a file, so a formatted
            // expression written into the XML would reach the device verbatim.
            string windowsRelative = exeTarget.Replace('/', '\\');
            string xml = TaskXml.Build(task,
                TaskXml.InstallDirToken + "\\" + windowsRelative,
                task.Arguments,
                TaskXml.InstallDirToken);
            byte[] xmlBytes = TaskXml.Encode(xml);

            sequence++;
            string taskFileName = MsiProject.Sanitize(task.Name).Replace(' ', '_') + ".xml";
            string taskFileId = UniqueId("f_task_" + taskFileName, identifiers);
            string taskComponentId = UniqueId("c_task_" + taskFileName, componentIds);
            Guid taskGuid = DeterministicGuid.Create(project.UpgradeCode, "Component/task/" + task.FullName);

            component.Add(taskComponentId, DeterministicGuid.Format(taskGuid), "INSTALLFOLDER",
                          componentAttributes, null, taskFileId);
            featureComponents.Add("MainFeature", taskComponentId);
            file.Add(taskFileId, taskComponentId, taskFileName, xmlBytes.Length, null, null, FileVital, sequence);

            byte[] taskMd5 = MD5.HashData(xmlBytes);
            fileHash.Add(taskFileId, 0,
                BitConverter.ToInt32(taskMd5, 0), BitConverter.ToInt32(taskMd5, 4),
                BitConverter.ToInt32(taskMd5, 8), BitConverter.ToInt32(taskMd5, 12));

            cabFiles.Add(new CabFile(taskFileId, xmlBytes, DateTime.Now));
            taskFiles.Add((task, taskFileId));
        }

        // One registrar script serves every task in the package.
        string? registrarFileId = null;
        if (taskFiles.Count > 0)
        {
            byte[] scriptBytes = new UTF8Encoding(false).GetBytes(TaskXml.RegistrarScript);
            sequence++;
            registrarFileId = UniqueId("f_msimaker_register_task.ps1", identifiers);
            string scriptComponentId = UniqueId("c_msimaker_register_task", componentIds);
            Guid scriptGuid = DeterministicGuid.Create(project.UpgradeCode, "Component/task-registrar");

            component.Add(scriptComponentId, DeterministicGuid.Format(scriptGuid), "INSTALLFOLDER",
                          componentAttributes, null, registrarFileId);
            featureComponents.Add("MainFeature", scriptComponentId);
            file.Add(registrarFileId, scriptComponentId, "MsiMaker-RegisterTask.ps1",
                     scriptBytes.Length, null, null, FileVital, sequence);

            byte[] scriptMd5 = MD5.HashData(scriptBytes);
            fileHash.Add(registrarFileId, 0,
                BitConverter.ToInt32(scriptMd5, 0), BitConverter.ToInt32(scriptMd5, 4),
                BitConverter.ToInt32(scriptMd5, 8), BitConverter.ToInt32(scriptMd5, 12));
            cabFiles.Add(new CabFile(registrarFileId, scriptBytes, DateTime.Now));
        }

        // SystemFolder is the 32-bit system directory (SysWOW64 on x64); the
        // native one is System64Folder. Added on demand, since a package with no
        // tasks and no shell commands has no use for it.
        string systemDirId = is64 ? "System64Folder" : "SystemFolder";
        string powershellDirId = "d_powershell_v1_0";

        void EnsureSystemDirectory()
        {
            if (identifiers.Add(systemDirId)) directory.Add(systemDirId, "TARGETDIR", ".");
        }

        if (taskFiles.Count > 0)
        {
            EnsureSystemDirectory();
            if (identifiers.Add("d_windowspowershell"))
                directory.Add("d_windowspowershell", systemDirId, "WindowsPowerShell");
            if (identifiers.Add(powershellDirId))
                directory.Add(powershellDirId, "d_windowspowershell", "v1.0");
        }

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
        const int immediateCloseSequence = 1490;   // after InstallValidate (1400)
        const int deferredCloseSequence = 1550;    // after RemoveExistingProducts (1501)

        foreach (var (action, condition, seq) in MsiSchema.Sequences["InstallExecuteSequence"])
            installSequence.Add(action, condition, seq);
        foreach (var table in new[] { "InstallUISequence", "AdminExecuteSequence", "AdminUISequence", "AdvtExecuteSequence" })
        {
            var t = db.Std(table);
            foreach (var (action, condition, seq) in MsiSchema.Sequences[table]) t.Add(action, condition, seq);
        }

        if (processesToClose.Count > 0)
        {
            EnsureSystemDirectory();
            var closeActions = db.Std("CustomAction");
            int immediateSlot = immediateCloseSequence;
            int deferredSlot = deferredCloseSequence;

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
                installSequence.Add(immediate, null, immediateSlot);
                immediateSlot++;

                string deferred = UniqueId($"ca_close_deferred_{image}", identifiers);
                closeActions.Add(deferred,
                    CaExeInDirectory | CaDeferred | CaNoImpersonate | CaIgnoreReturn,
                    systemDirId, command, null);
                installSequence.Add(deferred, null, deferredSlot);
                deferredSlot++;
            }
        }

        if (project.Run.Count > 0)
        {
            var customAction = db.Std("CustomAction");
            int installSlot = 4100;    // after InstallFiles (4000), before InstallFinalize (6600)
            int uninstallSlot = 3400;  // before RemoveFiles (3500), while the payload still exists

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

                    // The rollback must sit immediately ahead of the action it
                    // undoes; rollback actions replay in reverse order.
                    if (run.HasRollback)
                    {
                        string rollbackId = UniqueId($"ca_{run.Id}_rollback", identifiers);
                        customAction.Add(rollbackId,
                            rollbackBaseType | CaRollback | CaDeferred | CaNoImpersonate | CaIgnoreReturn,
                            rollbackSource, rollbackTarget, null);
                        installSequence.Add(rollbackId, condition, installSlot);
                        installSlot += 1;
                    }
                    else
                    {
                        warnings.Add($"Run action '{run.Id}' has no rollback. If the install fails "
                                     + "after it runs, whatever it did is left behind. Set "
                                     + "RollbackCommand or RollbackArguments.");
                    }

                    string id = UniqueId($"ca_{run.Id}_install", identifiers);
                    customAction.Add(id, type, source, target, null);
                    installSequence.Add(id, condition, installSlot);
                    installSlot += 10;
                }
                if (run.When is RunWhen.Uninstall or RunWhen.Both)
                {
                    string id = UniqueId($"ca_{run.Id}_uninstall", identifiers);
                    customAction.Add(id, type, source, target, null);
                    installSequence.Add(id, Combine(run.Condition, "REMOVE~=\"ALL\""), uninstallSlot);
                    uninstallSlot += 10;
                }
            }
        }

        if (taskFiles.Count > 0)
        {
            var customAction = db.Std("CustomAction");
            int createSlot = 4300;   // after InstallFiles, so the XML is on disk
            int deleteSlot = 3400;   // before RemoveFiles, while it still is

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
                installSequence.Add(undo, "NOT REMOVE", createSlot);
                createSlot += 1;

                string create = UniqueId($"ca_task_create_{task.Name}", identifiers);
                customAction.Add(create,
                    CaExeInDirectory | CaDeferred | CaNoImpersonate,
                    powershellDirId,
                    "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass "
                    + $"-File \"[#{registrarFileId}]\" "
                    + $"-Xml \"[#{taskFileId}]\" "
                    // A directory property ends in a backslash, and "...\\" would
                    // escape the closing quote. The trailing dot keeps the argument
                    // well formed and still names the same directory.
                    + "-InstallDir \"[INSTALLFOLDER].\" "
                    + $"-TaskName \"{task.FullName}\"",
                    null);
                installSequence.Add(create, "NOT REMOVE", createSlot);
                createSlot += 10;

                // Deleting is allowed to fail: the task may already be gone.
                string delete = UniqueId($"ca_task_delete_{task.Name}", identifiers);
                customAction.Add(delete,
                    CaExeInDirectory | CaDeferred | CaNoImpersonate | CaIgnoreReturn,
                    systemDirId,
                    $"schtasks.exe /Delete /TN \"{task.FullName}\" /F",
                    null);
                installSequence.Add(delete, "REMOVE~=\"ALL\"", deleteSlot);
                deleteSlot += 10;
            }
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
