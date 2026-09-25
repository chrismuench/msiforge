using MsiForge.Core.Msi;

namespace MsiForge.Core.Intune;

public enum UpgradeVerdict
{
    /// <summary>Windows Installer will remove the old version and install the new one.</summary>
    UpgradesInPlace,

    /// <summary>Both versions end up installed side by side.</summary>
    InstallsAlongside,

    /// <summary>Intune will consider the app already installed and do nothing.</summary>
    DoesNothing,

    /// <summary>The install will fail or be blocked.</summary>
    Fails,
}

public enum CheckStatus { Pass, Warn, Fail }

public sealed record UpgradeCheck(string Name, CheckStatus Status, string Detail);

public sealed class UpgradeReport
{
    public required string PreviousPath { get; init; }
    public required string CandidatePath { get; init; }
    public UpgradeVerdict Verdict { get; set; }
    public List<UpgradeCheck> Checks { get; } = [];

    public string Summary => Verdict switch
    {
        UpgradeVerdict.UpgradesInPlace => "Intune WILL upgrade in place.",
        UpgradeVerdict.InstallsAlongside => "Both versions will end up installed SIDE BY SIDE.",
        UpgradeVerdict.DoesNothing => "Intune will see the app as already installed and DO NOTHING.",
        UpgradeVerdict.Fails => "The install will FAIL.",
        _ => "Unknown.",
    };
}

/// <summary>
/// Compares a candidate package against the version already deployed and says
/// plainly whether Intune will actually upgrade.
///
/// Most failed Intune upgrades are not Intune's doing: they are a ProductCode
/// that did not change, a version that differs only in its fourth field, or a
/// platform that moved between builds. Every one of those is visible in the two
/// packages before anything is deployed.
/// </summary>
public static class UpgradeDoctor
{
    public static UpgradeReport Compare(string previousPath, string candidatePath)
    {
        var previous = MsiDatabase.Load(previousPath);
        var candidate = MsiDatabase.Load(candidatePath);
        return Compare(previous, candidate, previousPath, candidatePath);
    }

