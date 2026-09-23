using System.Security.Cryptography;
using System.Text;
using MsiMaker.Core.Cab;
using MsiMaker.Core.Model;
using MsiMaker.Core.Msi;

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
    private const int CaIgnoreReturn = 64;
    private const int CaDeferred = 1024;
    private const int CaNoImpersonate = 2048;

    private const int ComponentIs64Bit = 256;
    private const int ComponentRegistryKeyPath = 4;
    private const int FileVital = 512;

    private const string DowngradeProperty = "MSIMAKERDOWNGRADE";
    private const string UpgradeProperty = "MSIMAKERUPGRADE";

    public static BuildResult Build(MsiProject project, string outputPath)
    {
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
            file.Add(fileId, componentId, fileName, bytes.Length, null, null, FileVital, sequence);

            // Unversioned files are compared by hash, so MsiFileHash is what makes
            // repair and upgrade replace them correctly.
            byte[] md5 = MD5.HashData(bytes);
            fileHash.Add(fileId, 0,
                BitConverter.ToInt32(md5, 0), BitConverter.ToInt32(md5, 4),
                BitConverter.ToInt32(md5, 8), BitConverter.ToInt32(md5, 12));

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

        // -------------------------------------------------------- run actions
        var installSequence = db.Std("InstallExecuteSequence");
        foreach (var (action, condition, seq) in MsiSchema.Sequences["InstallExecuteSequence"])
            installSequence.Add(action, condition, seq);
        foreach (var table in new[] { "InstallUISequence", "AdminExecuteSequence", "AdminUISequence", "AdvtExecuteSequence" })
        {
            var t = db.Std(table);
            foreach (var (action, condition, seq) in MsiSchema.Sequences[table]) t.Add(action, condition, seq);
        }

        if (project.Run.Count > 0)
        {
            var customAction = db.Std("CustomAction");
            int installSlot = 4100;    // after InstallFiles (4000), before InstallFinalize (6600)
            int uninstallSlot = 3400;  // before RemoveFiles (3500), while the payload still exists

            foreach (var run in project.Run)
            {
                string exePath = run.ExePath.Replace('\\', '/').TrimStart('/');
                if (!fileIdByTarget.TryGetValue(exePath, out string? exeFileId))
                {
                    warnings.Add($"Run action '{run.Id}' targets '{run.ExePath}', which is not in the payload; skipped.");
                    continue;
                }

                int type = CaExeFromFile | CaDeferred | CaNoImpersonate | (run.IgnoreExitCode ? CaIgnoreReturn : 0);

                if (run.When is RunWhen.Install or RunWhen.Both)
                {
                    string id = UniqueId($"ca_{run.Id}_install", identifiers);
                    customAction.Add(id, type, exeFileId, run.Arguments, null);
                    installSequence.Add(id, Combine(run.Condition, "NOT REMOVE"), installSlot);
                    installSlot += 10;
                }
                if (run.When is RunWhen.Uninstall or RunWhen.Both)
                {
                    string id = UniqueId($"ca_{run.Id}_uninstall", identifiers);
                    customAction.Add(id, type, exeFileId, run.Arguments, null);
                    installSequence.Add(id, Combine(run.Condition, "REMOVE~=\"ALL\""), uninstallSlot);
                    uninstallSlot += 10;
                }
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
