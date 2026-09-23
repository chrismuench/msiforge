using System.Formats.Asn1;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MsiMaker.Core.Cfbf;

namespace MsiMaker.Core.Signing;

/// <summary>
/// Signs an MSI in process, with no signtool and no osslsigncode, so a package
/// built on a Mac can be signed there too.
///
/// The format was not recalled, it was pinned against already-signed packages:
/// the digest below reproduces, byte for byte, the digest embedded by
/// osslsigncode in two independently signed MSIs and the one embedded by
/// signtool in a third. That last one also carries a MsiDigitalSignatureEx
/// stream, and its digest matches once the 32-byte prehash from that stream is
/// fed in first - which is what proves the rest of the walk is right rather
/// than accidentally compensating somewhere.
/// </summary>
public static class MsiSigner
{
    /// <summary>The PKCS#7 blob. Its presence is what makes a package signed.</summary>
    public const string DigitalSignatureStream = "\u0005DigitalSignature";

    /// <summary>
    /// A prehash of compound-file metadata. signtool writes it, osslsigncode by
    /// default does not, and Windows accepts a package without it - so this
    /// signer does not write one either. It is still recognised on the way in,
    /// because a package being re-signed may already carry one and both
    /// signature streams have to come out before the digest is taken.
    /// </summary>
    public const string MsiDigitalSignatureExStream = "\u0005MsiDigitalSignatureEx";

    public const string SpcIndirectDataOid = "1.3.6.1.4.1.311.2.1.4";
    public const string SpcSipInfoOid = "1.3.6.1.4.1.311.2.1.30";
    public const string SpcStatementTypeOid = "1.3.6.1.4.1.311.2.1.11";
    public const string SpcIndividualKeyPurposeOid = "1.3.6.1.4.1.311.2.1.21";
    public const string SpcSpOpusInfoOid = "1.3.6.1.4.1.311.2.1.12";

    /// <summary>The RFC 3161 timestamp token, which rides as an unsigned attribute.</summary>
    public const string Rfc3161CounterSignatureOid = "1.3.6.1.4.1.311.3.3.1";

    /// <summary>
    /// 000C10F1-0000-0000-C000-000000000046 in little-endian GUID order: the
    /// subject interface package that tells Windows this digest covers an MSI
    /// rather than a PE image. A PE carries SpcPeImageData under a different
    /// OID here, which is the one difference that matters between the two.
    /// </summary>
    private static readonly byte[] MsiSipGuid =
    [
        0xF1, 0x10, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46,
    ];

    private static readonly Asn1Tag ExplicitZero = new(TagClass.ContextSpecific, 0, isConstructed: true);

    // ------------------------------------------------------------------ digest

    /// <summary>
    /// The Authenticode digest of an MSI: every stream's contents in compound-file
    /// order, then the root storage CLSID.
    ///
    /// Two details are easy to get wrong and were both settled against signed
    /// packages rather than from the description. The order is a plain memcmp of
    /// the NUL-terminated UTF-16LE name bytes - not of the decoded MSI table
    /// names, and not the length-then-uppercase order the directory red-black
    /// tree uses; those agree for most packages and diverge exactly when a name
    /// begins with 0x05, which every MSI has. And the root CLSID goes in at the
    /// end, not the start; leaving it out entirely also produces a
    /// plausible-looking wrong answer.
    /// </summary>
    public static byte[] ComputeDigest(byte[] msi, HashAlgorithmName algorithm) =>
        ComputeDigest(new CfbReader(msi), algorithm);

    /// <summary>
    /// As above, with a <paramref name="prehash"/> fed in first. That is the
    /// MsiDigitalSignatureEx stream of a package signtool signed; supplying it
    /// is how such a signature is checked without writing one.
    /// </summary>
    public static byte[] ComputeDigest(CfbReader reader, HashAlgorithmName algorithm, byte[]? prehash = null)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        if (prehash is { Length: > 0 }) hash.AppendData(prehash);

        foreach (var stream in DigestOrder(reader.Streams)) hash.AppendData(stream.Data);

        hash.AppendData(reader.RootClsid.ToByteArray());
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// The streams a digest covers, in the order it covers them. Public because
    /// the ordering is the whole difficulty, and a test that cannot see it can
    /// only report that some digest came out wrong.
    /// </summary>
    public static IReadOnlyList<CfbStream> DigestOrder(IEnumerable<CfbStream> streams) =>
        streams
            .Where(s => !IsSignatureStream(s.Name))
            .OrderBy(s => Encoding.Unicode.GetBytes(s.Name + '\0'), Utf16NameOrder.Instance)
            .ToList();

