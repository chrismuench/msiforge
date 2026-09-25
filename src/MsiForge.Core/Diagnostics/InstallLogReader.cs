using System.Text.RegularExpressions;

namespace MsiForge.Core.Diagnostics;

public sealed record LogFinding(
    string Code,
    string Summary,
    string Meaning,
    string Fix,
    string Evidence,
    int LineNumber);

/// <summary>
/// Reads a Windows Installer verbose log and says, in plain terms, what went
/// wrong.
///
/// A failed deployment gives an administrator a 1603 and half a megabyte of log.
/// The actual cause is in there, but it is one line among thousands and is
/// phrased for whoever wrote the installer engine: "Disallowing installation of
/// component", "RemoveExistingProducts action sequenced incorrectly", "could not
/// load table". Every entry below is a failure that was hit, diagnosed and fixed
/// while building this tool, so the explanation is what it actually turned out
/// to mean rather than a restatement of the message.
/// </summary>
public static partial class InstallLogReader
{
    private sealed record Rule(
        string Pattern,
        string Code,
        string Summary,
        string Meaning,
        string Fix);

    // Ordered by how specific they are: a precise cause beats a generic failure,
    // and 1603 in particular means only "something went wrong".
    private static readonly Rule[] Rules =
    [
        new(@"higher versioned keyfile exists",
            "component skipped",
            "A file was not installed because Windows Installer thought the copy already on disk was newer.",
            "The package declares a file as unversioned while the file on disk carries a version, so the "
            + "installer refuses to replace it. During an upgrade RemoveExistingProducts then deletes the old "
            + "copy and nothing puts it back, so the upgrade quietly removes part of the application.",
            "Populate the File table's Version column from the file's own version resource, and emit an "
            + "MsiFileHash row only for files that genuinely have no version."),

        new(@"RemoveExistingProducts action sequenced incorrectly|\bError 2613\b",
            "2613",
            "RemoveExistingProducts is in the wrong place in the sequence.",
            "Nothing deferred may run between InstallInitialize and RemoveExistingProducts; it has to be the "
            + "first thing in the execution script.",
            "Move any custom action that currently precedes it. An action that must run before the old build "
            + "is removed has to be immediate and scheduled before InstallInitialize."),

        new(@"\bError 2229\b|[Cc]ould not load table",
            "2229",
            "A table could not be read, so the package is structurally invalid.",
            "Usually the rows are not stored in primary-key order. Windows Installer requires that order, and "
            + "for string keys it is by string-pool index rather than by the text. It can also mean the stream "
            + "length is not a whole number of rows.",
            "Rebuild the package. If it was produced by hand or by an editor, re-save it through a tool that "
            + "writes the tables in order."),

        new(@"\bError 2211\b|[Cc]ould not create database table",
            "2211",
            "Windows Installer could not recreate a table.",
            "The _Validation table is missing, or is not listed in _Tables. Any operation that rebuilds tables, "
            + "including an administrative install, needs it.",
            "Rebuild the package with a complete _Validation table."),

        new(@"Installation success or error status: 1620|MainEngineThread is returning 1620|could not be opened.*Contact the application vendor",
            "1620",
            "The package could not be opened at all.",
            "Most often the summary information stream is malformed. Windows Installer will not read UTF-8 "
            + "there, even though the database string pool may use it: every string property reads back empty, "
            + "and a package with no Template is rejected outright.",
            "Rebuild the package. The summary stream must use an ANSI codepage."),

        new(@"\bError 1722\b",
            "1722",
            "A program run by the installer did not finish as expected.",
            "A custom action returned a non-zero exit code. Common causes are a quoting problem in the command "
            + "line - a directory property ends in a backslash, so a trailing \"[DIR]\" escapes its own closing "
            + "quote - or the program simply failing.",
            "Find the CustomActionSchedule line just above this in the log; it prints the exact command. Run "
            + "that command by hand to see the real error."),

        new(@"\bError 1721\b",
            "1721",
            "A program required by the installer could not be run at all.",
            "The executable was not found at the path the custom action names, or is not runnable in that "
            + "context.",
            "Check the Source and Target of the custom action. An action running a file from the package must "
            + "be sequenced after InstallFiles."),

        new(@"\bError 2762\b",
            "2762",
            "A custom action is scheduled outside the execution script.",
            "A deferred custom action was placed outside the InstallInitialize to InstallFinalize range, where "
            + "it cannot run.",
            "Move the action between InstallInitialize and InstallFinalize, or make it immediate."),

        // A bare number matches policy lines, properties and timestamps - every
        // normal log mentions reboots and 1603 - so the status codes below
        // require the line that actually reports the outcome.
        new(@"Installation success or error status: 1618|MainEngineThread is returning 1618|Another installation is (already )?in progress",
            "1618",
            "Another installation was already running.",
            "Windows Installer serialises installs. In a fleet this usually means an update or another "
            + "deployment held the mutex.",
            "Retry. In Intune, 1618 should be configured as a retry return code."),

        new(@"Installation success or error status: 1638|MainEngineThread is returning 1638|Another version of this product is already installed",
            "1638",
            "A different version of this product is already installed.",
            "The package tried to install over an existing version without an upgrade path, so Windows "
            + "Installer refused.",
            "Give both versions the same UpgradeCode and an increasing ProductVersion, with an Upgrade table "
            + "row covering the installed version."),

        new(@"Installation success or error status: 1603|MainEngineThread is returning 1603",
            "1603",
            "A fatal error during installation.",
            "This is the generic failure and says nothing on its own. The real cause is earlier in the log.",
            "Look for the first 'Return value 3' and read the lines just above it - that is the action that "
            + "actually failed."),

        new(@"Installation success or error status: 3010|MainEngineThread is returning 3010",
            "3010",
            "The install succeeded but asked for a reboot.",
            "Usually a file was in use and could only be replaced on restart.",
            "Close the application before installing. Naming the process in CloseProcesses does this "
            + "automatically. In Intune, 3010 should be configured as a soft-reboot return code."),
    ];

