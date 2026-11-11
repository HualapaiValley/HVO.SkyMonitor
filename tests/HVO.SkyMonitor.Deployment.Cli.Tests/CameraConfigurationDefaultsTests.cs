using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The installer's configuration-default registry (Refs #1779). Each shipped generation still writes exactly what it
/// shipped, so the instances it installed are recognised; the newest entry is what the installer writes now, and a change
/// to that output without a new named entry fails here; each entry describes its change truthfully; no generation moves
/// the rig profile or schedule identity; and the CameraAgent image declares the generations whose adoption needs it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraConfigurationDefaultsTests
{
    /// <summary>
    /// What to do when a frozen identity no longer matches: the installer recognises completed instances by these
    /// outputs, so a change to what it writes must be a new generation, not an edit of a shipped one.
    /// </summary>
    internal const string AddAGeneration =
        "CameraConfiguration.Generate changed what a shipped configuration-default generation writes. Instances installed " +
        "under it would no longer be recognised and their re-runs would be refused. Keep every shipped generation's output " +
        "unchanged: add a new named entry at the end of CameraConfigurationDefaults.Shipped, with an operator-facing " +
        "description of the change (and RequiresImageSupport if the CameraAgent image must support it), produce the new " +
        "output only for that generation, and freeze its hash in CameraConfigurationDefaultsTests.FrozenGenerations and its " +
        "output in tests/fixtures/installer-configuration-defaults.";

    /// <summary>
    /// Each shipped generation's canonical identity for <see cref="IlluminationModeTests.Request"/> with omitted lighting,
    /// oldest first. The last is the installer's current default-configuration hash.
    /// </summary>
    internal static readonly (string Id, string Sha256)[] FrozenGenerations =
    [
        // The value IlluminationModeTests froze before the cloud steps (Refs #1722) re-froze it, i.e. what every earlier installer wrote.
        ("installer-defaults-1", "6ec0a9605d346d76f5869accda8a5ac50b32d3cfda8942dbb05b0a36085e7f41"),
        ("installer-defaults-2", "3694bb506f528030cb2b61e6a8c6ba01f8057357afb186d6b876eecf055a2141")
    ];

    /// <summary>The installer's current default-configuration hash: that of the newest frozen generation.</summary>
    internal static string CurrentFrozenSha256 => FrozenGenerations[^1].Sha256;

    /// <summary>The application identity the frozen fixtures were written for.</summary>
    internal static readonly Guid FixtureIdentity = Guid.Parse("5afd133a-ddd2-4d1a-975b-a957c29784a5");

    private static readonly Guid ApplicationIdentity = Guid.Parse("65c0dd43-6490-4f10-b972-0f68c79e45a8");

    private static readonly string?[] Lighting = [null, "LegacyScalarSolar", "SolarDriven", "ControlledNight"];

    [TestMethod]
    public void TheFrozenDefaultConfigurationHash_IsTheNewestRegistryEntry()
    {
        CollectionAssert.AreEqual(
            FrozenGenerations.Select(static generation => generation.Id).ToArray(),
            CameraConfigurationDefaults.Shipped.Select(static generation => generation.Id).ToArray(),
            "Every shipped generation, and only those, has a frozen identity, in order. " + AddAGeneration);
        Assert.AreSame(CameraConfigurationDefaults.Shipped[^1], CameraConfigurationDefaults.Current);
        Assert.AreEqual(FrozenGenerations[^1].Id, CameraConfigurationDefaults.Current.Id);

        // What the installer writes now is the newest entry's output, and that output is still the frozen one.
        var written = CameraConfiguration.Generate(IlluminationModeTests.Request(null), ApplicationIdentity);
        Assert.AreEqual(CurrentFrozenSha256, written.Sha256, AddAGeneration);
        Assert.AreEqual(
            CameraConfigurationDefaults.Current.Generate(IlluminationModeTests.Request(null), ApplicationIdentity),
            written);
        foreach (var (id, sha256) in FrozenGenerations)
        {
            var generation = CameraConfigurationDefaults.Shipped.Single(candidate => candidate.Id == id);
            Assert.AreEqual(sha256, generation.Generate(IlluminationModeTests.Request(null), ApplicationIdentity).Sha256,
                $"{id}: {AddAGeneration}");
        }
    }

    [TestMethod]
    public void EveryShippedGeneration_StillWritesItsFrozenFixtureByteForByte()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "installer-configuration-defaults");
        CollectionAssert.AreEqual(
            CameraConfigurationDefaults.Shipped.Select(static generation => $"{generation.Id}.camera-module.json").ToArray(),
            Directory.EnumerateFiles(folder, "*.camera-module.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray(),
            "Every shipped generation, and only those, has a frozen fixture. " + AddAGeneration);
        foreach (var generation in CameraConfigurationDefaults.Shipped)
        {
            var fixture = File.ReadAllText(Path.Combine(folder, $"{generation.Id}.camera-module.json"));
            var written = generation.Generate(IlluminationModeTests.Request(null), FixtureIdentity);

            Assert.AreEqual(fixture, written.Json + "\n", $"{generation.Id}: {AddAGeneration}");
            Assert.AreEqual(written.Sha256, CanonicalSha256(fixture));
        }
    }

    [TestMethod]
    public void TheCloudGeneration_IsDescribedExactlyAsItsPlanItemNamesIt()
    {
        var cloud = CameraConfigurationDefaults.Shipped.Single(static generation => generation.Id == "installer-defaults-2");

        Assert.AreEqual("pipeline: 11 → 13 steps (adds CloudAssessment, CloudPresentation)", cloud.Change);
        Assert.IsTrue(cloud.RequiresImageSupport, "the cloud steps need an image that includes the change of Refs #1777");
        Assert.IsNull(CameraConfigurationDefaults.Shipped[0].Change);
        Assert.IsFalse(CameraConfigurationDefaults.Shipped[0].RequiresImageSupport);
    }

    /// <summary>A generation that describes a pipeline change describes the one its output makes.</summary>
    [TestMethod]
    public void EachPipelineChange_IsTheOneItsOutputMakes()
    {
        for (var index = 1; index < CameraConfigurationDefaults.Shipped.Count; index++)
        {
            var previous = CameraConfigurationDefaults.Shipped[index - 1];
            var generation = CameraConfigurationDefaults.Shipped[index];
            var before = StepIds(previous.Generate(IlluminationModeTests.Request(null), FixtureIdentity).Json);
            var after = StepIds(generation.Generate(IlluminationModeTests.Request(null), FixtureIdentity).Json);
            if (!generation.Change!.StartsWith("pipeline:", StringComparison.Ordinal))
            {
                CollectionAssert.AreEqual(before, after, $"{generation.Id} changes the pipeline but does not say so");
                continue;
            }
            var added = after.Except(before).ToArray();
            var removed = before.Except(after).ToArray();
            var changes = new List<string>();
            if (added.Length > 0) changes.Add($"adds {string.Join(", ", added)}");
            if (removed.Length > 0) changes.Add($"removes {string.Join(", ", removed)}");
            Assert.AreEqual($"pipeline: {before.Length} → {after.Length} steps ({string.Join("; ", changes)})", generation.Change);
        }
    }

    [TestMethod]
    public void NoGeneration_MovesTheRigProfileOrScheduleIdentity_AndEachChangesTheConfiguration()
    {
        foreach (var mode in Lighting)
        {
            var request = IlluminationModeTests.Request(mode);
            var outputs = CameraConfigurationDefaults.Shipped.Select(generation => generation.Generate(request, ApplicationIdentity)).ToArray();
            foreach (var output in outputs)
            {
                Assert.AreEqual(outputs[0].RigProfileSha256, output.RigProfileSha256);
                Assert.AreEqual(outputs[0].ScheduleSha256, output.ScheduleSha256);
                Assert.AreEqual(outputs[0].RigProfileName, output.RigProfileName);
                Assert.AreEqual(outputs[0].RigProfileVersion, output.RigProfileVersion);
                Assert.AreEqual(outputs[0].ScheduleSchemaVersion, output.ScheduleSchemaVersion);
                Assert.AreEqual(outputs[0].ScheduleState, output.ScheduleState);
            }
            Assert.AreEqual(outputs.Length, outputs.Select(static output => output.Sha256).Distinct(StringComparer.Ordinal).Count(),
                "a generation that writes what an earlier one wrote is not a change");
        }
    }

    [TestMethod]
    public void Recognize_NamesTheGenerationAndLightingOfEveryShippedIdentity()
    {
        foreach (var generation in CameraConfigurationDefaults.Shipped)
        {
            foreach (var mode in Lighting)
            {
                var written = generation.Generate(IlluminationModeTests.Request(mode), ApplicationIdentity);
                // The lighting asked for now does not decide: the configuration records the installed lighting.
                foreach (var asked in Lighting)
                {
                    var recognized = CameraConfigurationDefaults.Recognize(
                        IlluminationModeTests.Request(asked), ApplicationIdentity, written.Sha256, written.RigProfileSha256, written.ScheduleSha256);

                    Assert.IsNotNull(recognized, $"{generation.Id} / {mode ?? "omitted"}");
                    Assert.AreSame(generation, recognized.Generation);
                    Assert.AreEqual(mode, recognized.IlluminationMode);
                    Assert.AreEqual(written, recognized.Configuration);
                }
            }
        }
    }

    [TestMethod]
    public void Recognize_DoesNotRecognizeAnIdentityNoGenerationWritesForTheseAnswers()
    {
        var written = CameraConfigurationDefaults.Current.Generate(IlluminationModeTests.Request(null), ApplicationIdentity);
        var edited = JsonNode.Parse(written.Json)!.AsObject();
        edited["module"]!["options"]!["seed"] = 2026;
        var editedSha256 = CanonicalSha256(edited.ToJsonString());

        // An edited configuration, another instance's configuration, and a recorded rig or schedule identity that
        // no generation writes are each unknown.
        Assert.IsNull(CameraConfigurationDefaults.Recognize(
            IlluminationModeTests.Request(null), ApplicationIdentity, editedSha256, written.RigProfileSha256, written.ScheduleSha256));
        Assert.IsNull(CameraConfigurationDefaults.Recognize(
            IlluminationModeTests.Request(null), Guid.NewGuid(), written.Sha256, written.RigProfileSha256, written.ScheduleSha256));
        Assert.IsNull(CameraConfigurationDefaults.Recognize(
            IlluminationModeTests.Request(null), ApplicationIdentity, written.Sha256, new string('0', 64), written.ScheduleSha256));
        Assert.IsNull(CameraConfigurationDefaults.Recognize(
            IlluminationModeTests.Request(null), ApplicationIdentity, written.Sha256, written.RigProfileSha256, new string('0', 64)));
    }

    /// <summary>Whitespace and property order are not a configuration change: the canonical identity ignores them.</summary>
    [TestMethod]
    public void Reformatting_KeepsTheCanonicalIdentity()
    {
        var written = CameraConfigurationDefaults.Current.Generate(IlluminationModeTests.Request(null), ApplicationIdentity);
        var compact = JsonNode.Parse(written.Json)!.ToJsonString();
        var reordered = new JsonObject(JsonNode.Parse(written.Json)!.AsObject().Reverse()
            .Select(static property => KeyValuePair.Create(property.Key, property.Value?.DeepClone()))).ToJsonString();

        Assert.AreNotEqual(written.Json, compact);
        Assert.AreEqual(written.Sha256, CanonicalSha256(compact));
        Assert.AreEqual(written.Sha256, CanonicalSha256(reordered));
    }

    [TestMethod]
    public void Use_RefusesARegistryThatIsNotNumberedInOrderOrDoesNotDescribeItsChanges()
    {
        var first = CameraConfigurationDefaults.Shipped[0];
        var second = CameraConfigurationDefaults.Shipped[1];

        Assert.ThrowsExactly<ArgumentException>(() => CameraConfigurationDefaults.Use([]));
        Assert.ThrowsExactly<ArgumentException>(() => CameraConfigurationDefaults.Use([second]));
        Assert.ThrowsExactly<ArgumentException>(() => CameraConfigurationDefaults.Use([first, second with { Change = " " }]));
        Assert.ThrowsExactly<ArgumentException>(() => CameraConfigurationDefaults.Use([first with { Change = "x" }, second]));
        Assert.ThrowsExactly<ArgumentException>(() => CameraConfigurationDefaults.Use([first, second, second]));
        CameraConfigurationDefaults.Validate(CameraConfigurationDefaults.Shipped);
        Assert.AreSame(CameraConfigurationDefaults.Shipped, CameraConfigurationDefaults.Known);
    }

    [TestMethod]
    public async Task Use_ReplacesTheRegistryOnlyForTheCallingFlowAndOnlyUntilDisposed()
    {
        static async Task<string> UnderTheFirstGenerationAsync()
        {
            using var scope = CameraConfigurationDefaults.Use([CameraConfigurationDefaults.Shipped[0]]);
            await Task.Yield();
            return CameraConfiguration.Generate(IlluminationModeTests.Request(null), ApplicationIdentity).Sha256;
        }

        Assert.AreEqual(FrozenGenerations[0].Sha256, await UnderTheFirstGenerationAsync());
        // The replacement never leaks back into the flow that called it, nor outlives its scope.
        Assert.AreSame(CameraConfigurationDefaults.Shipped, CameraConfigurationDefaults.Known);
        using (CameraConfigurationDefaults.Use([CameraConfigurationDefaults.Shipped[0]]))
        {
            Assert.AreEqual("installer-defaults-1", CameraConfigurationDefaults.Current.Id);
        }
        Assert.AreSame(CameraConfigurationDefaults.Shipped, CameraConfigurationDefaults.Known);
        Assert.AreEqual(CurrentFrozenSha256, CameraConfiguration.Generate(IlluminationModeTests.Request(null), ApplicationIdentity).Sha256);
    }

    [TestMethod]
    [DataRow("installer-defaults-2", 2)]
    [DataRow("installer-defaults-1", 1)]
    [DataRow("installer-defaults-10", 10)]
    [DataRow("installer-defaults-02", 0)]
    [DataRow("installer-defaults-0", 0)]
    [DataRow("installer-defaults-", 0)]
    [DataRow("installer-defaults--2", 0)]
    [DataRow("installer-defaults-+2", 0)]
    [DataRow("installer-defaults-2 ", 0)]
    [DataRow("Installer-defaults-2", 0)]
    [DataRow("2", 0)]
    [DataRow("", 0)]
    [DataRow(null, 0)]
    public void DeclaredByImage_ReadsOnlyAGenerationName(string? label, int expected)
        => Assert.AreEqual(expected, CameraConfigurationDefaults.DeclaredByImage(label));

    [TestMethod]
    public void RequiredImageGeneration_IsTheNewestAdoptedGenerationThatNeedsTheImage()
    {
        var shipped = CameraConfigurationDefaults.Shipped;
        var later = new CameraConfigurationGeneration(3, "test-only change", false, shipped[1].Generate);

        Assert.AreEqual(0, CameraConfigurationDefaults.RequiredImageGeneration([]));
        Assert.AreEqual(2, CameraConfigurationDefaults.RequiredImageGeneration([shipped[1]]));
        Assert.AreEqual(2, CameraConfigurationDefaults.RequiredImageGeneration([shipped[1], later]));
        Assert.AreEqual(0, CameraConfigurationDefaults.RequiredImageGeneration([later]));
        Assert.AreEqual(3, CameraConfigurationDefaults.RequiredImageGeneration([shipped[1], later with { RequiresImageSupport = true }]));
        Assert.AreEqual(
            "pipeline: 11 → 13 steps (adds CloudAssessment, CloudPresentation); test-only change",
            CameraConfigurationDefaults.Describe([shipped[1], later]));
        CollectionAssert.AreEqual(new[] { shipped[1] }, CameraConfigurationDefaults.AdoptedAfter(shipped[0]).ToArray());
        Assert.IsEmpty(CameraConfigurationDefaults.AdoptedAfter(shipped[1]));
    }

    /// <summary>
    /// The CameraAgent image declares the newest generation it runs, which must cover every generation whose adoption
    /// needs the image and must not claim one this installer does not know.
    /// </summary>
    [TestMethod]
    public void TheCameraAgentImage_DeclaresEveryGenerationWhoseAdoptionNeedsIt()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            directory = directory.Parent;
        var dockerfile = File.ReadAllText(Path.Combine(
            directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found."),
            "src", "HVO.SkyMonitor.CameraAgent", "Dockerfile"));
        var declared = Regex.Matches(
                dockerfile, "^\\s*(?:LABEL\\s+)?io\\.hvo\\.skymonitor\\.configuration-defaults=\"([^\"]*)\"",
                RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5))
            .Select(static match => match.Groups[1].Value)
            .ToArray();

        Assert.HasCount(1, declared, "the CameraAgent image declares io.hvo.skymonitor.configuration-defaults once");
        var number = CameraConfigurationDefaults.DeclaredByImage(declared[0]);
        Assert.IsGreaterThanOrEqualTo(CameraConfigurationDefaults.RequiredImageGeneration(CameraConfigurationDefaults.Shipped), number,
            "the image must declare every generation whose adoption needs it");
        Assert.IsLessThanOrEqualTo(CameraConfigurationDefaults.Current.Number, number,
            "the image must not declare a generation this installer does not know");
    }

    internal static string[] StepIds(string configurationJson)
    {
        using var document = JsonDocument.Parse(configurationJson);
        return [.. document.RootElement.GetProperty("pipeline").GetProperty("steps").EnumerateArray()
            .Select(static step => step.GetProperty("id").GetString()!)];
    }

    /// <summary>The canonical identity of <paramref name="json"/>, as the installer and CameraAgent compute it.</summary>
    internal static string CanonicalSha256(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Convert.ToHexStringLower(Convert.FromHexString(CaptureContractJson.ComputeCanonicalJsonSha256(document.RootElement)));
    }
}
