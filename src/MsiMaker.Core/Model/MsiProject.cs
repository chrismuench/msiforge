using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
/// An executable, installed by the package, that the installer runs. This is how
/// non-MSI installers (SoftPaq, Inno, NSIS) get wrapped.
/// </summary>
public sealed class RunAction
{
    public required string Id { get; set; }

    /// <summary>Path of the executable relative to the install folder.</summary>
    public required string ExePath { get; set; }

    public string Arguments { get; set; } = "";
    public RunWhen When { get; set; } = RunWhen.Install;

    /// <summary>Let the install continue even if the process returns non-zero.</summary>
    public bool IgnoreExitCode { get; set; }

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

    /// <summary>Upload to Intune via Graph after building.</summary>
    public bool UploadToIntune { get; set; }

    /// <summary>Emit a Proactive Remediation detect/remediate pair.</summary>
    public bool RemediationScripts { get; set; }

    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
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
    public List<RunAction> Run { get; set; } = [];
    public List<RegistryValue> Registry { get; set; } = [];
    public List<ShortcutSpec> Shortcuts { get; set; } = [];
    public ArpInfo Arp { get; set; } = new();
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

    public static MsiProject Load(string path) => FromJson(File.ReadAllText(path));

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

        if (Files.Count == 0 && Run.Count == 0 && Registry.Count == 0)
            errors.Add("Package has no payload: add at least one file.");

        foreach (var f in Files)
            if (!File.Exists(f.SourcePath)) errors.Add($"Payload file not found: {f.SourcePath}");

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Files)
            if (!targets.Add(f.EffectiveTarget)) errors.Add($"Duplicate target path: {f.EffectiveTarget}");

        foreach (var r in Run)
            if (!Files.Any(f => string.Equals(f.EffectiveTarget, r.ExePath.Replace('\\', '/').TrimStart('/'),
                                              StringComparison.OrdinalIgnoreCase)))
                errors.Add($"Run action '{r.Id}' points at '{r.ExePath}', which is not in the payload.");

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
