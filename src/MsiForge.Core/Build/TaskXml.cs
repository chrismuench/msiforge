using System.Text;
using System.Xml.Linq;
using MsiForge.Core.Model;

namespace MsiForge.Core.Build;

/// <summary>
/// Emits Task Scheduler 1.4 XML.
///
/// Registering via XML rather than schtasks switches is deliberate: the settings
/// that decide whether a fleet-wide task is safe - random delay, catch-up after a
/// missed start, run-on-battery, execution time limit - have no switch equivalent
/// and are the difference between a staggered rollout and every device in the
/// estate downloading at once.
/// </summary>
public static class TaskXml
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// Stands in for the install directory inside the task XML. MSI formats its
    /// own table fields but never the contents of a file it installs, so
    /// "[#fileId]" written into the XML would reach the device verbatim. The
    /// registrar script substitutes this at install time, when the real
    /// directory is known.
    /// </summary>
    public const string InstallDirToken = "__MSIFORGE_INSTALLDIR__";

    /// <summary>
    /// PowerShell that resolves the token and hands the task to schtasks. Shipped
    /// as an ordinary payload file so it is versioned and removed with the package.
    /// </summary>
    public static string RegistrarScript => """
        param(
            [Parameter(Mandatory = $true)][string] $Xml,
            [Parameter(Mandatory = $true)][string] $InstallDir,
            [Parameter(Mandatory = $true)][string] $TaskName
        )

        # Registers a scheduled task from XML, substituting the install directory
        # that Windows Installer resolved. Written by MsiForge.
        #
        # Deliberately NOT $ErrorActionPreference = 'Stop': schtasks.exe writes
        # informational text to stderr, and under 'Stop' a redirected native
        # stderr stream becomes a terminating error, failing the install for no
        # real reason. Success is decided by the exit code alone.

        $log = Join-Path $env:TEMP 'MsiForge-RegisterTask.log'
        function Note($text) {
            try { Add-Content -LiteralPath $log -Value ("{0}  {1}" -f (Get-Date -Format s), $text) } catch { }
        }

        Note "register '$TaskName' from '$Xml' with install dir '$InstallDir'"

        $temp = $null
        try {
            $resolved = $InstallDir.TrimEnd('.').TrimEnd('\')
            $content = [System.IO.File]::ReadAllText($Xml)
            $content = $content.Replace('__MSIFORGE_INSTALLDIR__', $resolved)

            $temp = Join-Path $env:TEMP ('msiforge_task_' + [guid]::NewGuid().ToString('N') + '.xml')

            # schtasks.exe requires UTF-16.
            [System.IO.File]::WriteAllText($temp, $content, [System.Text.Encoding]::Unicode)

            $output = & schtasks.exe /Create /TN $TaskName /XML $temp /F 2>&1
            $code = $LASTEXITCODE
            Note "schtasks exit $code : $($output -join ' | ')"
            if ($code -ne 0) { Write-Output $output }
            exit $code
        } catch {
            Note "failed: $($_.Exception.Message)"
            Write-Output $_.Exception.Message
            exit 1
        } finally {
            if ($temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
        }
        """;

    public static string Build(ScheduledTaskSpec task, string command, string arguments, string workingDirectory)
    {
        // A start boundary is required even for triggers that ignore it.
        var start = DateTime.Today.AddDays(1);
        if (TimeSpan.TryParse(task.StartTime, out var time)) start = DateTime.Today.AddDays(1).Add(time);
        string boundary = start.ToString("yyyy-MM-ddTHH:mm:ss");

        XElement trigger = task.Trigger switch
        {
            TaskTrigger.Daily => new XElement(Ns + "CalendarTrigger",
                new XElement(Ns + "StartBoundary", boundary),
                new XElement(Ns + "Enabled", true),
                RandomDelay(task),
                new XElement(Ns + "ScheduleByDay",
                    new XElement(Ns + "DaysInterval", Math.Max(1, task.IntervalDays)))),

            TaskTrigger.Weekly => new XElement(Ns + "CalendarTrigger",
                new XElement(Ns + "StartBoundary", boundary),
                new XElement(Ns + "Enabled", true),
                RandomDelay(task),
                new XElement(Ns + "ScheduleByWeek",
                    new XElement(Ns + "WeeksInterval", 1),
                    new XElement(Ns + "DaysOfWeek",
                        task.DaysOfWeek.Select(d => new XElement(Ns + d.ToString()))))),

            TaskTrigger.Once => new XElement(Ns + "TimeTrigger",
                new XElement(Ns + "StartBoundary", boundary),
                new XElement(Ns + "Enabled", true),
                RandomDelay(task)),

            TaskTrigger.AtStartup => new XElement(Ns + "BootTrigger",
                new XElement(Ns + "Enabled", true),
                task.RandomDelay is null ? null : new XElement(Ns + "Delay", task.RandomDelay)),

            TaskTrigger.AtLogon => new XElement(Ns + "LogonTrigger",
                new XElement(Ns + "Enabled", true),
                task.RandomDelay is null ? null : new XElement(Ns + "Delay", task.RandomDelay)),

            _ => new XElement(Ns + "CalendarTrigger",
                new XElement(Ns + "StartBoundary", boundary),
                new XElement(Ns + "Enabled", true),
                new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", 1))),
        };

        // Principal children are a fixed sequence: UserId/GroupId, LogonType, RunLevel.
        var principal = new XElement(Ns + "Principal",
            new XAttribute("id", "Author"),
            task.RunAsSystem
                ? new XElement(Ns + "UserId", "S-1-5-18")          // LocalSystem
                : new XElement(Ns + "GroupId", "S-1-5-32-545"),    // Users
            task.RunAsSystem ? null : new XElement(Ns + "LogonType", "InteractiveToken"),
            new XElement(Ns + "RunLevel", task.HighestPrivileges ? "HighestAvailable" : "LeastPrivilege"));

        // Settings children are a sequence in the Task Scheduler schema, not a
        // free-form set. Out of order, schtasks.exe rejects the file with a
        // generic failure that says nothing about ordering.
        var settings = new XElement(Ns + "Settings",
            new XElement(Ns + "AllowStartOnDemand", true),
            new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
            new XElement(Ns + "DisallowStartIfOnBatteries", !task.RunOnBattery),
            new XElement(Ns + "StopIfGoingOnBatteries", !task.RunOnBattery),
            new XElement(Ns + "AllowHardTerminate", true),
            new XElement(Ns + "StartWhenAvailable", task.StartWhenAvailable),
            new XElement(Ns + "RunOnlyIfNetworkAvailable", task.RequireNetwork),
            new XElement(Ns + "WakeToRun", false),
            new XElement(Ns + "Enabled", true),
            new XElement(Ns + "Hidden", false),
            new XElement(Ns + "IdleSettings",
                new XElement(Ns + "StopOnIdleEnd", false),
                new XElement(Ns + "RestartOnIdle", false)),
            new XElement(Ns + "ExecutionTimeLimit", task.ExecutionTimeLimit ?? "PT0S"),
            new XElement(Ns + "Priority", 7),
            new XElement(Ns + "RunOnlyIfIdle", false));

        var actions = new XElement(Ns + "Actions",
            new XAttribute("Context", "Author"),
            new XElement(Ns + "Exec",
                new XElement(Ns + "Command", command),
                string.IsNullOrWhiteSpace(arguments) ? null : new XElement(Ns + "Arguments", arguments),
                string.IsNullOrWhiteSpace(workingDirectory) ? null : new XElement(Ns + "WorkingDirectory", workingDirectory)));

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(Ns + "Task",
                new XAttribute("version", "1.4"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Date", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")),
                    new XElement(Ns + "Author", "MsiForge"),
                    new XElement(Ns + "Description", task.Description ?? $"Runs {Path.GetFileName(command)}"),
                    new XElement(Ns + "URI", "\\" + task.FullName.TrimStart('\\'))),
                new XElement(Ns + "Triggers", trigger),
                new XElement(Ns + "Principals", principal),
                settings,
                actions));

        return document.Declaration + Environment.NewLine + document.Root;

        static XElement? RandomDelay(ScheduledTaskSpec t) =>
            t.RandomDelay is null ? null : new XElement(Ns + "RandomDelay", t.RandomDelay);
    }

    /// <summary>
    /// schtasks.exe reads task XML as UTF-16, and rejects anything else with the
    /// misleading "task XML is malformed ... one root element".
    ///
    /// The byte-order mark has to be written explicitly: Encoding.GetBytes never
    /// emits a preamble, so a file produced without this is BOM-less UTF-16 that
    /// every reader then decodes as UTF-8.
    /// </summary>
    public static byte[] Encode(string xml)
    {
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        byte[] preamble = encoding.GetPreamble();
        byte[] body = encoding.GetBytes(xml);

        byte[] result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }
}
