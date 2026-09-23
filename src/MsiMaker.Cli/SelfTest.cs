using System.Runtime.InteropServices;
using MsiMaker.Core.Build;
using MsiMaker.Core.Intune;
using MsiMaker.Core.Model;
using MsiMaker.Core.Msi;
using MsiMaker.Core.Payload;
using MsiMaker.Core.Signing;

namespace MsiMaker.Cli;

/// <summary>
/// Checks that the engine works on the machine it is running on.
///
/// The unit tests prove the pieces; this proves the whole thing end to end on a
/// real filesystem, which is what matters when the claim is "this builds MSIs on
/// macOS too". It runs the same way on every platform, so CI can use one command
/// everywhere and the difference between platforms shows up as a difference in
/// the output rather than in the script.
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "msimaker-selftest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        int failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!ok) failures++;
        }

        Console.WriteLine($"MsiMaker self-test on {RuntimeInformation.OSDescription} "
                          + $"({RuntimeInformation.ProcessArchitecture})");
        Console.WriteLine($"working in {dir}");
        Console.WriteLine();

        try
        {
            // --- a payload big enough to span the cabinet's block size
            string payloadDir = Path.Combine(dir, "payload");
            Directory.CreateDirectory(Path.Combine(payloadDir, "sub"));
            File.WriteAllText(Path.Combine(payloadDir, "readme.txt"), "self test payload");
            File.WriteAllText(Path.Combine(payloadDir, "sub", "nested.txt"), "nested");

            byte[] large = new byte[200_000];
            new Random(1234).NextBytes(large);
            File.WriteAllBytes(Path.Combine(payloadDir, "large.bin"), large);

            var project = new MsiProject
            {
                ProductName = "MsiMaker Self Test",
                Manufacturer = "MsiMaker",
                Version = "1.0.0",
                UpgradeCode = new Guid("2f8e4c11-9a3b-4d07-8e52-1c6f7a9b0d23"),
                Folders = { new PayloadFolder { SourceDir = payloadDir } },
                Registry = { new RegistryValue { Key = @"SOFTWARE\MsiMaker\SelfTest", Name = "Version", Value = "1.0.0" } },
            };

            // --- build
            string msi = Path.Combine(dir, "selftest-1.0.0.msi");
            var built = MsiBuilder.Build(project, msi);
            Check("builds an MSI", File.Exists(msi), $"{built.SizeBytes:N0} bytes");

            // --- read it back with our own reader
            var db = MsiDatabase.Load(msi);
            Check("per-machine", db.GetProperty("ALLUSERS") == "1");
            Check("ProductCode derived", db.GetProperty("ProductCode") == DeterministicGuid.Format(built.ProductCode));
            Check("cabinet embedded", db.Streams.Count == 1, string.Join(",", db.Streams.Keys));
            Check("folder payload expanded", db.Tables["File"].Rows.Count == 3,
                  $"{db.Tables["File"].Rows.Count} files");

            var sequence = db.Tables["InstallExecuteSequence"];
            int rep = (int)sequence.Rows.Single(r => (string?)r[0] == "RemoveExistingProducts")[2]!;
            Check("RemoveExistingProducts scheduled early", rep == 1501, $"at {rep}");

            // --- round-trip, which exercises the whole writer again
            string rewritten = Path.Combine(dir, "rewritten.msi");
            File.WriteAllBytes(rewritten, db.Write());
            var reread = MsiDatabase.Load(rewritten);
            Check("round-trips", reread.Tables.Count == db.Tables.Count
                  && reread.GetProperty("ProductCode") == db.GetProperty("ProductCode"));

            // --- the upgrade rules, which are the point of the tool
            var next = MsiProject.FromJson(project.ToJson());
            next.Version = "1.1.0";
            next.Folders.Clear();
            next.Folders.Add(new PayloadFolder { SourceDir = payloadDir });
            string upgraded = Path.Combine(dir, "selftest-1.1.0.msi");
            MsiBuilder.Build(next, upgraded);

            var report = UpgradeDoctor.Compare(msi, upgraded);
            Check("upgrade doctor approves a clean bump",
                  report.Verdict == UpgradeVerdict.UpgradesInPlace, report.Summary);

            // --- bundle portability
            string bundle = Path.Combine(dir, "selftest" + ProjectBundle.Extension);
            var info = ProjectBundle.Save(MsiProject.FromJson(project.ToJson()), bundle);
            var reopened = ProjectBundle.Open(bundle, Path.Combine(dir, "unpacked"));
            Check("bundles and reopens", info.FileCount == 3 && reopened.Files.Count == 3);

            // --- .intunewin
            string intunewin = Path.Combine(dir, "selftest.intunewin");
            var packaged = IntuneWinPackager.PackageMsi(msi, intunewin);
            Check("writes .intunewin", File.Exists(intunewin), $"{packaged.EncryptedSize:N0} bytes");

            // --- payload inspection
            var analysis = InstallerDetective.Analyse(msi);
            Check("detective recognises an MSI", analysis.Kind == InstallerKind.Msi);

            // --- signing availability is reported, not assumed
            string? signer = CodeSigner.FindSigner();
            Console.WriteLine();
            Console.WriteLine($"  signer: {signer ?? "(none installed - signing unavailable on this machine)"}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] unhandled: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* leave it */ }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "self-test passed"
            : $"self-test FAILED with {failures} problem(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Reports what this machine can and cannot do, without changing anything.</summary>
    public static int Doctor()
    {
        Console.WriteLine($"OS            {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Architecture  {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Runtime       {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine();

        Console.WriteLine("Building MSIs");
        Console.WriteLine("  supported    yes - the engine is pure managed and needs nothing installed");
        Console.WriteLine();

        string? signer = CodeSigner.FindSigner();
        Console.WriteLine("Code signing");
        if (signer is null)
        {
            Console.WriteLine("  signer       NOT FOUND - packages will build but cannot be signed");
            Console.WriteLine(OperatingSystem.IsWindows()
                ? "  install      the Windows SDK (signtool.exe), or osslsigncode"
                : "  install      osslsigncode (brew install osslsigncode, or apt install osslsigncode)");
        }
        else
        {
            bool ossl = Path.GetFileNameWithoutExtension(signer)
                .Equals("osslsigncode", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  signer       {signer}");
            Console.WriteLine($"  modes        PFX file{(ossl ? "" : ", Windows certificate store, Azure Trusted Signing")}");
            if (ossl)
                Console.WriteLine("  note         osslsigncode signs from a PFX only; the certificate store and "
                                  + "Azure Trusted Signing both need signtool, so they are Windows-only");
        }
        Console.WriteLine();

        Console.WriteLine("Installing and testing the packages you build");
        Console.WriteLine(OperatingSystem.IsWindows()
            ? "  msiexec      available - packages can be installed, upgraded and uninstalled here"
            : "  msiexec      not available - an MSI built here has to be tested on Windows");
        return 0;
    }
}
