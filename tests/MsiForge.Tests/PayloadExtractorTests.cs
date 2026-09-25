using MsiForge.Core.Payload;
using MsiForge.Core.Blueprints;
using Xunit;

namespace MsiForge.Tests;

/// <summary>
/// The extraction itself runs a vendor executable and cannot be exercised
/// without one, but the part that decides which folder to package - the marker
/// search that lets a SoftPaq unpack into a subfolder and still build - is pure
/// and is where a real bug would hide.
/// </summary>
public class PayloadExtractorTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "msiforge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void WithoutAMarkerThePayloadRootIsTheExtractionFolder()
    {
        string dir = TempDir();
        Assert.Equal(dir, PayloadExtractor.LocatePayloadRoot(dir, null));
        Assert.Equal(dir, PayloadExtractor.LocatePayloadRoot(dir, "   "));
    }

    [Fact]
    public void AMarkerAtTheRootKeepsTheRootAsThePayload()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "HPImageAssistant.exe"), "stub");

        Assert.Equal(dir, PayloadExtractor.LocatePayloadRoot(dir, "HPImageAssistant.exe"));
    }

    [Fact]
    public void AMarkerInASubfolderMakesThatSubfolderThePayload()
    {
        string dir = TempDir();
        string sub = Path.Combine(dir, "HPIA");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "HPImageAssistant.exe"), "stub");

        Assert.Equal(sub, PayloadExtractor.LocatePayloadRoot(dir, "HPImageAssistant.exe"));
    }

    [Fact]
    public void TheShallowestMatchWins()
    {
        string dir = TempDir();
        string deep = Path.Combine(dir, "a", "b");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(dir, "HPImageAssistant.exe"), "stub");
        File.WriteAllText(Path.Combine(deep, "HPImageAssistant.exe"), "stub");

        Assert.Equal(dir, PayloadExtractor.LocatePayloadRoot(dir, "HPImageAssistant.exe"));
    }

    [Fact]
    public void AMissingMarkerIsReportedRatherThanReturningTheWrongFolder()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "not the installer");

        var ex = Assert.Throws<FileNotFoundException>(
            () => PayloadExtractor.LocatePayloadRoot(dir, "HPImageAssistant.exe"));
        Assert.Contains("HPImageAssistant.exe", ex.Message);
    }

    /// <summary>
    /// Exercises the whole run: launch the extractor, substitute {dest}, wait for
    /// it, then hand back the folder holding the marker. PowerShell stands in for
    /// the SoftPaq's self-extractor, writing the marker where the switches say -
    /// Windows only, because that is the only place a SoftPaq runs anyway.
    /// </summary>
    [Fact]
    public void ExtractRunsTheInstallerAndReturnsTheFolderWithTheMarker()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? shell = FindPowerShell();
        if (shell is null) return;

        // Write the marker into a subfolder of {dest} to prove the marker search,
        // not just a fixed path, is what finds the payload.
        var spec = new BlueprintExtract
        {
            Switches = "-NoProfile -NonInteractive -Command "
                       + "\"$d = Join-Path '{dest}' 'HPIA'; New-Item -ItemType Directory -Force $d | Out-Null; "
                       + "Set-Content -LiteralPath (Join-Path $d 'HPImageAssistant.exe') -Value 'stub'\"",
            MarkerFile = "HPImageAssistant.exe",
            TimeoutSeconds = 60,
        };

        string root = PayloadExtractor.Extract(shell, spec, TempDir());

        Assert.EndsWith("HPIA", root);
        Assert.True(File.Exists(Path.Combine(root, "HPImageAssistant.exe")));
    }

    private static string? FindPowerShell()
    {
        foreach (var name in new[] { "pwsh.exe", "powershell.exe" })
        {
            string sys = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", name);
            if (File.Exists(sys)) return sys;
        }
        // Fall back to whatever is on PATH.
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "pwsh.exe", "powershell.exe" })
            {
                try
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* a malformed PATH entry */ }
            }
        }
        return null;
    }
}
