using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MsiMaker.Core.Build;
using MsiMaker.Core.Intune;
using MsiMaker.Core.Model;
using MsiMaker.Core.Payload;
using MsiMaker.Core.Recipes;
using MsiMaker.Core.Signing;

namespace MsiMaker.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class FileRow(PayloadFile file) : ObservableObject
{
    public PayloadFile Model { get; } = file;
    public string Source => Model.SourcePath;
    public string Target => Model.EffectiveTarget;
    public string Size =>
        File.Exists(Model.SourcePath) ? $"{new FileInfo(Model.SourcePath).Length:N0} bytes" : "missing";
}

public sealed class RunRow(RunAction action) : ObservableObject
{
    public RunAction Model { get; } = action;

    /// <summary>
    /// Empty rather than null when the action runs a shell command instead of a
    /// payload executable, so the grid has something to show either way.
    /// </summary>
    public string ExePath
    {
        get => Model.ExePath ?? "";
        set { Model.ExePath = string.IsNullOrWhiteSpace(value) ? null : value; Raise(); }
    }

    /// <summary>A shell command, for actions not backed by a payload file.</summary>
    public string Command
    {
        get => Model.Command ?? "";
        set { Model.Command = string.IsNullOrWhiteSpace(value) ? null : value; Raise(); }
    }

    public string Arguments
    {
        get => Model.Arguments;
        set { Model.Arguments = value; Raise(); }
    }

    public RunWhen When
    {
        get => Model.When;
        set { Model.When = value; Raise(); }
    }

    public bool IgnoreExitCode
    {
        get => Model.IgnoreExitCode;
        set { Model.IgnoreExitCode = value; Raise(); }
    }
}

public sealed class CheckRow(UpgradeCheck check)
{
    public string Name { get; } = check.Name;
    public string Detail { get; } = check.Detail;
    public string Mark { get; } = check.Status switch
    {
        CheckStatus.Pass => "PASS",
        CheckStatus.Warn => "WARN",
        _ => "FAIL",
    };

    // Exposed as three booleans rather than a colour string because the grid
    // cell picks its palette through style classes, which honour the theme
    // variant; a hard-coded hex would not.
    public bool IsPass { get; } = check.Status == CheckStatus.Pass;
    public bool IsWarn { get; } = check.Status == CheckStatus.Warn;
    public bool IsFail { get; } = check.Status is not (CheckStatus.Pass or CheckStatus.Warn);
}

/// <summary>
/// One row of the recipe library, and the whole of its detail pane.
///
/// Everything the detail pane needs is surfaced here rather than reached
/// through <see cref="Model"/>, because compiled bindings will not walk into a
/// nullable Core type and the emptiness flags have to be booleans anyway.
/// </summary>
public sealed class RecipeRow(Recipe recipe)
{
    public Recipe Model { get; } = recipe;

    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Vendor => string.IsNullOrWhiteSpace(Model.Vendor) ? "unknown vendor" : Model.Vendor;
    public string Description => Model.Description;
    public string Awkwardness => Model.Awkwardness;

    public string DownloadUrl => Model.DownloadUrl ?? "";
    public bool HasDownloadUrl => !string.IsNullOrWhiteSpace(Model.DownloadUrl);

