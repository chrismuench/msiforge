using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MsiMaker.App.ViewModels;
using MsiMaker.Core.Intune;
using MsiMaker.Core.Model;

namespace MsiMaker.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model = new();
    private string? _lastBuiltMsi;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _model;

        Find<Button>("AnalyseButton").Click += OnAnalyse;
        Find<Button>("AddFilesButton").Click += OnAddFiles;
        Find<Button>("RemoveFileButton").Click += OnRemoveFile;
        Find<Button>("NewUpgradeCodeButton").Click += OnNewUpgradeCode;
        Find<Button>("AddRunButton").Click += OnAddRun;
        Find<Button>("RemoveRunButton").Click += OnRemoveRun;
        Find<Button>("PickPreviousButton").Click += OnPickPrevious;
        Find<Button>("PickCandidateButton").Click += OnPickCandidate;
        Find<Button>("RunCheckButton").Click += OnRunCheck;
        Find<Button>("PickOutputButton").Click += OnPickOutput;
        Find<Button>("BuildButton").Click += OnBuild;
        Find<Button>("OpenOutputButton").Click += OnOpenOutput;
        Find<Button>("OpenProjectButton").Click += OnOpenProject;
        Find<Button>("SaveProjectButton").Click += OnSaveProject;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private T Find<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void Status(string text) => Find<TextBlock>("StatusText").Text = text;

    // ------------------------------------------------------------- pickers

    private async Task<IStorageFile?> PickOpen(string title, params FilePickerFileType[] types)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types.Length > 0 ? types : null,
        });
        return files.Count > 0 ? files[0] : null;
    }

    private static readonly FilePickerFileType ExeType = new("Installers") { Patterns = ["*.exe", "*.msi"] };
    private static readonly FilePickerFileType MsiType = new("Windows Installer") { Patterns = ["*.msi"] };
    private static readonly FilePickerFileType ProjectType = new("MsiMaker project") { Patterns = ["*.json"] };
    private static readonly FilePickerFileType AnyType = new("All files") { Patterns = ["*"] };

    // ------------------------------------------------------------- payload

    private async void OnAnalyse(object? sender, RoutedEventArgs e)
    {
        var file = await PickOpen("Choose an installer to analyse", ExeType, AnyType);
        if (file is null) return;
        try
        {
            _model.Analyse(file.Path.LocalPath);
            Status($"Analysed {file.Name}: {_model.AnalysisSummary}");
        }
        catch (Exception ex)
        {
            Status($"Could not analyse {file.Name}: {ex.Message}");
        }
    }

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add payload files",
            AllowMultiple = true,
        });
        foreach (var f in files) _model.AddFile(f.Path.LocalPath);
        if (files.Count > 0) Status($"Added {files.Count} file(s).");
    }

    private void OnRemoveFile(object? sender, RoutedEventArgs e)
    {
        if (Find<DataGrid>("FilesGrid").SelectedItem is FileRow row)
        {
            _model.RemoveFile(row);
            Status("Removed payload file.");
        }
    }

    // ------------------------------------------------------------ identity

    private void OnNewUpgradeCode(object? sender, RoutedEventArgs e)
    {
        _model.UpgradeCode = Guid.NewGuid().ToString();
        Status("New UpgradeCode generated. Keep this value for every future version of this product.");
    }

    // ----------------------------------------------------------------- run

    private void OnAddRun(object? sender, RoutedEventArgs e)
    {
        string exe = _model.Files.FirstOrDefault()?.Target ?? "setup.exe";
        _model.AddRun(new RunAction { Id = $"run{_model.Run.Count + 1}", ExePath = exe, Arguments = "" });
    }

    private void OnRemoveRun(object? sender, RoutedEventArgs e)
    {
        if (Find<DataGrid>("RunGrid").SelectedItem is RunRow row) _model.RemoveRun(row);
    }

    // ------------------------------------------------------- upgrade check

    private async void OnPickPrevious(object? sender, RoutedEventArgs e)
    {
        var file = await PickOpen("Choose the MSI currently deployed", MsiType);
        if (file is not null) _model.PreviousMsi = file.Path.LocalPath;
    }

    private async void OnPickCandidate(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_model.PreviousMsi))
        {
            Status("Choose the previously shipped MSI first.");
            return;
        }
        var file = await PickOpen("Choose the candidate MSI", MsiType);
        if (file is null) return;
        RunCheck(_model.PreviousMsi, file.Path.LocalPath);
    }

    private void OnRunCheck(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_model.PreviousMsi))
        {
            Status("Choose the previously shipped MSI first.");
            return;
        }
        if (_lastBuiltMsi is null || !File.Exists(_lastBuiltMsi))
        {
            Status("Build a package first, or pick a candidate MSI to compare against.");
            return;
        }
        RunCheck(_model.PreviousMsi, _lastBuiltMsi);
    }

    private void RunCheck(string previous, string candidate)
    {
        try
        {
            _model.RunUpgradeCheck(previous, candidate);
            Status(_model.Verdict);
        }
        catch (Exception ex)
        {
            Status($"Upgrade check failed: {ex.Message}");
        }
    }

    // --------------------------------------------------------------- build

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Where should the MSI be written?",
            SuggestedFileName = $"{MsiProject.Sanitize(_model.ProductName)}-{_model.Project.ComparableVersion}.msi",
            DefaultExtension = "msi",
            FileTypeChoices = [MsiType],
        });
        if (file is not null) _model.OutputPath = file.Path.LocalPath;
    }

    private void OnBuild(object? sender, RoutedEventArgs e)
    {
        var errors = _model.Project.Validate();
        if (errors.Count > 0)
        {
            foreach (var err in errors) _model.Append($"cannot build: {err}");
            Status($"{errors.Count} problem(s) - see the log.");
            return;
        }

        string output = _model.OutputPath;
        if (string.IsNullOrWhiteSpace(output))
        {
            output = Path.Combine(
                Path.GetDirectoryName(_model.Files[0].Source) ?? Directory.GetCurrentDirectory(),
                $"{MsiProject.Sanitize(_model.ProductName)}-{_model.Project.ComparableVersion}.msi");
            _model.OutputPath = output;
        }

        try
        {
            var result = _model.Build(output);
            _lastBuiltMsi = result.MsiPath;

            _model.Append($"built {result.MsiPath}");
            _model.Append($"  ProductVersion {result.Version}");
            _model.Append($"  ProductCode    {DeterministicGuid.Format(result.ProductCode)}");
            _model.Append($"  UpgradeCode    {DeterministicGuid.Format(result.UpgradeCode)}");
            _model.Append($"  PackageCode    {result.PackageCode}");
            _model.Append($"  {result.SizeBytes:N0} bytes");
            foreach (var w in result.Warnings) _model.Append($"  warning: {w}");

            if (_model.EmitIntuneWin)
            {
                string intunewin = Path.ChangeExtension(result.MsiPath, ".intunewin");
                var packaged = IntuneWinPackager.PackageMsi(result.MsiPath, intunewin);
                _model.Append($"built {packaged.Path} ({packaged.EncryptedSize:N0} bytes)");
            }

            if (_model.UploadToIntune)
                _model.Append("upload to Intune: not configured in this build - sign-in is not wired up yet.");

            // Comparing against the deployed version is the whole point, so offer
            // it as soon as there is something to compare.
            if (!string.IsNullOrWhiteSpace(_model.PreviousMsi) && File.Exists(_model.PreviousMsi))
            {
                _model.RunUpgradeCheck(_model.PreviousMsi, result.MsiPath);
                _model.Append($"upgrade check: {_model.Verdict}");
            }

            Status($"Built {Path.GetFileName(result.MsiPath)}.");
        }
        catch (Exception ex)
        {
            _model.Append($"build failed: {ex.Message}");
            Status("Build failed - see the log.");
        }
    }

    private void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        string? folder = string.IsNullOrWhiteSpace(_model.OutputPath)
            ? null
            : Path.GetDirectoryName(_model.OutputPath);
        if (folder is null || !Directory.Exists(folder))
        {
            Status("Nothing built yet.");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status($"Could not open the folder: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ projects

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        var file = await PickOpen("Open a MsiMaker project", ProjectType);
        if (file is null) return;
        try
        {
            _model.LoadProject(MsiProject.Load(file.Path.LocalPath));
            Status($"Opened {file.Name}.");
        }
        catch (Exception ex)
        {
            Status($"Could not open project: {ex.Message}");
        }
    }

    private async void OnSaveProject(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project",
            SuggestedFileName = $"{MsiProject.Sanitize(_model.ProductName)}.json",
            DefaultExtension = "json",
            FileTypeChoices = [ProjectType],
        });
        if (file is null) return;
        try
        {
            _model.Project.Save(file.Path.LocalPath);
            Status($"Saved {file.Name}. The UpgradeCode lives in this file - keep it in source control.");
        }
        catch (Exception ex)
        {
            Status($"Could not save project: {ex.Message}");
        }
    }
}
