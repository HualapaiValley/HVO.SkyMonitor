using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Distribution;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class DistributionVerifierTests
{
    [TestMethod]
    public void VerifyManifest_ExactSignedBytes_AreAccepted()
    {
        using var fixture = SigningFixture.Create();
        var bytes = fixture.ManifestBytes();

        var result = DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot);

        Assert.AreEqual("1.0.0", result.Release.Version);
        Assert.AreEqual(DistributionManifestKind.InstallerRelease, result.ManifestKind);
    }

    [TestMethod]
    public void VerifyManifest_TamperedBytes_AreRejected()
    {
        using var fixture = SigningFixture.Create();
        var original = fixture.ManifestBytes();
        var tampered = original.ToArray();
        tampered[^2] ^= 1;

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(tampered, fixture.Sign(original), fixture.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_UntrustedKey_IsRejected()
    {
        using var trusted = SigningFixture.Create();
        using var untrusted = SigningFixture.Create();
        var bytes = trusted.ManifestBytes();

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, untrusted.Sign(bytes), trusted.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_NoncanonicalSignatureText_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var bytes = fixture.ManifestBytes();
        var signature = fixture.Sign(bytes);
        byte[] noncanonical = [.. signature.AsSpan(0, 4), (byte)' ', .. signature.AsSpan(4)];

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, noncanonical, fixture.TrustRoot));
    }

    [TestMethod]
    [DataRow("\"schemaVersion\":1,")]
    [DataRow("\"unknown\":true,")]
    public void VerifyManifest_DuplicateOrUnknownMembers_AreRejected(string injectedMember)
    {
        using var fixture = SigningFixture.Create();
        var json = Encoding.UTF8.GetString(fixture.ManifestBytes()).Insert(1, injectedMember);
        var bytes = Encoding.UTF8.GetBytes(json);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_UnsafeAssetName_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var manifest = fixture.Manifest() with
        {
            Artifacts = fixture.Manifest().Artifacts
                .Select((artifact, index) => index == 0 ? artifact with { AssetName = "../installer" } : artifact)
                .ToArray()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DistributionJsonContext.Default.DistributionReleaseManifest);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public async Task VerifyAssetAsync_WrongLengthOrHash_IsRejected()
    {
        var bytes = "asset"u8.ToArray();
        var artifact = new DistributionArtifact(
            DistributionArtifactRole.Installer,
            "installer.tar.gz",
            "application/gzip",
            bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        await using var valid = new MemoryStream(bytes);
        await DistributionVerifier.VerifyAssetAsync(valid, artifact, CancellationToken.None);

        await using var invalid = new MemoryStream("other"u8.ToArray());
        await Assert.ThrowsExactlyAsync<DistributionValidationException>(
            () => DistributionVerifier.VerifyAssetAsync(invalid, artifact, CancellationToken.None));
    }

    [TestMethod]
    public async Task VerifyAssetAsync_OversizedSignedLength_IsRejectedBeforeReading()
    {
        var artifact = new DistributionArtifact(
            DistributionArtifactRole.Installer,
            "installer.tar.gz",
            "application/gzip",
            DistributionVerifier.MaximumInstallerBytes + 1,
            new string('a', 64));
        await using var stream = new MemoryStream();

        await Assert.ThrowsExactlyAsync<DistributionValidationException>(
            () => DistributionVerifier.VerifyAssetAsync(stream, artifact, CancellationToken.None));
    }

    [TestMethod]
    public void VerifyManifest_MissingRequiredArchitecture_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var manifest = fixture.Manifest() with
        {
            Artifacts = fixture.Manifest().Artifacts
                .Where(static artifact => artifact.Architecture != "arm64")
                .ToArray()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DistributionJsonContext.Default.DistributionReleaseManifest);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_DuplicateEvidenceRole_IsRejected()
    {
        using var fixture = SigningFixture.Create();
        var manifest = fixture.Manifest();
        var artifacts = manifest.Artifacts.Append(new DistributionArtifact(
            DistributionArtifactRole.Sbom,
            "second-sbom.spdx.json",
            "application/spdx+json",
            1,
            new string('d', 64))).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest with { Artifacts = artifacts }, DistributionJsonContext.Default.DistributionReleaseManifest);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public void VerifyManifest_NewerProductionCatalogRevision_IsAccepted()
    {
        using var fixture = SigningFixture.Create();
        var manifest = fixture.CatalogManifest("hyg-v4.2-p3-s2-r2");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DistributionJsonContext.Default.DistributionReleaseManifest);

        var result = DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot);

        Assert.AreEqual("hyg-v4.2-p3-s2-r2", result.Catalog?.PackageVersion);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public void VerifyManifest_NoncanonicalCatalogManifestVersion_IsRejected(int manifestVersion)
    {
        using var fixture = SigningFixture.Create();
        var manifest = fixture.CatalogManifest("hyg-v4.2-p3-s2-r1");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest with { Catalog = manifest.Catalog! with { ManifestVersion = manifestVersion } },
            DistributionJsonContext.Default.DistributionReleaseManifest);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    [DataRow("hyg-v4.2-p3-s2-r0")]
    [DataRow("other-v1")]
    public void VerifyManifest_InvalidProductionCatalogVersion_IsRejected(string version)
    {
        using var fixture = SigningFixture.Create();
        var manifest = fixture.CatalogManifest(version);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DistributionJsonContext.Default.DistributionReleaseManifest);

        Assert.ThrowsExactly<DistributionValidationException>(
            () => DistributionVerifier.VerifyManifest(bytes, fixture.Sign(bytes), fixture.TrustRoot));
    }

    [TestMethod]
    public void IsCanonicalKeyId_HistoricalFingerprint_IsAccepted()
    {
        Assert.IsTrue(DistributionTrustRoot.IsCanonicalKeyId($"p256-sha256:{new string('f', 64)}"));
        Assert.IsFalse(DistributionTrustRoot.IsCanonicalKeyId($"p256-sha256:{new string('F', 64)}"));
    }

    [TestMethod]
    public void VerifyIndex_SignedDefaultVersion_IsAccepted()
    {
        using var fixture = SigningFixture.Create();
        var index = new DistributionReleaseIndex(
            DistributionSchemaVersions.ReleaseIndex,
            "release-index",
            "installer",
            1,
            DateTimeOffset.Parse("2026-08-24T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            "1.0.0",
            new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, fixture.TrustRoot.KeyId),
            [new DistributionReleaseReference(
                "1.0.0",
                "installer-v1.0.0",
                "release-manifest.json",
                1024,
                new string('a', 64),
                "release-manifest.json.sig")]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(index, DistributionJsonContext.Default.DistributionReleaseIndex);

        var result = DistributionVerifier.VerifyIndex(bytes, fixture.Sign(bytes), fixture.TrustRoot);

        Assert.AreEqual(1, result.Sequence);
    }

    private sealed class SigningFixture(ECDsa key, DistributionTrustRoot trustRoot) : IDisposable
    {
        public DistributionTrustRoot TrustRoot { get; } = trustRoot;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The fixture owns and disposes the generated signing key.")]
        public static SigningFixture Create()
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return new SigningFixture(key, DistributionTrustRoot.FromPem(key.ExportSubjectPublicKeyInfoPem()));
        }

        public DistributionReleaseManifest Manifest() => new(
            DistributionSchemaVersions.ReleaseManifest,
            DistributionManifestKind.InstallerRelease,
            new DistributionReleaseIdentity(
                "installer",
                "1.0.0",
                "installer-v1.0.0",
                "RoySalisbury/HVO.SkyMonitor",
                new string('a', 40),
                new string('b', 40),
                DateTimeOffset.Parse("2026-08-24T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, TrustRoot.KeyId),
            [
                Artifact(DistributionArtifactRole.Installer, "hvo-skymonitor-installer-v1.0.0-linux-x64.tar.gz"),
                Artifact(DistributionArtifactRole.Installer, "hvo-skymonitor-installer-v1.0.0-linux-arm64.tar.gz") with
                {
                    OperatingSystem = "linux",
                    Architecture = "arm64"
                },
                Artifact(DistributionArtifactRole.Checksums, "SHA256SUMS"),
                Artifact(DistributionArtifactRole.Sbom, "sbom.spdx.json"),
                Artifact(DistributionArtifactRole.Provenance, "installer-provenance.json")
            ],
            null,
            []);

        public byte[] ManifestBytes()
            => JsonSerializer.SerializeToUtf8Bytes(Manifest(), DistributionJsonContext.Default.DistributionReleaseManifest);

        public DistributionReleaseManifest CatalogManifest(string version) => new(
            DistributionSchemaVersions.ReleaseManifest,
            DistributionManifestKind.CatalogRelease,
            new DistributionReleaseIdentity(
                "catalog",
                version,
                $"catalog-{version}",
                "RoySalisbury/HVO.SkyMonitor",
                new string('a', 40),
                new string('b', 40),
                DateTimeOffset.Parse("2026-08-24T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, TrustRoot.KeyId),
            [
                Artifact(DistributionArtifactRole.CatalogBundle, $"{version}.bundle.tar.gz"),
                Artifact(DistributionArtifactRole.Checksums, "SHA256SUMS"),
                Artifact(DistributionArtifactRole.Sbom, "catalog-sbom.spdx.json"),
                Artifact(DistributionArtifactRole.Provenance, "catalog-provenance.json"),
                Artifact(DistributionArtifactRole.License, "LICENSE-HYG.md"),
                Artifact(DistributionArtifactRole.Attribution, "ATTRIBUTION-HYG.md")
            ],
            new DistributionCatalogIdentity(
                "hyg-v42-production",
                version,
                "production",
                2,
                "2",
                "3",
                new string('d', 64),
                new string('e', 64),
                1,
                1,
                "MIT",
                "LICENSE-HYG.md",
                "ATTRIBUTION-HYG.md",
                "topology",
                new string('f', 64)),
            []);

        public byte[] Sign(ReadOnlySpan<byte> bytes)
            => Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(
                bytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) + "\n");

        public void Dispose() => key.Dispose();

        private static DistributionArtifact Artifact(DistributionArtifactRole role, string name)
            => new(
                role,
                name,
                "application/octet-stream",
                1,
                new string('c', 64),
                role == DistributionArtifactRole.Installer ? "linux" : null,
                role == DistributionArtifactRole.Installer ? "x64" : null);
    }
}
