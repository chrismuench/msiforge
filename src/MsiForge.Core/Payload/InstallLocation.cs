using System.Text.RegularExpressions;

namespace MsiForge.Core.Payload;

/// <summary>
/// Rules for a place a wrapped installer may be sent. Kept out of the dialog that
/// asks the question, because "is this somewhere a SYSTEM install may safely put
/// files" is a decision worth testing, not UI glue.
/// </summary>
public static partial class InstallLocation
{
    // A drive-letter absolute Windows path: C:\... . Validated by shape rather than
    // through System.IO.Path, whose notion of "rooted" is the build host's - so a
    // Windows target path checks the same whether the build runs on Windows, macOS
    // or Linux, and the CI runners agree with a real deployment.
    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex DriveLetterPath();

    /// <summary>
    /// A fully-qualified path on a fixed drive. Removable drives and network (UNC)
    /// paths are refused: the installer runs as SYSTEM during a deployment, and by
    /// the time it does, a removable path may be gone and a network path is outside
    /// the machine's control. The path targets Windows, so its shape is judged the
    /// same on any build host; the fixed-vs-removable check only applies on Windows
    /// when that drive is actually present (it cannot be known for a machine we are
    /// not on). <paramref name="reason"/> explains any rejection.
    /// </summary>
    public static bool IsFixedDrive(string? path, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Choose a folder.";
            return false;
        }

        path = path.Trim();
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = "Choose a folder on a fixed drive, not a network location.";
            return false;
        }
        if (!DriveLetterPath().IsMatch(path))
        {
            reason = "Use a full path, like C:\\Program Files\\App.";
            return false;
        }

        // Only judge the drive type on Windows, and only when the drive exists here:
        // an MSI targets Windows but may be built anywhere, and a drive letter this
        // machine does not have says nothing about the target's.
        if (OperatingSystem.IsWindows())
        {
            string root = path[..3];   // "C:\"
            try
            {
                var drive = DriveInfo.GetDrives()
                    .FirstOrDefault(d => d.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
                if (drive is not null && drive.DriveType != DriveType.Fixed)
                {
                    reason = "That drive is removable or a network drive. Choose a fixed drive.";
                    return false;
                }
            }
            catch (IOException) { /* enumeration hiccup: fall back to the shape check above */ }
        }

        return true;
    }
}
