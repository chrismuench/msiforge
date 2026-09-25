using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace MsiForge.App.Views;

/// <summary>One labelled, copyable deploy command shown as its own card.</summary>
public sealed record DeployCommand(string Label, string Command);

/// <summary>
/// Shown once a package is built: each command to install, silence, query and
/// uninstall it on its own card with its own Copy button, so an admin can find and
/// lift the one they need without reading a wall of near-identical lines, plus a
/// way straight to the folder it landed in.
/// </summary>
public partial class DeploymentDialog : Window
{
    private IReadOnlyList<DeployCommand> _commands = [];
    private string? _revealPath;

    public DeploymentDialog()
    {
        // Parameterless ctor for the XAML loader / designer only.
        InitializeComponent();
    }

    public DeploymentDialog(
        string heading,
        string sub,
        IReadOnlyList<DeployCommand> commands,
        string? parametersNote,
        string? revealPath) : this()
    {
        _commands = commands;
        _revealPath = revealPath;

        this.FindControl<TextBlock>("Heading")!.Text = heading;
        this.FindControl<TextBlock>("SubText")!.Text = sub;
        this.FindControl<ItemsControl>("CommandList")!.ItemsSource = commands;

        if (!string.IsNullOrWhiteSpace(parametersNote))
        {
            var note = this.FindControl<TextBlock>("ParametersText")!;
            note.Text = parametersNote;
            note.IsVisible = true;
        }

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("CopyButton")!.Click += OnCopyAll;

        var showFolder = this.FindControl<Button>("ShowFolderButton")!;
        showFolder.Click += OnShowFolder;
        showFolder.IsEnabled = _revealPath is not null;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // The one command on the card whose Copy button was clicked.
    private async void OnCopyOne(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string command } && GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(command);
            if (sender is Button b) b.Content = "Copied";
        }
    }

    private async void OnCopyAll(object? sender, RoutedEventArgs e)
    {
        if (GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        string all = string.Join(
            Environment.NewLine,
            _commands.Select(c => $"{c.Label}:{Environment.NewLine}    {c.Command}"));
        await clipboard.SetTextAsync(all);
        this.FindControl<Button>("CopyButton")!.Content = "Copied";
    }

    private void OnShowFolder(object? sender, RoutedEventArgs e)
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
            // paths are still on screen to copy by hand.
        }
    }
}
