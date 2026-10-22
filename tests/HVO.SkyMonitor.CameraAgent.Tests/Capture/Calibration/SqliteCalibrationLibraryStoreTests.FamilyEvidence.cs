using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

public sealed partial class SqliteCalibrationLibraryStoreTests
{
    [TestMethod]
    public async Task SelectFamilyMastersAsync_ExcludesAMasterWhoseBundleEvidenceIsCorrupt()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFamilyFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var family = fixture.Family(fixture.Member(LowGain), fixture.Member(HighGain));
            var high = fixture.Member(HighGain).Masters;
            Assert.IsTrue((await store.SelectFamilyMastersAsync(
                family, fixture.Lights[HighGain], CancellationToken.None).ConfigureAwait(false)).IsSelected);
            Assert.IsNull(await store.ConfirmFamilyMastersAsync(high, CancellationToken.None).ConfigureAwait(false));

            foreach (var (name, path, corrupt) in EvidenceCorruptions(root, fixture.Bundles[HighGain]))
            {
                var original = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                await corrupt(original).ConfigureAwait(false);

                var selected = await store.SelectFamilyMastersAsync(
                    family, fixture.Lights[HighGain], CancellationToken.None).ConfigureAwait(false);

                Assert.IsFalse(selected.IsSelected, name);
                Assert.AreEqual(CalibrationFamilyReasonCodes.MasterCorrupt, selected.ReasonCode, name);
                Assert.AreEqual(
                    CalibrationFamilyReasonCodes.MasterCorrupt,
                    await store.ConfirmFamilyMastersAsync(high, CancellationToken.None).ConfigureAwait(false),
                    name);
                Assert.IsTrue((await store.SelectFamilyMastersAsync(
                    family, fixture.Lights[LowGain], CancellationToken.None).ConfigureAwait(false)).IsSelected, name);

                await File.WriteAllBytesAsync(path, original).ConfigureAwait(false);
                Assert.IsTrue((await store.SelectFamilyMastersAsync(
                    family, fixture.Lights[HighGain], CancellationToken.None).ConfigureAwait(false)).IsSelected, name);
                Assert.IsNull(await store.ConfirmFamilyMastersAsync(high, CancellationToken.None).ConfigureAwait(false), name);
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ConfirmFamilyMastersAsync_ForgetsTheCachedEvidenceItFindsCorrupt()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFamilyFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var family = fixture.Family(fixture.Member(LowGain), fixture.Member(HighGain));
            var high = fixture.Member(HighGain).Masters;
            Assert.IsTrue((await store.SelectFamilyMastersAsync(
                family, fixture.Lights[HighGain], CancellationToken.None).ConfigureAwait(false)).IsSelected);

            // One changed byte that keeps the marker's length and write time is invisible to the selection cache, which
            // compares only those; this is the window the regression targets. Only a forced validation reads it again.
            var profile = Path.Combine(
                root, fixture.Bundles[HighGain].ProfileRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var written = File.GetLastWriteTimeUtc(profile);
            var flipped = await File.ReadAllBytesAsync(profile).ConfigureAwait(false);
            flipped[0] ^= 0x01;
            await File.WriteAllBytesAsync(profile, flipped).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(profile, written);
            Assert.IsTrue((await store.SelectFamilyMastersAsync(
                family, fixture.Lights[HighGain], CancellationToken.None).ConfigureAwait(false)).IsSelected);

            Assert.AreEqual(
                CalibrationFamilyReasonCodes.MasterCorrupt,
                await store.ConfirmFamilyMastersAsync(high, CancellationToken.None).ConfigureAwait(false));

            // The failed confirmation must not leave the earlier validation behind for selection to reuse.
            var selected = await store.SelectFamilyMastersAsync(
                family, fixture.Lights[HighGain], CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(selected.IsSelected);
            Assert.AreEqual(CalibrationFamilyReasonCodes.MasterCorrupt, selected.ReasonCode);
            Assert.IsTrue((await store.SelectFamilyMastersAsync(
                family, fixture.Lights[LowGain], CancellationToken.None).ConfigureAwait(false)).IsSelected);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ConfirmFamilyMastersAsync_KeepsAnEarlierPassFromRepublishingEvidenceALaterValidationFoundCorrupt()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFamilyFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var family = fixture.Family(fixture.Member(LowGain), fixture.Member(HighGain));
            var high = fixture.Member(HighGain).Masters;
            var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reached = 0;
            store.BeforeValidatedEvidenceCachedForTest = async _ =>
            {
                if (Interlocked.Increment(ref reached) == 1)
                {
                    paused.SetResult();
                    await resume.Task.ConfigureAwait(false);
                }
            };

            // The earlier confirmation verifies intact evidence and is held before it caches what it verified.
            var earlier = store.ConfirmFamilyMastersAsync(high, CancellationToken.None);
            await paused.Task.ConfigureAwait(false);
            var profile = Path.Combine(
                root, fixture.Bundles[HighGain].ProfileRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var written = File.GetLastWriteTimeUtc(profile);
            var flipped = await File.ReadAllBytesAsync(profile).ConfigureAwait(false);
            flipped[0] ^= 0x01;
            await File.WriteAllBytesAsync(profile, flipped).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(profile, written);
            Assert.AreEqual(
                CalibrationFamilyReasonCodes.MasterCorrupt,
                await store.ConfirmFamilyMastersAsync(high, CancellationToken.None).ConfigureAwait(false));
            resume.SetResult();
            Assert.IsNull(await earlier.ConfigureAwait(false));
            store.BeforeValidatedEvidenceCachedForTest = null;

            // The later failure outranks the earlier pass, so selection reads the bytes again rather than trusting it.
            var selected = await store.SelectFamilyMastersAsync(
                family, fixture.Lights[HighGain], CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(selected.IsSelected);
            Assert.AreEqual(CalibrationFamilyReasonCodes.MasterCorrupt, selected.ReasonCode);
            Assert.IsTrue((await store.SelectFamilyMastersAsync(
                family, fixture.Lights[LowGain], CancellationToken.None).ConfigureAwait(false)).IsSelected);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ConfirmFamilyMastersAsync_ReportsAFrozenMasterThatIsNoLongerIntact()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFamilyFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var high = fixture.Member(HighGain).Masters;
            var bias = high[0];
            (string Reason, CalibrationFamilyMasterV1 Master)[] cases =
            [
                (CalibrationFamilyReasonCodes.MasterMissing, bias with { ArtifactId = Guid.NewGuid() }),
                (CalibrationFamilyReasonCodes.MasterMismatch, bias with { PayloadSha256 = new string('A', 64) }),
                (CalibrationFamilyReasonCodes.MasterMismatch, bias with { Kind = CalibrationReferenceKinds.Dark })
            ];
            foreach (var (reason, master) in cases)
            {
                Assert.AreEqual(
                    reason,
                    await store.ConfirmFamilyMastersAsync([master], CancellationToken.None).ConfigureAwait(false));
            }

            await SetPublicationStateAsync(root, fixture.Bundles[HighGain].BundleId, "quarantined").ConfigureAwait(false);
            Assert.AreEqual(
                CalibrationFamilyReasonCodes.MasterUnpublished,
                await store.ConfirmFamilyMastersAsync(high, CancellationToken.None).ConfigureAwait(false));
            await SetPublicationStateAsync(root, fixture.Bundles[HighGain].BundleId, "published").ConfigureAwait(false);

            var payload = MasterPayloadPath(root, fixture.Bundles[HighGain], CalibrationReferenceKinds.Flat);
            var original = await File.ReadAllBytesAsync(payload).ConfigureAwait(false);
            var flipped = original.ToArray();
            flipped[0] ^= 0x01;
            await File.WriteAllBytesAsync(payload, flipped).ConfigureAwait(false);
            Assert.AreEqual(
                CalibrationFamilyReasonCodes.MasterCorrupt,
                await store.ConfirmFamilyMastersAsync(high, CancellationToken.None).ConfigureAwait(false));
            File.Delete(payload);
            Assert.AreEqual(
                CalibrationFamilyReasonCodes.MasterUnreadable,
                await store.ConfirmFamilyMastersAsync(high, CancellationToken.None).ConfigureAwait(false));
            await File.WriteAllBytesAsync(payload, original).ConfigureAwait(false);
            Assert.IsNull(await store.ConfirmFamilyMastersAsync(high, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task SelectFamilyMastersAsync_SelectsANullOffsetSyntheticMasterOnlyForANullOffsetMember()
    {
        var root = CreateRoot();
        try
        {
            var (store, bundle, light) = await CreateSyntheticFamilyFixtureAsync(root).ConfigureAwait(false);
            using var owned = store;
            Assert.IsNull(bundle.Applicability.MinimumOffset);
            Assert.IsNull(bundle.Applicability.MinimumLightExposure);
            Assert.IsNull(light.Controls.EffectiveOffset);
            var member = new CalibrationFamilyMemberV1(
                "synthetic",
                light.Controls.EffectiveGain,
                null,
                bundle.Applicability.InputLayout,
                bundle.Applicability.MinimumTemperatureC!.Value,
                bundle.Applicability.MaximumTemperatureC!.Value,
                light.Controls.EffectiveExposure,
                light.Controls.EffectiveExposure,
                bundle.Applicability.EffectiveFromUtc,
                null,
                light.Profiles.Mask.Sha256,
                [.. bundle.Artifacts.Select(static artifact =>
                    new CalibrationFamilyMasterV1(artifact.Kind, artifact.ArtifactId, artifact.PayloadSha256))]);
            var family = new CalibrationFamilyV1(
                CalibrationFamilyV1.CurrentSchemaVersion,
                "synthetic-family",
                light.Capture.AgentId,
                light.Capture.RigId,
                light.Profiles.Rig.Sha256,
                light.Profiles.Sensor.Sha256,
                light.Profiles.Calibration,
                light.Profiles.Mask.Sha256,
                [member]);

            var selected = await store.SelectFamilyMastersAsync(family, light, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(selected.IsSelected, selected.ReasonCode);
            CollectionAssert.AreEqual(
                CalibrationReferenceKinds.All.ToArray(),
                selected.Selection!.Masters.Select(static master => master.Kind).ToArray());

            var withOffset = family with { Members = [member with { Offset = 0 }] };
            var offsetLight = WithOffset(light, 0);
            Assert.IsTrue(CalibrationFamilyContract.Evaluate(withOffset, FamilyInput(offsetLight)).IsApplicable);

            var mismatch = await store.SelectFamilyMastersAsync(withOffset, offsetLight, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.IsFalse(mismatch.IsSelected);
            Assert.AreEqual(CalibrationFamilyReasonCodes.MasterMismatch, mismatch.ReasonCode);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task PrepareAsync_FreezesEachInputsMastersCoefficientAndExclusionDeterministically()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFamilyFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var family = fixture.Family(fixture.Member(LowGain), fixture.Member(HighGain));
            var response = VirtualResponse();
            var between = fixture.Lights[LowGain] with
            {
                Capture = fixture.Lights[LowGain].Capture with { CaptureId = Guid.NewGuid() },
                Artifact = fixture.Lights[LowGain].Artifact with { ArtifactId = Guid.NewGuid() },
                Controls = fixture.Lights[LowGain].Controls with { RequestedGain = 90, EffectiveGain = 90 }
            };
            ProcessingFrozenRawInput[] inputs =
            [
                FrozenInput(fixture.Lights[HighGain], 0),
                FrozenInput(fixture.Lights[LowGain], -1),
                FrozenInput(between, -2)
            ];

            var plan = await NormalizationPlanFreezer.PrepareAsync(
                store, family, GainNormalizationPolicyV1.Approved, response, inputs, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.IsTrue(NormalizationPlanContract.Validate(plan).IsValid);
            Assert.AreEqual(CalibrationFamilyContractJson.ComputeIdentitySha256(family), plan.CalibrationFamilyIdentitySha256);
            Assert.AreEqual(response.IdentitySha256, plan.ResponseIdentitySha256);
            Assert.AreEqual(fixture.Lights[HighGain].Layout, plan.InputLayout);
            foreach (var (input, gain) in plan.Inputs.Take(2).Zip(new[] { HighGain, LowGain }))
            {
                var selection = (await store.SelectFamilyMastersAsync(
                    family, fixture.Lights[gain], CancellationToken.None).ConfigureAwait(false)).Selection!;
                Assert.AreEqual(NormalizationPlanInputDispositions.Selected, input.Disposition);
                Assert.AreEqual(selection.MemberId, input.MemberId);
                CollectionAssert.AreEqual(
                    selection.Masters.Select(static master => (master.Kind, master.ArtifactId, master.PayloadSha256, master.Exposure)).ToArray(),
                    input.Masters!.Select(static master => (master.Kind, master.ArtifactId, master.PayloadSha256, master.Exposure)).ToArray());
                // The plan freezes each master's library exposure, the one the recipe scales the dark and flat by.
                CollectionAssert.AreEqual(
                    input.Masters!.Select(master => fixture.Bundles[gain].Artifacts
                        .Single(artifact => artifact.ArtifactId == master.ArtifactId).Exposure).ToArray(),
                    input.Masters!.Select(static master => master.Exposure).ToArray());
                Assert.AreEqual(response.ResolveCoefficient(gain).Coefficient, input.Coefficient);
            }
            Assert.AreEqual(NormalizationPlanInputDispositions.Excluded, plan.Inputs[2].Disposition);
            Assert.AreEqual(CalibrationFamilyReasonCodes.MissingMember, plan.Inputs[2].ExclusionReason);
            CollectionAssert.AreEqual(new[] { 0, -1, -2 }, plan.Inputs.Select(static input => input.WindowPosition).ToArray());

            // Preparing the same window again, even from an equivalent family in another member order, is the same plan.
            var again = await NormalizationPlanFreezer.PrepareAsync(
                store,
                fixture.Family(fixture.Member(HighGain), fixture.Member(LowGain)),
                GainNormalizationPolicyV1.Approved,
                VirtualResponse(),
                inputs,
                CancellationToken.None).ConfigureAwait(false);
            CollectionAssert.AreEqual(
                NormalizationPlanContractJson.Serialize(plan),
                NormalizationPlanContractJson.Serialize(again));

            // A master that fails its bundle evidence when the plan is prepared is a visible per-input exclusion.
            var manifest = MasterManifestPath(root, fixture.Bundles[HighGain], CalibrationReferenceKinds.Dark);
            var original = await File.ReadAllBytesAsync(manifest).ConfigureAwait(false);
            File.Delete(manifest);
            var corrupt = await NormalizationPlanFreezer.PrepareAsync(
                store, family, GainNormalizationPolicyV1.Approved, response, inputs, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(NormalizationPlanInputDispositions.Excluded, corrupt.Inputs[0].Disposition);
            Assert.AreEqual(CalibrationFamilyReasonCodes.MasterCorrupt, corrupt.Inputs[0].ExclusionReason);
            Assert.AreEqual(NormalizationPlanInputDispositions.Selected, corrupt.Inputs[1].Disposition);

            // At claim time the frozen plan is never changed: a corrupt master fails the claim explicitly.
            var claim = await Assert.ThrowsExactlyAsync<NormalizationPlanException>(async () =>
                await NormalizationPlanFreezer.ConfirmAsync(store, plan, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
            Assert.AreEqual(CalibrationFamilyReasonCodes.MasterCorrupt, claim.ReasonCode);
            await File.WriteAllBytesAsync(manifest, original).ConfigureAwait(false);
            await NormalizationPlanFreezer.ConfirmAsync(store, plan, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [DataRow("response")]
    [DataRow("family")]
    [DataRow("capture")]
    public async Task PrepareAsync_RejectsAResponseThatDoesNotMatchTheFamilyAndEveryCapture(string mismatch)
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFamilyFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var family = fixture.Family(fixture.Member(LowGain), fixture.Member(HighGain));
            var response = VirtualResponse();
            var low = fixture.Lights[LowGain];
            var high = fixture.Lights[HighGain];
            Assert.AreEqual(response.Descriptor.SensorProfileSha256, family.SensorProfileSha256);
            Assert.AreEqual(response.Descriptor.SensorProfileSha256, high.Profiles.Sensor.Sha256);
            switch (mismatch)
            {
                case "response":
                    response = VirtualResponse(VirtualResponseSensor() with { Name = "another-sensor" });
                    break;
                case "family":
                    family = family with { SensorProfileSha256 = new string('A', 64) };
                    break;
                case "capture":
                    // Keep one eligible input so silently excluding the other sensor cannot pass this regression.
                    low = low with { Profiles = low.Profiles with { Sensor = low.Profiles.Sensor with { Sha256 = new string('B', 64) } } };
                    break;
            }
            ProcessingFrozenRawInput[] inputs = [FrozenInput(high, 0), FrozenInput(low, -1)];

            var failure = await Assert.ThrowsExactlyAsync<NormalizationPlanException>(async () =>
                await NormalizationPlanFreezer.PrepareAsync(
                    store, family, GainNormalizationPolicyV1.Approved, response, inputs, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(CalibrationFamilyReasonCodes.IdentityMismatch, failure.ReasonCode);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task PrepareAsync_RejectsAResponseWhoseStoredCodesAreNotRightAligned()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFamilyFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            // Full-range-scaled 12-bit codes: the fixed ×16 output scale is defined over right-aligned codes only.
            var resolution = GainResponseResolver.Resolve(
                GainResponseSourceKind.VirtualAsi174MmModel,
                CreateVirtualConfiguration().Rig.Sensor,
                CreateVirtualConfiguration().Rig.Readout! with
                {
                    SampleDepthBits = 12,
                    StoredCodeTransform = FrameStoredCodeTransform.FullRangeScaledV1,
                    WhiteLevel = 4095
                });
            Assert.IsTrue(resolution.IsAvailable, resolution.ReasonCode);
            var response = resolution.Response!;
            Assert.AreEqual(FrameStoredCodeTransform.FullRangeScaledV1, response.Descriptor.StoredCodeTransform);
            var sensor = response.Descriptor.SensorProfileSha256;
            var family = fixture.Family(fixture.Member(LowGain), fixture.Member(HighGain)) with { SensorProfileSha256 = sensor };
            ProcessingFrozenRawInput[] inputs =
            [
                .. new[] { fixture.Lights[HighGain], fixture.Lights[LowGain] }.Select((light, index) => FrozenInput(
                    light with { Profiles = light.Profiles with { Sensor = light.Profiles.Sensor with { Sha256 = sensor } } },
                    -index))
            ];

            var failure = await Assert.ThrowsExactlyAsync<NormalizationPlanException>(async () =>
                await NormalizationPlanFreezer.PrepareAsync(
                    store, family, GainNormalizationPolicyV1.Approved, response, inputs, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            Assert.AreEqual(NormalizationPlanFreezer.NonRightAlignedResponse, failure.ReasonCode);
            Assert.AreEqual("normalization.plan.non-right-aligned-response", failure.ReasonCode);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task PrepareAsync_FailsWhenNoInputIsEligibleOrTheRatioBoundIsExceeded()
    {
        var root = CreateRoot();
        try
        {
            var fixture = await CreateFamilyFixtureAsync(root).ConfigureAwait(false);
            using var store = fixture.Store;
            var family = fixture.Family(fixture.Member(LowGain), fixture.Member(HighGain));
            ProcessingFrozenRawInput[] inputs =
            [
                FrozenInput(fixture.Lights[HighGain], 0),
                FrozenInput(fixture.Lights[LowGain], -1)
            ];

            var unrelated = await Assert.ThrowsExactlyAsync<NormalizationPlanException>(async () =>
                await NormalizationPlanFreezer.PrepareAsync(
                    store, family with { AgentId = "other-agent" }, GainNormalizationPolicyV1.Approved, VirtualResponse(),
                    inputs, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(NormalizationPlanFreezer.NoEligibleInput, unrelated.ReasonCode);

            var bound = await Assert.ThrowsExactlyAsync<NormalizationPlanException>(async () =>
                await NormalizationPlanFreezer.PrepareAsync(
                    store, family,
                    GainNormalizationPolicyV1.Approved with { MaximumCoefficientRatioMilli = 1000 },
                    VirtualResponse(), inputs, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(GainNormalizationReasonCodes.CoefficientRatioExceeded, bound.ReasonCode);

            var single = await NormalizationPlanFreezer.PrepareAsync(
                store, family, GainNormalizationPolicyV1.Approved with { MaximumCoefficientRatioMilli = 1000 },
                VirtualResponse(), inputs[..1], CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, single.Inputs.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// The four evidence corruptions PR-1503-R1 F1 probed: a deleted or truncated master manifest, a truncated profile
    /// marker, and a flipped byte in a source payload the master was built from.
    /// </summary>
    private static (string Name, string Path, Func<byte[], Task> Corrupt)[] EvidenceCorruptions(
        string root,
        CalibrationLibraryBundleV1 bundle)
    {
        var manifest = MasterManifestPath(root, bundle, CalibrationReferenceKinds.Flat);
        var profile = Path.Combine(root, bundle.ProfileRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var source = bundle.Artifacts.First(static artifact =>
            artifact.Role == CalibrationLibraryArtifactRoles.Source && artifact.Kind == CalibrationReferenceKinds.Bias);
        var sourcePayload = PayloadPath(root, source.ManifestRelativePath);
        return
        [
            ("deleted-manifest", manifest, _ =>
            {
                File.Delete(manifest);
                return Task.CompletedTask;
            }),
            ("truncated-manifest", manifest, original => File.WriteAllBytesAsync(manifest, original[..^1])),
            ("truncated-profile-marker", profile, original => File.WriteAllBytesAsync(profile, original[..^1])),
            ("flipped-source-byte", sourcePayload, original =>
            {
                var flipped = original.ToArray();
                flipped[0] ^= 0x01;
                return File.WriteAllBytesAsync(sourcePayload, flipped);
            })
        ];
    }

    private static string MasterManifestPath(string root, CalibrationLibraryBundleV1 bundle, string kind)
        => Path.Combine(root, LibraryMasters(bundle).Single(artifact => artifact.Kind == kind)
            .ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string MasterPayloadPath(string root, CalibrationLibraryBundleV1 bundle, string kind)
        => PayloadPath(root, LibraryMasters(bundle).Single(artifact => artifact.Kind == kind).ManifestRelativePath);

    private static string PayloadPath(string root, string manifestRelativePath)
        => Path.Combine(root, string.Concat(
            manifestRelativePath.AsSpan(0, manifestRelativePath.Length - ".json".Length), ".bin")
            .Replace('/', Path.DirectorySeparatorChar));

    private static async Task SetPublicationStateAsync(string root, string bundleId, string state)
    {
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE calibration_library_bundles SET publication_state = $state WHERE bundle_id = $bundle;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$bundle", bundleId);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    private static ProcessingFrozenRawInput FrozenInput(ReconstructionDescriptor light, int position)
        => new(
            Math.Abs(position) + 1,
            position,
            light,
            CaptureContractJson.ComputeDescriptorSha256(light),
            new string('D', 64),
            "raw/frame.bin");

    /// <summary>A pinned 16-bit simulation response for the family fixture's virtual sensor and readout.</summary>
    private static GainResponse VirtualResponse(SensorProfile? sensor = null)
    {
        var resolution = GainResponseResolver.Resolve(
            GainResponseSourceKind.VirtualConfiguredResponse, sensor ?? VirtualResponseSensor(), CreateVirtualConfiguration().Rig.Readout);
        Assert.IsTrue(resolution.IsAvailable, resolution.ReasonCode);
        return resolution.Response!;
    }

    private static SensorProfile VirtualResponseSensor()
        => CreateVirtualConfiguration().Rig.Sensor with
        {
            SimulationResponse = new ConfiguredSensorResponseProfile(
                "virtual-calibration-test-response-v1",
                16,
                0,
                400,
                4.0,
                200,
                60_000,
                [new SensorReadNoisePoint(0, 2.0), new SensorReadNoisePoint(400, 1.0)],
                0,
                "ZWO 0.1 dB",
                "virtual calibration test response")
        };

    /// <summary>
    /// A published synthetic-references bundle, whose master offsets and light-exposure ranges are null, for a light
    /// whose layout declares its readout, stored-code transform and level code space, as a family member requires.
    /// </summary>
    private static async Task<(SqliteCalibrationLibraryStore Store, CalibrationLibraryBundleV1 Bundle, ReconstructionDescriptor Light)>
        CreateSyntheticFamilyFixtureAsync(string root)
    {
        Directory.CreateDirectory(root);
        var options = CreateOptions(root);
        await new SqliteRawCaptureJournal(
            Path.Combine(root, "journal", "raw-ingress.db"), 1)
            .InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var model = new SyntheticCalibrationModelV1
        {
            Gain = 99.5,
            TemperatureC = -9.5,
            Defects = [new SyntheticCalibrationDefect(1, 1)]
        };
        var template = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        var light = template.Descriptor with
        {
            Layout = template.Descriptor.Layout with
            {
                Readout = new FrameReadoutDescriptor(2, 2, 0, 0, 2, 2, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null),
                StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
                LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
            },
            Controls = template.Descriptor.Controls with
            {
                RequestedGain = model.Gain,
                EffectiveGain = model.Gain,
                RequestedOffset = null,
                EffectiveOffset = null,
                TemperatureSetpointC = model.TemperatureC,
                EffectiveTemperatureC = model.TemperatureC
            },
            Profiles = template.Descriptor.Profiles with
            {
                Calibration = new ProfileIdentityDescriptor(
                    "synthetic-calibration-model",
                    model.SchemaVersion,
                    SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(model))
            }
        };
        var synthetic = await new SyntheticCalibrationReferenceStore(options, new CameraAgentClearReferenceLoader(options))
            .GetOrCreateAsync(light, model, CancellationToken.None).ConfigureAwait(false);
        var store = new SqliteCalibrationLibraryStore(new InitializedIngress(), options, TimeProvider.System);
        _ = await store.AdoptPublishedBundleAsync(synthetic.LibraryBundle, CancellationToken.None).ConfigureAwait(false);
        return (store, synthetic.LibraryBundle, light);
    }
}
