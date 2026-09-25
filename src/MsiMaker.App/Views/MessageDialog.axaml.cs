using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MsiMaker.App.Views;

/// <summary>A plain title-and-message dialog with a single OK button.</summary>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    public MessageDialog(string heading, string body) : this()
    {
        this.FindControl<TextBlock>("Heading")!.Text = heading;
        this.FindControl<TextBlock>("Body")!.Text = body;
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
