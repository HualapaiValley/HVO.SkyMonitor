using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.OrbitalPlanning;

/// <summary>
/// The release-bundled pair (#1474). scripts/time-data:update runs this class after rewriting the snapshot, so every
/// assertion on the vendored files derives its instants from the files themselves and holds for any approved pair.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentBundledTimeDataTests
{
    private static readonly string[] ApprovedHosts = ["maia.usno.navy.mil", "datacenter.iers.org", "hpiers.obspm.fr", "data.iana.org"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task VendoredPairMatchesItsProvenanceAndParsesWithTheProductValidatorsAsync()
    {
        var directory = CameraAgentPinnedOrbitalTimeSource.BundledDirectory;
        using var provenance = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory,
            CameraAgentPinnedOrbitalTimeSource.ProvenanceFileName), TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(CameraAgentPinnedOrbitalTimeSource.ProvenanceSchema, provenance.RootElement.GetProperty("schema").GetString());
        foreach (var name in new[] { "earthOrientation", "leapSeconds" })
        {
            var entry = provenance.RootElement.GetProperty(name);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, entry.GetProperty("file").GetString()!),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), entry.GetProperty("sha256").GetString(), name);
            Assert.AreEqual(bytes.Length, entry.GetProperty("lengthBytes").GetInt32(), name);
            foreach (var url in new[] { entry.GetProperty("sourceUrl").GetString(), entry.GetProperty("providerChecksum").GetProperty("sourceUrl").GetString(),
                entry.GetProperty("termsUrl").GetString() })
            {
                var uri = new Uri(url!);
                Assert.AreEqual(Uri.UriSchemeHttps, uri.Scheme, name);
                CollectionAssert.Contains(ApprovedHosts, uri.Host, name);
            }
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.GetProperty("providerChecksum").GetProperty("value").GetString()), name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.GetProperty("terms").GetString()), name);
            Assert.AreEqual(TimeSpan.Zero, entry.GetProperty("retrievedUtc").GetDateTimeOffset().Offset, name);
        }

        using var source = new CameraAgentPinnedOrbitalTimeSource(new PlanningTestInputs.Settings());
        var resolution = await source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Bundled, resolution.Source);
        Assert.AreEqual(CameraAgentOrbitalTimeDataProblem.None, resolution.Problem);
        var data = resolution.Data!;
        Assert.IsFalse(data.NominalZeroEarthOrientation);
        Assert.AreEqual(provenance.RootElement.GetProperty("earthOrientation").GetProperty("sha256").GetString(), data.EarthOrientationSha256);
        Assert.AreEqual(provenance.RootElement.GetProperty("leapSeconds").GetProperty("sha256").GetString(), data.LeapSecondsSha256);
        Assert.AreEqual(data.EarthOrientationSha256, resolution.EarthOrientationProvenance!.Sha256);
        Assert.AreEqual(data.LeapSecondsSha256, resolution.LeapSecondsProvenance!.Sha256);
        // An approved pair has final history, predictions after it, and a leap list that is still valid when the
        // predictions start; otherwise a release would ship with no usable planning window.
        var final = data.EarthOrientationFinalCoverage!;
        var predicted = data.EarthOrientationPredictedCoverage!;
        Assert.IsLessThan(predicted.StartUtc, final.EndUtc);
        Assert.IsLessThan(data.LeapTableExpiresUtc, predicted.StartUtc);
        _ = data.Evaluate(final.EndUtc);
        Assert.AreEqual(OrbitalEarthOrientationStatus.Predicted, data.Evaluate(predicted.StartUtc.AddHours(12)).Status);
    }

    [TestMethod]
    public async Task HashMismatchedMissingOrMalformedBundledPairIsRefusedAsync()
    {
        var directory = CopyBundle();
        try
        {
            var options = new PlanningTestInputs.Settings();
            var provenancePath = Path.Combine(directory, CameraAgentPinnedOrbitalTimeSource.ProvenanceFileName);
            var original = await File.ReadAllTextAsync(provenancePath, TestContext.CancellationToken).ConfigureAwait(false);
            var eopPath = Path.Combine(directory, JsonNode.Parse(original)!["earthOrientation"]!["file"]!.GetValue<string>());
            var eop = await File.ReadAllBytesAsync(eopPath, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(await ResolveAsync(options, directory).ConfigureAwait(false), "the unmodified copy verifies");

            var tampered = eop.ToArray();
            tampered[^2] = (byte)(tampered[^2] == (byte)'0' ? '1' : '0');
            await File.WriteAllBytesAsync(eopPath, tampered, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNull(await ResolveAsync(options, directory).ConfigureAwait(false), "a byte that differs from the provenance SHA-256 is refused");
            await File.WriteAllBytesAsync(eopPath, eop, TestContext.CancellationToken).ConfigureAwait(false);

            foreach (var edit in new Action<JsonNode>[]
            {
                node => node["earthOrientation"]!["sha256"] = new string('A', 64),
                node => node["leapSeconds"]!["sha256"] = "not-a-hash",
                node => node["earthOrientation"]!["file"] = "../" + Path.GetFileName(eopPath),
                node => node["leapSeconds"]!["sourceUrl"] = "http://hpiers.obspm.fr/iers/bul/bulc/ntp/leap-seconds.list",
                node => node["earthOrientation"]!["retrievedUtc"] = "2026-10-21T10:00:00-07:00",
                node => node["schema"] = "hvo-bundled-time-data-v0",
                node => node.AsObject().Remove("leapSeconds")
            })
            {
                var node = JsonNode.Parse(original)!;
                edit(node);
                await File.WriteAllTextAsync(provenancePath, node.ToJsonString(), TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(await ResolveAsync(options, directory).ConfigureAwait(false), node.ToJsonString());
            }
            await File.WriteAllTextAsync(provenancePath, "{", TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNull(await ResolveAsync(options, directory).ConfigureAwait(false), "malformed provenance");
            File.Delete(provenancePath);
            Assert.IsNull(await ResolveAsync(options, directory).ConfigureAwait(false), "missing provenance");
            Assert.IsNull(await ResolveAsync(options, Path.Combine(directory, "absent")).ConfigureAwait(false), "missing bundle");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task ExplicitOverrideTakesPrecedenceAndAnInvalidOverrideNeverFallsBackAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hvo-orbital-override-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new PlanningTestInputs.Settings();
            var settings = options.CurrentValue;
            var eop = PlanningTestInputs.Bytes("time", "predicted-2026.finals2000A");
            var leaps = PlanningTestInputs.Bytes("time", "leap-seconds.list");
            settings.EarthOrientationPath = Path.Combine(directory, "eop");
            settings.LeapSecondsPath = Path.Combine(directory, "leaps");
            await File.WriteAllBytesAsync(settings.EarthOrientationPath, eop, TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(settings.LeapSecondsPath, leaps, TestContext.CancellationToken).ConfigureAwait(false);
            var written = File.GetLastWriteTimeUtc(settings.EarthOrientationPath);
            using var source = new CameraAgentPinnedOrbitalTimeSource(options);
            using var bundledSource = new CameraAgentPinnedOrbitalTimeSource(new PlanningTestInputs.Settings());
            var bundled = (await bundledSource.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false)).Data!;

            // Two of the four override settings: the override is selected, incomplete, and the bundled pair is not used.
            var incomplete = await source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Override, incomplete.Source);
            Assert.AreEqual(CameraAgentOrbitalTimeDataProblem.OverrideIncomplete, incomplete.Problem);
            Assert.IsNull(incomplete.Data);

            settings.EarthOrientationSha256 = PlanningTestInputs.Hash(eop);
            settings.LeapSecondsSha256 = PlanningTestInputs.Hash(leaps);
            var active = await source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Override, active.Source);
            Assert.AreEqual(settings.EarthOrientationSha256, active.Data!.EarthOrientationSha256);
            Assert.AreNotEqual(bundled.EarthOrientationSha256, active.Data.EarthOrientationSha256);
            Assert.IsNull(active.EarthOrientationProvenance);

            settings.LeapSecondsSha256 = new string('B', 64);
            var invalid = await source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentOrbitalTimeDataProblem.OverrideInvalid, invalid.Problem);
            Assert.IsNull(invalid.Data, "an invalid override fails rather than falling back to the bundled pair");
            CollectionAssert.AreEqual(eop, await File.ReadAllBytesAsync(settings.EarthOrientationPath, TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(written, File.GetLastWriteTimeUtc(settings.EarthOrientationPath), "hand-configured files are never rewritten");

            settings.Enabled = false;
            var disabled = await source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual((CameraAgentOrbitalTimeDataSource.Override, CameraAgentOrbitalTimeDataProblem.Disabled), (disabled.Source, disabled.Problem));
            Assert.IsNull(disabled.Data);

            settings.Enabled = true;
            settings.EarthOrientationPath = settings.EarthOrientationSha256 = settings.LeapSecondsPath = settings.LeapSecondsSha256 = " ";
            var restored = await source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Bundled, restored.Source, "removing every override setting selects the bundled pair");
            Assert.AreEqual(bundled.EarthOrientationSha256, restored.Data!.EarthOrientationSha256);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task PlannerNamesTheCoverageEndAndTheUnavailableSourceWithoutSubstitutionAsync()
    {
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync().ConfigureAwait(false);
        var data = input.Times.Current!;
        var end = data.EarthOrientationCoverage!.EndUtc;
        Assert.AreEqual(end, CameraAgentSatellitePlanner.CoverageEndsBefore(data, end.AddTicks(1)));
        Assert.IsNull(CameraAgentSatellitePlanner.CoverageEndsBefore(data, end));
        var expired = await input.Planner.SearchAsync(request with { StartUtc = end.AddHours(1), EndUtc = end.AddHours(2) },
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable, expired.Outcome);
        Assert.Contains($"Time-data coverage ends at {end:O}", expired.Message, StringComparison.Ordinal);
        Assert.Contains("No approximation was substituted", expired.Message, StringComparison.Ordinal);

        // A window that starts before coverage is unavailable but is not reported as expiry.
        var first = data.EarthOrientationCoverage.StartUtc;
        var gap = await input.Planner.SearchAsync(request with { StartUtc = first.AddTicks(-1), EndUtc = first.AddHours(1) },
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable, gap.Outcome);
        Assert.DoesNotContain("coverage ends", gap.Message, StringComparison.Ordinal);

        input.Times.Current = null;
        foreach (var (problem, expected) in new[]
        {
            (CameraAgentOrbitalTimeDataProblem.OverrideInvalid, "The bundled pair is not used while an override is configured"),
            (CameraAgentOrbitalTimeDataProblem.OverrideIncomplete, "The bundled pair is not used while an override is configured"),
            (CameraAgentOrbitalTimeDataProblem.BundledInvalid, "The bundled time-data pair is missing or failed verification")
        })
        {
            input.Times.Unavailable = problem;
            var response = await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable, response.Outcome);
            Assert.Contains(expected, response.Message, StringComparison.Ordinal);
        }
        Assert.AreEqual(0, input.Engine.Searches);
        Assert.IsNull(input.Engine.BoundTime, "no engine is bound to substitute or nominal-zero time data");
        Assert.Contains("coverage has run out", CameraAgentSatellitePlanningUiService.Describe(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable),
            StringComparison.Ordinal);
    }

    private async Task<OrbitalTimeData?> ResolveAsync(PlanningTestInputs.Settings options, string directory)
    {
        using var source = new CameraAgentPinnedOrbitalTimeSource(options, directory);
        var resolution = await source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Bundled, resolution.Source);
        Assert.AreEqual(resolution.Data is null ? CameraAgentOrbitalTimeDataProblem.BundledInvalid : CameraAgentOrbitalTimeDataProblem.None,
            resolution.Problem);
        return resolution.Data;
    }

    private static string CopyBundle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hvo-bundled-time-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(CameraAgentPinnedOrbitalTimeSource.BundledDirectory))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        return directory;
    }
}
