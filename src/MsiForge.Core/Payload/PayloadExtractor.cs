using System.ComponentModel;
using System.Diagnostics;
using MsiForge.Core.Blueprints;

namespace MsiForge.Core.Payload;

/// <summary>
/// Turns a downloaded self-extracting installer into the folder a blueprint
/// packages, by running the vendor's own extractor. Only the extract path of
/// these installers is used (for an HP SoftPaq, the <c>-e</c> switch), so nothing
/// is installed on the machine building the package.
/// </summary>
public static class PayloadExtractor
{
    /// <summary>Replaced in <see cref="BlueprintExtract.Switches"/> with the extraction folder.</summary>
    public const string DestToken = "{dest}";

    /// <summary>
    /// Extracts <paramref name="archiveExe"/> per <paramref name="spec"/> and
    /// returns the folder that holds the extracted payload. Windows only: the
    /// archive is a Windows executable that has to be run to unpack.
    /// </summary>
    public static string Extract(string archiveExe, BlueprintExtract spec, string? destRoot = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Extracting a SoftPaq runs a Windows executable, so it can only be done on Windows. "
                + "Extract it yourself and hand the blueprint the resulting folder.");
        if (!File.Exists(archiveExe))
            throw new FileNotFoundException($"Installer to extract not found: {archiveExe}");

        string dest = Path.Combine(destRoot ?? Path.GetTempPath(),
            "msiforge-extract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);

        // ShellExecute, not a plain CreateProcess: a SoftPaq's manifest asks for
        // administrator, and only ShellExecute can raise the UAC prompt that lets
        // it start. Launched the other way, the process fails before it runs with
        // "requires elevation" (Win32 740). A silent switch keeps any window off
        // screen; WindowStyle replaces CreateNoWindow, which ShellExecute forbids.
        var psi = new ProcessStartInfo
        {
            FileName = archiveExe,
            Arguments = spec.Switches.Replace(DestToken, dest, StringComparison.Ordinal),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = dest,
        };

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(archiveExe)}.");
        }
        catch (Win32Exception ex)
        {
            // 1223: the user dismissed the elevation prompt.
            throw new InvalidOperationException(
                ex.NativeErrorCode == 1223
                    ? $"Extraction of {Path.GetFileName(archiveExe)} needs administrator rights, and the elevation prompt was cancelled."
                    : $"Could not start {Path.GetFileName(archiveExe)}: {ex.Message}",
                ex);
        }

        using (process)
        {
            int timeout = Math.Max(30, spec.TimeoutSeconds);
            if (!process.WaitForExit(TimeSpan.FromSeconds(timeout)))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* already gone */ }
                throw new TimeoutException(
                    $"{Path.GetFileName(archiveExe)} did not finish extracting within {timeout}s.");
            }
        }

        // Success is judged by the marker file, not the exit code: some SoftPaqs
        // return their own non-zero code after a perfectly good extraction.
        return LocatePayloadRoot(dest, spec.MarkerFile);
    }

    /// <summary>
    /// The folder to package: the shallowest one holding <paramref name="marker"/>
    /// if one is named (searched for, so a SoftPaq that unpacks into a subfolder
    /// is handled), otherwise <paramref name="dest"/> itself.
    /// </summary>
    public static string LocatePayloadRoot(string dest, string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker))
            return dest;

        string markerName = Path.GetFileName(marker.Replace('/', '\\'));
        string? hit = Directory.EnumerateFiles(dest, markerName, SearchOption.AllDirectories)
            .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
            .FirstOrDefault();
        if (hit is null)
            throw new FileNotFoundException(
                $"Extraction finished but '{markerName}' was not among the extracted files. "
                + "The download may not be the installer this blueprint expects.");
        return Path.GetDirectoryName(hit)!;
    }
}
