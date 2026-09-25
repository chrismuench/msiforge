using System.Text.Json;
using MsiForge.Core.Model;
using MsiForge.Core.Signing;
using Xunit;

namespace MsiForge.Tests;

/// <summary>
/// Cover for the parts of signing that can be checked without a certificate or
/// a signing service: the command line handed to signtool, the metadata the
/// Azure dlib reads, where that dlib is looked for, and the errors a
/// half-configured project should get. Nothing here signs anything.
/// </summary>
public class SigningTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "msiforge-signing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SigningOptions AzureOptions() => new()
    {
        Enabled = true,
        AzureEndpoint = "https://eus.codesigning.azure.net/",
        AzureCodeSigningAccountName = "contoso-signing",
        AzureCertificateProfileName = "contoso-public-trust",
    };

    [Fact]
    public void PfxArgumentsKeepTheProvenShape()
    {
        var options = new SigningOptions { Enabled = true, CertificatePath = @"C:\certs\dev.pfx" };

        Assert.Equal(
            ["sign", "/fd", "SHA256", "/f", @"C:\certs\dev.pfx", "/p", "hunter2",
             "/tr", "http://timestamp.digicert.com", "/td", "SHA256", @"C:\out\app.msi"],
            CodeSigner.BuildSigntoolArguments(@"C:\out\app.msi", options, "hunter2"));
    }

    [Fact]
    public void CertificateStoreArgumentsSelectByThumbprint()
    {
        var options = new SigningOptions { Enabled = true, Thumbprint = "AABBCCDD", TimestampUrl = "" };

        Assert.Equal(
            ["sign", "/fd", "SHA256", "/sha1", "AABBCCDD", @"C:\out\app.msi"],
            CodeSigner.BuildSigntoolArguments(@"C:\out\app.msi", options));
    }

    [Fact]
    public void AzureArgumentsDriveSigntoolThroughTheDlib()
    {
        var args = CodeSigner.BuildSigntoolArguments(
            @"C:\out\app.msi", AzureOptions(),
            dlibPath: @"C:\dlib\Azure.CodeSigning.Dlib.dll",
            metadataPath: @"C:\temp\metadata.json");

        Assert.Equal(
            ["sign", "/v", "/fd", "SHA256",
             "/tr", "http://timestamp.digicert.com", "/td", "SHA256",
             "/dlib", @"C:\dlib\Azure.CodeSigning.Dlib.dll",
             "/dmdf", @"C:\temp\metadata.json",
             @"C:\out\app.msi"],
            args);
    }

    [Fact]
    public void AzureArgumentsCarryNoCertificateOrPassword()
    {
        // The key lives in the service, so a stray /f or /p would be both wrong
        // and a secret on a command line other processes can read.
        var args = CodeSigner.BuildSigntoolArguments(
            "app.msi", AzureOptions(), password: "hunter2",
            dlibPath: "dlib.dll", metadataPath: "metadata.json");

        Assert.DoesNotContain("/p", args);
        Assert.DoesNotContain("/f", args);
        Assert.DoesNotContain("hunter2", args);
    }

    [Fact]
    public void AzureArgumentsRefuseToBeBuiltWithoutTheDlib()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CodeSigner.BuildSigntoolArguments("app.msi", AzureOptions(), metadataPath: "metadata.json"));
    }

    [Fact]
    public void ModeIsInferredFromWhicheverOptionIsFilledIn()
    {
        Assert.Equal(SigningMode.Pfx,
            new SigningOptions { CertificatePath = "dev.pfx" }.ResolveMode());
        Assert.Equal(SigningMode.CertificateStore,
            new SigningOptions { Thumbprint = "AABBCC" }.ResolveMode());
        Assert.Equal(SigningMode.AzureTrustedSigning,
            new SigningOptions { AzureEndpoint = "https://eus.codesigning.azure.net/" }.ResolveMode());
    }

    [Fact]
    public void AnExplicitModeBeatsInference()
    {
        var options = new SigningOptions
        {
            Mode = SigningMode.CertificateStore,
            CertificatePath = "dev.pfx",
            Thumbprint = "AABBCC",
        };
        Assert.Equal(SigningMode.CertificateStore, options.ResolveMode());
        Assert.Contains("/sha1", CodeSigner.BuildSigntoolArguments("app.msi", options));
    }

    [Fact]
    public void ModeSurvivesTheProjectFile()
    {
        var project = new MsiProject { ProductName = "Test", Manufacturer = "Test Co", Version = "1.0.0" };
        project.Signing.Mode = SigningMode.AzureTrustedSigning;

        string json = project.ToJson();
        Assert.Contains("AzureTrustedSigning", json); // a name, not a brittle ordinal
        Assert.Equal(SigningMode.AzureTrustedSigning, MsiProject.FromJson(json).Signing.Mode);
    }

    [Fact]
    public void AProjectWithoutAModeStillWritesNone()
    {
        // Older projects must round-trip unchanged rather than gaining a mode.
        var project = new MsiProject { ProductName = "Test", Manufacturer = "Test Co", Version = "1.0.0" };
        Assert.DoesNotContain("\"Mode\"", project.ToJson());
    }

    [Fact]
    public void MetadataCarriesTheServiceCoordinatesAndNothingElse()
    {
        using var document = JsonDocument.Parse(CodeSigner.BuildAzureMetadataJson(AzureOptions(), "correlation-1"));
        var root = document.RootElement;

        Assert.Equal("https://eus.codesigning.azure.net/", root.GetProperty("Endpoint").GetString());
        Assert.Equal("contoso-signing", root.GetProperty("CodeSigningAccountName").GetString());
        Assert.Equal("contoso-public-trust", root.GetProperty("CertificateProfileName").GetString());
        Assert.Equal("correlation-1", root.GetProperty("CorrelationId").GetString());
        Assert.Equal(4, root.EnumerateObject().Count());
    }

    [Fact]
    public void MetadataGetsAFreshCorrelationIdPerRun()
    {
        var options = AzureOptions();
        Assert.NotEqual(CodeSigner.BuildAzureMetadataJson(options), CodeSigner.BuildAzureMetadataJson(options));
    }

    [Fact]
    public void NothingConfiguredNamesEveryWayOfIdentifyingACertificate()
    {
        string? problem = CodeSigner.Validate(new SigningOptions { Enabled = true });

        Assert.NotNull(problem);
        Assert.Contains("CertificatePath", problem);
        Assert.Contains("Thumbprint", problem);
        Assert.Contains("AzureEndpoint", problem);
    }

    [Fact]
    public void CertificateStoreModeNeedsAThumbprint()
    {
        string? problem = CodeSigner.Validate(new SigningOptions { Enabled = true, Mode = SigningMode.CertificateStore });

        Assert.NotNull(problem);
        Assert.Contains("Thumbprint", problem);
    }

    [Fact]
    public void PfxModeNeedsACertificatePath()
    {
        string? problem = CodeSigner.Validate(new SigningOptions { Enabled = true, Mode = SigningMode.Pfx });

        Assert.NotNull(problem);
        Assert.Contains("CertificatePath", problem);
    }

    [Fact]
    public void PfxModeReportsAPasswordVariableThatIsNotSet()
    {
        string dir = TempDir();
        string pfx = Path.Combine(dir, "dev.pfx");
        File.WriteAllText(pfx, "not really a pfx");

        string? problem = CodeSigner.Validate(new SigningOptions
        {
            Enabled = true,
            CertificatePath = pfx,
            PasswordEnvironmentVariable = "MSIFORGE_TEST_PFX_PASSWORD",
        });

        Assert.NotNull(problem);
        Assert.Contains("MSIFORGE_TEST_PFX_PASSWORD", problem);
    }

    [Fact]
    public void AzureModeNamesEveryMissingField()
    {
        string? problem = CodeSigner.Validate(new SigningOptions
        {
            Enabled = true,
            Mode = SigningMode.AzureTrustedSigning,
            AzureEndpoint = "https://eus.codesigning.azure.net/",
        });

        Assert.NotNull(problem);
        Assert.Contains("AzureCodeSigningAccountName", problem);
        Assert.Contains("AzureCertificateProfileName", problem);
        Assert.DoesNotContain("needs AzureEndpoint", problem);
    }

    [Fact]
    public void AzureModePointsAtTheCredentialChainRatherThanTheProjectFile()
    {
        string? problem = CodeSigner.Validate(new SigningOptions { Enabled = true, Mode = SigningMode.AzureTrustedSigning });

        Assert.NotNull(problem);
        Assert.Contains("AZURE_CLIENT_ID", problem);
        Assert.Contains("az login", problem);
    }

    [Fact]
    public void AzureModeRejectsAnEndpointThatIsNotAUrl()
    {
        var options = AzureOptions();
        options.AzureEndpoint = "eus.codesigning.azure.net";

        string? problem = CodeSigner.Validate(options);

        Assert.NotNull(problem);
        Assert.Contains("absolute URL", problem);
    }

    [Fact]
    public void AzureModeRejectsADlibPathThatIsNotThere()
    {
        var options = AzureOptions();
        options.AzureDlibPath = Path.Combine(TempDir(), "Azure.CodeSigning.Dlib.dll");

        string? problem = CodeSigner.Validate(options);

        Assert.NotNull(problem);
        Assert.Contains("AzureDlibPath", problem);
    }

    [Fact]
    public void FullyConfiguredAzureOptionsValidate()
    {
        Assert.Null(CodeSigner.Validate(AzureOptions()));
    }

    [Fact]
    public void AnExplicitDlibPathIsUsedAsGivenOrNotAtAll()
    {
        string dir = TempDir();
        string dlib = Path.Combine(dir, "Azure.CodeSigning.Dlib.dll");
        File.WriteAllText(dlib, "stub");

        Assert.Equal(dlib, CodeSigner.FindAzureDlib(dlib));
        Assert.Null(CodeSigner.FindAzureDlib(Path.Combine(dir, "missing", "Azure.CodeSigning.Dlib.dll")));
    }

    [Fact]
    public void TheDlibIsFoundInTheNuGetCacheNewestVersionFirst()
    {
        string cache = TempDir();
        string package = Path.Combine(cache, "microsoft.trusted.signing.client");
        string older = Path.Combine(package, "1.0.52", "bin", "x64");
        string newer = Path.Combine(package, "1.0.60", "bin", "x64");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);
        File.WriteAllText(Path.Combine(older, "Azure.CodeSigning.Dlib.dll"), "stub");
        File.WriteAllText(Path.Combine(newer, "Azure.CodeSigning.Dlib.dll"), "stub");

        string? previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", cache);
            string? found = CodeSigner.FindAzureDlib();

            Assert.NotNull(found);
            Assert.Contains("1.0.60", found);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
        }
    }

    [Fact]
    public void AzureSigningStoresNoCredentialsInTheProject()
    {
        // Same rule as the PFX password: a project file belongs in source control.
        var names = typeof(SigningOptions).GetProperties().Select(p => p.Name).ToList();

        Assert.Contains("AzureEndpoint", names);
        Assert.DoesNotContain(names, n =>
            n.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || n.Equals("AzureClientId", StringComparison.OrdinalIgnoreCase)
            || n.Equals("AzureTenantId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AMissingDlibIsReportedAsAMissingPackage()
    {
        string message = CodeSigner.AzureDlibNotFoundMessage(AzureOptions());

        Assert.Contains("Azure.CodeSigning.Dlib.dll", message);
        Assert.Contains("Microsoft.Trusted.Signing.Client", message);
        // The path is machine configuration now, not a project field, so the
        // message has to name the environment variable that actually sets it.
        Assert.Contains("MSIFORGE_AZURE_DLIB", message);
    }

    [Fact]
    public void AzureMetadataNeverReachesDiskWhenSigningIsDeclined()
    {
        // No signer is installed at that path, so Sign gives up before the
        // metadata is written - and must leave no account name in the temp
        // directory on the way out.
        int before = Directory.GetFiles(Path.GetTempPath(), "msiforge-trusted-signing-*.json").Length;

        var options = AzureOptions();
        options.SignerPath = Path.Combine(TempDir(), "signtool.exe"); // deliberately absent
        var result = CodeSigner.Sign("nonexistent.msi", options);

        Assert.False(result.Signed);
        Assert.Contains("No signer found", result.Detail);
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "msiforge-trusted-signing-*.json").Length);
    }
}
