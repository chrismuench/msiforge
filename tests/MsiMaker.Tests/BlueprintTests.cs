using MsiMaker.Core.Model;
using MsiMaker.Core.Payload;
using MsiMaker.Core.Blueprints;
using Xunit;

namespace MsiMaker.Tests;

/// <summary>
/// The blueprint library is data, and data rots quietly: a blueprint that no longer
/// deserialises, or whose project no longer validates, would otherwise only be
/// discovered by the admin who trusted it. These tests hold every embedded
/// blueprint to the same bar - it loads, it says why the application is awkward,
/// and it produces a project that would actually build.
/// </summary>
public class BlueprintTests
{
    /// <summary>
    /// Loads with the user folder pointed at somewhere empty, so a blueprint
    /// sitting in the developer's own AppData cannot change the result.
    /// </summary>
    private static BlueprintLibrary BuiltIn() =>
        BlueprintLibrary.Load(Path.Combine(Path.GetTempPath(), "msimaker-tests", "no-user-blueprints"));

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "msimaker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static TheoryData<string> BlueprintIds()
    {
        var data = new TheoryData<string>();
        foreach (var blueprint in BuiltIn().List()) data.Add(blueprint.Id);
        return data;
    }

    [Fact]
    public void LibraryLoadsTheEmbeddedBlueprints()
    {
        var blueprints = BuiltIn().List();
        Assert.True(blueprints.Count >= 4, $"expected the embedded library, got {blueprints.Count} blueprints");
        Assert.Contains(blueprints, r => r.Id == "hp-image-assistant");
        Assert.Contains(blueprints, r => r.Id == "git-for-windows");
    }

    /// <summary>
    /// A blueprint that extracts its payload must be able to prove the extraction
    /// worked: its marker file has to be a file the project actually runs (a task
    /// or a run action), or a stale marker would pass a broken download.
    /// </summary>
    [Theory]
    [MemberData(nameof(BlueprintIds))]
    public void ExtractingBlueprintsNameAMarkerTheProjectRuns(string id)
    {
        var blueprint = BuiltIn().Find(id)!;
        if (blueprint.Extract is null) return;

        Assert.Contains(PayloadExtractor.DestToken, blueprint.Extract.Switches);

        string? marker = blueprint.Extract.MarkerFile;
        Assert.False(string.IsNullOrWhiteSpace(marker),
            $"blueprint '{id}' extracts but names no marker file to confirm it worked");

        string markerName = Path.GetFileName(marker!.Replace('/', '\\'));
        bool runsIt =
            blueprint.Project.Tasks.Any(t => EndsWithFile(t.ExePath, markerName)) ||
            blueprint.Project.Run.Any(r => EndsWithFile(r.ExePath, markerName));
        Assert.True(runsIt,
            $"blueprint '{id}' marker '{markerName}' is not run by any task or run action");
    }

