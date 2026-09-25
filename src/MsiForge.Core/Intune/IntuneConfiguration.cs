using MsiForge.Core.Cfbf;
using MsiForge.Core.Msi;

namespace MsiForge.Core.Intune;

/// <summary>
/// Exactly what to put in the Intune portal for a given package.
///
/// Every value here is already inside the MSI, but an admin standing in the
/// portal has to dig each one out with Orca or a PowerShell one-liner, and a
/// mistyped ProductCode in a detection rule fails silently - the app reports as
/// installed, or never as installed, and neither says why.
/// </summary>
public sealed class IntuneConfiguration
{
    public required string FileName { get; init; }
    public required string ProductName { get; init; }
    public required string Publisher { get; init; }
    public required string Version { get; init; }
    public required string ProductCode { get; init; }
    public required string UpgradeCode { get; init; }
    public required string Architecture { get; init; }
    public bool PerMachine { get; init; }
    public bool Signed { get; init; }
    public long SizeBytes { get; init; }

    public string InstallCommand => $"msiexec /i \"{FileName}\" /qn /norestart";

    /// <summary>
    /// Uninstall addresses the ProductCode, not the file. The MSI will not be on
    /// the device at removal time.
    /// </summary>
    public string UninstallCommand => $"msiexec /x {ProductCode} /qn /norestart";

    public List<string> Warnings { get; } = [];

    public static IntuneConfiguration From(string msiPath)
    {
        var db = MsiDatabase.Load(msiPath);
        string template = (db.Summary.Template ?? "").Split(';').FirstOrDefault() ?? "";

        var config = new IntuneConfiguration
        {
            FileName = Path.GetFileName(msiPath),
            ProductName = db.GetProperty("ProductName") ?? "",
            Publisher = db.GetProperty("Manufacturer") ?? "",
            Version = db.GetProperty("ProductVersion") ?? "",
            ProductCode = db.GetProperty("ProductCode") ?? "",
            UpgradeCode = db.GetProperty("UpgradeCode") ?? "",
            Architecture = template.Equals("Intel", StringComparison.OrdinalIgnoreCase) ? "x86" : template,
            PerMachine = db.GetProperty("ALLUSERS") == "1",
            Signed = IsSigned(msiPath),
            SizeBytes = new FileInfo(msiPath).Length,
        };

        if (!config.PerMachine)
            config.Warnings.Add("This package is not per-machine. Intune installs as SYSTEM, so a per-user "
                                + "package lands in the service account's profile and the user never sees it.");

        if (string.IsNullOrWhiteSpace(config.UpgradeCode))
            config.Warnings.Add("No UpgradeCode. Nothing ties this build to the next one, so a future version "
                                + "will install alongside rather than upgrade.");

        if (!config.Signed)
            config.Warnings.Add("Unsigned. Signing is not required by Intune, but most security baselines "
                                + "expect it and SmartScreen is friendlier to signed packages.");

        var parts = config.Version.Split('.');
        if (parts.Length > 3)
            config.Warnings.Add($"Version '{config.Version}' has a fourth field, which Windows Installer "
                                + "ignores when deciding whether to upgrade.");

        return config;
    }

    /// <summary>
    /// The signature lives in a stream whose name begins with , which the
    /// database reader treats as metadata and does not surface, so the container
    /// has to be asked directly.
    /// </summary>
    private static bool IsSigned(string msiPath)
    {
        try
        {
            var reader = CfbReader.FromFile(msiPath);
            return reader.Streams.Any(s => MsiName.Decode(s.Name)
                .Contains("DigitalSignature", StringComparison.Ordinal));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// The portal fields, in the order the Intune wizard asks for them, so the
    /// output can be worked through top to bottom.
    /// </summary>
    public string Describe()
    {
        var text = new System.Text.StringBuilder();
        void Section(string name) { text.AppendLine(); text.AppendLine(name); }
        void Field(string label, string value) => text.AppendLine($"  {label,-22} {value}");

        text.AppendLine($"Intune configuration for {FileName}  ({SizeBytes:N0} bytes)");

        Section("App information");
        Field("Name", ProductName);
        Field("Publisher", Publisher);
        Field("App version", Version);

        Section("Program  (Win32 app only - a line-of-business MSI fills these in itself)");
        Field("Install command", InstallCommand);
        Field("Uninstall command", UninstallCommand);
        Field("Install behaviour", PerMachine ? "System" : "User");
        Field("Device restart", "No specific action");

        Section("Return codes");
        Field("0", "Success");
        Field("1707", "Success");
        Field("3010", "Soft reboot");
        Field("1641", "Hard reboot");
        Field("1618", "Retry  (another install is already running)");

        Section("Requirements");
        Field("Architecture", Architecture);
        Field("Minimum OS", "Windows 10 1809 or later");

        Section("Detection rule  (no script needed)");
        Field("Rule type", "MSI");
        Field("MSI product code", ProductCode);
        Field("Version check", $"Yes  -  greater than or equal to  {Version}");

        Section("Supersedence  (Win32 app only)");
        Field("Supersedes", "the previous version of this app");
        Field("Uninstall previous", "No  -  the MSI removes it itself during the upgrade");

        Section("For your records");
        Field("UpgradeCode", UpgradeCode);
        Field("", "Stable for the life of the product. Every future version must carry it.");
        Field("Signed", Signed ? "yes" : "no");

        if (Warnings.Count > 0)
        {
            Section("Worth fixing before you deploy");
            foreach (var w in Warnings) text.AppendLine($"  - {w}");
        }

        return text.ToString();
    }
}
