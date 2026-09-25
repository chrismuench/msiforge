using System.Text.Json.Serialization;
using MsiForge.Core.Model;

namespace MsiForge.Core.Blueprints;

/// <summary>
/// A starting project for an application that is awkward to package, together
/// with the reason it is awkward.
///
/// The silent switches are the least valuable part of a blueprint: they are in a
/// vendor PDF somewhere and the detective can often fingerprint them anyway.
/// <see cref="Awkwardness"/> is the part nobody can look up - the trap that
/// makes the obvious packaging of this application fail quietly across a fleet,
/// months after it was deployed and long after anyone is still watching.
/// </summary>
/// <summary>What a blueprint expects to be handed as its payload.</summary>
public enum BlueprintPayloadKind
{
    /// <summary>A single installer, typically a vendor EXE.</summary>
    File,

    /// <summary>A directory, such as an extracted SoftPaq.</summary>
    Folder,
}

/// <summary>
/// How to turn a downloaded self-extracting installer into the folder a
/// blueprint packages. When a blueprint carries this, the admin hands the blueprint
/// the file they actually downloaded - an HP SoftPaq, say - and MsiForge runs
/// the vendor's own extractor to unpack it, rather than making them extract it by
/// hand first. Only the extract path of these installers is used, so nothing is
/// installed on the machine building the package.
/// </summary>
public sealed class BlueprintExtract
{
    /// <summary>
    /// The switches to run the downloaded installer with. <c>{dest}</c> is
    /// replaced with a fresh temporary folder to extract into. For an HP SoftPaq
    /// this is <c>-s -e -f"{dest}"</c>: silent, extract only (do not install),
    /// into dest.
    /// </summary>
    public required string Switches { get; set; }

    /// <summary>
    /// A file that must exist once extraction finishes - proof it worked, and
    /// usually the executable the blueprint's task runs. The folder that actually
    /// holds it becomes the payload, so a SoftPaq that unpacks into a subfolder
    /// still packages correctly.
    /// </summary>
    public string? MarkerFile { get; set; }

    /// <summary>How long to wait for the extractor before giving up.</summary>
    public int TimeoutSeconds { get; set; } = 300;
}

public sealed class Blueprint
{
    /// <summary>Lower-case and hyphenated; this is what the CLI takes as an argument.</summary>
    public required string Id { get; set; }

    public required string Name { get; set; }
    public string Vendor { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>
    /// Groups the blueprint in the library list. Device and driver tools (Dell,
    /// HP, Lenovo update utilities) behave differently enough from ordinary
    /// applications - scheduled scans, machine-specific payloads - to be worth
    /// keeping apart. Free text; blueprints with the same category list together.
    /// </summary>
    public string Category { get; set; } = "Applications";

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
    /// before it is built rather than after it is deployed. Some blueprints want a
    /// single installer; HP Image Assistant wants an extracted folder.
    /// </summary>
    public string? SetupFileHint { get; set; }

    /// <summary>
    /// Present when the payload can be extracted from the file the admin
    /// downloaded, so they can hand the blueprint that file directly instead of
    /// unpacking it first. Null for a blueprint that takes its payload as-is.
    /// </summary>
    public BlueprintExtract? Extract { get; set; }

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
    /// instead of a real path; <see cref="BlueprintLibrary.Instantiate"/> fills it
    /// in, and leaves it visible when no payload was supplied so the gap is
    /// obvious in the written file.
    /// </summary>
    public required MsiProject Project { get; set; }

    /// <summary>Stands in for the payload the admin supplies.</summary>
    public const string PayloadToken = "{payload}";

    /// <summary>
    /// Whether this blueprint expects a single installer file or a whole directory.
    /// A SoftPaq has to be extracted before packaging, so HPIA wants a folder
    /// while a vendor EXE wants one file - and a caller asking for the wrong one
    /// sends the admin looking for something that does not exist.
    /// </summary>
    [JsonIgnore]
    public BlueprintPayloadKind PayloadKind =>
        Project.Folders.Count > 0 ? BlueprintPayloadKind.Folder : BlueprintPayloadKind.File;

    /// <summary>One line telling the admin what to supply.</summary>
    [JsonIgnore]
    public string PayloadPrompt =>
        Extract is not null ? "Choose the downloaded installer to extract"
        : PayloadKind == BlueprintPayloadKind.Folder ? "Choose the folder to package"
        : "Choose the installer file";

    /// <summary>Marks a blueprint that came from the user folder rather than the built-in library.</summary>
    [JsonIgnore]
    public bool FromUserFolder { get; set; }
}
