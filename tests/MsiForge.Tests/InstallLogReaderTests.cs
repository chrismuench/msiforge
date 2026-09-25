using MsiForge.Core.Diagnostics;
using Xunit;

namespace MsiForge.Tests;

/// <summary>
/// The explanations here came from failures hit while building this tool, so the
/// tests use the shape of a real verbose log rather than invented text.
/// </summary>
public class InstallLogReaderTests
{
    private static string WriteLog(params string[] lines)
    {
        string dir = Path.Combine(Path.GetTempPath(), "msiforge-logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "install.log");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void RecognisesTheUpgradeThatDeletesFiles()
    {
        // The worst failure mode: a package every reader accepts, whose upgrade
        // silently removes most of the application.
        string log = WriteLog(
            "MSI (s) (D0:20) [10:55:12:948]: Doing action: CostFinalize",
            "MSI (s) (D0:20) [10:55:12:948]: Disallowing installation of component: {D9CD25FE-57DE-54D5-A6A5-0F0E2B01FB3E} since the same component with higher versioned keyfile exists",
            "MSI (s) (D0:20) [10:55:13:230]: Component: c_app.exe; Installed: Absent;   Request: Local;   Action: Null");

        var findings = InstallLogReader.Read(log);
        Assert.Contains(findings, f => f.Code == "component skipped");
        Assert.Contains("File table", findings.Single(f => f.Code == "component skipped").Fix);
    }

    [Fact]
    public void RecognisesTheSequencingError()
    {
        string log = WriteLog(
            "Action start 11:42:24: RemoveExistingProducts.",
            "DEBUG: Error 2613:  RemoveExistingProducts action sequenced incorrectly.",
            "Action ended 11:42:24: RemoveExistingProducts. Return value 3.",
            "Action ended 11:42:24: INSTALL. Return value 3.");

        var findings = InstallLogReader.Read(log);
        Assert.Contains(findings, f => f.Code == "2613");
        Assert.Equal("RemoveExistingProducts", InstallLogReader.FailingAction(log));
    }

    [Fact]
    public void NamesTheInnermostFailingAction()
    {
        // INSTALL and InstallFinalize are the outer frames and say nothing useful.
        string log = WriteLog(
            "Action ended 11:05:10: ca_task_create. Return value 3.",
            "Action ended 11:05:10: InstallFinalize. Return value 3.",
            "Action ended 11:05:10: INSTALL. Return value 3.");

        Assert.Equal("ca_task_create", InstallLogReader.FailingAction(log));
    }

    [Fact]
    public void ACleanLogProducesNoFindings()
    {
        // Every normal log mentions reboots, rollback policy and property values;
        // matching a bare number against those is how a diagnosis becomes noise.
        string log = WriteLog(
            "MSI (s) (88:E4) [11:32:33:866]: Machine policy value 'DisableRollback' is 0",
            "MSI (s) (88:E4) [11:32:33:866]: PROPERTY CHANGE: Adding REBOOT property. Its value is 'ReallySuppress'.",
            "MSI (s) (88:E4) [11:32:33:866]: Note: 1: 1603 2: some unrelated note",
            "Action ended 11:32:34: INSTALL. Return value 1.",
            "MSI (s) (88:E4) [11:32:34:000]: Windows Installer installed the product. Installation success or error status: 0.");

        Assert.Empty(InstallLogReader.Read(log));
    }

    [Fact]
    public void AGenericFailureIsDroppedOnceSomethingSpecificIsFound()
    {
        string log = WriteLog(
            "DEBUG: Error 2229:  Database: . Could not load table 'Property'",
            "MSI (s) (D0:20): Installation success or error status: 1603.");

        var findings = InstallLogReader.Read(log);
        Assert.Contains(findings, f => f.Code == "2229");
        Assert.DoesNotContain(findings, f => f.Code == "1603");
    }
}
