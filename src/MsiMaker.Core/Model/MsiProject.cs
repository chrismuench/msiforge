using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MsiMaker.Core.Signing;

namespace MsiMaker.Core.Model;

public enum MsiPlatform { X86, X64, Arm64 }

/// <summary>When a wrapped executable should be run.</summary>
public enum RunWhen { Install, Uninstall, Both }

/// <summary>Where the package installs to.</summary>
public enum InstallRoot { ProgramFiles, ProgramFilesX86, CommonAppData, Windows }

/// <summary>A file to lay down, relative to the install folder.</summary>
public sealed class PayloadFile
{
    public required string SourcePath { get; set; }

    /// <summary>Path under the install folder, using '/' or '\'. Defaults to the file name.</summary>
    public string? TargetPath { get; set; }

    public string EffectiveTarget =>
        (TargetPath ?? Path.GetFileName(SourcePath)).Replace('\\', '/').TrimStart('/');
}

/// <summary>
/// A whole directory to install, expanded to individual files at build time.
/// Real payloads are rarely a handful of files - an extracted SoftPaq is well
/// over a hundred - so folders are a first-class input.
/// </summary>
public sealed class PayloadFolder
{
    public required string SourceDir { get; set; }

    /// <summary>Destination under the install folder. Empty means the root.</summary>
    public string? TargetPath { get; set; }

    /// <summary>Glob-free exclusions, matched against the relative path.</summary>
    public List<string> Exclude { get; set; } = [];
}

/// <summary>
/// An executable, installed by the package, that the installer runs. This is how
/// non-MSI installers (SoftPaq, Inno, NSIS) get wrapped.
/// </summary>
public sealed class RunAction
{
    public required string Id { get; set; }

    /// <summary>
    /// Path of the executable relative to the install folder. Leave this null and
    /// set <see cref="Command"/> instead to run something the package did not
    /// install.
    /// </summary>
    public string? ExePath { get; set; }

    /// <summary>
    /// A shell command, run through cmd.exe, for something that is not in the
    /// payload - most often a vendor uninstaller that a wrapped installer put
    /// somewhere of its own choosing, which the MSI could otherwise never
    /// remove. Quote inner paths yourself.
    /// </summary>
    public string? Command { get; set; }

    public string Arguments { get; set; } = "";
    public RunWhen When { get; set; } = RunWhen.Install;

    /// <summary>Let the install continue even if the process returns non-zero.</summary>
    public bool IgnoreExitCode { get; set; }

    /// <summary>
    /// Arguments that undo this action if the install fails later on, run against
    /// the same executable. Windows Installer cannot roll back an arbitrary
    /// executable by itself, so without a rollback a failed install leaves
    /// whatever the executable did in place.
    /// </summary>
    public string? RollbackArguments { get; set; }

    /// <summary>
    /// A shell command that undoes this action, for when the undo is a different
    /// program from the one that did it - a wrapped installer is undone by the
    /// vendor's uninstaller, not by running the installer again. Takes precedence
    /// over <see cref="RollbackArguments"/>.
    /// </summary>
    public string? RollbackCommand { get; set; }

    /// <summary>True when this action registers a rollback of either kind.</summary>
    [JsonIgnore]
    public bool HasRollback =>
        !string.IsNullOrWhiteSpace(RollbackArguments) || !string.IsNullOrWhiteSpace(RollbackCommand);

    public string? Condition { get; set; }
}

public sealed class RegistryValue
{
    /// <summary>HKLM, HKCU, HKCR, HKU. Root 2 (HKLM) is the sane default for per-machine.</summary>
    public string Root { get; set; } = "HKLM";
    public required string Key { get; set; }
    public string? Name { get; set; }
    public required string Value { get; set; }

    /// <summary>"string", "integer", or "expandable".</summary>
    public string Type { get; set; } = "string";
}

public sealed class ShortcutSpec
{
    public required string Name { get; set; }

    /// <summary>Target executable, relative to the install folder.</summary>
    public required string TargetPath { get; set; }

    public string? Arguments { get; set; }
    public string? Description { get; set; }

    /// <summary>"ProgramMenuFolder" or "DesktopFolder".</summary>
    public string Directory { get; set; } = "ProgramMenuFolder";
}

/// <summary>Add/Remove Programs presentation.</summary>
public sealed class ArpInfo
{
    public string? HelpLink { get; set; }
    public string? AboutUrl { get; set; }
    public string? Contact { get; set; }
    public string? Comments { get; set; }
    public bool NoModify { get; set; } = true;
    public bool NoRepair { get; set; } = true;
    public bool NoRemove { get; set; }
}

/// <summary>Everything beyond the .msi is opt-in.</summary>
public sealed class OutputOptions
{
    /// <summary>Also emit a Win32 .intunewin package.</summary>
    public bool IntuneWin { get; set; }

    /// <summary>Emit a Proactive Remediation detect/remediate pair.</summary>
    public bool RemediationScripts { get; set; }
}