    /// <summary>Only http and https are ever handed to the shell; see MainWindow.OnOpenRecipeUrl.</summary>
    public bool CanOpenDownloadUrl =>
        Uri.TryCreate(Model.DownloadUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public string DownloadNotes => Model.DownloadNotes ?? "";
    public bool HasDownloadNotes => !string.IsNullOrWhiteSpace(Model.DownloadNotes);

    public string SetupFileHint => Model.SetupFileHint ?? "";
    public bool HasSetupFileHint => !string.IsNullOrWhiteSpace(Model.SetupFileHint);

    public IReadOnlyList<string> Notes => Model.Notes;
    public bool HasNotes => Model.Notes.Count > 0;

    public bool IsVerified => Model.Verified;
    public bool IsUntested => !Model.Verified;

    /// <summary>The badge the list shows. Short enough to sit beside a long product name.</summary>
    public string Badge => Model.Verified ? "VERIFIED" : "UNTESTED";

    public bool WantsFolder => Model.PayloadKind == RecipePayloadKind.Folder;

    /// <summary>
    /// Names the shape of the payload on the button itself. An admin who has
    /// downloaded a SoftPaq and is looking for a file will otherwise open the
    /// picker, find nothing, and conclude the recipe is broken.
    /// </summary>
    public string UseLabel => WantsFolder
        ? "Use this recipe - choose a folder…"
        : "Use this recipe - choose a file…";

    public string PayloadPrompt => Model.PayloadPrompt;

    public string TemplateVersion => Model.Project.Version;

    public string Origin => Model.FromUserFolder
        ? "From your recipes folder, so it replaces any built-in recipe of the same id."
        : "Built into MsiMaker.";
}

public sealed class MainViewModel : ObservableObject
{
    private MsiProject _project = new() { UpgradeCode = Guid.NewGuid() };

    public ObservableCollection<FileRow> Files { get; } = [];
    public ObservableCollection<RunRow> Run { get; } = [];
    public ObservableCollection<CheckRow> Checks { get; } = [];
    public ObservableCollection<RecipeRow> Recipes { get; } = [];

    public MsiProject Project => _project;

    public MainViewModel()
    {
        // Read once at start-up: the library is a handful of embedded resources
        // plus whatever JSON sits in the user folder, and re-reading it on every
        // selection would only make the list flicker.
        _library = RecipeLibrary.Load();
        foreach (var recipe in _library.List()) Recipes.Add(new RecipeRow(recipe));
        SelectedRecipe = Recipes.FirstOrDefault();
    }

    // ------------------------------------------------------------- library

    private readonly RecipeLibrary _library;

    private RecipeRow? _selectedRecipe;
    public RecipeRow? SelectedRecipe
    {
        get => _selectedRecipe;
        set { if (Set(ref _selectedRecipe, value)) Raise(nameof(HasSelectedRecipe)); }
    }

    public bool HasSelectedRecipe => _selectedRecipe is not null;

    /// <summary>Where a user's own recipe JSON is read from, shown so it can be found.</summary>
    public string RecipeUserDirectory => _library.UserDirectory;

    public bool HasRecipeProblems => _library.Problems.Count > 0;

    /// <summary>Files in the user folder that would not parse. Silence here would be worse.</summary>
    public string RecipeProblems =>
        "Ignored in your recipes folder: " + string.Join("; ", _library.Problems);

    /// <summary>
    /// Replaces the current project with a recipe's template, payload filled in.
    /// </summary>
    public void UseRecipe(Recipe recipe, string payloadPath) =>
        LoadProject(RecipeLibrary.Instantiate(recipe, payloadPath));

    // ------------------------------------------------------------- identity

    public string ProductName
    {
        get => _project.ProductName;
        set { _project.ProductName = value; Raise(); Raise(nameof(CanBuild)); }
    }

    public string Manufacturer
    {
        get => _project.Manufacturer;
        set { _project.Manufacturer = value; Raise(); Raise(nameof(CanBuild)); }
    }

    public string Version
    {
        get => _project.Version;
        set
        {
            _project.Version = value;
            Raise();
            Raise(nameof(VersionNote));
            Raise(nameof(CanBuild));
        }
    }

    /// <summary>Spells out the three-field rule the moment a fourth field appears.</summary>
    public string VersionNote
    {
        get
        {
            var parts = (_project.Version ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries);
            string comparable = _project.ComparableVersion;
            if (parts.Length > 3)
                return $"Windows Installer compares only '{comparable}'. The fourth field is ignored, "
                       + "so a change there alone will not trigger an upgrade.";
            return $"Upgrades are decided on '{comparable}'.";
        }
    }

    public string UpgradeCode
    {
        get => _project.UpgradeCode.ToString();
        set
        {
            if (Guid.TryParse(value, out var g)) { _project.UpgradeCode = g; Raise(); Raise(nameof(ProductCodePreview)); }
        }
    }

