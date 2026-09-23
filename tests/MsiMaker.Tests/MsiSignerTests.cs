using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MsiMaker.Core.Build;
using MsiMaker.Core.Cfbf;
using MsiMaker.Core.Model;
using MsiMaker.Core.Msi;
using MsiMaker.Core.Signing;
using Xunit;

namespace MsiMaker.Tests;

/// <summary>
/// Cover for signing an MSI in process.
///
/// The load-bearing test is <see cref="DigestMatchesTheDigestOsslsigncodeComputed"/>.
/// Everything else here could pass with the digest subtly wrong, which is the
/// failure that matters: a package that looks signed and is rejected on the
/// device. That one test checks our answer against a number produced by an
/// independent signer, so it cannot agree with us by construction.
///
/// The same walk was also checked against three real packages during
/// development - two signed by osslsigncode and one by signtool, the last of
/// which also carries a MsiDigitalSignatureEx prehash - but those packages are
/// not in the repository, so the vector below stands in for them. It is built
/// to stress the two orderings that were actually got wrong first time round.
/// </summary>
public class MsiSignerTests
{
    /// <summary>
    /// What osslsigncode computed over <see cref="VectorStreams"/> written as a
    /// compound file. Not produced by this code: it came out of
    /// <c>osslsigncode sign -h sha256</c> and was read back from the
    /// DigitalSignature stream it embedded.
    /// </summary>
    private const string OsslsigncodeVectorDigest =
        "D380F3ED3CF5C63128B61C34F4074B1B84CD134FC66FB14CCF482C65F63CD3D4";

    /// <summary>
    /// Deliberately awkward. "!File" encodes to the first three code units of
    /// "!FileSFPCatalog" and "ab" to the first of "abcd", so both tie-breaks in
    /// the name comparison are exercised; and the 0x05-prefixed summary stream
    /// sorts where byte order and code-unit order disagree.
    /// </summary>
    private static List<CfbStream> VectorStreams() =>
    [
        new("\u0005SummaryInformation", Enumerable.Range(0, 48).Select(i => (byte)i).ToArray()),
        new(MsiName.EncodeTable("File"), [0x01, 0x02, 0x03, 0x04]),
        new(MsiName.EncodeTable("FileSFPCatalog"), [0x05, 0x06]),
        new(MsiName.EncodeTable("_StringPool"), [0xE9, 0x03, 0x00, 0x00]),
        new(MsiName.EncodeStream("payload.cab"), Enumerable.Range(0, 200).Select(i => (byte)(i * 7)).ToArray()),
        new(MsiName.EncodeStream("ab"), [0x11]),
        new(MsiName.EncodeStream("abcd"), [0x22, 0x33]),
    ];

