using System.Text.Json.Serialization;
using MsiMaker.Core.Model;

namespace MsiMaker.Core.Recipes;

/// <summary>
/// A starting project for an application that is awkward to package, together
/// with the reason it is awkward.
///
/// The silent switches are the least valuable part of a recipe: they are in a
/// vendor PDF somewhere and the detective can often fingerprint them anyway.
/// <see cref="Awkwardness"/> is the part nobody can look up - the trap that
/// makes the obvious packaging of this application fail quietly across a fleet,
/// months after it was deployed and long after anyone is still watching.
/// </summary>
public sealed class Recipe
{
    /// <summary>Lower-case and hyphenated; this is what the CLI takes as an argument.</summary>
    public required string Id { get; set; }

    public required string Name { get; set; }
    public string Vendor { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>
    /// Why this application is hard to package: the detection rule that can
    /// never go false, the installer that extracts rather than installs, the
    /// switch whose quoting is silently ignored. Prose, not a switch list.
    /// </summary>
    public string Awkwardness { get; set; } = "";

    /// <summary>A stable download address, where the vendor offers one.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>
    /// How to obtain the payload when there is no stable URL - a support page
    /// that wants a model number, a login, or an extraction step that has to
    /// happen before the payload is even the right shape.
    /// </summary>
    public string? DownloadNotes { get; set; }

    /// <summary>
    /// What the payload should look like once fetched, so a wrong one is caught
    /// before it is built rather than after it is deployed. Some recipes want a
    /// single installer; HP Image Assistant wants an extracted folder.
    /// </summary>
    public string? SetupFileHint { get; set; }

    /// <summary>
    /// True only where the package was actually built, installed, upgraded and
    /// uninstalled from this repository. Everything else is transcribed vendor
    /// documentation, which is usually right and occasionally a release out of
    /// date - and a wrong silent switch breaks a fleet deployment quietly.
    /// </summary>
    public bool Verified { get; set; }

    public List<string> Notes { get; set; } = [];

    /// <summary>
    /// The project template. Payload paths hold <see cref="PayloadToken"/>
    /// instead of a real path; <see cref="RecipeLibrary.Instantiate"/> fills it
    /// in, and leaves it visible when no payload was supplied so the gap is
    /// obvious in the written file.
    /// </summary>
    public required MsiProject Project { get; set; }

    /// <summary>Stands in for the payload the admin supplies.</summary>
    public const string PayloadToken = "{payload}";

    /// <summary>Marks a recipe that came from the user folder rather than the built-in library.</summary>
    [JsonIgnore]
    public bool FromUserFolder { get; set; }
}
