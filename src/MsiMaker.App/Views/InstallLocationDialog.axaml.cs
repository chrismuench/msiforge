using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MsiMaker.Core.Payload;

namespace MsiMaker.App.Views;

/// <summary>
/// The outcome of asking where a wrapped installer should install: a specific
/// folder, or the installer's own default. A null result means the question was
/// cancelled.
/// </summary>
public sealed record InstallLocationChoice(bool UseInstallerDefault, string? Path);

/// <summary>
/// Asks where a wrapped installer should put its files. Neither answer is assumed:
/// the two choices start unselected and the build cannot proceed until one is
/// picked, because "a specific folder" and "the installer's own default" are
/// genuinely different decisions and guessing either would be wrong.
///
/// A specific folder is restricted to fixed drives: the installer runs as SYSTEM
/// during a deployment, and a removable or network path is either gone or
/// attacker-controlled by the time it does.
/// </summary>
public partial class InstallLocationDialog : Window
{
    public InstallLocationDialog()
    {
        // Parameterless ctor for the XAML loader / designer only.
        InitializeComponent();
    }

    /// <summary>
    /// Whether a specific folder can actually be written into this installer's
    /// command line. False for an installer family with no known directory switch,
    /// where the only honest option is the installer's own default - so the folder
    /// choice is disabled rather than offered and then ignored.
    /// </summary>
    public bool DirectorySwitchKnown
    {
        get;
        set
        {
            field = value;
            if (!value)
            {
                // No known switch means neither a fixed folder nor an install-time
                // chooser can be written into the command line - only the default.
                this.FindControl<RadioButton>("SpecificChoice")!.IsEnabled = false;
                this.FindControl<TextBox>("PathBox")!.IsEnabled = false;
                this.FindControl<Button>("BrowseButton")!.IsEnabled = false;
                this.FindControl<TextBlock>("UnknownNote")!.IsVisible = true;
            }
        }
    } = true;

    public InstallLocationDialog(string productName, string suggestedPath) : this()
    {
        this.FindControl<TextBlock>("Heading")!.Text = $"Where should {productName} install?";

        // The suggested path is a convenience for when the folder choice is picked,
        // not a preselected answer - the radio it belongs to starts unchecked.
        this.FindControl<TextBox>("PathBox")!.Text = suggestedPath;

        this.FindControl<Button>("BrowseButton")!.Click += OnBrowse;
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
        this.FindControl<Button>("OkButton")!.Click += OnOk;

        this.FindControl<RadioButton>("SpecificChoice")!.IsCheckedChanged += (_, _) => Revalidate();
        this.FindControl<RadioButton>("DefaultChoice")!.IsCheckedChanged += (_, _) => Revalidate();
        this.FindControl<TextBox>("PathBox")!.TextChanged += (_, _) => Revalidate();

        Revalidate();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the install folder",
            AllowMultiple = false,
        });
        if (folders.Count > 0)
        {
            this.FindControl<TextBox>("PathBox")!.Text = folders[0].Path.LocalPath;
            this.FindControl<RadioButton>("SpecificChoice")!.IsChecked = true;
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<RadioButton>("DefaultChoice")!.IsChecked == true)
        {
            Close(new InstallLocationChoice(UseInstallerDefault: true, Path: null));
            return;
        }

        string path = (this.FindControl<TextBox>("PathBox")!.Text ?? "").Trim();
        if (this.FindControl<RadioButton>("SpecificChoice")!.IsChecked == true
            && InstallLocation.IsFixedDrive(path, out _))
            Close(new InstallLocationChoice(UseInstallerDefault: false, Path: path));
    }

    /// <summary>
    /// OK is enabled only once a choice is made and, for a specific folder, once
    /// that folder is a valid fixed-drive path.
    /// </summary>
    private void Revalidate()
    {
        bool useDefault = this.FindControl<RadioButton>("DefaultChoice")!.IsChecked == true;
        bool useSpecific = this.FindControl<RadioButton>("SpecificChoice")!.IsChecked == true;
        string path = (this.FindControl<TextBox>("PathBox")!.Text ?? "").Trim();

        bool pathOk = InstallLocation.IsFixedDrive(path, out string reason);
        this.FindControl<Button>("OkButton")!.IsEnabled = useDefault || (useSpecific && pathOk);

        var error = this.FindControl<TextBlock>("ErrorText")!;
        error.Text = reason;
        error.IsVisible = useSpecific && !pathOk && path.Length > 0;
    }
}