    private static byte[] VectorFile(IEnumerable<CfbStream>? streams = null) =>
        new CfbWriter(sectorSize: 4096).Write(streams ?? VectorStreams());

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "msimaker-signer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// A throwaway signing certificate, made here rather than taken from the
    /// machine so the tests need nothing installed and leave nothing behind.
    /// </summary>
    private static X509Certificate2 TestCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=MsiMaker Test Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], critical: false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void DigestMatchesTheDigestOsslsigncodeComputed()
    {
        byte[] digest = MsiSigner.ComputeDigest(VectorFile(), HashAlgorithmName.SHA256);

        Assert.Equal(OsslsigncodeVectorDigest, Convert.ToHexString(digest));
    }

    [Fact]
    public void SignatureStreamsAreNotPartOfTheDigestTheySign()
    {
        // Otherwise the digest would depend on the signature it is about to be
        // put inside, and no package could ever verify.
        var withSignature = VectorStreams();
        withSignature.Add(new CfbStream(MsiSigner.DigitalSignatureStream, new byte[3000]));
        withSignature.Add(new CfbStream(MsiSigner.MsiDigitalSignatureExStream, new byte[32]));

        Assert.Equal(
            OsslsigncodeVectorDigest,
            Convert.ToHexString(MsiSigner.ComputeDigest(VectorFile(withSignature), HashAlgorithmName.SHA256)));
    }

    [Fact]
    public void StreamsAreHashedInNulTerminatedNameByteOrder()
    {
        var order = MsiSigner.DigestOrder(VectorStreams()).Select(s => MsiName.Decode(s.Name)).ToList();

        Assert.Equal(
            ["\u0005SummaryInformation", "payload.cab", "!File", "!FileSFPCatalog", "!_StringPool", "ab", "abcd"],
            order);
    }

    [Fact]
    public void RootClsidIsHashedTooSoARelabelledContainerIsADifferentPackage()
    {
        byte[] relabelled = new CfbWriter(sectorSize: 4096)
        {
            RootClsid = new Guid("11111111-2222-3333-4444-555555555555"),
        }.Write(VectorStreams());

        Assert.NotEqual(
            OsslsigncodeVectorDigest,
            Convert.ToHexString(MsiSigner.ComputeDigest(relabelled, HashAlgorithmName.SHA256)));
    }

    [Fact]
    public void ASignedPackageCommitsToTheDigestOfItsOwnContents()
    {
        using var certificate = TestCertificate();
        byte[] signed = MsiSigner.SignBytes(
            VectorFile(), certificate, null, HashAlgorithmName.SHA256, timestampUrl: null, out string? problem);

        Assert.Null(problem);
        byte[]? embedded = MsiSigner.ReadEmbeddedDigest(signed, out string? algorithmOid);

        Assert.Equal("2.16.840.1.101.3.4.2.1", algorithmOid);
        Assert.Equal(OsslsigncodeVectorDigest, Convert.ToHexString(embedded!));
        Assert.Equal(embedded, MsiSigner.ComputeDigest(signed, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void SigningTwiceReplacesTheSignatureRatherThanStackingOne()
    {
        using var certificate = TestCertificate();
        byte[] once = MsiSigner.SignBytes(VectorFile(), certificate, null, HashAlgorithmName.SHA256, null, out _);
        byte[] twice = MsiSigner.SignBytes(once, certificate, null, HashAlgorithmName.SHA256, null, out _);

        Assert.Single(new CfbReader(twice).Streams, s => s.Name == MsiSigner.DigitalSignatureStream);
        Assert.Equal(OsslsigncodeVectorDigest,
            Convert.ToHexString(MsiSigner.ReadEmbeddedDigest(twice, out _)!));
    }

    /// <summary>
    /// The two encoding details Windows insists on and RFC 5652 does not. Both
    /// were found by a package that verified everywhere except on Windows,
    /// which reported 0x8009200D - "not a cryptographic message" - and said
    /// nothing about which part it disliked.
    /// </summary>
    [Fact]
    public void SignatureUsesTheEncodingWindowsAcceptsRatherThanTheOneRfc5652Prescribes()
    {
        using var certificate = TestCertificate();
        byte[] signed = MsiSigner.SignBytes(VectorFile(), certificate, null, HashAlgorithmName.SHA256, null, out _);
        byte[] pkcs7 = new CfbReader(signed).Streams.First(s => s.Name == MsiSigner.DigitalSignatureStream).Data;

        var contentInfo = new AsnReader(pkcs7, AsnEncodingRules.BER).ReadSequence();
        Assert.Equal("1.2.840.113549.1.7.2", contentInfo.ReadObjectIdentifier());

        var signedData = contentInfo
            .ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))
            .ReadSequence();

        // Version 1, not the 3 that a non-id-data content type calls for.
        Assert.Equal(1, signedData.ReadInteger());
        signedData.ReadSetOf();

        var encapsulated = signedData.ReadSequence();
        Assert.Equal(MsiSigner.SpcIndirectDataOid, encapsulated.ReadObjectIdentifier());

        // The content is a bare SEQUENCE, not the OCTET STRING CMS asks for.
        ReadOnlyMemory<byte> content = encapsulated
            .ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))
            .ReadEncodedValue();
        Assert.Equal(0x30, content.Span[0]);
    }

    [Fact]
    public void SignatureNamesTheMsiSubjectInterfaceAndNotAPeImage()
    {
        byte[] digest = RandomNumberGenerator.GetBytes(32);
        byte[] content = MsiSigner.BuildSpcIndirectDataContent(digest, HashAlgorithmName.SHA256);

        // BuildSpcIndirectDataContent returns the SEQUENCE body, so it is read
        // back as the pair of fields it holds rather than as one value.
        var reader = new AsnReader(content, AsnEncodingRules.DER);
        var attribute = reader.ReadSequence();
        Assert.Equal(MsiSigner.SpcSipInfoOid, attribute.ReadObjectIdentifier());

        var sipInfo = attribute.ReadSequence();
        Assert.Equal(1, sipInfo.ReadInteger());
        Assert.Equal(
            "F1100C0000000000C000000000000046",
            Convert.ToHexString(sipInfo.ReadOctetString()));

        var digestInfo = reader.ReadSequence();
        Assert.Equal("2.16.840.1.101.3.4.2.1", digestInfo.ReadSequence().ReadObjectIdentifier());
        Assert.Equal(digest, digestInfo.ReadOctetString());
    }

    [Fact]
    public void AnUnreachableTimestampServerIsACaveatAndNotAFailedSignature()
    {
        using var certificate = TestCertificate();

        // Refused straight away rather than left to time out.
        byte[] signed = MsiSigner.SignBytes(
            VectorFile(), certificate, null, HashAlgorithmName.SHA256,
            "http://127.0.0.1:1/timestamp", out string? problem);

        Assert.NotNull(problem);
        Assert.Equal(OsslsigncodeVectorDigest, Convert.ToHexString(MsiSigner.ReadEmbeddedDigest(signed, out _)!));
    }

    [Fact]
    public void VerifyReportsATamperedPackageAndSaysWhatItDidNotCheck()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "vector.msi");

        using var certificate = TestCertificate();
        File.WriteAllBytes(path, MsiSigner.SignBytes(VectorFile(), certificate, null, HashAlgorithmName.SHA256, null, out _));

        var good = MsiSigner.Verify(path);
        Assert.True(good.Signed);
        Assert.Contains("trust was not checked", good.Detail);

        // Change one stream and the digest no longer matches.
        var streams = new CfbReader(File.ReadAllBytes(path)).Streams.ToList();
        var signature = streams.First(s => s.Name == MsiSigner.DigitalSignatureStream);
        streams.RemoveAll(s => MsiName.Decode(s.Name) == "!_StringPool");
        streams.Add(new CfbStream(MsiName.EncodeTable("_StringPool"), [0xFF, 0xFF, 0xFF, 0xFF]));
        File.WriteAllBytes(path, new CfbWriter(sectorSize: 4096).Write(streams));

        Assert.NotNull(signature);
        Assert.False(MsiSigner.Verify(path).Signed);
    }

    [Fact]
    public void AnUnsignedPackageIsReportedAsUnsignedRatherThanAsBroken()
    {
        string path = Path.Combine(TempDir(), "unsigned.msi");
        File.WriteAllBytes(path, VectorFile());

        var result = MsiSigner.Verify(path);

        Assert.False(result.Signed);
        Assert.Contains("no signature", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The whole point of signing in process is that the package still installs,
    /// so the tables have to survive the rewrite. Windows Installer is the real
    /// judge of that and cannot be consulted from a unit test; our own reader
    /// agreeing is the part that can be, and a signed package it cannot read is
    /// a failure either way.
    /// </summary>
    [Fact]
    public void ARealPackageStillReadsBackAfterBeingSigned()
    {
        string dir = TempDir();
        string payload = Path.Combine(dir, "readme.txt");
        File.WriteAllText(payload, "payload");

        var project = new MsiProject
        {
            ProductName = "Signed Product",
            Manufacturer = "Test Co",
            Version = "1.2.3",
            UpgradeCode = new Guid("0f4d6a11-2c88-4c6a-9f21-b0a5d3e77c10"),
            Files = { new PayloadFile { SourcePath = payload } },
        };

        string msi = Path.Combine(dir, "signed.msi");
        MsiBuilder.Build(project, msi);
        var before = MsiDatabase.Load(msi);

        using var certificate = TestCertificate();
        File.WriteAllBytes(msi, MsiSigner.SignBytes(
            File.ReadAllBytes(msi), certificate, null, HashAlgorithmName.SHA256, null, out _));

        var after = MsiDatabase.Load(msi);
        Assert.Equal(before.Tables.Count, after.Tables.Count);
        Assert.Equal(before.GetProperty("ProductCode"), after.GetProperty("ProductCode"));
        Assert.Equal(before.Summary.Template, after.Summary.Template);
        Assert.Equal(before.Streams.Count, after.Streams.Count);
        Assert.True(MsiSigner.Verify(msi).Signed);
    }

    [Fact]
    public void SigningFromAPfxIsThePathThatNeedsNothingInstalled()
    {
        string dir = TempDir();
        string pfx = Path.Combine(dir, "test.pfx");
        string msi = Path.Combine(dir, "vector.msi");

        using (var certificate = TestCertificate())
            File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pkcs12, "secret"));
        File.WriteAllBytes(msi, VectorFile());

        Environment.SetEnvironmentVariable("MSIMAKER_TEST_PFX_PASSWORD", "secret");
        try
        {
            var options = new SigningOptions
            {
                Enabled = true,
                CertificatePath = pfx,
                PasswordEnvironmentVariable = "MSIMAKER_TEST_PFX_PASSWORD",
                TimestampUrl = "",
            };

            Assert.True(CodeSigner.CanSignInProcess(options));

            var result = CodeSigner.Sign(msi, options);

            Assert.True(result.Signed, result.Detail);
            Assert.Equal("managed", result.Tool);
            Assert.Contains("MsiMaker Test Signer", result.Detail);
            Assert.Equal(OsslsigncodeVectorDigest,
                Convert.ToHexString(MsiSigner.ReadEmbeddedDigest(File.ReadAllBytes(msi), out _)!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSIMAKER_TEST_PFX_PASSWORD", null);
        }
    }

    [Fact]
    public void ModesThatKeepTheKeyElsewhereStillNeedSigntool()
    {
        // Both of these hold the private key somewhere this process cannot
        // reach, so the in-process signer must say so rather than try.
        Assert.False(CodeSigner.CanSignInProcess(new SigningOptions { Enabled = true, Thumbprint = "AABBCC" }));
        Assert.False(CodeSigner.CanSignInProcess(new SigningOptions
        {
            Enabled = true,
            AzureEndpoint = "https://eus.codesigning.azure.net/",
        }));

        var result = CodeSigner.Sign("nonexistent.msi", new SigningOptions
        {
            Enabled = true,
            Thumbprint = "AABBCC",
            UseManagedSigner = true,
        });

        Assert.False(result.Signed);
        Assert.Contains("signtool", result.Detail);
    }

    [Fact]
    public void AWrongPasswordIsReportedRatherThanThrown()
    {
        string dir = TempDir();
        string pfx = Path.Combine(dir, "test.pfx");
        string msi = Path.Combine(dir, "vector.msi");

        using (var certificate = TestCertificate())
            File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pkcs12, "secret"));
        File.WriteAllBytes(msi, VectorFile());

        var result = MsiSigner.Sign(msi, new SigningOptions { Enabled = true, CertificatePath = pfx }, "wrong");

        Assert.False(result.Signed);
        Assert.Equal("managed", result.Tool);
        Assert.False(File.ReadAllBytes(msi).AsSpan().Length == 0);
    }
}