    /// <summary>The ProductCode is derived, so it is shown rather than edited.</summary>
    public string ProductCodePreview => DeterministicGuid.Format(_project.EffectiveProductCode);

    public string InstallFolder
    {
        get => _project.InstallFolder ?? "";
        set { _project.InstallFolder = value; Raise(); }
    }

    public Array Platforms => Enum.GetValues<MsiPlatform>();

    public MsiPlatform Platform
    {
        get => _project.Platform;
        set { _project.Platform = value; Raise(); }
    }

    public Array RunWhenValues => Enum.GetValues<RunWhen>();

    // -------------------------------------------------------------- outputs

    public bool EmitIntuneWin
    {
        get => _project.Output.IntuneWin;
        set { _project.Output.IntuneWin = value; Raise(); }
    }

    private string _outputPath = "";
    public string OutputPath { get => _outputPath; set => Set(ref _outputPath, value); }

    // -------------------------------------------------------------- signing

    private SigningOptions Signing => _project.Signing;

    public bool SignEnabled
    {
        get => Signing.Enabled;
        set { Signing.Enabled = value; Raise(); }
    }

    /// <summary>
    /// Friendly names for <see cref="SigningMode"/>. The enum spellings are what
    /// goes in the project file and what the CLI takes; they are not what an
    /// admin should have to read out of a drop-down.
    /// </summary>
    public IReadOnlyList<string> SigningModeNames { get; } =
        ["PFX file", "Windows certificate store", "Azure Trusted Signing"];

    public string SigningModeName
    {
        get => Signing.ResolveMode() switch
        {
            SigningMode.CertificateStore => SigningModeNames[1],
            SigningMode.AzureTrustedSigning => SigningModeNames[2],
            _ => SigningModeNames[0],
        };
        set
        {
            // Written through to Mode rather than left to be inferred, so the
            // choice survives a save and a reload even before any field is filled.
            Signing.Mode = value == SigningModeNames[1] ? SigningMode.CertificateStore
                         : value == SigningModeNames[2] ? SigningMode.AzureTrustedSigning
                         : SigningMode.Pfx;
            Raise();
            Raise(nameof(IsPfxMode));
            Raise(nameof(IsStoreMode));
            Raise(nameof(IsAzureMode));
        }
    }

    public bool IsPfxMode => Signing.ResolveMode() == SigningMode.Pfx;
    public bool IsStoreMode => Signing.ResolveMode() == SigningMode.CertificateStore;
    public bool IsAzureMode => Signing.ResolveMode() == SigningMode.AzureTrustedSigning;

    public string CertificatePath
    {
        get => Signing.CertificatePath ?? "";
        set { Signing.CertificatePath = Blank(value); Raise(); }
    }

    public string PasswordEnvironmentVariable
    {
        get => Signing.PasswordEnvironmentVariable ?? "";
        set { Signing.PasswordEnvironmentVariable = Blank(value); Raise(); }
    }

    public string Thumbprint
    {
        get => Signing.Thumbprint ?? "";
        set { Signing.Thumbprint = Blank(value); Raise(); }
    }

    public string AzureEndpoint
    {
        get => Signing.AzureEndpoint ?? "";
        set { Signing.AzureEndpoint = Blank(value); Raise(); }
    }

    public string AzureCodeSigningAccountName
    {
        get => Signing.AzureCodeSigningAccountName ?? "";
        set { Signing.AzureCodeSigningAccountName = Blank(value); Raise(); }
    }

    public string AzureCertificateProfileName
    {
        get => Signing.AzureCertificateProfileName ?? "";
        set { Signing.AzureCertificateProfileName = Blank(value); Raise(); }
    }

    public string AzureDlibPath
    {
        get => Signing.AzureDlibPath ?? "";
        set { Signing.AzureDlibPath = Blank(value); Raise(); }
    }

    public string TimestampUrl
    {
        get => Signing.TimestampUrl;
        set { Signing.TimestampUrl = value; Raise(); }
    }

    /// <summary>An empty box means "unset", not an empty string the validator would trip over.</summary>
    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private string? _signerPath;
    private bool _signerChecked;