public sealed class MsiProject
{
    public string ProductName { get; set; } = "";
    public string Manufacturer { get; set; } = "";

    /// <summary>
    /// Only the first three fields are compared by Windows Installer. A fourth
    /// field is allowed but never affects upgrade detection.
    /// </summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>Stable for the lifetime of the product. This is what ties versions together.</summary>
    public Guid UpgradeCode { get; set; } = Guid.NewGuid();

    /// <summary>Pin the ProductCode explicitly. Normally left null so it is derived from the version.</summary>
    public Guid? ProductCode { get; set; }

    public MsiPlatform Platform { get; set; } = MsiPlatform.X64;
    public int Language { get; set; } = 1033;

    public InstallRoot Root { get; set; } = InstallRoot.ProgramFiles;

    /// <summary>Folder under the install root. Defaults to the product name.</summary>
    public string? InstallFolder { get; set; }

    /// <summary>Optional manufacturer folder above the install folder.</summary>
    public string? ManufacturerFolder { get; set; }

    public List<PayloadFile> Files { get; set; } = [];
    public List<PayloadFolder> Folders { get; set; } = [];
    public List<RunAction> Run { get; set; } = [];
    public List<RegistryValue> Registry { get; set; } = [];
    public List<ShortcutSpec> Shortcuts { get; set; } = [];
    public List<ScheduledTaskSpec> Tasks { get; set; } = [];

    /// <summary>
    /// Executable names to close before the package touches any file, e.g.
    /// "HPImageAssistant.exe". A running process holds its files open, so an
    /// upgrade cannot replace them and an uninstall cannot delete them - Windows
    /// Installer then either fails or quietly schedules a reboot, which on a
    /// silent Intune deployment nobody sees.
    /// </summary>
    public List<string> CloseProcesses { get; set; } = [];
    public ArpInfo Arp { get; set; } = new();
    public SigningOptions Signing { get; set; } = new();
    public OutputOptions Output { get; set; } = new();

    [JsonIgnore]
    public string EffectiveInstallFolder =>
        Sanitize(string.IsNullOrWhiteSpace(InstallFolder) ? ProductName : InstallFolder!);

    /// <summary>
    /// Version normalised to the three fields Windows Installer actually compares.
    /// </summary>
    [JsonIgnore]
    public string ComparableVersion
    {
        get
        {
            var parts = (Version ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries);
            int Field(int i) => i < parts.Length && int.TryParse(parts[i], out int v) ? v : 0;
            return $"{Field(0)}.{Field(1)}.{Field(2)}";
        }
    }

    /// <summary>
    /// The ProductCode, derived from UpgradeCode and version unless pinned. Deriving
    /// it means the code changes exactly when the version changes - never by accident,
    /// and never forgotten - which is precisely what Intune upgrade detection needs.
    /// </summary>
    public Guid EffectiveProductCode =>
        ProductCode ?? DeterministicGuid.Create(UpgradeCode, "ProductCode/" + ComparableVersion);

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
            if (!Path.GetInvalidFileNameChars().Contains(c)) sb.Append(c);
        string s = sb.ToString().Trim();
        return s.Length == 0 ? "Product" : s;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static MsiProject FromJson(string json) =>
        JsonSerializer.Deserialize<MsiProject>(json, JsonOptions)
        ?? throw new InvalidDataException("Project file is empty or malformed.");

    public void Save(string path) => File.WriteAllText(path, ToJson());

    /// <summary>
    /// Loads a project and resolves every relative payload path against the
    /// project file's own folder, which is what the author meant. Resolving
    /// against the process working directory instead makes a project behave
    /// differently depending on where it was opened from.
    /// </summary>
    public static MsiProject Load(string path)
    {
        string full = Path.GetFullPath(path);
        var project = FromJson(File.ReadAllText(full));
        project.ResolvePathsAgainst(Path.GetDirectoryName(full)!);
        return project;
    }

    /// <summary>Makes every relative payload path absolute, relative to <paramref name="baseDir"/>.</summary>
    public void ResolvePathsAgainst(string baseDir)
    {
        foreach (var file in Files)
            if (!Path.IsPathRooted(file.SourcePath))
                file.SourcePath = Path.GetFullPath(Path.Combine(baseDir, file.SourcePath));

        foreach (var folder in Folders)
            if (!Path.IsPathRooted(folder.SourceDir))
                folder.SourceDir = Path.GetFullPath(Path.Combine(baseDir, folder.SourceDir));

        // The signing certificate is a project-relative path like any other.
        if (!string.IsNullOrWhiteSpace(Signing.CertificatePath)
            && !Path.IsPathRooted(Signing.CertificatePath))
            Signing.CertificatePath = Path.GetFullPath(Path.Combine(baseDir, Signing.CertificatePath!));
    }

