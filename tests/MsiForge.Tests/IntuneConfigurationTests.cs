using MsiForge.Core.Build;
using MsiForge.Core.Intune;
using MsiForge.Core.Model;
using Xunit;

namespace MsiForge.Tests;

/// <summary>
/// The portal values are already inside the MSI, but a mistyped ProductCode in a
/// detection rule fails silently, so these are worth pinning down.
/// </summary>
public class IntuneConfigurationTests
{
    private static string BuildSample(out MsiProject project, string version = "1.0.0")
    {
        string dir = Path.Combine(Path.GetTempPath(), "msiforge-intune", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string payload = Path.Combine(dir, "app.txt");
        File.WriteAllText(payload, "payload");

        project = new MsiProject
        {
            ProductName = "Portal Sample",
            Manufacturer = "MsiForge",
            Version = version,
            UpgradeCode = new Guid("1d2e3f40-5a6b-4c7d-8e9f-0a1b2c3d4e5f"),
            Files = { new PayloadFile { SourcePath = payload } },
        };

        string msi = Path.Combine(dir, "sample.msi");
        MsiBuilder.Build(project, msi);
        return msi;
    }

    [Fact]
    public void UninstallAddressesTheProductCodeNotTheFile()
    {
        // The MSI will not be on the device at removal time, so an uninstall
        // command naming the file cannot work.
        string msi = BuildSample(out var project);
        var config = IntuneConfiguration.From(msi);

        Assert.Equal(DeterministicGuid.Format(project.EffectiveProductCode), config.ProductCode);
        Assert.Contains(config.ProductCode, config.UninstallCommand);
        Assert.DoesNotContain("sample.msi", config.UninstallCommand);
        Assert.Contains("sample.msi", config.InstallCommand);
    }

    [Fact]
    public void ReportsPerMachineAndArchitecture()
    {
        var config = IntuneConfiguration.From(BuildSample(out _));
        Assert.True(config.PerMachine);
        Assert.Equal("x64", config.Architecture);
        Assert.Contains("System", config.Describe());
    }

    [Fact]
    public void AnUnsignedPackageSaysSo()
    {
        var config = IntuneConfiguration.From(BuildSample(out _));
        Assert.False(config.Signed);
        Assert.Contains(config.Warnings, w => w.Contains("Unsigned"));
    }

    [Fact]
    public void TheDetectionRuleCarriesTheProductCodeAndVersion()
    {
        string msi = BuildSample(out _, "2.5.9");
        string text = IntuneConfiguration.From(msi).Describe();

        Assert.Contains("MSI product code", text);
        Assert.Contains("2.5.9", text);
        Assert.Contains("greater than or equal to", text);
    }
}

/// <summary>
/// The digest walks a flat list of streams. An MSI database is flat; a patch
/// package is not, and signing one anyway would produce a signature that looks
/// right and fails on the device.
/// </summary>
public class NestedStorageTests
{
    [Fact]
    public void AFlatDatabaseHasNoSubStorages()
    {
        var bytes = new MsiForge.Core.Cfbf.CfbWriter().Write(
            [new MsiForge.Core.Cfbf.CfbStream("only", [1, 2, 3])]);
        var reader = new MsiForge.Core.Cfbf.CfbReader(bytes);
        Assert.Empty(reader.SubStorages);
    }

    [Fact]
    public void SigningAFlatDatabaseIsAllowed()
    {
        var bytes = new MsiForge.Core.Cfbf.CfbWriter().Write(
            [new MsiForge.Core.Cfbf.CfbStream("only", [1, 2, 3])]);
        var reader = new MsiForge.Core.Cfbf.CfbReader(bytes);

        var digest = MsiForge.Core.Signing.MsiSigner.ComputeDigest(
            reader, System.Security.Cryptography.HashAlgorithmName.SHA256);
        Assert.Equal(32, digest.Length);
    }
}
