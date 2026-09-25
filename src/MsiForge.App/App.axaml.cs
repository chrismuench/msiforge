using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MsiForge.App.Views;

namespace MsiForge.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string? project = desktop.Args?.FirstOrDefault(a =>
                a.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            desktop.MainWindow = new MainWindow(project);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
