using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

public sealed partial class SqliteCalibrationLibraryStoreTests
{
    private const double LowGain = 82;
    private const double HighGain = 100;

    [TestMethod]
    public async Task SelectFamilyMastersAsync_SelectsEachGainsOwnVerifiedMasters()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFamilyFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var low = fixture.Member(LowGain);
            var family = fixture.Family(low with { Masters = [.. low.Masters.Reverse()] }, fixture.Member(HighGain));

            foreach (var gain in new[] { LowGain, HighGain })
            {
                var result = await store.SelectFamilyMastersAsync(
                    family, fixture.Lights[gain], CancellationToken.None).ConfigureAwait(false);

                Assert.IsTrue(result.IsSelected, result.ReasonCode);
                Assert.AreEqual(CalibrationFamilyReasonCodes.Selected, result.ReasonCode);
                Assert.AreEqual(CalibrationFamilyContractJson.ComputeIdentitySha256(family), result.Selection!.FamilyIdentitySha256);
                Assert.AreEqual(MemberId(gain), result.Selection.MemberId);
                CollectionAssert.AreEqual(
                    LibraryMasters(fixture.Bundles[gain])
                        .Select(static master => (master.Kind, master.ArtifactId, master.PayloadSha256.ToUpperInvariant()))
                        .ToArray(),
                    result.Selection.Masters.Select(static master => (master.Kind, master.ArtifactId, master.PayloadSha256)).ToArray());
            }
            CollectionAssert.AreEqual(
                CalibrationReferenceKinds.All.ToArray(),
                (await store.SelectFamilyMastersAsync(family, fixture.Lights[LowGain], CancellationToken.None).ConfigureAwait(false))
                    .Selection!.Masters.Select(static master => master.Kind).ToArray());

