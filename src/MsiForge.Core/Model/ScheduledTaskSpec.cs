namespace MsiForge.Core.Model;

public enum TaskTrigger
{
    /// <summary>Every <see cref="ScheduledTaskSpec.IntervalDays"/> days at <see cref="ScheduledTaskSpec.StartTime"/>.</summary>
    Daily,

    /// <summary>On the chosen days of the week.</summary>
    Weekly,

    /// <summary>Once, shortly after the task is registered.</summary>
    Once,

    /// <summary>At every system start.</summary>
    AtStartup,

    /// <summary>At user logon.</summary>
    AtLogon,
}

/// <summary>
/// A Windows scheduled task created on install and removed on uninstall.
///
/// This is how a tool that has to run repeatedly - HP Image Assistant being the
/// obvious case - actually keeps running. An Intune platform script executes
/// once and will not run again unless the script or policy changes, so it
/// cannot keep a fleet's drivers current on its own.
/// </summary>
public sealed class ScheduledTaskSpec
{
    public required string Name { get; set; }

    /// <summary>Task Scheduler folder, e.g. "\HP". Root when empty.</summary>
    public string? Folder { get; set; }

    public string? Description { get; set; }

    /// <summary>Executable to run, relative to the install folder.</summary>
    public required string ExePath { get; set; }

    public string Arguments { get; set; } = "";

    public TaskTrigger Trigger { get; set; } = TaskTrigger.Daily;

    /// <summary>24-hour local start time, "HH:mm".</summary>
    public string StartTime { get; set; } = "03:00";

    public int IntervalDays { get; set; } = 1;

    /// <summary>Days of the week for a weekly trigger.</summary>
    public List<DayOfWeek> DaysOfWeek { get; set; } = [DayOfWeek.Sunday];

    /// <summary>
    /// Run as SYSTEM. Driver and firmware work needs it, and it avoids depending
    /// on anyone being signed in.
    /// </summary>
    public bool RunAsSystem { get; set; } = true;

    public bool HighestPrivileges { get; set; } = true;

    /// <summary>Run as soon as possible after a missed start, e.g. a laptop that was off.</summary>
    public bool StartWhenAvailable { get; set; } = true;

    public bool RequireNetwork { get; set; } = true;

    /// <summary>Run even on battery. Off means laptops may never run it.</summary>
    public bool RunOnBattery { get; set; } = true;

    /// <summary>
    /// Random delay before starting, as an ISO 8601 duration. Spreads a fleet's
    /// downloads instead of having every device hit the network at once.
    /// </summary>
    public string? RandomDelay { get; set; } = "PT1H";

    /// <summary>Kill the task if it overruns, as an ISO 8601 duration.</summary>
    public string? ExecutionTimeLimit { get; set; } = "PT2H";

    /// <summary>Full task path as Task Scheduler addresses it.</summary>
    public string FullName =>
        string.IsNullOrWhiteSpace(Folder) ? Name : $"{Folder!.TrimEnd('\\')}\\{Name}";
}
