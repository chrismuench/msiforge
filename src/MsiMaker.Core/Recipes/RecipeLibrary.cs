using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MsiMaker.Core.Model;

namespace MsiMaker.Core.Recipes;

/// <summary>
/// The built-in recipe library, plus anything the admin has added.
///
/// Built-in recipes are embedded resources so the CLI is still a single file
/// that works on a machine with no network and no install step. A user folder
/// is read on top of them, and a user recipe with the same id wins: when a
/// vendor changes a switch, the fleet owner must be able to correct the library
/// today rather than wait for a release of this tool.
/// </summary>
public sealed class RecipeLibrary
{
    /// <summary>Resource names are stamped by the .csproj as "MsiMaker.Core.Recipes.&lt;file&gt;.json".</summary>
    private const string ResourcePrefix = "MsiMaker.Core.Recipes.";

    /// <summary>Overrides the default user folder; useful for tests and for a shared team folder.</summary>
    public const string DirectoryVariable = "MSIMAKER_RECIPES";

    private readonly Dictionary<string, Recipe> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The user folder that was read, whether or not it existed.</summary>
    public string UserDirectory { get; private init; } = "";

    /// <summary>Files under <see cref="UserDirectory"/> that would not parse, with the reason.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>
    /// Where a user's own recipes live. Honours <see cref="DirectoryVariable"/>
    /// first so a team can point every admin at one shared folder.
    /// </summary>
    public static string DefaultUserDirectory
    {
        get
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "msimaker", "recipes");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Hand-written recipes are the point, so be forgiving about casing and
        // let a trailing comma or a "// why" line through rather than failing.
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static RecipeLibrary Load(string? userDirectory = null)
    {
        var library = new RecipeLibrary { UserDirectory = userDirectory ?? DefaultUserDirectory };

        var assembly = typeof(RecipeLibrary).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            if (!resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            library.AddParsed(ReadResource(assembly, resource), resource, fromUser: false);
        }

        if (Directory.Exists(library.UserDirectory))
            foreach (string path in Directory.EnumerateFiles(library.UserDirectory, "*.json")
                                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try { library.AddParsed(File.ReadAllText(path), path, fromUser: true); }
                catch (Exception ex) { library.Problems.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
            }

        return library;
    }

    /// <summary>Verified recipes first, then alphabetically; the tested ones deserve the top of the list.</summary>
    public IReadOnlyList<Recipe> List() =>
        _byId.Values
             .OrderByDescending(r => r.Verified)
             .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
             .ToList();

    public Recipe? Find(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : _byId.GetValueOrDefault(id.Trim());

    /// <summary>
    /// Produces a project from a recipe, with the payload token replaced.
    ///
    /// The template is deep-copied, so instantiating the same recipe twice in
    /// one process cannot leave one caller's payload path in the other's
    /// project. A null payload leaves the token in place: a project file that
    /// visibly says "{payload}" is better than one that silently points at
    /// nothing.
    /// </summary>
    public static MsiProject Instantiate(Recipe recipe, string? payloadPath = null)
    {
        var project = MsiProject.FromJson(recipe.Project.ToJson());

        // Resolved against the current directory here, because the builder
        // resolves a relative payload against the project file's folder - not
        // the folder the admin typed the command in.
        string? payload = string.IsNullOrWhiteSpace(payloadPath)
            ? null
            : Path.GetFullPath(payloadPath);
        if (payload is null) return project;

        foreach (var file in project.Files)
            file.SourcePath = file.SourcePath.Replace(Recipe.PayloadToken, payload, StringComparison.Ordinal);
        foreach (var folder in project.Folders)
            folder.SourceDir = folder.SourceDir.Replace(Recipe.PayloadToken, payload, StringComparison.Ordinal);

        return project;
    }

    private void AddParsed(string json, string origin, bool fromUser)
    {
        var recipe = JsonSerializer.Deserialize<Recipe>(json, JsonOptions)
                     ?? throw new InvalidDataException($"{origin} is empty.");
        if (string.IsNullOrWhiteSpace(recipe.Id))
            throw new InvalidDataException($"{origin} has no Id.");

        recipe.FromUserFolder = fromUser;

        // User recipes are read after the built-ins, so this assignment is what
        // lets one deliberately replace a built-in of the same id.
        _byId[recipe.Id] = recipe;
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidDataException($"Embedded recipe '{name}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
