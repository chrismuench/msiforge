using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MsiMaker.Core.Build;
using MsiMaker.Core.Intune;
using MsiMaker.Core.Model;
using MsiMaker.Core.Payload;

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

    public string ExePath
    {
        get => Model.ExePath;
        set { Model.ExePath = value; Raise(); }
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
    public string Colour { get; } = check.Status switch
    {
        CheckStatus.Pass => "#2E7D32",
        CheckStatus.Warn => "#B26A00",
        _ => "#C62828",
    };
}

public sealed class MainViewModel : ObservableObject
{
    private MsiProject _project = new() { UpgradeCode = Guid.NewGuid() };

    public ObservableCollection<FileRow> Files { get; } = [];
    public ObservableCollection<RunRow> Run { get; } = [];
    public ObservableCollection<CheckRow> Checks { get; } = [];

    public MsiProject Project => _project;

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

    public bool UploadToIntune
    {
        get => _project.Output.UploadToIntune;
        set { _project.Output.UploadToIntune = value; Raise(); }
    }

    private string _outputPath = "";
    public string OutputPath { get => _outputPath; set => Set(ref _outputPath, value); }

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

    public void AddFile(string path)
    {
        var file = new PayloadFile { SourcePath = path };
        _project.Files.Add(file);
        Files.Add(new FileRow(file));
        Raise(nameof(CanBuild));
    }

    public void RemoveFile(FileRow row)
    {
        _project.Files.Remove(row.Model);
        Files.Remove(row);
        Raise(nameof(CanBuild));
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

    private string _verdictColour = "#666666";
    public string VerdictColour { get => _verdictColour; set => Set(ref _verdictColour, value); }

    public void RunUpgradeCheck(string previous, string candidate)
    {
        Checks.Clear();
        var report = UpgradeDoctor.Compare(previous, candidate);
        foreach (var c in report.Checks) Checks.Add(new CheckRow(c));
        Verdict = report.Summary;
        VerdictColour = report.Verdict == UpgradeVerdict.UpgradesInPlace ? "#2E7D32" : "#C62828";
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
        Files.Clear();
        foreach (var f in project.Files) Files.Add(new FileRow(f));
        Run.Clear();
        foreach (var r in project.Run) Run.Add(new RunRow(r));
        foreach (var name in new[]
        {
            nameof(ProductName), nameof(Manufacturer), nameof(Version), nameof(UpgradeCode),
            nameof(ProductCodePreview), nameof(InstallFolder), nameof(Platform),
            nameof(EmitIntuneWin), nameof(UploadToIntune), nameof(VersionNote), nameof(CanBuild),
        }) Raise(name);
    }
}
