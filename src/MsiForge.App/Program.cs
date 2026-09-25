using System.Runtime.InteropServices;
using Avalonia;
using MsiForge.Cli;

namespace MsiForge.App;

/// <summary>
/// One executable, two front ends. Started with no arguments it opens the
/// window; started with a command it behaves exactly like the CLI and exits.
///
/// The subsystem is the awkward part of that, and it is worth writing down. A
/// GUI-subsystem binary has no stdout: cmd does not wait for it, and
/// "MsiForge inspect x.msi &gt; out.txt" writes an empty file - useless in a
/// script and worse than useless in CI, because it looks like it worked.
/// AttachConsole does not rescue it either; by the time this process runs, the
/// shell has already been told not to wait.
///
/// So this is a console binary that gets rid of its console when it turns out to
/// be the GUI. The cost is a console window appearing for a moment when the app
/// is launched from Explorer. The alternative is shipping two executables, and
/// the point was to ship one.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Only a recognised verb means the CLI. Anything else is left to the GUI,
        // because a bare path is how Windows passes a double-clicked .msiforge or
        // a file dropped on the icon, and that must open the window rather than
        // print a usage message.
        if (args.Length > 0 && CommandLine.Commands.Contains(args[0]))
            return CommandLine.Run(args);

        DetachConsole();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Gets rid of the console before the window opens.
    ///
    /// It is hidden only when this process is the only one attached, which means
    /// Windows created that console for us because we were launched from
    /// Explorer. Someone who typed "MsiForge" at a prompt is sharing their own
    /// terminal, and hiding that would be rude - FreeConsole is enough there and
    /// leaves their prompt alone.
    /// </summary>
    private static void DetachConsole()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var processes = new uint[2];
            if (GetConsoleProcessList(processes, processes.Length) == 1)
            {
                const int Hide = 0;
                IntPtr window = GetConsoleWindow();
                if (window != IntPtr.Zero) ShowWindow(window, Hide);
            }

            FreeConsole();
        }
        catch (DllNotFoundException)
        {
            // No console to be rid of; the window opens either way.
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetConsoleWindow();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, int count);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