    /// <summary>
    /// Deferred because finding signtool means walking the Windows Kits tree,
    /// which is not worth doing on every start-up for a feature most projects
    /// never enable. The Build tab is realised on first view, so this runs then.
    /// </summary>
    private void EnsureSignerChecked()
    {
        if (_signerChecked) return;
        _signerChecked = true;
        try { _signerPath = CodeSigner.FindSigner(Signing.SignerPath); }
        catch (Exception) { _signerPath = null; }
    }

    public bool SignerFound
    {
        get { EnsureSignerChecked(); return _signerPath is not null; }
    }

    public string SignerBadge => SignerFound ? "FOUND" : "MISSING";

    public string SignerStatus
    {
        get
        {
            EnsureSignerChecked();
            return _signerPath
                ?? "No signer is installed. Signing needs signtool.exe from the Windows SDK, or "
                   + "osslsigncode on any platform. Until one is present every build with signing "
                   + "enabled will report SIGNING FAILED and leave the MSI unsigned.";
        }
    }

    /// <summary>Re-runs discovery, for the admin who has just installed the SDK.</summary>
    public void RecheckSigner()
    {
        _signerChecked = false;
        _signerPath = null;
        Raise(nameof(SignerFound));
        Raise(nameof(SignerBadge));
        Raise(nameof(SignerStatus));
    }

    private string _log = "";
    public string Log { get => _log; set => Set(ref _log, value); }

    public void Append(string line) => Log += line + Environment.NewLine;

    public bool CanBuild =>
        !string.IsNullOrWhiteSpace(ProductName)
        && !string.IsNullOrWhiteSpace(Manufacturer)
        && Files.Count > 0;

    // ------------------------------------------------------------- payload

    private string _analysisSummary = "No payload analysed yet.";
    public string AnalysisSummary { get => _analysisSummary; set => Set(ref _analysisSummary, value); }

    private string _analysisDetail = "";
    public string AnalysisDetail { get => _analysisDetail; set => Set(ref _analysisDetail, value); }

    private bool _hasAnalysis;
    public bool HasAnalysis { get => _hasAnalysis; set => Set(ref _hasAnalysis, value); }

    /// <summary>Drives the payload tab's empty state.</summary>
    public bool HasFiles => Files.Count > 0;

    /// <summary>
    /// A .exe or .msi is worth handing to the detective; anything else is just
    /// payload sitting beside it.
    /// </summary>
    public static bool LooksLikeInstaller(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase);
    }

    public void AddFile(string path)
    {
        var file = new PayloadFile { SourcePath = path };
        _project.Files.Add(file);
        Files.Add(new FileRow(file));
        Raise(nameof(CanBuild));
        Raise(nameof(HasFiles));
    }

    public void RemoveFile(FileRow row)
    {
        _project.Files.Remove(row.Model);
        Files.Remove(row);
        Raise(nameof(CanBuild));
        Raise(nameof(HasFiles));
    }

    /// <summary>
    /// Runs the detective over an executable and seeds identity and switches
    /// from what it finds.
    /// </summary>
    public void Analyse(string path)
    {
        var a = InstallerDetective.Analyse(path);
        HasAnalysis = true;
        AnalysisSummary = $"{a.KindName}  -  confidence {a.Confidence}%";

        var detail = new System.Text.StringBuilder();
        if (a.Version is not null)
        {
            detail.AppendLine($"Publisher   {a.Version.CompanyName ?? "-"}");
            detail.AppendLine($"Product     {a.Version.ProductName ?? "-"}");
            detail.AppendLine($"Version     {a.Version.ProductVersion ?? a.Version.FileVersion ?? "-"}");
        }
        detail.AppendLine($"Architecture {a.Architecture}");
        detail.AppendLine();
        detail.AppendLine($"Silent switches: {a.SilentArguments ?? "(unknown)"}");
        if (a.SilentArgumentsWithDirectory is not null)
            detail.AppendLine($"With directory:  {a.SilentArgumentsWithDirectory}");
        detail.AppendLine();
        foreach (var e in a.Evidence) detail.AppendLine($"Evidence: {e}");
        foreach (var w in a.Warnings) detail.AppendLine($"Warning:  {w}");
        AnalysisDetail = detail.ToString();

        if (string.IsNullOrWhiteSpace(ProductName))
            ProductName = a.Version?.ProductName ?? Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(Manufacturer))
            Manufacturer = a.Version?.CompanyName ?? "";
        if (Version is "1.0.0" or "")
            Version = InstallerDetective.NormaliseVersion(a.Version?.ProductVersion ?? a.Version?.FileVersion);