    public static bool IsSignatureStream(string name) =>
        name is DigitalSignatureStream or MsiDigitalSignatureExStream;

    /// <summary>
    /// memcmp over the name bytes, terminator included - which is the form the
    /// compound-file directory stores, and the terminator is not decoration.
    /// The MSI name encoding lets one name be a byte-for-byte prefix of another
    /// ("File" packs to the first two code units of "FileSFPCatalog"), and the
    /// trailing NUL is what then puts the shorter name first. Checked against
    /// osslsigncode by building a package with such a pair on purpose, since no
    /// ordinary MSI has one and the two tie-breaks otherwise look alike.
    /// </summary>
    private sealed class Utf16NameOrder : IComparer<byte[]>
    {
        public static readonly Utf16NameOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) =>
            x is null || y is null ? (x is null ? (y is null ? 0 : -1) : 1) : x.AsSpan().SequenceCompareTo(y);
    }

    // --------------------------------------------------------------- signature

    /// <summary>
    /// The SpcIndirectDataContent that the PKCS#7 signs over, returned without
    /// its outer SEQUENCE header.
    ///
    /// That headerless form is not a convenience: Authenticode's messageDigest
    /// attribute covers the contents of the SEQUENCE and not the SEQUENCE
    /// itself, which was confirmed by hashing both halves of a signed package
    /// and seeing which one the signer had committed to. Handing the body to
    /// SignedCms as an OCTET STRING payload therefore produces the right digest,
    /// and the tag is corrected to a SEQUENCE afterwards.
    /// </summary>
    public static byte[] BuildSpcIndirectDataContent(byte[] digest, HashAlgorithmName algorithm)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(SpcSipInfoOid);
                using (writer.PushSequence())
                {
                    // Version 1 says "no MsiDigitalSignatureEx"; signtool writes 2
                    // when it adds one. Claiming 2 without the stream would make
                    // the package unverifiable.
                    writer.WriteInteger(1);
                    writer.WriteOctetString(MsiSipGuid);
                    for (int i = 0; i < 5; i++) writer.WriteInteger(0);
                }
            }
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(DigestOid(algorithm));
                    writer.WriteNull();
                }
                writer.WriteOctetString(digest);
            }
        }

        byte[] encoded = writer.Encode();
        AsnDecoder.ReadSequence(encoded, AsnEncodingRules.DER, out int contentOffset, out int contentLength, out _);
        return encoded.AsSpan(contentOffset, contentLength).ToArray();
    }

    /// <summary>
    /// Builds the PKCS#7 SignedData for a digest.
    ///
    /// <paramref name="timestampProblem"/> reports a timestamp that could not be
    /// obtained. The signature is still returned in that case: an untimestamped
    /// signature stops being trusted when the certificate expires, which is a
    /// caveat worth reporting but not a reason to ship nothing.
    /// </summary>
    public static byte[] BuildSignature(
        byte[] digest,
        HashAlgorithmName algorithm,
        X509Certificate2 certificate,
        X509Certificate2Collection? extraCertificates,
        string? timestampUrl,
        out string? timestampProblem)
    {
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException(
                $"Certificate '{certificate.Subject}' has no private key, so it cannot sign.");

        var content = new ContentInfo(new Oid(SpcIndirectDataOid), BuildSpcIndirectDataContent(digest, algorithm));
        var cms = new SignedCms(content, detached: false);

        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new Oid(DigestOid(algorithm)),
            // A self-signed certificate is its own root, and ExcludeRoot would
            // drop the only certificate in the blob - leaving Windows unable to
            // name a signer at all.
            IncludeOption = X509IncludeOption.EndCertOnly,
        };

        if (extraCertificates is not null)
            foreach (var extra in extraCertificates)
                if (!extra.RawData.AsSpan().SequenceEqual(certificate.RawData))
                    signer.Certificates.Add(extra);

        signer.SignedAttributes.Add(new AsnEncodedData(new Oid(SpcStatementTypeOid), StatementTypeValue()));
        signer.SignedAttributes.Add(new AsnEncodedData(new Oid(SpcSpOpusInfoOid), EmptyOpusInfoValue()));

        cms.ComputeSignature(signer, silent: true);

        timestampProblem = null;
        if (!string.IsNullOrWhiteSpace(timestampUrl))
        {
            try
            {
                Timestamp(cms, algorithm, timestampUrl!);
            }
            catch (Exception ex) when (ex is HttpRequestException or CryptographicException
                                          or TaskCanceledException or InvalidOperationException)
            {
                timestampProblem = $"{timestampUrl}: {ex.Message}";
            }
        }

        byte[] encoded = cms.Encode();
        MakeAuthenticodeCompatible(encoded);
        return encoded;
    }

    /// <summary>
    /// Asks an RFC 3161 authority to countersign, and attaches the token.
    /// Cross-platform by construction: the request and the response parsing are
    /// managed code and the transport is plain HTTP, so no signtool and no
    /// platform timestamp service is involved.
    /// </summary>
    private static void Timestamp(SignedCms cms, HashAlgorithmName algorithm, string timestampUrl)
    {
        var signerInfo = cms.SignerInfos[0];
        var request = Rfc3161TimestampRequest.CreateFromSignerInfo(signerInfo, algorithm, requestSignerCertificates: true);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var body = new ByteArrayContent(request.Encode());
        body.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");

        using HttpResponseMessage response = http.PostAsync(timestampUrl, body).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        byte[] reply = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

        Rfc3161TimestampToken token = request.ProcessResponse(reply, out _);
        signerInfo.AddUnsignedAttribute(
            new AsnEncodedData(new Oid(Rfc3161CounterSignatureOid), token.AsSignedCms().Encode()));
    }

    /// <summary>
    /// Two edits that turn a correct CMS message into one Windows will read.
    /// Both sit outside everything any signature covers - the version is not an
    /// input to any digest, and the content bytes are untouched - so this
    /// rewrites the encoding without invalidating anything.
    ///
    /// First, the encapsulated content. RFC 5652 wraps eContent in an OCTET
    /// STRING and SignedCms follows it; Authenticode puts the
    /// SpcIndirectDataContent SEQUENCE there directly. Both tags carry the same
    /// length encoding for the same payload, so one byte changes - but it has
    /// to be the right byte, so the structure is walked, not searched.
    ///
    /// Second, the SignedData version. RFC 5652 says 3 when the content type is
    /// not id-data, which SpcIndirectData is not, and SignedCms is right to
    /// write 3. Windows disagrees: with version 3 it returns 0x8009200D, "not a
    /// cryptographic message", and refuses to look further - which is a
    /// signature that looks fine everywhere except on the machine that has to
    /// accept it. osslsigncode and signtool both write 1.
    /// </summary>
    private static void MakeAuthenticodeCompatible(byte[] cms)
    {
        int position = Into(cms, 0);        // ContentInfo
        position = Skip(cms, position);     // contentType, id-signedData
        position = Into(cms, position);     // [0] EXPLICIT
        position = Into(cms, position);     // SignedData

        int version = Into(cms, position);
        if (cms[version] == 3) cms[version] = 1;

        position = Skip(cms, position);     // version
        position = Skip(cms, position);     // digestAlgorithms
        position = Into(cms, position);     // EncapsulatedContentInfo
        position = Skip(cms, position);     // eContentType
        position = Into(cms, position);     // [0] EXPLICIT

        if (cms[position] != 0x04)
            throw new CryptographicException(
                "Encapsulated content is not the OCTET STRING SignedCms was expected to write, "
                + "so the Authenticode retag would corrupt the signature.");

        cms[position] = 0x30;

        static int Into(byte[] buffer, int offset)
        {
            AsnDecoder.ReadEncodedValue(buffer.AsSpan(offset), AsnEncodingRules.BER,
                out int contentOffset, out _, out _);
            return offset + contentOffset;
        }

        static int Skip(byte[] buffer, int offset)
        {
            AsnDecoder.ReadEncodedValue(buffer.AsSpan(offset), AsnEncodingRules.BER, out _, out _, out int consumed);
            return offset + consumed;
        }
    }

    /// <summary>SEQUENCE { OID individual code signing }, as signtool writes it.</summary>
    private static byte[] StatementTypeValue()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence()) writer.WriteObjectIdentifier(SpcIndividualKeyPurposeOid);
        return writer.Encode();
    }

    /// <summary>
    /// An empty SpcSpOpusInfo. Both of its fields - a program name and a URL -
    /// are optional, and neither is anything a package builder should invent.
    /// </summary>
    private static byte[] EmptyOpusInfoValue()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.PopSequence();
        return writer.Encode();
    }

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// The digest a signed MSI commits to, or null if it carries no signature.
    /// This is the other half of the verification story: read what the oracle
    /// wrote, compute our own over the same file, and require them to agree.
    /// </summary>
    public static byte[]? ReadEmbeddedDigest(byte[] msi, out string? digestAlgorithmOid)
    {
        digestAlgorithmOid = null;
        var reader = new CfbReader(msi);
        var signature = reader.Streams.FirstOrDefault(s => s.Name == DigitalSignatureStream);
        return signature is null ? null : ReadDigestFromSignature(signature.Data, out digestAlgorithmOid);
    }

    /// <summary>As above, from the PKCS#7 blob on its own.</summary>
    public static byte[] ReadDigestFromSignature(byte[] pkcs7, out string? digestAlgorithmOid)
    {
        var contentInfo = new AsnReader(pkcs7, AsnEncodingRules.BER).ReadSequence();
        contentInfo.ReadObjectIdentifier();
        var signedData = contentInfo.ReadSequence(ExplicitZero).ReadSequence();
        signedData.ReadInteger();
        signedData.ReadSetOf();

        var encapsulated = signedData.ReadSequence();
        string contentType = encapsulated.ReadObjectIdentifier();
        if (contentType != SpcIndirectDataOid)
            throw new CryptographicException($"Signature content is {contentType}, not SpcIndirectData.");

        var spc = encapsulated.ReadSequence(ExplicitZero).ReadSequence();
        spc.ReadEncodedValue(); // SpcAttributeTypeAndOptionalValue: the SIP, not the digest

        var digestInfo = spc.ReadSequence();
        digestAlgorithmOid = digestInfo.ReadSequence().ReadObjectIdentifier();
        return digestInfo.ReadOctetString();
    }

    /// <summary>The MsiDigitalSignatureEx prehash, when the package carries one.</summary>
    public static byte[]? ReadPrehash(byte[] msi) =>
        new CfbReader(msi).Streams.FirstOrDefault(s => s.Name == MsiDigitalSignatureExStream)?.Data;

    /// <summary>
    /// Recomputes the digest and compares it with the one the signature commits
    /// to. This answers whether the package has been altered since it was
    /// signed; it says nothing about whether the certificate is one to trust,
    /// which needs a trust store and is the operating system's job.
    /// </summary>
    public static SigningResult Verify(string msiPath)
    {
        if (!File.Exists(msiPath)) return new SigningResult(false, "managed", $"Package not found: {msiPath}");

        byte[] bytes;
        byte[]? embedded;
        string? algorithmOid;
        try
        {
            bytes = File.ReadAllBytes(msiPath);
            embedded = ReadEmbeddedDigest(bytes, out algorithmOid);
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or AsnContentException)
        {
            return new SigningResult(false, "managed", $"Signature could not be read: {ex.Message}");
        }

        if (embedded is null)
            return new SigningResult(false, "managed", "Package carries no signature.");

        HashAlgorithmName algorithm = AlgorithmFromOid(algorithmOid);
        byte[] computed = ComputeDigest(new CfbReader(bytes), algorithm, ReadPrehash(bytes));

        return computed.AsSpan().SequenceEqual(embedded)
            ? new SigningResult(true, "managed",
                $"{algorithm.Name} digest matches the signature. Certificate trust was not checked: "
                + "that needs a trust store, and only the installing machine has the right one.")
            : new SigningResult(false, "managed",
                "Digest does not match the signature - the package has been altered since it was signed.");
    }

    private static HashAlgorithmName AlgorithmFromOid(string? oid) =>
        oid switch
        {
            "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
            "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
            "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256,
        };

    // ------------------------------------------------------------------ signing

    /// <summary>
    /// Returns the bytes of <paramref name="msi"/> with a signature embedded.
    /// Any existing signature comes off first, so re-signing a package is the
    /// same operation as signing a fresh one.
    /// </summary>
    public static byte[] SignBytes(
        byte[] msi,
        X509Certificate2 certificate,
        X509Certificate2Collection? extraCertificates,
        HashAlgorithmName algorithm,
        string? timestampUrl,
        out string? timestampProblem)
    {
        var reader = new CfbReader(msi);
        var streams = reader.Streams.Where(s => !IsSignatureStream(s.Name)).ToList();

        // Recomputed from the stripped set rather than from the file as read, so
        // that re-signing cannot fold a previous signature into the digest.
        using var hash = IncrementalHash.CreateHash(algorithm);
        foreach (var stream in DigestOrder(streams)) hash.AppendData(stream.Data);
        hash.AppendData(reader.RootClsid.ToByteArray());

        byte[] signature = BuildSignature(
            hash.GetHashAndReset(), algorithm, certificate, extraCertificates, timestampUrl, out timestampProblem);

        streams.Add(new CfbStream(DigitalSignatureStream, signature));
        return new CfbWriter(sectorSize: 4096) { RootClsid = reader.RootClsid }.Write(streams);
    }

    /// <summary>
    /// Signs the file in place from a PFX.
    ///
    /// The password arrives as an argument rather than from the options, which
    /// carry only the name of the environment variable holding it - a project
    /// file belongs in source control and a secret in it would go too.
    /// </summary>
    public static SigningResult Sign(string msiPath, SigningOptions options, string? password)
    {
        if (string.IsNullOrWhiteSpace(options.CertificatePath))
            return new SigningResult(false, "managed", "Managed signing needs CertificatePath, a PFX file.");
        if (!File.Exists(msiPath))
            return new SigningResult(false, "managed", $"Package not found: {msiPath}");

        X509Certificate2Collection bundle;
        try
        {
            bundle = LoadPfx(options.CertificatePath!, password);
        }
        catch (CryptographicException ex)
        {
            return new SigningResult(false, "managed",
                $"Could not open {Path.GetFileName(options.CertificatePath)}: {ex.Message}");
        }

        try
        {
            var certificate = bundle.FirstOrDefault(c => c.HasPrivateKey);
            if (certificate is null)
                return new SigningResult(false, "managed",
                    $"{Path.GetFileName(options.CertificatePath)} holds no private key, so it cannot sign.");

            HashAlgorithmName algorithm = ResolveAlgorithm(options.DigestAlgorithm);

            byte[] signed = SignBytes(
                File.ReadAllBytes(msiPath), certificate, bundle, algorithm,
                options.TimestampUrl, out string? timestampProblem);

            File.WriteAllBytes(msiPath, signed);

            string detail = $"{algorithm.Name} signature by {certificate.Subject}";
            return new SigningResult(true, "managed",
                timestampProblem is null
                    ? (string.IsNullOrWhiteSpace(options.TimestampUrl) ? detail + ", not timestamped" : detail + ", timestamped")
                    : detail + $", NOT timestamped ({timestampProblem})");
        }
        catch (CryptographicException ex)
        {
            return new SigningResult(false, "managed", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return new SigningResult(false, "managed", ex.Message);
        }
        finally
        {
            foreach (var certificate in bundle) certificate.Dispose();
        }
    }

    /// <summary>
    /// Loads a PFX, keys and all. X509Certificate2's PFX constructors are
    /// obsolete from .NET 9, and the loader is also the only form that returns
    /// the intermediates alongside the leaf - which a signature needs, or a
    /// verifier has no chain to build.
    /// </summary>
    public static X509Certificate2Collection LoadPfx(string path, string? password)
    {
        // An ephemeral key never lands in the user's key store, which matters on
        // a build agent. macOS has no such thing, and asking for it there throws.
        if (!OperatingSystem.IsMacOS())
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12CollectionFromFile(
                    path, password, X509KeyStorageFlags.EphemeralKeySet);
            }
            catch (CryptographicException)
            {
                // Some providers refuse ephemeral keys; fall through rather than
                // fail a signature that the default store would have managed.
            }
        }
        return X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password, X509KeyStorageFlags.DefaultKeySet);
    }

    public static HashAlgorithmName ResolveAlgorithm(string? name) =>
        (name ?? "").Trim().ToUpperInvariant() switch
        {
            "SHA1" => HashAlgorithmName.SHA1,
            "SHA384" => HashAlgorithmName.SHA384,
            "SHA512" => HashAlgorithmName.SHA512,
            _ => HashAlgorithmName.SHA256,
        };

    private static string DigestOid(HashAlgorithmName algorithm) =>
        algorithm.Name switch
        {
            "SHA1" => "1.3.14.3.2.26",
            "SHA384" => "2.16.840.1.101.3.4.2.2",
            "SHA512" => "2.16.840.1.101.3.4.2.3",
            "SHA256" => "2.16.840.1.101.3.4.2.1",
            _ => throw new ArgumentException($"Unsupported digest algorithm: {algorithm.Name}", nameof(algorithm)),
        };
}
