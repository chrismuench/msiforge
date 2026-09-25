using MsiMaker.Core.Model;

namespace MsiMaker.Core.Payload;

public enum InstallerKind
{
    Unknown,
    Msi,
    InnoSetup,
    Nsis,
    WixBurn,
    InstallShield,
    SevenZipSfx,
    HpSoftPaq,
    WinZipSfx,
    AdvancedInstaller,
    InstallAware,
    SquirrelSetup,
    MsiWrappedExe,
}

/// <summary>What the detective worked out about a payload, and what to do with it.</summary>
public sealed class PayloadAnalysis
{
    public required string Path { get; init; }
    public InstallerKind Kind { get; set; } = InstallerKind.Unknown;
    public string KindName => Describe(Kind);

    /// <summary>0-100. Below ~60 the switches are a starting point, not an answer.</summary>
    public int Confidence { get; set; }

    /// <summary>Silent-install switches for this installer family.</summary>
    public string? SilentArguments { get; set; }

    /// <summary>Switches that also direct the install to a chosen directory.</summary>
    public string? SilentArgumentsWithDirectory { get; set; }

    public string? UninstallHint { get; set; }
    public PeArchitecture Architecture { get; set; } = PeArchitecture.Unknown;
    public PeVersionInfo? Version { get; set; }

    /// <summary>What the fingerprint was, so the suggestion can be argued with.</summary>
    public List<string> Evidence { get; } = [];

    public List<string> Warnings { get; } = [];

    public static string Describe(InstallerKind kind) => kind switch
    {
        InstallerKind.Msi => "Windows Installer package",
        InstallerKind.InnoSetup => "Inno Setup",
        InstallerKind.Nsis => "NSIS (Nullsoft)",
        InstallerKind.WixBurn => "WiX Burn bundle",
        InstallerKind.InstallShield => "InstallShield",
        InstallerKind.SevenZipSfx => "7-Zip self-extracting archive",
        InstallerKind.HpSoftPaq => "HP SoftPaq",
        InstallerKind.WinZipSfx => "WinZip self-extractor",
        InstallerKind.AdvancedInstaller => "Advanced Installer",
        InstallerKind.InstallAware => "InstallAware",
        InstallerKind.SquirrelSetup => "Squirrel",
        InstallerKind.MsiWrappedExe => "EXE wrapping an MSI",
        _ => "Unrecognised",
    };
}

/// <summary>
/// Works out which installer framework produced an executable and proposes the
/// switches that make it run silently. Silent switches are not guesswork: every
/// framework leaves an identifiable fingerprint in the binary.
/// </summary>
public static partial class InstallerDetective
{
    private sealed record Signature(
        InstallerKind Kind,
        string Marker,
        bool Unicode,
        int Confidence,
        string Silent,
        string? SilentWithDirectory,
        string? Uninstall,
        string Evidence);

    // Ordered most specific first: an Inno or NSIS installer may also contain
    // generic strings that a weaker signature would match.
    private static readonly Signature[] Signatures =
    [
        new(InstallerKind.InnoSetup, "Inno Setup Setup Data", false, 95,
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=\"{0}\"",
            "unins000.exe /VERYSILENT /NORESTART",
            "PE contains the 'Inno Setup Setup Data' marker"),

        new(InstallerKind.Nsis, "NullsoftInst", false, 95,
            "/S",
            "/S /D={0}",
            "Uninstall.exe /S",
            "PE contains the Nullsoft 'NullsoftInst' signature"),

        new(InstallerKind.Nsis, "Nullsoft Install System", false, 90,
            "/S",
            "/S /D={0}",
            "Uninstall.exe /S",
            "PE contains 'Nullsoft Install System'"),

        new(InstallerKind.SevenZipSfx, "!@Install@!UTF-8!", false, 90,
            "-y",
            "-y -o\"{0}\"",
            null,
            "PE contains the 7-Zip SFX config marker '!@Install@!UTF-8!'"),

        new(InstallerKind.InstallShield, "InstallShield", false, 75,
            "/s /v\"/qn\"",
            "/s /v\"/qn INSTALLDIR=\\\"{0}\\\"\"",
            null,
            "PE contains 'InstallShield'"),

        new(InstallerKind.WinZipSfx, "WinZip Self-Extractor", false, 85,
            "/auto",
            "/auto \"{0}\"",
            null,
            "PE contains 'WinZip Self-Extractor'"),

        new(InstallerKind.AdvancedInstaller, "Advanced Installer", false, 80,
            "/exenoui /qn",
            "/exenoui /qn APPDIR=\"{0}\"",
            null,
            "PE contains 'Advanced Installer'"),

        new(InstallerKind.InstallAware, "InstallAware", false, 80,
            "/s",
            "/s /D={0}",
            null,
            "PE contains 'InstallAware'"),

        new(InstallerKind.SquirrelSetup, "Squirrel", false, 70,
            "--silent",
            null,
            null,
            "PE contains 'Squirrel'"),
    ];