            var between = fixture.Lights[LowGain] with
            {
                Controls = fixture.Lights[LowGain].Controls with { RequestedGain = 90, EffectiveGain = 90 }
            };
            var excluded = await store.SelectFamilyMastersAsync(family, between, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(excluded.IsSelected);
            Assert.IsNull(excluded.Selection);
            Assert.AreEqual(CalibrationFamilyReasonCodes.MissingMember, excluded.ReasonCode);

            // A defect mask may serve every gain of its mask family.
            var lowDefect = fixture.Master(LowGain, CalibrationReferenceKinds.Defect);
            var sharedDefect = fixture.Family(fixture.Member(LowGain), WithMaster(fixture.Member(HighGain), lowDefect));
            var shared = await store.SelectFamilyMastersAsync(
                sharedDefect, fixture.Lights[HighGain], CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(shared.IsSelected, shared.ReasonCode);
            Assert.AreEqual(lowDefect.ArtifactId, shared.Selection!.Masters[3].ArtifactId);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task SelectFamilyMastersAsync_ExcludesAMasterWhoseStoredConditionsContradictItsMember()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFamilyFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var high = fixture.Member(HighGain);
            var light = fixture.Lights[HighGain];
            var exposure = high.MinimumLightExposure;
            var bias = fixture.Master(HighGain, CalibrationReferenceKinds.Bias);
            var dark = fixture.Master(HighGain, CalibrationReferenceKinds.Dark);
            var otherModel = light.Profiles.Calibration with { Sha256 = new string('F', 64) };
            var otherLayout = light.Layout with
            {
                StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
                LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
            };
            (string Name, CalibrationFamilyV1 Family, ReconstructionDescriptor Light)[] cases =
            [
                ("kind", fixture.Family(high with
                {
                    Masters = [.. high.Masters.Select(master =>
                        master.Kind == CalibrationReferenceKinds.Bias ? dark with { Kind = CalibrationReferenceKinds.Bias } :
                        master.Kind == CalibrationReferenceKinds.Dark ? bias with { Kind = CalibrationReferenceKinds.Dark } :
                        master)]
                }), light),
                ("payload-hash", fixture.Family(WithMaster(high, bias with { PayloadSha256 = new string('A', 64) })), light),
                ("bias-gain", fixture.Family(WithMaster(high, fixture.Master(LowGain, CalibrationReferenceKinds.Bias))), light),
                ("dark-gain", fixture.Family(WithMaster(high, fixture.Master(LowGain, CalibrationReferenceKinds.Dark))), light),
                ("flat-gain", fixture.Family(WithMaster(high, fixture.Master(LowGain, CalibrationReferenceKinds.Flat))), light),
                ("offset", fixture.Family(high with { Offset = 2 }), WithOffset(light, 2)),
                ("layout", fixture.Family(high with { InputLayout = otherLayout }), light with { Layout = otherLayout }),
                ("minimum-temperature", fixture.Family(high with { MinimumTemperatureC = -12 }), light),
                ("maximum-temperature", fixture.Family(high with { MaximumTemperatureC = -8 }), light),
                ("minimum-exposure", fixture.Family(high with { MinimumLightExposure = exposure / 2 }), light),
                ("maximum-exposure", fixture.Family(high with { MaximumLightExposure = exposure * 2 }), light),
                ("acquisition-model", fixture.Family(high) with { AcquisitionModel = otherModel },
                    light with { Profiles = light.Profiles with { Calibration = otherModel } }),
                ("agent", fixture.Family(high) with { AgentId = "other-agent" },
                    light with { Capture = light.Capture with { AgentId = "other-agent" } }),
                ("rig", fixture.Family(high) with { RigId = "other-rig" },
                    light with { Capture = light.Capture with { RigId = "other-rig" } }),
                ("rig-profile", fixture.Family(high) with { RigProfileSha256 = new string('B', 64) },
                    light with { Profiles = light.Profiles with { Rig = light.Profiles.Rig with { Sha256 = new string('B', 64) } } }),
                ("sensor-profile", fixture.Family(high) with { SensorProfileSha256 = new string('C', 64) },
                    light with { Profiles = light.Profiles with { Sensor = light.Profiles.Sensor with { Sha256 = new string('C', 64) } } })
            ];

            foreach (var (name, family, input) in cases)
            {
                Assert.IsTrue(CalibrationFamilyContract.Validate(family).IsValid, name);
                Assert.IsTrue(CalibrationFamilyContract.Evaluate(family, FamilyInput(input)).IsApplicable, name);

                var result = await store.SelectFamilyMastersAsync(family, input, CancellationToken.None).ConfigureAwait(false);

                Assert.IsFalse(result.IsSelected, name);
                Assert.AreEqual(CalibrationFamilyReasonCodes.MasterMismatch, result.ReasonCode, name);
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task SelectFamilyMastersAsync_ExcludesAMissingUnpublishedStaleOrCorruptLibraryMaster()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFamilyFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var high = fixture.Member(HighGain);
            var light = fixture.Lights[HighGain];
            var applicability = fixture.Bundles[HighGain].Applicability;
            var source = fixture.Bundles[HighGain].Artifacts.First(static artifact =>
                artifact.Role == CalibrationLibraryArtifactRoles.Source && artifact.Kind == CalibrationReferenceKinds.Bias);
            var earlier = high with { EffectiveFromUtc = applicability.EffectiveFromUtc.AddDays(-2) };
            (string Name, CalibrationFamilyV1 Family, ReconstructionDescriptor Light, string Reason)[] cases =
            [
                ("unknown-artifact", fixture.Family(WithMaster(high, new CalibrationFamilyMasterV1(
                    CalibrationReferenceKinds.Bias, Guid.NewGuid(), new string('A', 64)))), light,
                    CalibrationFamilyReasonCodes.MasterMissing),
                ("source-artifact", fixture.Family(WithMaster(high, new CalibrationFamilyMasterV1(
                    CalibrationReferenceKinds.Bias, source.ArtifactId, source.PayloadSha256))), light,
                    CalibrationFamilyReasonCodes.MasterMissing),
                ("before-effective", fixture.Family(earlier), ObservedAt(light, applicability.EffectiveFromUtc.AddHours(-1)),
                    CalibrationFamilyReasonCodes.MasterStale),
                ("after-effective", fixture.Family(high), ObservedAt(light, applicability.EffectiveUntilUtc!.Value),
                    CalibrationFamilyReasonCodes.MasterStale)
            ];
            foreach (var (name, family, input, reason) in cases)
            {
                Assert.IsTrue(CalibrationFamilyContract.Evaluate(family, FamilyInput(input)).IsApplicable, name);

                var result = await store.SelectFamilyMastersAsync(family, input, CancellationToken.None).ConfigureAwait(false);

                Assert.IsFalse(result.IsSelected, name);
                Assert.AreEqual(reason, result.ReasonCode, name);
            }

            var bundleId = fixture.Bundles[HighGain].BundleId;
            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE calibration_library_bundles SET publication_state = 'quarantined' WHERE bundle_id = $bundle;";
                command.Parameters.AddWithValue("$bundle", bundleId);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            Assert.AreEqual(
                CalibrationFamilyReasonCodes.MasterUnpublished,
                (await store.SelectFamilyMastersAsync(fixture.Family(high), light, CancellationToken.None).ConfigureAwait(false)).ReasonCode);

            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE calibration_library_bundles SET publication_state = 'published', agent_id = 'tampered-agent'
                    WHERE bundle_id = $bundle;
                    """;
                command.Parameters.AddWithValue("$bundle", bundleId);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            Assert.AreEqual(
                CalibrationFamilyReasonCodes.MasterCorrupt,
                (await store.SelectFamilyMastersAsync(fixture.Family(high), light, CancellationToken.None).ConfigureAwait(false)).ReasonCode);
            Assert.IsTrue((await store.SelectFamilyMastersAsync(
                fixture.Family(fixture.Member(LowGain)), fixture.Lights[LowGain], CancellationToken.None).ConfigureAwait(false)).IsSelected);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task SelectFamilyMastersAsync_ExcludesAPayloadThatDoesNotHashOrCannotBeRead()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFamilyFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var family = fixture.Family(fixture.Member(LowGain), fixture.Member(HighGain));
            var light = fixture.Lights[HighGain];
            var flat = fixture.Bundles[HighGain].Artifacts.Single(static artifact =>
                artifact.Role == CalibrationLibraryArtifactRoles.Master && artifact.Kind == CalibrationReferenceKinds.Flat);
            var path = Path.Combine(root, string.Concat(
                flat.ManifestRelativePath.AsSpan(0, flat.ManifestRelativePath.Length - ".json".Length), ".bin")
                .Replace('/', Path.DirectorySeparatorChar));
            var original = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            Assert.AreEqual(flat.PayloadSha256, Convert.ToHexString(SHA256.HashData(original)), ignoreCase: true);
            Assert.IsTrue((await store.SelectFamilyMastersAsync(family, light, CancellationToken.None).ConfigureAwait(false)).IsSelected);

            var flipped = original.ToArray();
            flipped[^1] ^= 0x01;
            byte[][] corruptions = [flipped, [.. original, 0], original[..^1]];
            foreach (var corruption in corruptions)
            {
                await File.WriteAllBytesAsync(path, corruption).ConfigureAwait(false);

                var corrupt = await store.SelectFamilyMastersAsync(family, light, CancellationToken.None).ConfigureAwait(false);

                Assert.IsFalse(corrupt.IsSelected, corruption.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.AreEqual(CalibrationFamilyReasonCodes.MasterCorrupt, corrupt.ReasonCode);
            }

            File.Delete(path);
            var unreadable = await store.SelectFamilyMastersAsync(family, light, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(unreadable.IsSelected);
            Assert.AreEqual(CalibrationFamilyReasonCodes.MasterUnreadable, unreadable.ReasonCode);
            Assert.IsTrue((await store.SelectFamilyMastersAsync(
                family, fixture.Lights[LowGain], CancellationToken.None).ConfigureAwait(false)).IsSelected);

            await File.WriteAllBytesAsync(path, original).ConfigureAwait(false);
            Assert.IsTrue((await store.SelectFamilyMastersAsync(family, light, CancellationToken.None).ConfigureAwait(false)).IsSelected);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task SelectFamilyMastersAsync_LeavesTheSingleBundleSelectionUnchanged()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFamilyFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            _ = await store.ActivateAsync(
                fixture.Bundles[LowGain].BundleId, "activate-low", 0, "operator", null,
                CancellationToken.None).ConfigureAwait(false);
            var selected = await store.SelectAsync(fixture.Lights[LowGain], CancellationToken.None).ConfigureAwait(false);
            var rejected = await store.SelectAsync(fixture.Lights[HighGain], CancellationToken.None).ConfigureAwait(false);
            var before = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(selected.IsSelected, selected.ReasonCode);
            Assert.AreEqual(CalibrationLibraryReasonCodes.IncompatibleConditions, rejected.ReasonCode);

            var family = fixture.Family(fixture.Member(LowGain), fixture.Member(HighGain));
            foreach (var gain in new[] { LowGain, HighGain })
            {
                Assert.IsTrue((await store.SelectFamilyMastersAsync(
                    family, fixture.Lights[gain], CancellationToken.None).ConfigureAwait(false)).IsSelected);
            }

            Assert.AreEqual(StateFacts(before), StateFacts(await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)));
            Assert.AreEqual(
                SelectionFacts(selected),
                SelectionFacts(await store.SelectAsync(fixture.Lights[LowGain], CancellationToken.None).ConfigureAwait(false)));
            Assert.AreEqual(
                SelectionFacts(rejected),
                SelectionFacts(await store.SelectAsync(fixture.Lights[HighGain], CancellationToken.None).ConfigureAwait(false)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task<FamilyFixture> CreateFamilyFixtureAsync(string root)
    {
        Directory.CreateDirectory(root);
        var options = CreateOptions(root);
        await new SqliteRawCaptureJournal(
            Path.Combine(root, "journal", "raw-ingress.db"), 1)
            .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var telemetry = new CaptureControlTelemetry();
        using var admission = new CaptureAdmissionCoordinator(
            new InitializedIngress(), options, TimeProvider.System, telemetry);
        await admission.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var store = new SqliteCalibrationLibraryStore(new InitializedIngress(), options, TimeProvider.System);
        _ = await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var accessor = new CameraAgentConfigurationAccessor();
        accessor.SetConfiguration(CreateVirtualConfiguration());
        using var coordinator = new VirtualCalibrationAcquisitionCoordinator(
            store,
            new CalibrationArtifactPublisher(options, NullCalibrationPublicationFaultInjector.Instance),
            admission,
            accessor,
            TimeProvider.System);
        var effectiveFrom = DateTimeOffset.UtcNow.AddDays(-1);
        var bundles = new Dictionary<double, CalibrationLibraryBundleV1>();
        var lights = new Dictionary<double, ReconstructionDescriptor>();
        foreach (var gain in new[] { LowGain, HighGain })
        {
            var job = await coordinator.AcquireAsync(
                new VirtualCalibrationAcquisitionRequestV1(
                    VirtualCalibrationAcquisitionRequestV1.CurrentSchemaVersion,
                    FormattableString.Invariant($"family-fixture-{gain}"),
                    gain,
                    1,
                    -10,
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(3),
                    TimeSpan.FromMilliseconds(32),
                    effectiveFrom,
                    effectiveFrom.AddYears(1),
                    new VirtualCalibrationSourceModelV1 { Seed = 208 },
                    "operator",
                    "family fixture"),
                CancellationToken.None).ConfigureAwait(false);
            var bundle = (await store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false))
                .Single(snapshot => snapshot.Bundle.Applicability.MinimumGain == gain).Bundle;
            var source = bundle.Artifacts.First(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Source);
            var parsed = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(Path.Combine(
                root, source.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar))).ConfigureAwait(false));
            Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
            var descriptor = parsed.Document!.Manifest!.Descriptor;
            var started = effectiveFrom.AddHours(1);
            var ended = started.Add(job.Plan.ApplicableLightExposure);
            bundles.Add(gain, bundle);
            lights.Add(gain, descriptor with
            {
                Timing = new CaptureTimingDescriptor(started, started, ended, ended, ended),
                Controls = descriptor.Controls with
                {
                    RequestedExposure = job.Plan.ApplicableLightExposure,
                    EffectiveExposure = job.Plan.ApplicableLightExposure
                }
            });
        }
        return new FamilyFixture(store, bundles, lights);
    }

    private static (string? Bundle, long Version, string? Reason, DateTimeOffset? SelectedUtc, DateTimeOffset UpdatedUtc) StateFacts(
        CalibrationLibraryStateSnapshot state)
        => (state.ActiveBundle?.BundleIdentitySha256, state.Version, state.LastSelectionReason, state.LastSelectionUtc, state.UpdatedUtc);

    private static (string? Bundle, string Reason, long Version) SelectionFacts(CalibrationLibrarySelectionResult result)
        => (result.Bundle?.BundleIdentitySha256, result.ReasonCode, result.StateVersion);

    private static string MemberId(double gain)
        => FormattableString.Invariant($"gain-{gain}");

    private static IEnumerable<CalibrationLibraryArtifactV1> LibraryMasters(CalibrationLibraryBundleV1 bundle)
        => bundle.Artifacts.Where(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Master);

    private static CalibrationFamilyMemberV1 WithMaster(CalibrationFamilyMemberV1 member, CalibrationFamilyMasterV1 replacement)
        => member with
        {
            Masters = [.. member.Masters.Select(master => master.Kind == replacement.Kind ? replacement : master)]
        };

    private static ReconstructionDescriptor WithOffset(ReconstructionDescriptor light, double offset)
        => light with { Controls = light.Controls with { RequestedOffset = offset, EffectiveOffset = offset } };

    private static ReconstructionDescriptor ObservedAt(ReconstructionDescriptor light, DateTimeOffset started)
    {
        var ended = started.Add(light.Controls.EffectiveExposure);
        return light with { Timing = new CaptureTimingDescriptor(started, started, ended, ended, ended) };
    }

    private static CalibrationFamilyInputV1 FamilyInput(ReconstructionDescriptor light)
        => new(
            light.Capture.AgentId,
            light.Capture.RigId,
            light.Profiles.Rig.Sha256,
            light.Profiles.Sensor.Sha256,
            light.Profiles.Calibration,
            light.Profiles.Mask.Sha256,
            light.Layout,
            light.Controls.EffectiveGain,
            light.Controls.EffectiveOffset,
            light.Controls.EffectiveTemperatureC,
            light.Controls.EffectiveExposure,
            light.Timing.ExposureStartedUtc);

    private sealed record FamilyFixture(
        SqliteCalibrationLibraryStore Store,
        IReadOnlyDictionary<double, CalibrationLibraryBundleV1> Bundles,
        IReadOnlyDictionary<double, ReconstructionDescriptor> Lights)
    {
        public CalibrationFamilyMasterV1 Master(double gain, string kind)
            => LibraryMasters(Bundles[gain])
                .Where(artifact => artifact.Kind == kind)
                .Select(static artifact => new CalibrationFamilyMasterV1(artifact.Kind, artifact.ArtifactId, artifact.PayloadSha256))
                .Single();

        public CalibrationFamilyMemberV1 Member(double gain)
        {
            var applicability = Bundles[gain].Applicability;
            return new CalibrationFamilyMemberV1(
                MemberId(gain),
                gain,
                applicability.MinimumOffset,
                applicability.InputLayout,
                applicability.MinimumTemperatureC!.Value,
                applicability.MaximumTemperatureC!.Value,
                applicability.MinimumLightExposure!.Value,
                applicability.MaximumLightExposure!.Value,
                applicability.EffectiveFromUtc,
                null,
                Lights[gain].Profiles.Mask.Sha256,
                [.. CalibrationReferenceKinds.All.Select(kind => Master(gain, kind))]);
        }

        public CalibrationFamilyV1 Family(params CalibrationFamilyMemberV1[] members)
        {
            var light = Lights[LowGain];
            return new CalibrationFamilyV1(
                CalibrationFamilyV1.CurrentSchemaVersion,
                "virtual-family",
                light.Capture.AgentId,
                light.Capture.RigId,
                light.Profiles.Rig.Sha256,
                light.Profiles.Sensor.Sha256,
                light.Profiles.Calibration,
                light.Profiles.Mask.Sha256,
                members);
        }
    }
}
