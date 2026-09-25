using System.IO.Compression;
using MsiForge.Core.Build;
using MsiForge.Core.Model;
using MsiForge.Core.Msi;
using Xunit;

namespace MsiForge.Tests;

/// <summary>
/// A bundle exists so a package is one artefact that can be versioned and moved.
/// These tests hold it to that: everything travels with it, and it builds the
/// same package from anywhere.
/// </summary>
public class BundleTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "msiforge-bundle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A project whose payload is a folder, written to disk with relative paths.</summary>
    private static (string ProjectPath, string Dir) WriteFolderProject(string version = "1.0.0")
    {
        string dir = TempDir();
        string payload = Path.Combine(dir, "payload");
        Directory.CreateDirectory(Path.Combine(payload, "sub"));
        File.WriteAllText(Path.Combine(payload, "app.txt"), "app");
        File.WriteAllText(Path.Combine(payload, "sub", "nested.txt"), "nested");

        var project = new MsiProject
        {
            ProductName = "Bundled",
            Manufacturer = "Test Co",
            Version = version,
            UpgradeCode = new Guid("5b1f7c90-2d4e-4a61-8c33-9e0d1a2b3c4d"),
            Folders = { new PayloadFolder { SourceDir = "payload" } },   // deliberately relative
        };

        string projectPath = Path.Combine(dir, "project.json");
        project.Save(projectPath);
        return (projectPath, dir);
    }

    [Fact]
    public void RelativePathsResolveAgainstTheProjectFileNotTheWorkingDirectory()
    {
        // Resolving against the process working directory makes a project behave
        // differently depending on where it was opened from, which is how a
        // folder payload came to look empty in the GUI.
        var (projectPath, _) = WriteFolderProject();

        string elsewhere = TempDir();
        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(elsewhere);
            var project = MsiProject.Load(projectPath);
            project.ExpandFolders();
            Assert.Equal(2, project.Files.Count);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void BundleCarriesEveryPayloadFileAndFlattensFolders()
    {
        var (projectPath, dir) = WriteFolderProject();
        string bundle = Path.Combine(dir, "out" + ProjectBundle.Extension);

        var info = ProjectBundle.Save(MsiProject.Load(projectPath), bundle);
        Assert.Equal(2, info.FileCount);

        using var archive = ZipFile.OpenRead(bundle);
        Assert.NotNull(archive.GetEntry("project.json"));
        Assert.NotNull(archive.GetEntry("payload/app.txt"));
        Assert.NotNull(archive.GetEntry("payload/sub/nested.txt"));

        var reopened = ProjectBundle.Open(bundle, Path.Combine(dir, "unpacked"));

        // Folders are expanded when packing, so a reopened bundle must not carry
        // both the folder and the files it produced.
        Assert.Empty(reopened.Folders);
        Assert.Equal(2, reopened.Files.Count);
        Assert.All(reopened.Files, f => Assert.True(File.Exists(f.SourcePath), f.SourcePath));
        Assert.Contains(reopened.Files, f => f.EffectiveTarget == "sub/nested.txt");
    }

    [Fact]
    public void ABundleBuildsTheSamePackageFromAnyDirectory()
    {
        // This is the whole promise of the format: move the one file, get the
        // same MSI.
        var (projectPath, dir) = WriteFolderProject("2.3.4");
        string bundle = Path.Combine(dir, "portable" + ProjectBundle.Extension);
        ProjectBundle.Save(MsiProject.Load(projectPath), bundle);

        string fromHere = Path.Combine(dir, "a.msi");
        MsiBuilder.Build(ProjectBundle.Open(bundle, Path.Combine(dir, "u1")), fromHere);

        string elsewhere = TempDir();
        string moved = Path.Combine(elsewhere, "moved" + ProjectBundle.Extension);
        File.Copy(bundle, moved);

        string fromThere = Path.Combine(elsewhere, "b.msi");
        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(elsewhere);
            MsiBuilder.Build(ProjectBundle.Open(moved, Path.Combine(elsewhere, "u2")), fromThere);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        var a = MsiDatabase.Load(fromHere);
        var b = MsiDatabase.Load(fromThere);

        Assert.Equal(a.GetProperty("ProductCode"), b.GetProperty("ProductCode"));
        Assert.Equal(a.GetProperty("UpgradeCode"), b.GetProperty("UpgradeCode"));
        Assert.Equal("2.3.4", b.GetProperty("ProductVersion"));
        Assert.Equal(a.Tables["File"].Rows.Count, b.Tables["File"].Rows.Count);

        // The package code must still differ: it identifies the build, not the product.
        Assert.NotEqual(a.Summary.RevisionNumber, b.Summary.RevisionNumber);
    }

    [Fact]
    public void LoadAnyAcceptsBothForms()
    {
        var (projectPath, dir) = WriteFolderProject();
        string bundle = Path.Combine(dir, "either" + ProjectBundle.Extension);
        ProjectBundle.Save(MsiProject.Load(projectPath), bundle);

        Assert.True(ProjectBundle.IsBundle(bundle));
        Assert.False(ProjectBundle.IsBundle(projectPath));

        Assert.Equal("Bundled", ProjectBundle.LoadAny(bundle).ProductName);
        Assert.Equal("Bundled", ProjectBundle.LoadAny(projectPath).ProductName);
    }

    [Fact]
    public void AMissingPayloadFileIsReportedRatherThanSilentlySkipped()
    {
        string dir = TempDir();
        var project = new MsiProject
        {
            ProductName = "Broken",
            Manufacturer = "Test Co",
            UpgradeCode = Guid.NewGuid(),
            Files = { new PayloadFile { SourcePath = Path.Combine(dir, "not-here.txt") } },
        };

        Assert.Throws<FileNotFoundException>(
            () => ProjectBundle.Save(project, Path.Combine(dir, "bad" + ProjectBundle.Extension)));
    }

    [Fact]
    public void AnEntryEscapingTheExtractionFolderIsRefused()
    {
        // A zip may name a path outside the destination; unpacking it blindly
        // writes wherever the archive says.
        string dir = TempDir();
        string bundle = Path.Combine(dir, "evil" + ProjectBundle.Extension);

        using (var stream = File.Create(bundle))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("project.json");
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write(new MsiProject { ProductName = "E", Manufacturer = "E" }.ToJson());

            var escape = archive.CreateEntry("payload/../../escaped.txt");
            using var bad = new StreamWriter(escape.Open());
            bad.Write("nope");
        }

        Assert.Throws<InvalidDataException>(
            () => ProjectBundle.Open(bundle, Path.Combine(dir, "unpack")));
    }
}