    public static PayloadAnalysis Analyse(string path)
    {
        var result = new PayloadAnalysis { Path = path };
        byte[] data = File.ReadAllBytes(path);

        // An MSI is a compound file, not a PE at all.
        if (data.Length >= 8 && data[0] == 0xD0 && data[1] == 0xCF && data[2] == 0x11 && data[3] == 0xE0)
        {
            result.Kind = InstallerKind.Msi;
            result.Confidence = 100;
            result.SilentArguments = "/qn";
            result.Evidence.Add("File is a compound document (MSI), not an executable");
            result.Warnings.Add("This is already an MSI. Repackaging it inside another MSI is not "
                                + "supported by Windows Installer - deploy it directly, or use a "
                                + "transform for customisation.");
            return result;
        }

        var pe = new PeFile(data);
        if (!pe.IsValid)
        {
            result.Warnings.Add("Not a PE executable; cannot determine an installer type.");
            return result;
        }

        result.Architecture = pe.Architecture;
        result.Version = pe.Version;

        // A .wixburn section is definitive and cheap to check.
        if (pe.SectionNames.Any(s => s.Equals(".wixburn", StringComparison.OrdinalIgnoreCase)))
        {
            Apply(result, new Signature(InstallerKind.WixBurn, "", false, 100,
                "/quiet /norestart",
                "/quiet /norestart InstallFolder=\"{0}\"",
                "/uninstall /quiet /norestart",
                "PE has a '.wixburn' section"));
            AddHpNote(result, pe, path);
            return result;
        }

        foreach (var signature in Signatures)
        {
            bool hit = signature.Unicode ? pe.ContainsUnicode(signature.Marker)
                                         : pe.ContainsAscii(signature.Marker);
            if (!hit) continue;
            Apply(result, signature);
            break;
        }

        AddHpNote(result, pe, path);

        if (result.Kind == InstallerKind.Unknown)
        {
            result.Warnings.Add("Installer family not recognised. Try '/?' or '/help' against the "
                                + "executable, or check the vendor's documentation, then set the "
                                + "arguments by hand.");
            result.SilentArguments = "/S";
            result.Confidence = 10;
            result.Evidence.Add("No known fingerprint matched; '/S' is only a common default");
        }

        return result;
    }

    /// <summary>
    /// HP SoftPaqs are self-extractors whose switches differ from the generic
    /// form, and HPIA in particular only extracts - it does not install.
    /// </summary>
    private static void AddHpNote(PayloadAnalysis result, PeFile pe, string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        bool looksHp = name.StartsWith("sp", StringComparison.OrdinalIgnoreCase)
                       && name.Length > 2 && name[2..].All(char.IsDigit);
        bool hpVendor = (pe.Version?.CompanyName ?? "").Contains("HP", StringComparison.OrdinalIgnoreCase)
                        || (pe.Version?.CompanyName ?? "").Contains("Hewlett", StringComparison.OrdinalIgnoreCase);

        if (!looksHp && !hpVendor) return;

        result.Kind = InstallerKind.HpSoftPaq;
        result.Confidence = Math.Max(result.Confidence, looksHp && hpVendor ? 90 : 70);
        result.SilentArguments = "/s /e /f \"%TEMP%\\hp\"";
        result.SilentArgumentsWithDirectory = "/s /e /f \"{0}\"";
        result.Evidence.Add(looksHp
            ? $"File name '{name}' matches the HP SoftPaq spNNNNNN convention"
            : $"Version resource names HP as the vendor ('{pe.Version?.CompanyName}')");
        result.Warnings.Add("A SoftPaq extracts rather than installs: '/s /e /f <dir>' unpacks the "
                            + "payload and nothing is registered. Package the extracted files, and "
                            + "run the real entry point yourself.");
    }

    private static void Apply(PayloadAnalysis result, Signature signature)
    {
        result.Kind = signature.Kind;
        result.Confidence = signature.Confidence;
        result.SilentArguments = signature.Silent;
        result.SilentArgumentsWithDirectory = signature.SilentWithDirectory;
        result.UninstallHint = signature.Uninstall;
        result.Evidence.Add(signature.Evidence);
    }