        Platform = a.Architecture switch
        {
            PeArchitecture.X86 => MsiPlatform.X86,
            PeArchitecture.Arm64 => MsiPlatform.Arm64,
            _ => MsiPlatform.X64,
        };

        if (!Files.Any(f => string.Equals(f.Source, path, StringComparison.OrdinalIgnoreCase)))
            AddFile(path);

        string fileName = Path.GetFileName(path);
        if (!Run.Any(r => string.Equals(r.ExePath, fileName, StringComparison.OrdinalIgnoreCase)))
            AddRun(new RunAction
            {
                Id = Path.GetFileNameWithoutExtension(path),
                ExePath = fileName,
                Arguments = a.SilentArguments ?? "",
                When = RunWhen.Install,
            });
    }

    public void AddRun(RunAction action)
    {
        _project.Run.Add(action);
        Run.Add(new RunRow(action));
    }

    public void RemoveRun(RunRow row)
    {
        _project.Run.Remove(row.Model);
        Run.Remove(row);
    }

    // -------------------------------------------------------- upgrade check

    private string _previousMsi = "";
    public string PreviousMsi { get => _previousMsi; set => Set(ref _previousMsi, value); }

    private string _verdict = "";
    public string Verdict { get => _verdict; set => Set(ref _verdict, value); }

    private bool _verdictIsGood;
    public bool VerdictIsGood { get => _verdictIsGood; set => Set(ref _verdictIsGood, value); }

    /// <summary>True once a comparison has run, so the tab can show a result instead of an empty grid.</summary>
    public bool HasChecks => Checks.Count > 0;

    public void RunUpgradeCheck(string previous, string candidate)
    {
        Checks.Clear();
        var report = UpgradeDoctor.Compare(previous, candidate);
        foreach (var c in report.Checks) Checks.Add(new CheckRow(c));
        Verdict = report.Summary;
        VerdictIsGood = report.Verdict == UpgradeVerdict.UpgradesInPlace;
        Raise(nameof(HasChecks));
    }

    // --------------------------------------------------------------- build

    public BuildResult Build(string outputPath)
    {
        var result = MsiBuilder.Build(_project, outputPath);
        Raise(nameof(ProductCodePreview));
        return result;
    }

    public void LoadProject(MsiProject project)
    {
        _project = project;

        // Folder payloads are expanded at build time, so without this a project
        // that installs a directory - an extracted SoftPaq, say - opens looking
        // as though it has no payload at all.
        _project.ExpandFolders();

        Files.Clear();
        foreach (var f in project.Files) Files.Add(new FileRow(f));
        Run.Clear();
        foreach (var r in project.Run) Run.Add(new RunRow(r));
        foreach (var name in new[]
        {
            nameof(ProductName), nameof(Manufacturer), nameof(Version), nameof(UpgradeCode),
            nameof(ProductCodePreview), nameof(InstallFolder), nameof(Platform),
            nameof(EmitIntuneWin), nameof(VersionNote), nameof(CanBuild), nameof(HasFiles),

            // Signing lives on the project too, so a loaded project - or a
            // recipe template - has to repaint the whole signing section.
            nameof(SignEnabled), nameof(SigningModeName), nameof(IsPfxMode), nameof(IsStoreMode),
            nameof(IsAzureMode), nameof(CertificatePath), nameof(PasswordEnvironmentVariable),
            nameof(Thumbprint), nameof(AzureEndpoint), nameof(AzureCodeSigningAccountName),
            nameof(AzureCertificateProfileName), nameof(AzureDlibPath), nameof(TimestampUrl),
        }) Raise(name);
    }
}
