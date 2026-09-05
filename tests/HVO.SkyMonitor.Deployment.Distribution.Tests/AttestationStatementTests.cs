using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

/// <summary>
/// Contract tests for the in-toto statement check the release applies to the attestations it pushes.
/// <para>
/// The fixtures are recorded from two public images that were built and pushed by buildx, so they are the real
/// shape rather than a guess. That matters here: an earlier disposition on this pull request claimed buildx
/// leaves the statement subject empty, which is true only of a local <c>--output type=oci</c> export, where
/// there is no image reference to name. A registry push populates <c>subject[].digest.sha256</c> with the
/// platform manifest digest, which is exactly the binding this check verifies.
/// </para>
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class AttestationStatementTests
{
    /// <summary>linux/amd64 platform manifest of <c>docker/buildx-bin:latest</c>, the subject of its attestations.</summary>
    private const string BuildxAmd64Manifest = "sha256:108c5ca2dfc2cc3fd57674065f8c1e1d5e53c860163fcb1d15df54dfffaee806";

    /// <summary>linux/amd64 platform manifest of <c>docker/dockerfile:1.9.0</c>.</summary>
    private const string DockerfileAmd64Manifest = "sha256:dc9e236567481e0aca4c1f52351af213b9a176622f10e3f4a86e5cc48919fa01";

    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Attestations", name);

    private static async Task<(int ExitCode, string Diagnostics)> RunAsync(params string[] arguments)
    {
        var original = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            return (await ReleaseTool.Program.Main(arguments), captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    /// <summary>
    /// Both statement versions buildx has shipped are accepted. An older BuildKit emits the in-toto v0.1
    /// statement with an SLSA v0.2 predicate and a newer one emits v1 with SLSA v1; the release must not abort
    /// on that difference, because the check runs after the immutable registry push.
    /// </summary>
    [TestMethod]
    [DataRow("docker-buildx-bin-amd64-slsa-v1.json", BuildxAmd64Manifest, "https://slsa.dev/provenance/v1")]
    [DataRow("docker-buildx-bin-amd64-spdx.json", BuildxAmd64Manifest, "https://spdx.dev/Document")]
    [DataRow("docker-dockerfile-1.9.0-amd64-slsa-v0.2.json", DockerfileAmd64Manifest, "https://slsa.dev/provenance/v0.2")]
    [DataRow("docker-dockerfile-1.9.0-amd64-spdx.json", DockerfileAmd64Manifest, "https://spdx.dev/Document")]
    public async Task VerifyAttestation_RecordedRegistryStatement_AttestsItsPlatformManifest(
        string fixture, string manifestDigest, string predicateType)
    {
        var (exitCode, diagnostics) = await RunAsync(
            "verify-attestation", "--manifest-digest", manifestDigest,
            "--statement", Fixture(fixture), "--predicate-type", predicateType);

        Assert.AreEqual(0, exitCode, diagnostics);
    }

    /// <summary>
    /// The whole point of reading the payload: an attestation whose wrapper is annotated for this platform but
    /// whose statement attests other bytes must be refused.
    /// </summary>
    [TestMethod]
    public async Task VerifyAttestation_StatementForAnotherImage_IsRejected()
    {
        var (exitCode, diagnostics) = await RunAsync(
            "verify-attestation", "--manifest-digest", DockerfileAmd64Manifest,
            "--statement", Fixture("docker-buildx-bin-amd64-slsa-v1.json"));

        Assert.AreEqual(1, exitCode, diagnostics);
        StringAssert.Contains(diagnostics, "attests a subject that is not the published platform manifest", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task VerifyAttestation_StatementWhosePredicateContradictsItsManifestLayer_IsRejected()
    {
        var (exitCode, diagnostics) = await RunAsync(
            "verify-attestation", "--manifest-digest", BuildxAmd64Manifest,
            "--statement", Fixture("docker-buildx-bin-amd64-spdx.json"),
            "--predicate-type", "https://slsa.dev/provenance/v1");

        Assert.AreEqual(1, exitCode, diagnostics);
        StringAssert.Contains(diagnostics, "but the manifest", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task VerifyAttestation_StatementWithAnEmptySubject_IsRejected()
    {
        var path = await RewriteAsync("empty-subject.json", node => node!["subject"] = new JsonArray());

        var (exitCode, diagnostics) = await RunAsync(
            "verify-attestation", "--manifest-digest", BuildxAmd64Manifest, "--statement", path);

        Assert.AreEqual(1, exitCode, diagnostics);
        StringAssert.Contains(diagnostics, "names no subject", StringComparison.Ordinal);
    }

    /// <summary>
    /// A statement that attests this platform's manifest *and* something else is not this image's provenance,
    /// so a single matching subject is not enough.
    /// </summary>
    [TestMethod]
    public async Task VerifyAttestation_StatementThatAlsoAttestsAnotherSubject_IsRejected()
    {
        var path = await RewriteAsync("extra-subject.json", node =>
            node!["subject"]!.AsArray().Add(new JsonObject
            {
                ["name"] = "pkg:docker/other",
                ["digest"] = new JsonObject { ["sha256"] = new string('b', 64) }
            }));

        var (exitCode, diagnostics) = await RunAsync(
            "verify-attestation", "--manifest-digest", BuildxAmd64Manifest, "--statement", path);

        Assert.AreEqual(1, exitCode, diagnostics);
        StringAssert.Contains(diagnostics, "attests a subject that is not", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task VerifyAttestation_UnknownPredicateType_IsRejected()
    {
        var path = await RewriteAsync("unknown-predicate.json",
            node => node!["predicateType"] = "https://example.invalid/predicate");

        var (exitCode, diagnostics) = await RunAsync(
            "verify-attestation", "--manifest-digest", BuildxAmd64Manifest, "--statement", path);

        Assert.AreEqual(1, exitCode, diagnostics);
        StringAssert.Contains(diagnostics, "not an SBOM or SLSA provenance statement", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task VerifyAttestation_DocumentThatIsNotAnInTotoStatement_IsRejected()
    {
        var path = await RewriteAsync("not-in-toto.json", node => node!["_type"] = "https://example.invalid/Statement");

        var (exitCode, diagnostics) = await RunAsync(
            "verify-attestation", "--manifest-digest", BuildxAmd64Manifest, "--statement", path);

        Assert.AreEqual(1, exitCode, diagnostics);
        StringAssert.Contains(diagnostics, "not an in-toto statement", StringComparison.Ordinal);
    }

    private static async Task<string> RewriteAsync(string name, Action<JsonNode?> mutate)
    {
        var bytes = await File.ReadAllBytesAsync(Fixture("docker-buildx-bin-amd64-slsa-v1.json"));
        var node = JsonNode.Parse(bytes);
        mutate(node);
        var path = Path.Combine(Path.GetTempPath(), $"hvo-attestation-{Guid.NewGuid():N}-{name}");
        await File.WriteAllTextAsync(path, node!.ToJsonString(), new UTF8Encoding(false));
        return path;
    }
}
