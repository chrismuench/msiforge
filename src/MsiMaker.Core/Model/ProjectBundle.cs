using System.IO.Compression;
using System.Text;

namespace MsiMaker.Core.Model;

public sealed record BundleInfo(string Path, int FileCount, long PackedBytes, long PayloadBytes);

/// <summary>
/// A project and everything it needs, in one zip.
///
/// A bare .json is only half a package: it points at payload files sitting
/// somewhere on the machine that wrote it, so it breaks the moment it is moved,
/// shared, or checked out on another machine. A bundle carries the payload with
/// it, which makes a package a single artefact that can be versioned, handed to
/// a colleague, or archived alongside the MSI it produced.
///
/// Layout:
///   project.json        the definition, with payload paths rewritten as bundle-relative
///   payload/...         every file, keeping the folder structure it installs with
/// </summary>
public static class ProjectBundle
{
    public const string Extension = ".msimaker";
    private const string ManifestEntry = "project.json";
    private const string PayloadPrefix = "payload/";

    public static bool IsBundle(string path) =>
        Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes a bundle. Folder payloads are expanded first, so what is captured
    /// is exactly the set of files the build would install - not a folder
    /// reference that might mean something different later.
    /// </summary>
    public static BundleInfo Save(MsiProject project, string bundlePath)
    {
        var snapshot = MsiProject.FromJson(project.ToJson());
        snapshot.ResolvePathsAgainst(Directory.GetCurrentDirectory());
        snapshot.ExpandFolders();

        // Folders have been flattened into Files, so keeping them would double
        // the payload when the bundle is reopened.
        snapshot.Folders.Clear();

        long payloadBytes = 0;
        int count = 0;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(bundlePath))!);
        if (File.Exists(bundlePath)) File.Delete(bundlePath);

        using (var stream = File.Create(bundlePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var file in snapshot.Files)
            {
                if (!File.Exists(file.SourcePath))
                    throw new FileNotFoundException($"Payload file is missing: {file.SourcePath}");

                string target = file.EffectiveTarget;
                string entryName = PayloadPrefix + target;

                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using (var entryStream = entry.Open())
                using (var source = File.OpenRead(file.SourcePath))
                {
                    source.CopyTo(entryStream);
                    payloadBytes += source.Length;
                }

                // Inside the bundle a payload path is always bundle-relative.
                file.SourcePath = entryName;
                file.TargetPath = target;
                count++;
            }

            var manifest = archive.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
            using var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false));
            writer.Write(snapshot.ToJson());
        }

        return new BundleInfo(bundlePath, count, new FileInfo(bundlePath).Length, payloadBytes);
    }

    /// <summary>
    /// Unpacks a bundle into <paramref name="workingDir"/> and returns the
    /// project, with payload paths pointing at the unpacked files.
    /// </summary>
    public static MsiProject Open(string bundlePath, string workingDir)
    {
        Directory.CreateDirectory(workingDir);

        using var archive = ZipFile.OpenRead(bundlePath);
        var manifest = archive.GetEntry(ManifestEntry)
            ?? throw new InvalidDataException($"{Path.GetFileName(bundlePath)} is not a MsiMaker bundle: no {ManifestEntry}.");

        string json;
        using (var reader = new StreamReader(manifest.Open(), Encoding.UTF8)) json = reader.ReadToEnd();
        var project = MsiProject.FromJson(json);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length == 0 || entry.FullName.EndsWith('/')) continue;
            if (!entry.FullName.StartsWith(PayloadPrefix, StringComparison.Ordinal)) continue;

            string destination = Path.GetFullPath(Path.Combine(workingDir, entry.FullName));

            // A zip entry may name a path outside the destination; refuse those.
            if (!destination.StartsWith(Path.GetFullPath(workingDir), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Bundle entry escapes the extraction folder: {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        project.ResolvePathsAgainst(workingDir);
        return project;
    }

    /// <summary>
    /// Opens a bundle into a scratch folder derived from its name, so repeated
    /// opens reuse the same location rather than filling the temp directory.
    /// </summary>
    public static MsiProject Open(string bundlePath)
    {
        string name = Path.GetFileNameWithoutExtension(bundlePath);
        string working = Path.Combine(Path.GetTempPath(), "msimaker", "bundles", name);
        return Open(bundlePath, working);
    }

    /// <summary>Loads a project from either a bundle or a plain .json.</summary>
    public static MsiProject LoadAny(string path) =>
        IsBundle(path) ? Open(path) : MsiProject.Load(path);
}
