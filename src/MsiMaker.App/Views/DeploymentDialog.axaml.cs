using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MsiMaker.App.Views;

/// <summary>
/// Shown once a package is built: the exact command lines to install, silence,
/// query and uninstall it, ready to copy, plus a way straight to the folder it
/// landed in. The point is that an admin never has to reconstruct msiexec syntax
/// or hunt for the file.
/// </summary>
public partial class DeploymentDialog : Window
{
    private string _commands = "";
    private string? _revealPath;

    public DeploymentDialog()
    {
        // Parameterless ctor for the XAML loader / designer only.
        InitializeComponent();
    }

    /// <param name="heading">e.g. "App.msi is ready".</param>
    /// <param name="sub">One line under the heading.</param>
    /// <param name="commands">The deployment command block, one per line.</param>
    /// <param name="revealPath">The built file, whose folder "Show in folder" opens.</param>
    public DeploymentDialog(string heading, string sub, string commands, string? revealPath) : this()
    {
        _commands = commands;
        _revealPath = revealPath;

        this.FindControl<TextBlock>("Heading")!.Text = heading;
        this.FindControl<TextBlock>("SubText")!.Text = sub;
        this.FindControl<TextBox>("CommandsBox")!.Text = commands;

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("CopyButton")!.Click += OnCopy;

        var showFolder = this.FindControl<Button>("ShowFolderButton")!;
        showFolder.Click += OnShowFolder;
        showFolder.IsEnabled = _revealPath is not null;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnCopy(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(_commands);
        // Confirm in place, since a copy gives no other visible feedback.
        this.FindControl<Button>("CopyButton")!.Content = "Copied";
    }

    private void OnShowFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string? folder = string.IsNullOrWhiteSpace(_revealPath)
            ? null
            : Path.GetDirectoryName(_revealPath);
        if (folder is null || !Directory.Exists(folder)) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening a file manager is a convenience; if the shell refuses, the
            // path is still on screen in the command block to copy by hand.
        }
    }
}
