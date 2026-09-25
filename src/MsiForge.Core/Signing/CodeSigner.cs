using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

using System.Text.Json.Serialization;

namespace MsiForge.Core.Signing;

/// <summary>
/// Which key material signs the package. Left unset, it is inferred from the
/// options that are filled in, so projects written before the mode existed keep
/// building unchanged.
/// </summary>
public enum SigningMode
{
    /// <summary>A PFX file on disk, unlocked by a password from the environment.</summary>
    Pfx,

    /// <summary>A certificate already installed in the Windows certificate store.</summary>
    CertificateStore,

    /// <summary>Azure Trusted Signing: the private key never leaves the service.</summary>
    AzureTrustedSigning,
}

/// <summary>How the package should be signed. Never holds a password.</summary>
public sealed class SigningOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Which signing mode to use. Leave null to infer it, which is what every
    /// project written before this property existed does.
    /// </summary>
    public SigningMode? Mode { get; set; }

    /// <summary>PFX file to sign with. Mutually exclusive with <see cref="Thumbprint"/>.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    /// Name of an environment variable holding the PFX password. The password
    /// itself is deliberately not a field: a project file belongs in source
    /// control, and a secret in it would go with it.
    /// </summary>
    public string? PasswordEnvironmentVariable { get; set; }

    /// <summary>SHA-1 thumbprint of a certificate in the Windows store.</summary>
    public string? Thumbprint { get; set; }

    /// <summary>Trusted Signing account endpoint, for example https://eus.codesigning.azure.net/.</summary>
    public string? AzureEndpoint { get; set; }

    /// <summary>Name of the Trusted Signing account inside the Azure subscription.</summary>
    public string? AzureCodeSigningAccountName { get; set; }

    /// <summary>
    /// Certificate profile within that account. The profile, not the project,
    /// decides which identity the signature carries.
    /// </summary>
    public string? AzureCertificateProfileName { get; set; }

    /// <summary>
    /// Explicit path to Azure.CodeSigning.Dlib.dll. Only needed when the
    /// Microsoft.Trusted.Signing.Client package sits somewhere the NuGet cache
    /// sweep below will not look.
    /// </summary>
    [JsonIgnore]
    public string? AzureDlibPath { get; set; } = Environment.GetEnvironmentVariable("MSIFORGE_AZURE_DLIB");

    /// <summary>
    /// RFC 3161 timestamp server. Without a timestamp the signature stops being
    /// trusted the day the certificate expires, rather than covering everything
    /// signed while it was valid.
    /// </summary>
    public string TimestampUrl { get; set; } = "http://timestamp.digicert.com";

    public string DigestAlgorithm { get; set; } = "SHA256";

    /// <summary>
    /// Explicit path to signtool.exe or osslsigncode, when discovery is not enough.
    ///
    /// Machine configuration, not project data, and deliberately not serialised:
    /// this path gets executed. A project file travels - inside a bundle, out of a
    /// blueprint, from whoever asked for the package - and honouring this field from
    /// one turns "open someone's bundle and press Build" into running their
    /// executable. Set MSIFORGE_SIGNER on the machine that does the signing.
    /// </summary>
    [JsonIgnore]
    public string? SignerPath { get; set; } = Environment.GetEnvironmentVariable("MSIFORGE_SIGNER");

    /// <summary>
    /// Sign in process instead of driving an external tool. Null means decide,
    /// which is in process whenever the key material allows it - that is the
    /// only path that works on a machine with nothing installed, which is the
    /// machine this tool is meant to run on. Set it false to insist on signtool
    /// or osslsigncode; set it true to be told plainly when a mode cannot be
    /// signed in process rather than quietly falling back.
    /// </summary>
    public bool? UseManagedSigner { get; set; }

    /// <summary>
    /// Settles the mode. An explicit <see cref="Mode"/> always wins; otherwise
    /// the first field that is filled in decides, which keeps older projects
    /// behaving exactly as they did.
    /// </summary>
    public SigningMode ResolveMode()
    {
        if (Mode is { } chosen) return chosen;
        if (!string.IsNullOrWhiteSpace(CertificatePath)) return SigningMode.Pfx;
        if (!string.IsNullOrWhiteSpace(Thumbprint)) return SigningMode.CertificateStore;
        if (!string.IsNullOrWhiteSpace(AzureEndpoint)
            || !string.IsNullOrWhiteSpace(AzureCodeSigningAccountName)
            || !string.IsNullOrWhiteSpace(AzureCertificateProfileName)
            || !string.IsNullOrWhiteSpace(AzureDlibPath))
            return SigningMode.AzureTrustedSigning;

        // Nothing at all is configured. Pfx is the mode whose validation message
        // spells out every option the user could have meant.
        return SigningMode.Pfx;
    }
}

