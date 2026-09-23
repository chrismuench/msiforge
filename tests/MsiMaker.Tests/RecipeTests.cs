using MsiMaker.Core.Model;
using MsiMaker.Core.Recipes;
using Xunit;

namespace MsiMaker.Tests;

/// <summary>
/// The recipe library is data, and data rots quietly: a recipe that no longer
/// deserialises, or whose project no longer validates, would otherwise only be
/// discovered by the admin who trusted it. These tests hold every embedded
/// recipe to the same bar - it loads, it says why the application is awkward,
/// and it produces a project that would actually build.
/// </summary>
public class RecipeTests
{
    /// <summary>
    /// Loads with the user folder pointed at somewhere empty, so a recipe
    /// sitting in the developer's own AppData cannot change the result.
    /// </summary>
    private static RecipeLibrary BuiltIn() =>
        RecipeLibrary.Load(Path.Combine(Path.GetTempPath(), "msimaker-tests", "no-user-recipes"));

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "msimaker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static TheoryData<string> RecipeIds()
    {
        var data = new TheoryData<string>();
        foreach (var recipe in BuiltIn().List()) data.Add(recipe.Id);
        return data;
    }

    [Fact]
    public void LibraryLoadsTheEmbeddedRecipes()
    {
        var recipes = BuiltIn().List();
        Assert.True(recipes.Count >= 8, $"expected the embedded library, got {recipes.Count} recipes");
        Assert.Contains(recipes, r => r.Id == "hp-image-assistant");
        Assert.Contains(recipes, r => r.Id == "7zip");
    }

    [Fact]
    public void RecipeIdsAreUnique()
    {
        var ids = BuiltIn().List().Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [MemberData(nameof(RecipeIds))]
    public void RecipeIsDescribed(string id)
    {
        var recipe = BuiltIn().Find(id)!;
        Assert.False(string.IsNullOrWhiteSpace(recipe.Id));
        Assert.False(string.IsNullOrWhiteSpace(recipe.Name));

        // The awkwardness note is the point of the library, so it has to say
        // something: a one-word placeholder would pass a null check.
        Assert.True(recipe.Awkwardness.Length > 80,
            $"recipe '{id}' does not explain why the application is awkward");
    }

    /// <summary>
    /// Only the two recipes that were actually built, installed, upgraded and
    /// uninstalled here may claim to be verified. A recipe added later inherits
    /// the honest default, and this test is what stops it quietly claiming more.
    /// </summary>
    [Fact]
    public void OnlyTestedRecipesClaimToBeVerified()
    {
        var verified = BuiltIn().List().Where(r => r.Verified).Select(r => r.Id).OrderBy(i => i);
        Assert.Equal(["7zip", "hp-image-assistant"], verified);
    }

    /// <summary>An untested recipe must say so where the admin will read it, not only in a flag.</summary>
    [Theory]
    [MemberData(nameof(RecipeIds))]
    public void UntestedRecipesSaySoInTheirNotes(string id)
    {
        var recipe = BuiltIn().Find(id)!;
        if (recipe.Verified) return;
        Assert.Contains(recipe.Notes, n => n.Contains("NOT tested", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(RecipeIds))]
    public void RecipeProducesABuildableProject(string id)
    {
        var recipe = BuiltIn().Find(id)!;
        string dir = TempDir();

        // Stand in for the real download. A folder payload needs the executable
        // the scheduled task names, so the task's own validation has something
        // to find; a file payload is just the installer.
        string payload = MakePayload(recipe, dir);

        var project = RecipeLibrary.Instantiate(recipe, payload);
        project.ExpandFolders();

        var errors = project.Validate();
        Assert.True(errors.Count == 0, $"recipe '{id}': {string.Join("; ", errors)}");
    }

    [Theory]
    [MemberData(nameof(RecipeIds))]
    public void NoPayloadTokenSurvivesInstantiation(string id)
    {
        var recipe = BuiltIn().Find(id)!;
        string dir = TempDir();
        var project = RecipeLibrary.Instantiate(recipe, MakePayload(recipe, dir));

        Assert.DoesNotContain(project.Files, f => f.SourcePath.Contains(Recipe.PayloadToken));
        Assert.DoesNotContain(project.Folders, f => f.SourceDir.Contains(Recipe.PayloadToken));
    }

    /// <summary>Without a payload the token stays, so the gap is visible in the written file.</summary>
    [Fact]
    public void InstantiateWithoutAPayloadLeavesTheTokenInPlace()
    {
        var recipe = BuiltIn().Find("7zip")!;
        var project = RecipeLibrary.Instantiate(recipe);
        Assert.Contains(project.Files, f => f.SourcePath.Contains(Recipe.PayloadToken));
    }

    /// <summary>Instantiating twice must not leave one caller's payload in the other's project.</summary>
    [Fact]
    public void InstantiateDoesNotMutateTheTemplate()
    {
        var library = BuiltIn();
        var recipe = library.Find("7zip")!;
        string dir = TempDir();

        RecipeLibrary.Instantiate(recipe, MakePayload(recipe, dir));
        var second = RecipeLibrary.Instantiate(recipe);

        Assert.Contains(second.Files, f => f.SourcePath.Contains(Recipe.PayloadToken));
    }

    /// <summary>
    /// The extension point: a file in the user folder is loaded alongside the
    /// built-ins, and one reusing a built-in id replaces it outright, so a fleet
    /// owner can correct a switch today rather than waiting for a release.
    /// </summary>
    [Fact]
    public void UserFolderAddsAndOverridesRecipes()
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
              "Id": "7zip",
              "Name": "7-Zip, our way",
              "Awkwardness": "Same as the built-in, but our install path.",
              "Project": { "ProductName": "7-Zip", "Manufacturer": "Igor Pavlov",
                           "Version": "25.1.0",
                           "UpgradeCode": "9e4d1a77-2c63-4f0b-8a51-c7b93e5d6a20",
                           "Files": [ { "SourcePath": "{payload}" } ] }
            }
            """);

        var library = RecipeLibrary.Load(dir);

        var mine = library.Find("in-house-tool");
        Assert.NotNull(mine);
        Assert.True(mine!.FromUserFolder);

        var overridden = library.Find("7zip")!;
        Assert.Equal("7-Zip, our way", overridden.Name);
        Assert.True(overridden.FromUserFolder);

        // Overriding replaces one recipe, it does not hide the rest.
        Assert.NotNull(library.Find("hp-image-assistant"));
        Assert.Empty(library.Problems);
    }

    [Fact]
    public void AMalformedUserRecipeIsReportedRatherThanThrown()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ this is not json");

        var library = RecipeLibrary.Load(dir);

        Assert.Single(library.Problems);
        Assert.Contains("broken.json", library.Problems[0]);
        Assert.NotNull(library.Find("7zip"));
    }

    /// <summary>
    /// Creates whatever shape of payload the recipe expects: an extracted folder
    /// for a recipe that installs folders, otherwise a single installer file.
    /// </summary>
    private static string MakePayload(Recipe recipe, string dir)
    {
        if (recipe.Project.Folders.Count > 0)
        {
            string extracted = Path.Combine(dir, "extracted");
            Directory.CreateDirectory(extracted);
            foreach (var task in recipe.Project.Tasks)
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
