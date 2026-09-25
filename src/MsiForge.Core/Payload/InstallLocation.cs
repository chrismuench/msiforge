namespace MsiForge.Core.Payload;

/// <summary>
/// Rules for a place a wrapped installer may be sent. Kept out of the dialog that
/// asks the question, because "is this somewhere a SYSTEM install may safely put
/// files" is a decision worth testing, not UI glue.
/// </summary>
public static class InstallLocation
{
    /// <summary>
    /// A fully-qualified path on a fixed drive that exists. Removable drives and
    /// network (UNC) paths are refused: the installer runs as SYSTEM during a
    /// deployment, and by the time it does, a removable path may be gone and a
    /// network path is outside the machine's control. <paramref name="reason"/> is
    /// set to a plain-language explanation when the path is rejected.
    /// </summary>
    public static bool IsFixedDrive(string? path, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Choose a folder.";
            return false;
        }
        if (!Path.IsPathRooted(path) || !Path.IsPathFullyQualified(path))
        {
            reason = "Use a full path, like C:\\Program Files\\App.";
            return false;
        }

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));

            // A UNC root has no drive letter (\\server\share); reject it before
            // DriveInfo, which does not model it.
            if (string.IsNullOrEmpty(root) || !root.Contains(':'))
            {
                reason = "Choose a folder on a fixed drive, not a network location.";
                return false;
            }
            if (new DriveInfo(root).DriveType != DriveType.Fixed)
            {
                reason = "That drive is removable or a network drive. Choose a fixed drive.";
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            reason = "That path is not valid.";
            return false;
        }
        return true;
    }
}