    /// <summary>
    /// Folds every <see cref="Folders"/> entry into <see cref="Files"/>. Safe to
    /// call more than once: already-expanded folders are not duplicated.
    /// </summary>
    public void ExpandFolders()
    {
        foreach (var folder in Folders)
        {
            if (!Directory.Exists(folder.SourceDir)) continue;
            string root = Path.GetFullPath(folder.SourceDir);
            string prefix = (folder.TargetPath ?? "").Replace('\\', '/').Trim('/');

            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (folder.Exclude.Any(x => relative.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;

                string target = prefix.Length == 0 ? relative : $"{prefix}/{relative}";
                if (Files.Any(f => string.Equals(f.EffectiveTarget, target, StringComparison.OrdinalIgnoreCase)))
                    continue;
                Files.Add(new PayloadFile { SourcePath = path, TargetPath = target });
            }
        }
    }

    /// <summary>Problems that would stop the package building or deploying correctly.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ProductName)) errors.Add("Product name is required.");
        if (string.IsNullOrWhiteSpace(Manufacturer)) errors.Add("Manufacturer is required.");
        if (UpgradeCode == Guid.Empty) errors.Add("UpgradeCode must be a real GUID; it ties versions together.");

        var parts = (Version ?? "").Split('.');
        if (parts.Length < 2 || !parts.Take(Math.Min(3, parts.Length)).All(p => int.TryParse(p, out _)))
            errors.Add($"Version '{Version}' is not a valid numeric version.");
        if (parts.Length > 0 && int.TryParse(parts[0], out int major) && major > 255)
            errors.Add("Version major field must be 0-255.");

        // Report a folder that is not there by name. ExpandFolders skips a
        // missing folder, so without this the only symptom is "no payload",
        // which sends the author looking in entirely the wrong place.
        foreach (var folder in Folders)
            if (!Directory.Exists(folder.SourceDir))
                errors.Add($"Payload folder not found: {folder.SourceDir}");

        if (Files.Count == 0 && Folders.Count == 0 && Run.Count == 0 && Registry.Count == 0)
            errors.Add("Package has no payload: add at least one file or folder.");

        foreach (var f in Files)
            if (!File.Exists(f.SourcePath)) errors.Add($"Payload file not found: {f.SourcePath}");

        // Caught here as well as in the cabinet writer, so the message names the
        // project rather than surfacing from deep inside the build.
        if (Files.Count > 65535)
            errors.Add($"A package holds at most 65,535 files; this one has {Files.Count:N0}.");

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Files)
            if (!targets.Add(f.EffectiveTarget)) errors.Add($"Duplicate target path: {f.EffectiveTarget}");

        foreach (var t in Tasks)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) errors.Add("A scheduled task needs a name.");
            string target = t.ExePath.Replace('\\', '/').TrimStart('/');
            if (!Files.Any(f => string.Equals(f.EffectiveTarget, target, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"Scheduled task '{t.Name}' runs '{t.ExePath}', which is not in the payload.");
        }

        foreach (var r in Run)
        {
            bool hasExe = !string.IsNullOrWhiteSpace(r.ExePath);
            bool hasCommand = !string.IsNullOrWhiteSpace(r.Command);

            if (hasExe == hasCommand)
            {
                errors.Add($"Run action '{r.Id}' needs exactly one of ExePath or Command.");
                continue;
            }
            if (hasExe && !Files.Any(f => string.Equals(
                    f.EffectiveTarget, r.ExePath!.Replace('\\', '/').TrimStart('/'),
                    StringComparison.OrdinalIgnoreCase)))
                errors.Add($"Run action '{r.Id}' points at '{r.ExePath}', which is not in the payload.");
        }

        return errors;
    }
}

/// <summary>
/// RFC 4122 name-based (version 5) GUIDs. Component codes must stay identical
/// across versions or upgrades leave orphaned files behind, so they are derived
/// from stable inputs rather than generated afresh each build.
/// </summary>
public static class DeterministicGuid
{
    public static Guid Create(Guid ns, string name)
    {
        Span<byte> nsBytes = stackalloc byte[16];
        ns.TryWriteBytes(nsBytes);
        SwapToBigEndian(nsBytes);

        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] buffer = new byte[16 + nameBytes.Length];
        nsBytes.CopyTo(buffer);
        nameBytes.CopyTo(buffer, 16);

        byte[] hash = SHA1.HashData(buffer);
        Span<byte> result = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(result);

        result[6] = (byte)((result[6] & 0x0F) | 0x50); // version 5
        result[8] = (byte)((result[8] & 0x3F) | 0x80); // RFC 4122 variant

        SwapToBigEndian(result);
        return new Guid(result);
    }

    /// <summary>GUID braces, uppercase - the form Windows Installer stores.</summary>
    public static string Format(Guid g) => "{" + g.ToString().ToUpperInvariant() + "}";

    private static void SwapToBigEndian(Span<byte> g)
    {
        (g[0], g[3]) = (g[3], g[0]);
        (g[1], g[2]) = (g[2], g[1]);
        (g[4], g[5]) = (g[5], g[4]);
        (g[6], g[7]) = (g[7], g[6]);
    }
}
