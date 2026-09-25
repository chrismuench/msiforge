using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using MsiForge.Core.Msi;

namespace MsiForge.Core.Intune;

public sealed record IntuneWinResult(string Path, long EncryptedSize, long PlainSize, string SetupFile);

/// <summary>
/// Builds Win32 .intunewin packages.
///
/// The format is a plain zip holding an encrypted payload plus a Detection.xml
/// describing it, so it is produced here directly rather than by shelling out to
/// IntuneWinAppUtil.exe - which means it works on macOS too.
///
/// Layout:
///   IntuneWinPackage/Metadata/Detection.xml
///   IntuneWinPackage/Contents/IntunePackage.intunewin   (HMAC | IV | AES-CBC ciphertext)
/// </summary>
public static class IntuneWinPackager
{
    private const string ProfileIdentifier = "ProfileVersion1";

    /// <summary>Packages a single MSI. The MSI becomes the setup file.</summary>
    public static IntuneWinResult PackageMsi(string msiPath, string outputPath)
    {
        var db = MsiDatabase.Load(msiPath);
        string name = db.GetProperty("ProductName") ?? Path.GetFileNameWithoutExtension(msiPath);
        return Package(
            [(Path.GetFileName(msiPath), File.ReadAllBytes(msiPath))],
            setupFile: Path.GetFileName(msiPath),
            name: name,
            outputPath: outputPath,
            msiInfo: MsiInfo.From(db));
    }

    /// <summary>Packages an arbitrary set of files.</summary>
    public static IntuneWinResult Package(
        IReadOnlyList<(string RelativePath, byte[] Content)> files,
        string setupFile,
        string name,
        string outputPath,
        MsiInfo? msiInfo = null)
    {
        if (files.Count == 0) throw new ArgumentException("An .intunewin package needs at least one file.");

        // 1. Zip the payload. This is what Intune decrypts and unpacks on the device.
        byte[] payloadZip;
        using (var ms = new MemoryStream())
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (relativePath, content) in files)
                {
                    var entry = archive.CreateEntry(relativePath.Replace('\\', '/'), CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    stream.Write(content);
                }
            payloadZip = ms.ToArray();
        }

        // 2. Encrypt: AES-256-CBC, then an HMAC-SHA256 over IV+ciphertext prepended
        //    to the whole thing. Intune verifies the MAC before decrypting.
        byte[] encryptionKey = RandomNumberGenerator.GetBytes(32);
        byte[] macKey = RandomNumberGenerator.GetBytes(32);
        byte[] iv = RandomNumberGenerator.GetBytes(16);

        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.Key = encryptionKey;
            // The IV is 16 fresh CSPRNG bytes per package, prepended to the
            // ciphertext and covered by the HMAC below (encrypt-then-MAC). The
            // analyzer flags any explicit IV assignment; omitting it here would
            // mean Aes.Create() picking one we could not then write into the
            // header, which Intune needs in order to decrypt.
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
#pragma warning disable CA5401 // the IV above is per-package CSPRNG output, not a fixed one
            using var encryptor = aes.CreateEncryptor();
#pragma warning restore CA5401
            ciphertext = encryptor.TransformFinalBlock(payloadZip, 0, payloadZip.Length);
        }

        byte[] ivAndCipher = new byte[iv.Length + ciphertext.Length];
        iv.CopyTo(ivAndCipher, 0);
        ciphertext.CopyTo(ivAndCipher, iv.Length);

        byte[] mac = HMACSHA256.HashData(macKey, ivAndCipher);

        byte[] encrypted = new byte[mac.Length + ivAndCipher.Length];
        mac.CopyTo(encrypted, 0);
        ivAndCipher.CopyTo(encrypted, mac.Length);

        byte[] plainDigest = SHA256.HashData(payloadZip);

        // 3. Detection.xml describes the payload and carries the keys.
        var detection = BuildDetectionXml(
            name: name,
            setupFile: setupFile,
            unencryptedSize: payloadZip.Length,
            encryptionKey: encryptionKey,
            macKey: macKey,
            iv: iv,
            mac: mac,
            digest: plainDigest,
            msiInfo: msiInfo);

        // 4. The outer zip, which is the .intunewin file itself.
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using (var output = File.Create(outputPath))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
        {
            var metadata = archive.CreateEntry("IntuneWinPackage/Metadata/Detection.xml", CompressionLevel.Optimal);
            using (var stream = metadata.Open())
                stream.Write(new UTF8Encoding(false).GetBytes(detection));

            // Already encrypted, so compressing again only costs time.
            var contents = archive.CreateEntry("IntuneWinPackage/Contents/IntunePackage.intunewin", CompressionLevel.NoCompression);
            using (var stream = contents.Open())
                stream.Write(encrypted);
        }

        return new IntuneWinResult(outputPath, encrypted.Length, payloadZip.Length, setupFile);
    }

    private static string BuildDetectionXml(
        string name, string setupFile, long unencryptedSize,
        byte[] encryptionKey, byte[] macKey, byte[] iv, byte[] mac, byte[] digest,
        MsiInfo? msiInfo)
    {
        var info = new XElement("ApplicationInfo",
            new XAttribute("ToolVersion", "1.8.6"),
            new XElement("Name", name),
            new XElement("UnencryptedContentSize", unencryptedSize),
            new XElement("FileName", "IntunePackage.intunewin"),
            new XElement("SetupFile", setupFile),
            new XElement("EncryptionInfo",
                new XElement("EncryptionKey", Convert.ToBase64String(encryptionKey)),
                new XElement("MacKey", Convert.ToBase64String(macKey)),
                new XElement("InitializationVector", Convert.ToBase64String(iv)),
                new XElement("Mac", Convert.ToBase64String(mac)),
                new XElement("ProfileIdentifier", ProfileIdentifier),
                new XElement("FileDigest", Convert.ToBase64String(digest)),
                new XElement("FileDigestAlgorithm", "SHA256")));

        if (msiInfo is not null)
            info.Add(new XElement("MsiInfo",
                new XElement("MsiProductCode", msiInfo.ProductCode),
                new XElement("MsiProductVersion", msiInfo.ProductVersion),
                new XElement("MsiPackageCode", msiInfo.PackageCode),
                new XElement("MsiUpgradeCode", msiInfo.UpgradeCode),
                new XElement("MsiExecutionContext", msiInfo.PerMachine ? "System" : "User"),
                new XElement("MsiRequiresLogon", false),
                new XElement("MsiRequiresReboot", false),
                new XElement("MsiIsMachineInstall", msiInfo.PerMachine),
                new XElement("MsiIsUserInstall", !msiInfo.PerMachine),
                new XElement("MsiIncludesServices", false),
                new XElement("MsiContainsSystemRegistryKeys", false),
                new XElement("MsiContainsSystemFolders", false),
                new XElement("MsiPublisher", msiInfo.Manufacturer)));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), info).ToString();
    }
}

/// <summary>The MSI facts Intune records alongside a Win32 package.</summary>
public sealed record MsiInfo(
    string ProductCode,
    string ProductVersion,
    string PackageCode,
    string UpgradeCode,
    string Manufacturer,
    bool PerMachine)
{
    public static MsiInfo From(MsiDatabase db) => new(
        db.GetProperty("ProductCode") ?? "",
        db.GetProperty("ProductVersion") ?? "",
        db.Summary.RevisionNumber ?? "",
        db.GetProperty("UpgradeCode") ?? "",
        db.GetProperty("Manufacturer") ?? "",
        db.GetProperty("ALLUSERS") == "1");
}