public sealed record SigningResult(bool Signed, string Tool, string Detail);

/// <summary>
/// Signs an MSI, in process where it can and by driving an external signer
/// where it must.
///
/// A PFX is signed in process by <see cref="MsiSigner"/>, which needs nothing
/// installed and therefore works on a Mac. The two remaining modes cannot be:
/// the Windows certificate store and Azure Trusted Signing both keep the
/// private key where only signtool can reach it, so those still shell out.
///
/// Azure Trusted Signing is driven through signtool's dlib hook:
/// the digest goes to the service and the signature comes back, so the private
/// key is never on the build machine at all. Credentials come from the ambient
/// Azure credential chain (AZURE_TENANT_ID, AZURE_CLIENT_ID and
/// AZURE_CLIENT_SECRET, or an interactive az login), never from the project
/// file, for the same reason the PFX password is not a field here.
/// </summary>
public static class CodeSigner
{
    private const string TrustedSigningPackage = "Microsoft.Trusted.Signing.Client";
    private const string DlibFileName = "Azure.CodeSigning.Dlib.dll";

    /// <summary>Locates a usable signer, or null if none is installed.</summary>
    public static string? FindSigner(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;

        // osslsigncode works on every platform and is the only option off Windows.
        string? onPath = FindOnPath(OperatingSystem.IsWindows() ? "osslsigncode.exe" : "osslsigncode");
        if (onPath is not null) return onPath;

        if (!OperatingSystem.IsWindows()) return null;

        string? signtool = FindOnPath("signtool.exe");
        if (signtool is not null) return signtool;

        // The Windows SDK installs signtool per-version and never on PATH.
        foreach (string root in new[]
        {
            @"C:\Program Files (x86)\Windows Kits\10\bin",
            @"C:\Program Files\Windows Kits\10\bin",
            @"C:\Program Files (x86)\Windows Kits\10\Tools\bin",
        })
        {
            if (!Directory.Exists(root)) continue;
            var candidates = Directory
                .EnumerateFiles(root, "signtool.exe", SearchOption.AllDirectories)
                .OrderByDescending(PreferredArchitecture)
                .ThenByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count > 0) return candidates[0];
        }
        return null;
    }

    /// <summary>
    /// Locates Azure.CodeSigning.Dlib.dll. It ships only inside a NuGet package,
    /// so there is no installed product to probe for: the restored package cache
    /// is the one place it reliably lands.
    /// </summary>
    public static string? FindAzureDlib(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;

        foreach (string root in NuGetPackageRoots())
        {
            string package = Path.Combine(root, TrustedSigningPackage.ToLowerInvariant());
            if (!Directory.Exists(package)) continue;

            List<string> candidates;
            try
            {
                candidates = Directory
                    .EnumerateFiles(package, DlibFileName, SearchOption.AllDirectories)
                    .OrderByDescending(p => PackageVersionOf(package, p))
                    .ThenByDescending(PreferredArchitecture)
                    .ToList();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (candidates.Count > 0) return candidates[0];
        }
        return null;
    }

    /// <summary>Where NuGet may have restored packages to, most specific first.</summary>
    private static IEnumerable<string> NuGetPackageRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured!;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home)) yield return Path.Combine(home, ".nuget", "packages");
    }

    /// <summary>
    /// The version folder a cached file sits under, so the newest restore wins.
    /// Prerelease suffixes are dropped because they cannot be ordered sensibly
    /// against a plain version and only matter when both are present.
    /// </summary>
    private static Version PackageVersionOf(string packageRoot, string filePath)
    {
        string relative = Path.GetRelativePath(packageRoot, filePath);
        string folder = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        int dash = folder.IndexOf('-');
        if (dash >= 0) folder = folder[..dash];
        return Version.TryParse(folder, out Version? version) ? version : new Version(0, 0);
    }

    private static int PreferredArchitecture(string path)
    {
        string p = path.Replace('\\', '/').ToLowerInvariant();
        if (p.Contains("/arm64/")) return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 3 : 0;
        if (p.Contains("/x64/")) return 2;
        if (p.Contains("/x86/") || p.Contains("/i386/")) return 1;
        return 0;
    }

    private static string? FindOnPath(string exe)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }

    /// <summary>
    /// Reports the first thing that would stop a signing run, or null when the
    /// options hang together. Returned rather than thrown so a build can carry
    /// on and report an unsigned package instead of collapsing.
    /// </summary>
    public static string? Validate(SigningOptions options, string? password = null)
    {
        switch (options.ResolveMode())
        {
            case SigningMode.Pfx:
                if (string.IsNullOrWhiteSpace(options.CertificatePath))
                    return options.Mode is null
                        ? "No certificate identified. Set CertificatePath for a PFX file, Thumbprint for a "
                          + "certificate in the Windows store, or the Azure Trusted Signing options "
                          + "(AzureEndpoint, AzureCodeSigningAccountName, AzureCertificateProfileName)."
                        : "SigningMode Pfx needs CertificatePath, the PFX file to sign with.";

                if (!File.Exists(options.CertificatePath))
                    return $"Certificate not found: {options.CertificatePath}";

                if (!string.IsNullOrWhiteSpace(options.PasswordEnvironmentVariable)
                    && string.IsNullOrWhiteSpace(password))
                    return $"Environment variable '{options.PasswordEnvironmentVariable}' is not set, so the "
                        + "certificate password is unavailable.";
                return null;

            case SigningMode.CertificateStore:
                return string.IsNullOrWhiteSpace(options.Thumbprint)
                    ? "SigningMode CertificateStore needs Thumbprint, the SHA-1 thumbprint of a certificate "
                      + "in the Windows store."
                    : null;

            case SigningMode.AzureTrustedSigning:
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(options.AzureEndpoint)) missing.Add("AzureEndpoint");
                if (string.IsNullOrWhiteSpace(options.AzureCodeSigningAccountName)) missing.Add("AzureCodeSigningAccountName");
                if (string.IsNullOrWhiteSpace(options.AzureCertificateProfileName)) missing.Add("AzureCertificateProfileName");
                if (missing.Count > 0)
                    return "Azure Trusted Signing needs " + string.Join(", ", missing)
                        + ". Credentials are not project settings: they come from AZURE_TENANT_ID, "
                        + "AZURE_CLIENT_ID and AZURE_CLIENT_SECRET, or from az login.";

                if (!Uri.TryCreate(options.AzureEndpoint, UriKind.Absolute, out _))
                    return $"AzureEndpoint '{options.AzureEndpoint}' is not an absolute URL. It looks like "
                        + "https://eus.codesigning.azure.net/, with the region matching the account.";

                if (!string.IsNullOrWhiteSpace(options.AzureDlibPath) && !File.Exists(options.AzureDlibPath))
                    return $"AzureDlibPath does not exist: {options.AzureDlibPath}";
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Whether <see cref="MsiSigner"/> can do this job on its own. Only a PFX
    /// qualifies: the Windows certificate store and Azure Trusted Signing both
    /// hold the key somewhere this process cannot reach it.
    /// </summary>
    public static bool CanSignInProcess(SigningOptions options) =>
        options.ResolveMode() == SigningMode.Pfx && !string.IsNullOrWhiteSpace(options.CertificatePath);

    public static SigningResult Sign(string msiPath, SigningOptions options)
    {
        if (!options.Enabled) return new SigningResult(false, "none", "Signing is not enabled for this project.");

        // Refused rather than obliged. signtool accepts a PFX password only as
        // /p on the command line, readable by any process that can enumerate
        // command lines, and there is nothing to gain by it: the in-process
        // signer produces a signature verified byte for byte against signtool's
        // own, needs nothing installed, and keeps the password in this process.
        if (options.UseManagedSigner == false
            && CanSignInProcess(options)
            && OperatingSystem.IsWindows()
            && !string.IsNullOrEmpty(ReadPassword(options)))
        {
            return new SigningResult(false, "none",
                "UseManagedSigner is false, but signing a password-protected PFX with signtool would "
                + "put the password on a command line where any process on the machine can read it. "
                + "signtool has no way to take it otherwise. Remove UseManagedSigner to sign in "
                + "process, which needs nothing installed and produces the same signature.");
        }

        if (options.UseManagedSigner ?? CanSignInProcess(options))
        {
            string? pfxPassword = ReadPassword(options);
            string? complaint = Validate(options, pfxPassword);
            if (complaint is not null) return new SigningResult(false, "managed", complaint);

            if (!CanSignInProcess(options))
                return new SigningResult(false, "managed",
                    $"SigningMode {options.ResolveMode()} keeps the private key out of this process, so it "
                    + "needs signtool. Leave UseManagedSigner unset, or set it false, to drive signtool instead.");

            return MsiSigner.Sign(msiPath, options, pfxPassword);
        }

        string? signer = FindSigner(options.SignerPath);
        if (signer is null)
            return new SigningResult(false, "none",
                "No signer found. Install the Windows SDK (signtool.exe) or osslsigncode, "
                + "or set MSIFORGE_SIGNER to its path.");

        bool useOssl = Path.GetFileNameWithoutExtension(signer)
            .Equals("osslsigncode", StringComparison.OrdinalIgnoreCase);

        string? password = ReadPassword(options);

        string? problem = Validate(options, password);
        if (problem is not null) return new SigningResult(false, signer, problem);

        if (options.ResolveMode() == SigningMode.AzureTrustedSigning)
        {
            if (useOssl)
                return new SigningResult(false, signer,
                    "Azure Trusted Signing runs through signtool's dlib hook, which osslsigncode does not "
                    + "implement. Install the Windows SDK, or set MSIFORGE_SIGNER to signtool.exe.");
            return SignWithAzureTrustedSigning(signer, msiPath, options);
        }

        return useOssl
            ? SignWithOsslsigncode(signer, msiPath, options, password)
            : SignWithSigntool(signer, msiPath, options, password);
    }

    /// <summary>
    /// Builds the signtool command line for any mode. Kept separate from running
    /// the process so the exact argument list can be asserted in a test, which is
    /// the only way to check the Azure path without an account to sign against.
    /// </summary>
    public static IReadOnlyList<string> BuildSigntoolArguments(
        string filePath,
        SigningOptions options,
        string? password = null,
        string? dlibPath = null,
        string? metadataPath = null)
    {
        SigningMode mode = options.ResolveMode();
        var args = new List<string> { "sign" };

        // Verbose output is how the service reports which certificate profile it
        // actually used, which is worth having in the log when signing is remote.
        if (mode == SigningMode.AzureTrustedSigning) args.Add("/v");

        args.Add("/fd");
        args.Add(options.DigestAlgorithm);

        switch (mode)
        {
            case SigningMode.CertificateStore:
                args.Add("/sha1");
                args.Add(options.Thumbprint!);
                break;

            case SigningMode.Pfx:
                // signtool takes a PFX password only as /p on the command line,
                // where every process on the machine can read it out of the
                // process list. osslsigncode has -readpass and is used that way
                // below; signtool has no equivalent, so the in-process signer -
                // which needs no external tool and never forms a command line -
                // is the only PFX path offered on Windows. Reaching here means a
                // project set UseManagedSigner false, and the right answer is to
                // say so rather than to leak the password.
                args.Add("/f");
                args.Add(options.CertificatePath!);
                if (!string.IsNullOrWhiteSpace(password)) { args.Add("/p"); args.Add(password!); }
                break;
        }

        if (!string.IsNullOrWhiteSpace(options.TimestampUrl))
        {
            args.Add("/tr");
            args.Add(options.TimestampUrl);
            args.Add("/td");
            args.Add(options.DigestAlgorithm);
        }

        if (mode == SigningMode.AzureTrustedSigning)
        {
            args.Add("/dlib");
            args.Add(dlibPath ?? throw new ArgumentNullException(nameof(dlibPath),
                "Azure Trusted Signing needs the path to " + DlibFileName + "."));
            args.Add("/dmdf");
            args.Add(metadataPath ?? throw new ArgumentNullException(nameof(metadataPath),
                "Azure Trusted Signing needs the path to the generated metadata file."));
        }

        args.Add(filePath);
        return args;
    }

    /// <summary>
    /// The metadata document the dlib reads to find the account and profile.
    /// Only names go in it: authentication is the credential chain's business,
    /// so there is nothing here that would hurt if it leaked.
    /// </summary>
    public static string BuildAzureMetadataJson(SigningOptions options, string? correlationId = null) =>
        JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["Endpoint"] = options.AzureEndpoint,
            ["CodeSigningAccountName"] = options.AzureCodeSigningAccountName,
            ["CertificateProfileName"] = options.AzureCertificateProfileName,
            ["CorrelationId"] = correlationId ?? Guid.NewGuid().ToString("N"),
        }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Says where the dlib comes from, because nothing on the machine hints at
    /// it: a missing NuGet package looks exactly like a broken install.
    /// </summary>
    public static string AzureDlibNotFoundMessage(SigningOptions options) =>
        $"{DlibFileName} not found"
        + (string.IsNullOrWhiteSpace(options.AzureDlibPath) ? "" : $" at {options.AzureDlibPath}")
        + $". It ships in the {TrustedSigningPackage} NuGet package: restore or install that package "
        + $"(the file lands under .nuget/packages/{TrustedSigningPackage.ToLowerInvariant()}/<version>/bin/x64/), "
        + "or set MSIFORGE_AZURE_DLIB to it.";

    private static SigningResult SignWithAzureTrustedSigning(string signer, string msiPath, SigningOptions options)
    {
        string? dlib = FindAzureDlib(options.AzureDlibPath);
        if (dlib is null) return new SigningResult(false, signer, AzureDlibNotFoundMessage(options));

        string metadata = Path.Combine(Path.GetTempPath(), $"msiforge-trusted-signing-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(metadata, BuildAzureMetadataJson(options));
            var (code, output) = Run(signer, BuildSigntoolArguments(msiPath, options, dlibPath: dlib, metadataPath: metadata));
            return new SigningResult(code == 0, "signtool", Summarise(output));
        }
        finally
        {
            // The metadata names the account and profile and has no business
            // outliving the run, least of all in a shared temp directory.
            try { if (File.Exists(metadata)) File.Delete(metadata); }
            catch (IOException) { /* a locked temp file is not worth failing a good signature over */ }
        }
    }

    private static SigningResult SignWithSigntool(string signer, string msiPath, SigningOptions options, string? password)
    {
        var (code, output) = Run(signer, BuildSigntoolArguments(msiPath, options, password));
        return code == 0
            ? new SigningResult(true, "signtool", Summarise(output))
            : new SigningResult(false, "signtool", Redact(Summarise(output), password));
    }

    private static SigningResult SignWithOsslsigncode(string signer, string msiPath, SigningOptions options, string? password)
    {
        // osslsigncode cannot sign in place.
        string temp = msiPath + ".signing";
        var args = new List<string>
        {
            "sign",
            "-h", options.DigestAlgorithm.ToLowerInvariant(),
            "-in", msiPath,
            "-out", temp,
        };

        string? passwordFile = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(options.CertificatePath))
            {
                args.Add("-pkcs12");
                args.Add(options.CertificatePath!);

                if (!string.IsNullOrWhiteSpace(password))
                {
                    // -pass would put the password in the process command line,
                    // where any other user on the machine can read it out of the
                    // process list. -readpass takes a file instead.
                    passwordFile = Path.Combine(Path.GetTempPath(),
                        "msiforge-pass-" + Guid.NewGuid().ToString("N"));
                    WritePrivateFile(passwordFile, password!);
                    args.Add("-readpass");
                    args.Add(passwordFile);
                }
            }
            if (!string.IsNullOrWhiteSpace(options.TimestampUrl)) { args.Add("-ts"); args.Add(options.TimestampUrl); }

            var (code, output) = Run(signer, args);
            if (code != 0)
            {
                if (File.Exists(temp)) File.Delete(temp);
                return new SigningResult(false, "osslsigncode", Redact(Summarise(output), password));
            }

            File.Move(temp, msiPath, overwrite: true);
            return new SigningResult(true, "osslsigncode", Summarise(output));
        }
        finally
        {
            if (passwordFile is not null && File.Exists(passwordFile))
            {
                try { File.Delete(passwordFile); } catch (IOException) { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// The PFX password, from the environment variable the project names. The
    /// project never holds the password itself, only that name.
    /// </summary>
    private static string? ReadPassword(SigningOptions options) =>
        string.IsNullOrWhiteSpace(options.PasswordEnvironmentVariable)
            ? null
            : Environment.GetEnvironmentVariable(options.PasswordEnvironmentVariable!);

    /// <summary>Asks the signer whether the file carries a valid signature.</summary>
    public static SigningResult Verify(string msiPath, string? signerPath = null)
    {
        string? signer = FindSigner(signerPath);

        // With nothing installed the digest can still be recomputed in process.
        // That answers "has this package been altered since it was signed",
        // which is most of the question, and says so rather than implying it
        // checked the certificate chain as well.
        if (signer is null) return MsiSigner.Verify(msiPath);

        bool useOssl = Path.GetFileNameWithoutExtension(signer)
            .Equals("osslsigncode", StringComparison.OrdinalIgnoreCase);

        var (code, output) = useOssl
            ? Run(signer, ["verify", "-in", msiPath])
            : Run(signer, ["verify", "/pa", "/v", msiPath]);

        return new SigningResult(code == 0, useOssl ? "osslsigncode" : "signtool", Summarise(output));
    }

    /// <summary>
    /// Writes a short-lived secret, readable only by the current user on
    /// platforms that express that in file mode.
    /// </summary>
    private static void WritePrivateFile(string path, string contents)
    {
        File.WriteAllText(path, contents);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static (int ExitCode, string Output) Run(string exe, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in arguments) info.ArgumentList.Add(a);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start {exe}.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, (stdout + "\n" + stderr).Trim());
    }

    private static string Summarise(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        return lines.Count == 0 ? "" : string.Join(" | ", lines.TakeLast(4));
    }

    /// <summary>Keeps a password out of any message that reaches a log.</summary>
    private static string Redact(string text, string? password) =>
        string.IsNullOrEmpty(password) ? text : text.Replace(password, "***");
}