    /// <summary>
    /// A verbose install log is routinely hundreds of megabytes, and the one
    /// someone brings to be explained is the pathological one. Streamed a line at
    /// a time rather than materialised as a string[], and matched with
    /// NonBacktracking so a single very long line cannot take exponential time -
    /// every pattern here is compatible with it.
    /// </summary>
    private static readonly RegexOptions MatchOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    /// <summary>The first real failure in the log, and anything else worth knowing.</summary>
    public static List<LogFinding> Read(string logPath)
    {
        var findings = new List<LogFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var regexes = Rules.Select(r => new Regex(r.Pattern, MatchOptions)).ToArray();

        // One pass over the file rather than one per rule: thirteen passes over a
        // half-gigabyte log is thirteen reads of it.
        int lineNumber = 0;
        foreach (string line in File.ReadLines(logPath))
        {
            lineNumber++;
            if (seen.Count == Rules.Length) break;

            for (int i = 0; i < Rules.Length; i++)
            {
                if (seen.Contains(Rules[i].Code) || !regexes[i].IsMatch(line)) continue;
                seen.Add(Rules[i].Code);
                findings.Add(new LogFinding(
                    Rules[i].Code, Rules[i].Summary, Rules[i].Meaning, Rules[i].Fix,
                    Tidy(line), lineNumber));
            }
        }

        // A generic 1603 is noise once something specific has been found.
        if (findings.Count > 1) findings.RemoveAll(f => f.Code == "1603");

        return findings
            .OrderBy(f => f.Code == "3010" ? 1 : 0)
            .ThenBy(f => f.LineNumber)
            .ToList();
    }

    /// <summary>The action Windows Installer was running when it gave up.</summary>
    public static string? FailingAction(string logPath)
    {
        var ended = ActionEnded();
        string? last = null;

        // Streamed forwards, keeping the most recent match, rather than reading
        // the whole log into memory to walk it backwards.
        foreach (string line in File.ReadLines(logPath))
        {
            var match = ended.Match(line);
            if (!match.Success || match.Groups[2].Value != "3") continue;

            string action = match.Groups[1].Value;
            // INSTALL and InstallFinalize are the outer frames; the action that
            // actually failed is the innermost one that returned 3.
            if (action is "INSTALL" or "InstallFinalize" or "InstallExecute") continue;
            last = action;
        }
        return last;
    }

    private static string Tidy(string line)
    {
        string text = TimestampPrefix().Replace(line, "").Trim();
        return text.Length > 200 ? text[..200] + "..." : text;
    }

    [GeneratedRegex(@"^MSI \([a-z]\) \([0-9A-F:]+\) \[[0-9:]+\]:\s*")]
    private static partial Regex TimestampPrefix();

    [GeneratedRegex(@"Action ended [0-9:]+: ([^.]+)\. Return value (\d)")]
    private static partial Regex ActionEnded();
}
