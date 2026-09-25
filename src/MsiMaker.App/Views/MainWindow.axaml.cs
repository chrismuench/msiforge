using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MsiMaker.App.ViewModels;
using MsiMaker.Core.Intune;
using MsiMaker.Core.Model;
using MsiMaker.Core.Payload;
using MsiMaker.Core.Blueprints;
using MsiMaker.Core.Signing;

namespace MsiMaker.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model = new();
    private string? _lastBuiltMsi;

    /// <summary>
    /// Opens with a project already loaded, so a .json can be double-clicked or
    /// passed on the command line rather than opened through the dialog.
    /// </summary>
    public MainWindow(string? projectPath) : this()
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath)) return;
        try
        {
            _model.LoadProject(ProjectBundle.LoadAny(projectPath));
            Status($"Opened {Path.GetFileName(projectPath)}.");
        }
        catch (Exception ex)
        {
            Status($"Could not open {Path.GetFileName(projectPath)}: {ex.Message}");
        }
    }

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
        Find<Button>("ResultShowFolderButton").Click += OnOpenOutput;
        Find<Button>("OpenProjectButton").Click += OnOpenProject;
        Find<Button>("SaveProjectButton").Click += OnSaveProject;

        Find<Button>("UseBlueprintButton").Click += OnUseBlueprint;
        Find<Button>("OpenBlueprintUrlButton").Click += OnOpenBlueprintUrl;
        Find<Button>("OpenBlueprintsFolderButton").Click += OnOpenBlueprintsFolder;
        Find<Button>("OpenProjectOnLibraryButton").Click += OnOpenProject;
        Find<Button>("StartBlankButton").Click += OnStartBlank;

        Find<Button>("PickCertificateButton").Click += OnPickCertificate;
        Find<Button>("PickDlibButton").Click += OnPickDlib;
        Find<Button>("RecheckSignerButton").Click += OnRecheckSigner;

        // The wizard footer. Both buttons only ever move the one index the
        // indicator and the TabControl are both bound to, so the step strip,
        // the page and the footer can never disagree about where you are.
        Find<Button>("WizardBackButton").Click += (_, _) =>
        {
            if (_model.CurrentStep > 0) _model.CurrentStep--;
        };
        Find<Button>("WizardNextButton").Click += (_, _) =>
        {
            if (_model.CurrentStep < _model.Steps.Count - 1) _model.CurrentStep++;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Shrinks the window to whatever display it actually opened on.
    ///
    /// The declared size is comfortable on a desktop and taller than the work
    /// area of a 1366x768 laptop, or of any screen at 150% scaling - where the
    /// bottom of the window, status bar and all, ends up below the edge of the
    /// screen with no way to drag it back. The screen is not known until the
    /// window is open, so the clamp cannot live in XAML.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        // WorkingArea excludes the taskbar and is in physical pixels, so it has
        // to be divided by the scaling factor before it can be compared with a
        // logical size. A driver reporting nonsense is treated as unscaled.
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        double availableWidth = screen.WorkingArea.Width / scaling;
        double availableHeight = screen.WorkingArea.Height / scaling;
        if (availableWidth <= 0 || availableHeight <= 0) return;

        // Two caps, whichever is tighter: a proportion of the work area so the
        // window never fills the screen edge to edge, and a fixed allowance for
        // the title bar and border, which are outside the size set here and are
        // exactly what pushes the bottom of the window past the taskbar.
        double widthCap = Math.Min(availableWidth * 0.92, availableWidth - 24);
        double heightCap = Math.Min(availableHeight * 0.90, availableHeight - 56);

        // Only ever smaller: on a large display the declared default is the
        // deliberate size, not a starting point to be inflated from.
        Width = Math.Max(MinWidth, Math.Min(Width, widthCap));
        Height = Math.Max(MinHeight, Math.Min(Height, heightCap));

        // Re-centre unconditionally. A window can be entirely the right size and
        // still hang off the bottom because the window manager cascaded it down
        // from the last one, which is its own version of the same bug. Centred
        // on the frame, not the client area, so the title bar and border are
        // inside the work area too.
        double frameWidth = (FrameSize?.Width ?? Width) * scaling;
        double frameHeight = (FrameSize?.Height ?? Height) * scaling;
        Position = new PixelPoint(
            screen.WorkingArea.X + (int)Math.Max(0, (screen.WorkingArea.Width - frameWidth) / 2),
            screen.WorkingArea.Y + (int)Math.Max(0, (screen.WorkingArea.Height - frameHeight) / 2));
    }

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
    private static readonly FilePickerFileType BundleType = new("MsiMaker blueprint (bundles the installer)") { Patterns = ["*.msimaker"] };
    private static readonly FilePickerFileType ProjectType = new("MsiMaker blueprint (settings only)") { Patterns = ["*.json"] };
    private static readonly FilePickerFileType AnyType = new("All files") { Patterns = ["*"] };
    private static readonly FilePickerFileType PfxType = new("Certificate") { Patterns = ["*.pfx", "*.p12"] };
    private static readonly FilePickerFileType DllType = new("Signing library") { Patterns = ["*.dll"] };

    // ------------------------------------------------------------- library

    /// <summary>Tab order is fixed in the XAML; these are the two the library sends people to.</summary>
    private const int PayloadTab = 1;
    private const int IdentityTab = 2;

    private void ShowTab(int index) => _model.CurrentStep = index;

    /// <summary>
    /// Turns a blueprint into the current project.
    ///
    /// The picker the blueprint asks for matters: HP Image Assistant wants the
    /// folder a SoftPaq was extracted into, and opening a file picker for it
    /// sends the admin looking for an installer that does not exist.
    /// </summary>
    private async void OnUseBlueprint(object? sender, RoutedEventArgs e)
    {
        if (_model.SelectedBlueprint is not { } row) return;
        Blueprint blueprint = row.Model;

        // What the admin points at, and the friendly name to report it by. A
        // blueprint that can extract (an HP SoftPaq) takes the downloaded file
        // directly and is unpacked here into a folder; the rest take a folder or a
        // file as-is.
        string? payload;
        string payloadName;
        if (blueprint.Extract is { } extract)
        {
            var file = await PickOpen(blueprint.PayloadPrompt, ExeType, AnyType);
            if (file is null) return;
            string archive = file.Path.LocalPath;
            payloadName = Path.GetFileName(archive);
            Status($"Extracting {payloadName}… this can take a minute.");
            try
            {
                payload = await Task.Run(() => PayloadExtractor.Extract(archive, extract));
            }
            catch (Exception ex)
            {
                Status($"Could not extract {payloadName}: {ex.Message}");
                return;
            }
        }
        else if (row.WantsFolder)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = blueprint.PayloadPrompt,
                AllowMultiple = false,
            });
            payload = folders.Count > 0 ? folders[0].Path.LocalPath : null;
            if (payload is null) return;
            payloadName = Path.GetFileName(payload.TrimEnd(Path.DirectorySeparatorChar));
        }
        else
        {
            var file = await PickOpen(blueprint.PayloadPrompt, ExeType, AnyType);
            payload = file?.Path.LocalPath;
            if (payload is null) return;
            payloadName = Path.GetFileName(payload);
        }

        try
        {
            _model.UseBlueprint(blueprint, payload);

            // The blueprint's template version is a guess frozen when it was
            // written; the version that matters is the one in the payload the user
            // actually downloaded. Read it from the file and use it, so the MSI
            // name and the ProductCode Intune derives from it match reality rather
            // than a stale number.
            string? detected = DetectPayloadVersion(blueprint, payload);
            if (detected is not null) _model.Version = detected;

            // Ask where it installs now, while the app is front of mind, rather
            // than taking the blueprint's baked-in path as the answer. The blueprint's
            // path is offered as the suggestion; the choice is still the user's.
            await ResolveInstallLocations(askEvenWhenAlreadySet: true);

            ShowTab(IdentityTab);

            Status(detected is not null
                ? $"Loaded the {blueprint.Name} blueprint. Version {detected} read from {payloadName}."
                : $"Loaded the {blueprint.Name} blueprint with {payloadName}. Could not read a version from it - "
                  + $"the template says {row.TemplateVersion}; set Version to the one you downloaded, since the "
                  + "ProductCode Intune detects on is derived from it.");
        }
        catch (Exception ex)
        {
            Status($"Could not load the {blueprint.Name} blueprint: {ex.Message}");
        }
    }

    /// <summary>
    /// The version stamped into the payload the user handed the blueprint, or null
    /// if none can be read. For a single-file payload it is that file's version
    /// resource; for a folder payload (an extracted bundle like HP Image Assistant)
    /// it is the version of the blueprint's own main executable inside the folder.
    /// </summary>
    private static string? DetectPayloadVersion(Blueprint blueprint, string payload)
    {
        string? exe = null;
        if (File.Exists(payload))
        {
            exe = payload;
        }
        else if (Directory.Exists(payload))
        {
            // The main executable is the one the blueprint runs or schedules.
            string? relative = blueprint.Project.Run
                                   .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.ExePath))?.ExePath
                               ?? blueprint.Project.Tasks.FirstOrDefault()?.ExePath;
            if (!string.IsNullOrWhiteSpace(relative))
            {
                string candidate = Path.Combine(payload, relative.Replace('/', '\\').TrimStart('\\'));
                if (File.Exists(candidate)) exe = candidate;
            }
        }
        if (exe is null) return null;

        try
        {
            var analysis = InstallerDetective.Analyse(exe);
            string? raw = analysis.Version?.ProductVersion ?? analysis.Version?.FileVersion;
            return string.IsNullOrWhiteSpace(raw) ? null : InstallerDetective.NormaliseVersion(raw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a blueprint's download page. Only http and https are handed to the
    /// shell: a blueprint can come from a folder anyone on the team can write to,
    /// and UseShellExecute on an arbitrary scheme would run whatever it names.
    /// </summary>
    private void OnOpenBlueprintUrl(object? sender, RoutedEventArgs e)
    {
        if (_model.SelectedBlueprint is not { CanOpenDownloadUrl: true } row) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = row.DownloadUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status($"Could not open the download page: {ex.Message}");
        }
    }

    private void OnOpenBlueprintsFolder(object? sender, RoutedEventArgs e)
    {
        string folder = _model.BlueprintUserDirectory;
        try
        {
            // Created on demand: the folder is only read at start-up, so it
            // normally does not exist until somebody wants to put a file in it.
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                Status($"Created {folder}. Blueprint JSON dropped here is read the next time MsiMaker starts.");
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status($"Could not open {folder}: {ex.Message}");
        }
    }

    private void OnStartBlank(object? sender, RoutedEventArgs e)
    {
        ShowTab(PayloadTab);
        Status("Starting from nothing. Add the installer you want to wrap on the Payload tab.");
    }

    // ------------------------------------------------------------- payload

    /// <summary>
    /// The manual escape hatch. Adding files analyses on its own, so this only
    /// earns its place when the automatic choice picked the wrong installer out
    /// of several, or when an installer was renamed to an extension we ignore.
    /// </summary>
    private async void OnAnalyse(object? sender, RoutedEventArgs e)
    {
        var file = await PickOpen("Choose an installer to analyse", ExeType, AnyType);
        if (file is null) return;
        await Analyse(file.Path.LocalPath, "");
    }

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add payload files",
            AllowMultiple = true,
        });
        if (files.Count == 0) return;

        var paths = files.Select(f => f.Path.LocalPath).ToList();

        // Add everything first so the payload keeps the order it was picked in.
        // Analyse() adds its own file too, but guards against a duplicate, so by
        // the time it runs the file is already present and it will not add it twice.
        foreach (string path in paths) _model.AddFile(path);

        string added = $"Added {paths.Count} file(s).";
        var installers = paths.Where(MainViewModel.LooksLikeInstaller).ToList();

        if (installers.Count == 0)
        {
            Status($"{added} Nothing that looks like an installer, so no analysis was run.");
            return;
        }

        // With more than one candidate there is no way to know which is the real
        // installer, so take the first and say so rather than guessing silently.
        string target = installers[0];
        string note = installers.Count > 1
            ? $" {installers.Count} installers added - analysed the first. Use Re-analyse to choose another."
            : "";
        await Analyse(target, added, note);
    }

    private async Task Analyse(string path, string prefix, string note = "")
    {
        string name = Path.GetFileName(path);
        try
        {
            await _model.AnalyseAsync(path);
            Status($"{prefix} Analysed {name}: {_model.AnalysisSummary}.{note}".TrimStart());
        }
        catch (Exception ex)
        {
            Status($"{prefix} Could not analyse {name}: {ex.Message}".TrimStart());
        }
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
        await RunCheck(_model.PreviousMsi, file.Path.LocalPath);
    }

    private async void OnRunCheck(object? sender, RoutedEventArgs e)
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
        await RunCheck(_model.PreviousMsi, _lastBuiltMsi);
    }

    private async Task RunCheck(string previous, string candidate)
    {
        try
        {
            await _model.RunUpgradeCheckAsync(previous, candidate);
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

    private async void OnPickCertificate(object? sender, RoutedEventArgs e)
    {
        var file = await PickOpen("Choose the certificate to sign with", PfxType, AnyType);
        if (file is not null) _model.CertificatePath = file.Path.LocalPath;
    }

    private async void OnPickDlib(object? sender, RoutedEventArgs e)
    {
        var file = await PickOpen("Choose Azure.CodeSigning.Dlib.dll", DllType, AnyType);
        if (file is not null) _model.AzureDlibPath = file.Path.LocalPath;
    }

    private void OnRecheckSigner(object? sender, RoutedEventArgs e)
    {
        _model.RecheckSigner();
        Status(_model.SignerFound ? $"Signer found: {_model.SignerStatus}" : "Still no signer installed.");
    }

    private async void OnBuild(object? sender, RoutedEventArgs e)
    {
        // The log is folded away until it has something to say; a build is
        // exactly when it does, whether it succeeded or not.
        Find<Expander>("LogExpander").IsExpanded = true;

        // A fresh attempt starts with no result showing, so the banner never
        // reflects an earlier build once this one is under way.
        _model.HasBuildResult = false;

        var errors = _model.Project.Validate();
        if (errors.Count > 0)
        {
            foreach (var err in errors) _model.Append($"cannot build: {err}");
            Status($"{errors.Count} problem(s) - see the log.");
            _model.ShowBuildResult(false, $"{errors.Count} problem(s) stopped the build", "",
                "The blueprint is not valid yet - see the log below for what to fix.");
            return;
        }

        // A wrapped installer with no install-directory switch installs wherever
        // the vendor defaults, ignoring the Install folder. Rather than build that
        // silently, ask where it should go and put the switch back. Cancelling the
        // dialog cancels the build - the choice is deliberate, not skippable.
        if (!await EnsureWrappedInstallersHaveALocation())
        {
            Status("Build cancelled - no install location chosen.");
            return;
        }

        // No output chosen yet: ask where to save rather than dropping the MSI
        // silently next to the payload. The payload's own folder is offered as the
        // starting location, and the name is pre-filled; cancelling cancels the
        // build, so nothing is written to a place the user did not pick.
        string output = _model.OutputPath;
        if (string.IsNullOrWhiteSpace(output))
        {
            string? payloadDir = _model.Files.Count > 0
                ? Path.GetDirectoryName(_model.Files[0].Source)
                : null;
            var start = string.IsNullOrWhiteSpace(payloadDir)
                ? null
                : await StorageProvider.TryGetFolderFromPathAsync(payloadDir);

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Where should the MSI be written?",
                SuggestedFileName = $"{MsiProject.Sanitize(_model.ProductName)}-{_model.Project.ComparableVersion}.msi",
                DefaultExtension = "msi",
                FileTypeChoices = [MsiType],
                SuggestedStartLocation = start,
            });
            if (file is null)
            {
                Status("Build cancelled - no output location chosen.");
                return;
            }
            output = file.Path.LocalPath;
            _model.OutputPath = output;
        }

        try
        {
            var result = await _model.BuildAsync(output);
            _lastBuiltMsi = result.MsiPath;
            _model.MarkBuilt();

            _model.Append($"built {result.MsiPath}");
            _model.Append($"  ProductVersion {result.Version}");
            _model.Append($"  ProductCode    {DeterministicGuid.Format(result.ProductCode)}");
            _model.Append($"  UpgradeCode    {DeterministicGuid.Format(result.UpgradeCode)}");
            _model.Append($"  PackageCode    {result.PackageCode}");
            _model.Append($"  {result.SizeBytes:N0} bytes");
            foreach (var w in result.Warnings) _model.Append($"  warning: {w}");

            // Signing comes after the build and before anything is packaged
            // around the MSI: any edit to the database invalidates the
            // signature, and an .intunewin built first would carry the unsigned
            // file. A failure here is reported, not thrown, so the MSI on disk
            // is still named in the log.
            string signedNote = "";
            if (_model.Project.Signing.Enabled)
            {
                var signed = CodeSigner.Sign(result.MsiPath, _model.Project.Signing);
                if (signed.Signed)
                {
                    _model.Append($"signed {result.MsiPath} with {signed.Tool}");
                    if (!string.IsNullOrWhiteSpace(signed.Detail)) _model.Append($"  {signed.Detail}");
                    signedNote = " Signed.";
                }
                else
                {
                    _model.Append($"SIGNING FAILED: {signed.Detail}");
                    _model.Append("  the package was built but is not signed.");
                    signedNote = " SIGNING FAILED - see the log.";
                }
            }

            string intunewinNote = "";
            if (_model.EmitIntuneWin)
            {
                string intunewin = Path.ChangeExtension(result.MsiPath, ".intunewin");
                var packaged = IntuneWinPackager.PackageMsi(result.MsiPath, intunewin);
                _model.Append($"built {packaged.Path} ({packaged.EncryptedSize:N0} bytes)");
                intunewinNote = "  +  .intunewin";
            }

            // Comparing against the deployed version is the whole point, so offer
            // it as soon as there is something to compare.
            if (!string.IsNullOrWhiteSpace(_model.PreviousMsi) && File.Exists(_model.PreviousMsi))
            {
                await _model.RunUpgradeCheckAsync(_model.PreviousMsi, result.MsiPath);
                _model.Append($"upgrade check: {_model.Verdict}");
            }

            Status($"Built {Path.GetFileName(result.MsiPath)}.{signedNote}");

            // The unmistakable part: a green banner naming the file and its size,
            // the same information the log carries but where it cannot be missed.
            string detail = $"{result.SizeBytes:N0} bytes   ·   version {result.Version}"
                            + (signedNote.Length > 0 ? "   ·  " + signedNote.Trim().TrimEnd('.') : "")
                            + intunewinNote;
            _model.ShowBuildResult(true, $"{Path.GetFileName(result.MsiPath)} is ready",
                                   result.MsiPath, detail);
        }
        catch (Exception ex)
        {
            _model.Append($"build failed: {ex.Message}");
            Status("Build failed - see the log.");
            _model.ShowBuildResult(false, "Build failed", "", ex.Message);
        }
    }

    /// <summary>
    /// For every install-time action that runs an installer without naming where
    /// it should land, asks the user and writes the vendor's directory switch back
    /// into the arguments. Returns false if the user cancels, which cancels the
    /// build. The right switch for the path is learned by re-reading the payload
    /// EXE, so it works whether the action came from a blueprint or from analysis,
    /// and whatever the installer family is.
    /// </summary>
    private Task<bool> EnsureWrappedInstallersHaveALocation() =>
        ResolveInstallLocations(askEvenWhenAlreadySet: false);

    /// <summary>
    /// Asks where each wrapped installer should put its files and records the
    /// answer. With <paramref name="askEvenWhenAlreadySet"/> true - used right after
    /// a blueprint is loaded - it asks even when the arguments already carry a path,
    /// because a blueprint's baked-in location is a suggestion, not a decision the
    /// person made. At build time it is false, so a location already settled is
    /// left alone. Returns false if the user cancels, which cancels what prompted
    /// it. The vendor's directory switch is learned by reading the payload EXE, so
    /// this works whatever the installer family is.
    /// </summary>
    private async Task<bool> ResolveInstallLocations(bool askEvenWhenAlreadySet)
    {
        foreach (var row in _model.Run.ToList())
        {
            var action = row.Model;
            bool isInstall = action.When is RunWhen.Install or RunWhen.Both;
            if (!isInstall || string.IsNullOrWhiteSpace(action.ExePath)) continue;

            bool alreadySet = action.UseInstallerDefaultLocation
                              || InstallerDetective.MentionsInstallDirectory(action.Arguments);
            if (alreadySet && !askEvenWhenAlreadySet) continue;

            string? source = _model.Files
                .FirstOrDefault(f => string.Equals(
                    Path.GetFileName(f.Source), Path.GetFileName(action.ExePath),
                    StringComparison.OrdinalIgnoreCase))?.Source;

            // A folder can be written when the arguments already carry a directory
            // switch (a blueprint's own /D=, say) or when the payload is a recognised
            // installer family whose switch we know. The blueprint's switch counts even
            // if live detection fails to recognise the EXE.
            string? template = source is not null && File.Exists(source)
                ? InstallerDetective.Analyse(source).SilentArgumentsWithDirectory
                : null;
            bool hasExistingSwitch = InstallerDetective.MentionsInstallDirectory(action.Arguments);
            bool canWriteFolder = hasExistingSwitch || template is not null;

            // Only prompt for something that is actually an installer. A blueprint may
            // carry a plain post-install command with no directory concept at all.
            if (!canWriteFolder && !hasExistingSwitch) continue;

            string appName = string.IsNullOrWhiteSpace(_model.ProductName) ? "this app" : _model.ProductName;

            // Seed the suggestion from whatever real path is already known - a
            // blueprint's own choice - falling back to Program Files under the product
            // name.
            string suggested = InstallerDetective.ExtractInstallDirectory(action.Arguments)
                               ?? $@"C:\Program Files\{MsiProject.Sanitize(_model.ProductName)}";

            var dialog = new InstallLocationDialog(appName, suggested)
            {
                DirectorySwitchKnown = canWriteFolder,
            };
            var choice = await dialog.ShowDialog<InstallLocationChoice?>(this);
            if (choice is null) return false;

            if (choice.UseInstallerDefault)
            {
                // Strip any path a blueprint carried, so "use the default" is true.
                row.Arguments = InstallerDetective.RemoveInstallDirectory(action.Arguments);
                action.UseInstallerDefaultLocation = true;
                _model.Append($"'{action.ExePath}' will install to the installer's own default location.");
                continue;
            }

            // A fixed folder is baked straight into the visible arguments as a real,
            // editable path - reusing the switch already there if there is one, so a
            // blueprint's other flags and quoting survive; otherwise the switch the
            // detected installer family uses.
            string chosenPath = choice.Path!.TrimEnd('\\');
            row.Arguments = hasExistingSwitch
                ? InstallerDetective.WithInstallDirectory(action.Arguments, chosenPath)
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, template!, chosenPath);
            action.UseInstallerDefaultLocation = false;
            _model.Append($"install location for '{action.ExePath}' set to {choice.Path}");
        }
        return true;
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
        var file = await PickOpen("Open a blueprint", BundleType, ProjectType);
        if (file is null) return;
        try
        {
            _model.LoadProject(ProjectBundle.LoadAny(file.Path.LocalPath));
            Status($"Opened {file.Name}.");
        }
        catch (Exception ex)
        {
            Status($"Could not open blueprint: {ex.Message}");
        }
    }

    private async void OnSaveProject(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save blueprint",
            SuggestedFileName = $"{MsiProject.Sanitize(_model.ProductName)}{ProjectBundle.Extension}",
            DefaultExtension = ProjectBundle.Extension.TrimStart('.'),
            FileTypeChoices = [BundleType, ProjectType],
        });
        if (file is null) return;
        try
        {
            string path = file.Path.LocalPath;
            if (ProjectBundle.IsBundle(path))
            {
                var info = ProjectBundle.Save(_model.Project, path);
                Status($"Saved {file.Name} - {info.FileCount:N0} file(s) bundled in, including the installer, "
                       + $"{info.PackedBytes:N0} bytes. This one file rebuilds the package on any machine.");
            }
            else
            {
                _model.Project.Save(path);
                Status($"Saved {file.Name}. This stores settings only and points at your payload files by path; "
                       + "save as .msimaker to bundle the installer in so it works on another machine.");
            }
        }
        catch (Exception ex)
        {
            Status($"Could not save blueprint: {ex.Message}");
        }
    }
}