    public static UpgradeReport Compare(MsiDatabase previous, MsiDatabase candidate,
                                        string previousPath, string candidatePath)
    {
        var report = new UpgradeReport { PreviousPath = previousPath, CandidatePath = candidatePath };
        void Check(string name, CheckStatus status, string detail) =>
            report.Checks.Add(new UpgradeCheck(name, status, detail));

        string? oldUpgrade = previous.GetProperty("UpgradeCode");
        string? newUpgrade = candidate.GetProperty("UpgradeCode");
        string? oldProduct = previous.GetProperty("ProductCode");
        string? newProduct = candidate.GetProperty("ProductCode");
        string oldVersion = previous.GetProperty("ProductVersion") ?? "0";
        string newVersion = candidate.GetProperty("ProductVersion") ?? "0";

        bool fatal = false;
        bool sameLineage = string.Equals(oldUpgrade, newUpgrade, StringComparison.OrdinalIgnoreCase);

        // ---- UpgradeCode: what ties the two versions together.
        if (string.IsNullOrWhiteSpace(newUpgrade))
        {
            Check("UpgradeCode", CheckStatus.Fail, "The candidate has no UpgradeCode, so nothing links it to the installed version.");
            fatal = true;
        }
        else if (sameLineage)
        {
            Check("UpgradeCode", CheckStatus.Pass, $"Unchanged ({newUpgrade}).");
        }
        else
        {
            Check("UpgradeCode", CheckStatus.Fail,
                  $"Changed: {oldUpgrade} -> {newUpgrade}. The old version will not be removed.");
        }

        // ---- ProductCode: must be new for every version.
        if (string.Equals(oldProduct, newProduct, StringComparison.OrdinalIgnoreCase))
        {
            Check("ProductCode", CheckStatus.Fail,
                  $"Identical ({newProduct}). Intune detects on ProductCode, so it will report the app "
                  + "as already installed and never deploy the new build.");
            fatal = true;
        }
        else
        {
            Check("ProductCode", CheckStatus.Pass, $"Changed: {oldProduct} -> {newProduct}.");
        }

        // ---- ProductVersion: only the first three fields are compared.
        var (oldCompare, oldHadFourth) = ThreeFields(oldVersion);
        var (newCompare, newHadFourth) = ThreeFields(newVersion);
        int cmp = CompareVersions(oldCompare, newCompare);

        if (cmp < 0)
        {
            Check("ProductVersion", CheckStatus.Pass, $"{oldVersion} -> {newVersion} (increases).");
        }
        else if (cmp == 0 && (oldHadFourth || newHadFourth) && oldVersion != newVersion)
        {
            Check("ProductVersion", CheckStatus.Fail,
                  $"{oldVersion} -> {newVersion} differ only in the fourth field. Windows Installer "
                  + $"compares '{oldCompare}' against '{newCompare}' and sees no change, so no upgrade happens.");
            fatal = true;
        }
        else if (cmp == 0)
        {
            Check("ProductVersion", CheckStatus.Warn,
                  $"Unchanged ({newVersion}). Only a same-version reinstall is possible.");
        }
        else
        {
            Check("ProductVersion", CheckStatus.Fail,
                  $"{oldVersion} -> {newVersion} is a downgrade and will be blocked.");
            fatal = true;
        }

        // ---- Platform: a package cannot upgrade across architectures.
        string oldTemplate = (previous.Summary.Template ?? "").Split(';').FirstOrDefault() ?? "";
        string newTemplate = (candidate.Summary.Template ?? "").Split(';').FirstOrDefault() ?? "";
        if (string.Equals(oldTemplate, newTemplate, StringComparison.OrdinalIgnoreCase))
            Check("Platform", CheckStatus.Pass, $"Unchanged ({newTemplate}).");
        else
        {
            Check("Platform", CheckStatus.Fail,
                  $"Changed: {oldTemplate} -> {newTemplate}. Windows Installer will not upgrade across platforms.");
            fatal = true;
        }

        // ---- Install scope: Intune installs as SYSTEM.
        string? scope = candidate.GetProperty("ALLUSERS");
        if (scope == "1")
            Check("Install scope", CheckStatus.Pass, "Per-machine (ALLUSERS=1).");
        else
            Check("Install scope", CheckStatus.Fail,
                  "Not per-machine. Intune installs as SYSTEM, so a per-user package lands in the "
                  + "service account's profile and is invisible to the user.");

        // ---- Package code must be unique per build.
        string? oldPackage = previous.Summary.RevisionNumber;
        string? newPackage = candidate.Summary.RevisionNumber;
        Check("Package code", string.Equals(oldPackage, newPackage, StringComparison.OrdinalIgnoreCase)
                ? CheckStatus.Fail : CheckStatus.Pass,
              string.Equals(oldPackage, newPackage, StringComparison.OrdinalIgnoreCase)
                ? $"Identical ({newPackage}). Every build needs a fresh package code or caching misbehaves."
                : "Unique to this build.");

        // ---- Upgrade table must actually cover the installed version.
        bool covers = false;
        if (candidate.Tables.TryGetValue("Upgrade", out var upgrade))
        {
            foreach (var row in upgrade.Rows)
            {
                if (!string.Equals(row[0] as string, oldUpgrade, StringComparison.OrdinalIgnoreCase)) continue;
                int attributes = row[4] as int? ?? 0;
                if ((attributes & 2) != 0) continue; // msidbUpgradeAttributesOnlyDetect

                string? min = row[1] as string;
                string? max = row[2] as string;
                bool minOk = string.IsNullOrEmpty(min) || CompareVersions(min, oldCompare) <= 0;
                bool maxOk = string.IsNullOrEmpty(max)
                             || CompareVersions(oldCompare, max) < 0
                             || ((attributes & 512) != 0 && CompareVersions(oldCompare, max) == 0);
                if (minOk && maxOk) { covers = true; break; }
            }
            Check("Upgrade table", covers ? CheckStatus.Pass : CheckStatus.Fail,
                  covers ? $"A removal row covers the installed version {oldCompare}."
                         : $"No removal row covers version {oldCompare}, so the old build stays installed.");
        }
        else
        {
            Check("Upgrade table", CheckStatus.Fail, "Absent. Nothing will remove the previous version.");
        }

        // ---- RemoveExistingProducts scheduling.
        if (candidate.Tables.TryGetValue("InstallExecuteSequence", out var sequence))
        {
            var rep = sequence.Rows.FirstOrDefault(r => (r[0] as string) == "RemoveExistingProducts");
            var init = sequence.Rows.FirstOrDefault(r => (r[0] as string) == "InstallInitialize");
            int repSeq = rep?[2] as int? ?? -1;
            int initSeq = init?[2] as int? ?? -1;

            if (rep is null)
                Check("RemoveExistingProducts", CheckStatus.Fail, "Missing from InstallExecuteSequence.");
            else if (repSeq > initSeq && repSeq < 1600)
                Check("RemoveExistingProducts", CheckStatus.Pass,
                      $"Scheduled at {repSeq}, just after InstallInitialize ({initSeq}).");
            else
                Check("RemoveExistingProducts", CheckStatus.Warn,
                      $"Scheduled at {repSeq}. Placing it immediately after InstallInitialize ({initSeq}) "
                      + "keeps the old files from being removed after the new ones are laid down.");
        }

        // ---- Verdict.
        bool anyFail = report.Checks.Any(c => c.Status == CheckStatus.Fail);
        report.Verdict =
            fatal && cmp > 0 ? UpgradeVerdict.Fails
            : string.Equals(oldProduct, newProduct, StringComparison.OrdinalIgnoreCase) ? UpgradeVerdict.DoesNothing
            : cmp == 0 && (oldHadFourth || newHadFourth) && oldVersion != newVersion ? UpgradeVerdict.DoesNothing
            : !sameLineage || !covers ? UpgradeVerdict.InstallsAlongside
            : anyFail ? UpgradeVerdict.InstallsAlongside
            : UpgradeVerdict.UpgradesInPlace;

        return report;
    }

    /// <summary>The three fields Windows Installer compares, and whether a fourth was present.</summary>
    private static (string Compare, bool HadFourth) ThreeFields(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int Field(int i) => i < parts.Length && int.TryParse(parts[i], out int v) ? v : 0;
        return ($"{Field(0)}.{Field(1)}.{Field(2)}", parts.Length > 3);
    }

    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (int i = 0; i < 3; i++)
        {
            int va = i < pa.Length && int.TryParse(pa[i], out int x) ? x : 0;
            int vb = i < pb.Length && int.TryParse(pb[i], out int y) ? y : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }
}
