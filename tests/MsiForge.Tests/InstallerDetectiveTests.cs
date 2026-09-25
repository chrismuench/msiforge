using MsiForge.Core.Model;
using MsiForge.Core.Payload;
using Xunit;

namespace MsiForge.Tests;

/// <summary>
/// The detective proposes the command line a wrapped installer will run with. The
/// install location lives in that command line and nowhere else, and which folder
/// it should be is a real decision - so the detective does not guess one. It seeds
/// the bare silent switch and leaves the location to be chosen when the package is
/// built, rather than assuming a default the person never asked for.
/// </summary>
public class InstallerDetectiveTests
{
    private static PayloadAnalysis Analysis(string? withDirectory, PeArchitecture arch = PeArchitecture.X64) =>
        new()
        {
            Path = @"C:\downloads\setup.exe",
            SilentArguments = "/S",
            SilentArgumentsWithDirectory = withDirectory,
            Architecture = arch,
        };

    [Fact]
    public void SeedingDoesNotInventAnInstallDirectory()
    {
        // Even though this installer can take a directory switch, the detective
        // must not assume where to install - it seeds the silent switch only, and
        // the location is asked for at build time.
        var project = InstallerDetective.ToProject(Analysis("/S /D={0}"));
        var install = Assert.Single(project.Run);

        Assert.Equal("/S", install.Arguments);
        Assert.False(InstallerDetective.MentionsInstallDirectory(install.Arguments));
        Assert.False(install.UseInstallerDefaultLocation, "the default must not be silently chosen either");
    }

    [Fact]
    public void SeedingDoesNotAssumeADirectoryForAnUnrecognisedInstaller()
    {
        var project = InstallerDetective.ToProject(Analysis(withDirectory: null));
        Assert.Equal("/S", project.Run.Single().Arguments);
        Assert.False(project.Run.Single().UseInstallerDefaultLocation);
    }

    [Theory]
    [InlineData(@"/S /D=C:\Program Files\App")]
    [InlineData(@"/VERYSILENT /DIR=""C:\Program Files\App""")]
    [InlineData("/exenoui /qn APPDIR=\"C:\\x\"")]
    public void MentionsInstallDirectoryRecognisesAChosenLocation(string arguments)
    {
        Assert.True(InstallerDetective.MentionsInstallDirectory(arguments), arguments);
    }

    [Theory]
    [InlineData("/S")]
    [InlineData("/VERYSILENT /NORESTART")]
    [InlineData("")]
    public void MentionsInstallDirectorySeesNoLocationInABareSwitch(string arguments)
    {
        Assert.False(InstallerDetective.MentionsInstallDirectory(arguments), arguments);
    }

    // A blueprint's baked-in path becomes the suggestion in the prompt, so it has to
    // be read back out of whatever switch the blueprint used.
    [Theory]
    [InlineData(@"/S /D=C:\Program Files\Notepad++", @"C:\Program Files\Notepad++")]
    [InlineData(@"/VERYSILENT /DIR=""C:\Apps\Thing""", @"C:\Apps\Thing")]
    [InlineData("/exenoui /qn APPDIR=\"C:\\x\\y\"", @"C:\x\y")]
    [InlineData("-y -o\"C:\\Tools\\7z\"", @"C:\Tools\7z")]
    public void ExtractInstallDirectoryReadsTheBlueprintsPath(string arguments, string expected)
    {
        Assert.Equal(expected, InstallerDetective.ExtractInstallDirectory(arguments));
    }

    [Theory]
    [InlineData("/S")]
    [InlineData("/VERYSILENT /NORESTART")]
    public void ExtractInstallDirectoryIsNullWhenNoneIsSet(string arguments)
    {
        Assert.Null(InstallerDetective.ExtractInstallDirectory(arguments));
    }

    // Choosing "use the installer's default" has to actually remove the blueprint's
    // path, or the default is a lie and the blueprint's path still wins.
    [Theory]
    [InlineData(@"/S /D=C:\Program Files\Notepad++", "/S")]
    [InlineData(@"/VERYSILENT /DIR=""C:\Apps\Thing"" /NORESTART", "/VERYSILENT /NORESTART")]
    [InlineData("/exenoui /qn APPDIR=\"C:\\x\"", "/exenoui /qn")]
    public void RemoveInstallDirectoryStripsThePath(string arguments, string expected)
    {
        Assert.Equal(expected, InstallerDetective.RemoveInstallDirectory(arguments));
        Assert.False(InstallerDetective.MentionsInstallDirectory(
            InstallerDetective.RemoveInstallDirectory(arguments)));
    }

    // A blueprint already carries the switch, so a chosen folder is written by reusing
    // it - even when live detection does not recognise the payload, which is exactly
    // the case that wrongly disabled the folder choice for Notepad++.
    [Theory]
    [InlineData(@"/S /D=C:\Program Files\Notepad++", @"D:\Apps\NPP", @"/S /D=D:\Apps\NPP")]
    [InlineData(@"/VERYSILENT /DIR=""C:\Apps\Thing"" /NORESTART", @"D:\Thing", @"/VERYSILENT /DIR=""D:\Thing"" /NORESTART")]
    [InlineData("-y -o\"C:\\Tools\\7z\"", @"D:\7z", "-y -o\"D:\\7z\"")]
    public void WithInstallDirectoryRewritesTheExistingSwitch(string arguments, string path, string expected)
    {
        string result = InstallerDetective.WithInstallDirectory(arguments, path);
        Assert.Equal(expected, result);
        Assert.Equal(path, InstallerDetective.ExtractInstallDirectory(result));
    }

    // ---------------------------------------------------- install location

    [Theory]
    [InlineData(@"C:\Program Files\App")]
    [InlineData(@"C:\Users\Public\App")]
    public void AFixedDrivePathIsAccepted(string path)
    {
        Assert.True(MsiForge.Core.Payload.InstallLocation.IsFixedDrive(path, out _), path);
    }

    [Theory]
    [InlineData(@"\\server\share\App")]
    [InlineData(@"App\sub")]
    [InlineData(@"Program Files\App")]
    [InlineData("")]
    [InlineData("   ")]
    public void ANonFixedOrRelativePathIsRefusedWithAReason(string path)
    {
        bool ok = MsiForge.Core.Payload.InstallLocation.IsFixedDrive(path, out string reason);
        Assert.False(ok, path);
        Assert.NotEqual("", reason);
    }
}