    /// <summary>
    /// Seeds a project from a payload: identity from the version resource,
    /// architecture from the PE, and the proposed silent switches.
    /// </summary>
    public static MsiProject ToProject(PayloadAnalysis analysis)
    {
        var version = analysis.Version;
        string product = version?.ProductName
                         ?? version?.FileDescription
                         ?? Path.GetFileNameWithoutExtension(analysis.Path);

        var project = new MsiProject
        {
            ProductName = product,
            Manufacturer = version?.CompanyName ?? "Unknown",
            Version = NormaliseVersion(version?.ProductVersion ?? version?.FileVersion),
            UpgradeCode = Guid.NewGuid(),
            Platform = analysis.Architecture switch
            {
                PeArchitecture.X86 => MsiPlatform.X86,
                PeArchitecture.Arm64 => MsiPlatform.Arm64,
                _ => MsiPlatform.X64,
            },
        };

        string fileName = Path.GetFileName(analysis.Path);
        project.Files.Add(new PayloadFile { SourcePath = analysis.Path });

        // Just the silent switch, with no install directory invented. Where a
        // wrapped installer lands is a real decision - a specific folder, or the
        // vendor's own default - and picking one here would be guessing at it. The
        // choice is put to whoever builds the package, not assumed; the detective's
        // job is only to record that the installer can take a directory switch, so
        // that prompt knows the switch to write.
        project.Run.Add(new RunAction
        {
            Id = "payload",
            ExePath = fileName,
            Arguments = analysis.SilentArguments ?? "",
            When = RunWhen.Install,
        });
        return project;
    }

    /// <summary>
    /// Whether a wrapped installer's arguments already name where to install. The
    /// tokens are the directory switches of the installer kinds recognised above,
    /// plus [INSTALLFOLDER] for anyone wiring the MSI's own folder in by hand.
    /// Deliberately broad: a false positive only skips a prompt or a warning, while
    /// a false negative nags a package that is already correct.
    /// </summary>
    public static bool MentionsInstallDirectory(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return false;
        string[] hints = ["/d=", "/dir", "installdir", "appdir", "targetdir", "-o", "[installfolder]"];
        return hints.Any(h => arguments.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    // Value-bearing directory switches, quoted or bare, with the switch itself and
    // its value captured. /D= is handled separately everywhere below because NSIS
    // defines it to swallow the rest of the command line, unquoted.
    [System.Text.RegularExpressions.GeneratedRegex(
        @"(/DIR=|INSTALLDIR=|APPDIR=|TARGETDIR=|-o)(""[^""]*""|\S+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DirectorySwitch();

    /// <summary>
    /// The install path already named in a set of arguments, or null. Used to seed
    /// the location prompt from a blueprint's own value, so the blueprint's path is a
    /// suggestion the user confirms rather than a decision made for them.
    /// </summary>
    public static string? ExtractInstallDirectory(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;

        int d = arguments.IndexOf("/D=", StringComparison.OrdinalIgnoreCase);
        if (d >= 0) return arguments[(d + 3)..].Trim().Trim('"');

        var m = DirectorySwitch().Match(arguments);
        return m.Success ? m.Groups[2].Value.Trim().Trim('"') : null;
    }

    /// <summary>
    /// The arguments with the install directory changed to <paramref name="path"/>,
    /// reusing whatever switch is already there so a blueprint's other arguments and
    /// its quoting style survive. Returns the arguments unchanged if none is found.
    /// </summary>
    public static string WithInstallDirectory(string? arguments, string path)
    {
        arguments ??= "";

        int d = arguments.IndexOf("/D=", StringComparison.OrdinalIgnoreCase);
        if (d >= 0) return arguments[..(d + 3)] + path;   // NSIS: /D= runs to end of line, unquoted

        var m = DirectorySwitch().Match(arguments);
        if (!m.Success) return arguments;

        string replacement = $"{m.Groups[1].Value}\"{path}\"";
        return arguments[..m.Groups[1].Index] + replacement + arguments[(m.Groups[2].Index + m.Groups[2].Length)..];
    }

    /// <summary>
    /// The arguments with any install-directory switch removed, for when the choice
    /// is to let the installer use its own default. A blueprint's baked-in path is
    /// otherwise still there and still obeyed, which would make the "default" choice
    /// a lie.
    /// </summary>
    public static string RemoveInstallDirectory(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return "";

        int d = arguments.IndexOf("/D=", StringComparison.OrdinalIgnoreCase);
        if (d >= 0) return arguments[..d].Trim();

        return DirectorySwitch().Replace(arguments, "").Replace("  ", " ").Trim();
    }

    /// <summary>
    /// Reduces a version to the three fields Windows Installer compares, and
    /// clamps the major field to the 0-255 the format allows.
    /// </summary>
    public static string NormaliseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "1.0.0";
        var parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int Field(int i, int max)
        {
            if (i >= parts.Length) return 0;
            var digits = new string(parts[i].TakeWhile(char.IsAsciiDigit).ToArray());
            return int.TryParse(digits, out int v) ? Math.Clamp(v, 0, max) : 0;
        }
        return $"{Field(0, 255)}.{Field(1, 255)}.{Field(2, 65535)}";
    }
}