    private static bool EndsWithFile(string? path, string fileName) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetFileName(path.Replace('/', '\\')), fileName, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void BlueprintIdsAreUnique()
    {
        var ids = BuiltIn().List().Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [MemberData(nameof(BlueprintIds))]
    public void BlueprintIsDescribed(string id)
    {
        var blueprint = BuiltIn().Find(id)!;
        Assert.False(string.IsNullOrWhiteSpace(blueprint.Id));
        Assert.False(string.IsNullOrWhiteSpace(blueprint.Name));

        // The awkwardness note is the point of the library, so it has to say
        // something: a one-word placeholder would pass a null check.
        Assert.True(blueprint.Awkwardness.Length > 80,
            $"blueprint '{id}' does not explain why the application is awkward");
    }

    /// <summary>
    /// Only the blueprints that were actually built, installed, upgraded and
    /// uninstalled here may claim to be verified. A blueprint added later inherits
    /// the honest default, and this test is what stops it quietly claiming more.
    /// </summary>
    [Fact]
    public void OnlyTestedBlueprintsClaimToBeVerified()
    {
        var verified = BuiltIn().List().Where(r => r.Verified).Select(r => r.Id).OrderBy(i => i);
        Assert.Equal(["hp-image-assistant"], verified);
    }

    /// <summary>An untested blueprint must say so where the admin will read it, not only in a flag.</summary>
    [Theory]
    [MemberData(nameof(BlueprintIds))]
    public void UntestedBlueprintsSaySoInTheirNotes(string id)
    {
        var blueprint = BuiltIn().Find(id)!;
        if (blueprint.Verified) return;
        Assert.Contains(blueprint.Notes, n => n.Contains("NOT tested", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(BlueprintIds))]
    public void BlueprintProducesABuildableProject(string id)
    {
        var blueprint = BuiltIn().Find(id)!;
        string dir = TempDir();

        // Stand in for the real download. A folder payload needs the executable
        // the scheduled task names, so the task's own validation has something
        // to find; a file payload is just the installer.
        string payload = MakePayload(blueprint, dir);

        var project = BlueprintLibrary.Instantiate(blueprint, payload);
        project.ExpandFolders();

        var errors = project.Validate();
        Assert.True(errors.Count == 0, $"blueprint '{id}': {string.Join("; ", errors)}");
    }

    [Theory]
    [MemberData(nameof(BlueprintIds))]
    public void NoPayloadTokenSurvivesInstantiation(string id)
    {
        var blueprint = BuiltIn().Find(id)!;
        string dir = TempDir();
        var project = BlueprintLibrary.Instantiate(blueprint, MakePayload(blueprint, dir));

        Assert.DoesNotContain(project.Files, f => f.SourcePath.Contains(Blueprint.PayloadToken));
        Assert.DoesNotContain(project.Folders, f => f.SourceDir.Contains(Blueprint.PayloadToken));
    }

    /// <summary>Without a payload the token stays, so the gap is visible in the written file.</summary>
    [Fact]
    public void InstantiateWithoutAPayloadLeavesTheTokenInPlace()
    {
        var blueprint = BuiltIn().Find("git-for-windows")!;
        var project = BlueprintLibrary.Instantiate(blueprint);
        Assert.Contains(project.Files, f => f.SourcePath.Contains(Blueprint.PayloadToken));
    }

    /// <summary>Instantiating twice must not leave one caller's payload in the other's project.</summary>
    [Fact]
    public void InstantiateDoesNotMutateTheTemplate()
    {
        var library = BuiltIn();
        var blueprint = library.Find("git-for-windows")!;
        string dir = TempDir();

        BlueprintLibrary.Instantiate(blueprint, MakePayload(blueprint, dir));
        var second = BlueprintLibrary.Instantiate(blueprint);

        Assert.Contains(second.Files, f => f.SourcePath.Contains(Blueprint.PayloadToken));
    }

    /// <summary>
    /// The extension point: a file in the user folder is loaded alongside the
    /// built-ins, and one reusing a built-in id replaces it outright, so a fleet
    /// owner can correct a switch today rather than waiting for a release.
    /// </summary>
    [Fact]
    public void UserFolderAddsAndOverridesBlueprints()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "mine.json"), """
            {
              "Id": "in-house-tool",
              "Name": "In-house tool",
              "Awkwardness": "Ours, and nobody else's problem.",
              "Project": { "ProductName": "In-house tool", "Manufacturer": "Us",
                           "Version": "1.0.0",
                           "UpgradeCode": "6f2c9b30-4a17-4d55-9e81-0c3f7a2b5d94",
                           "Files": [ { "SourcePath": "{payload}" } ] }
            }
            """);
        File.WriteAllText(Path.Combine(dir, "override.json"), """
            {
              "Id": "git-for-windows",
              "Name": "Git, our way",
              "Awkwardness": "Same as the built-in, but our install path.",
              "Project": { "ProductName": "Git", "Manufacturer": "Git for Windows project",
                           "Version": "25.1.0",
                           "UpgradeCode": "9e4d1a77-2c63-4f0b-8a51-c7b93e5d6a20",
                           "Files": [ { "SourcePath": "{payload}" } ] }
            }
            """);

        var library = BlueprintLibrary.Load(dir);

        var mine = library.Find("in-house-tool");
        Assert.NotNull(mine);
        Assert.True(mine!.FromUserFolder);

        var overridden = library.Find("git-for-windows")!;
        Assert.Equal("Git, our way", overridden.Name);
        Assert.True(overridden.FromUserFolder);

        // Overriding replaces one blueprint, it does not hide the rest.
        Assert.NotNull(library.Find("hp-image-assistant"));
        Assert.Empty(library.Problems);
    }

    [Fact]
    public void AMalformedUserBlueprintIsReportedRatherThanThrown()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ this is not json");

        var library = BlueprintLibrary.Load(dir);

        Assert.Single(library.Problems);
        Assert.Contains("broken.json", library.Problems[0]);
        Assert.NotNull(library.Find("hp-image-assistant"));
    }

    /// <summary>
    /// Creates whatever shape of payload the blueprint expects: an extracted folder
    /// for a blueprint that installs folders, otherwise a single installer file.
    /// </summary>
    private static string MakePayload(Blueprint blueprint, string dir)
    {
        if (blueprint.Project.Folders.Count > 0)
        {
            string extracted = Path.Combine(dir, "extracted");
            Directory.CreateDirectory(extracted);
            foreach (var task in blueprint.Project.Tasks)
            {
                string exe = Path.Combine(extracted, task.ExePath.Replace('\\', '/').TrimStart('/'));
                Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
                File.WriteAllText(exe, "payload");
            }
            if (!Directory.EnumerateFileSystemEntries(extracted).Any())
                File.WriteAllText(Path.Combine(extracted, "payload.bin"), "payload");
            return extracted;
        }

        string setup = Path.Combine(dir, "setup-download.exe");
        File.WriteAllText(setup, "payload");
        return setup;
    }
}
