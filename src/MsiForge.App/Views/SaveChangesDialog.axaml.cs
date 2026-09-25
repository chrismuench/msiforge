using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MsiForge.App.Views;

/// <summary>What to do with the current package before the canvas is cleared.</summary>
public enum SaveChangesChoice { Save, Discard, Cancel }

/// <summary>
/// Asked before starting a new package when the current one has content, so a
/// half-finished package is never thrown away without the choice being offered.
/// </summary>
public partial class SaveChangesDialog : Window
{
    public SaveChangesDialog()
    {
        InitializeComponent();
        this.FindControl<Button>("SaveButton")!.Click += (_, _) => Close(SaveChangesChoice.Save);
        this.FindControl<Button>("DiscardButton")!.Click += (_, _) => Close(SaveChangesChoice.Discard);
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(SaveChangesChoice.Cancel);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
