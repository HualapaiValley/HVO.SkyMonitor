using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Tests.Contracts;

[TestClass]
[TestCategory("Unit")]
public sealed class ReconstructableCaptureContractTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void SensorReadoutResolver_DerivesAsi174RoiAndRejectsImpossibleGeometry()
    {
        var sensor = new SensorProfile(
            "ASI174", 1936, 1216, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16,
            SensorResponseMode.Monochrome, 3872);
        var profile = new SensorReadoutProfile(
            new SensorCrop(648, 368, 640, 480),
            4,
            4,
            FrameBinningAlgorithm.DigitalAverageV1,
            CameraPixelFormat.Mono8,
            8,
            8,
            FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.IdentityV1,
            FrameLevelCodeSpace.StoredContainer,
            4,
            255);

        var resolved = SensorReadoutResolver.Resolve(sensor, profile);

        Assert.AreEqual(160, resolved.Layout.Width);
        Assert.AreEqual(120, resolved.Layout.Height);
        Assert.AreEqual(160, resolved.Layout.StrideBytes);
        Assert.AreEqual(19_200, resolved.Layout.ByteLength);
        Assert.AreEqual(1936, resolved.Geometry.NativeWidth);
        Assert.ThrowsExactly<ArgumentException>(() => SensorReadoutResolver.Resolve(
            sensor,
            profile with { Roi = new SensorCrop(0, 0, 640, 480), BinX = 1, BinY = 1 }));
        Assert.ThrowsExactly<ArgumentException>(() => SensorReadoutResolver.Resolve(
            sensor,
            profile with { Roi = new SensorCrop(1500, 900, 640, 480) }));
        Assert.ThrowsExactly<ArgumentException>(() => SensorReadoutResolver.Resolve(
            sensor,
            profile with { ByteOrder = (SampleByteOrder)int.MaxValue }));
    }

    [TestMethod]
    public void ManifestV2_CloudScenarioProvenanceRoundTripsWithoutChangingDescriptorIdentity()
    {
        var original = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        var parameters = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "virtual-cloud-scenario-v1",
            scenarioId = "scenario-104-a",
            seed = 104
        }));
        var parametersSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(parameters);
        var scene = new SceneProvenance(
            "scene-104",
            "rig-v1",
            "HYG",
            "4.2",
            new string('A', 64),
            "EquidistantFisheye",
            "projection-v1",
            "astronomy-v1",
            "sensor-v1",
            CloudScenario: new CloudScenarioProvenance(
                "virtual-cloud-scenario-v1",
                "scenario-104-a",
                "1",
                "virtual-cloud-value-field-v1",
                parametersSha256,
                104,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(10),
                DateTimeOffset.UnixEpoch.AddSeconds(14),
                4,
                parameters));
        var enriched = original with { Scene = scene };

        var encoded = CaptureContractJson.Serialize(enriched);
        var parsed = CaptureContractJson.ParseManifest(encoded);
        var cloud = parsed.Document!.Manifest!.Scene!.CloudScenario!;

        Assert.IsTrue(parsed.IsValid);
        Assert.AreEqual(original.IdempotencyKey, enriched.IdempotencyKey);
        Assert.AreNotEqual(CaptureContractJson.ComputeManifestSha256(original), CaptureContractJson.ComputeManifestSha256(enriched));
        Assert.AreEqual(CaptureContractJson.ComputeManifestSha256(enriched), CaptureContractJson.ComputeManifestSha256(encoded));
        var reformatted = Encoding.UTF8.GetBytes($"\n{Encoding.UTF8.GetString(encoded)}");
        Assert.IsTrue(CaptureContractJson.ParseManifest(reformatted).IsValid);
        Assert.AreNotEqual(CaptureContractJson.ComputeManifestSha256(encoded), CaptureContractJson.ComputeManifestSha256(reformatted));
        Assert.AreEqual(parametersSha256, cloud.ParametersSha256);
        Assert.AreEqual("scenario-104-a", cloud.ScenarioId);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddSeconds(14), cloud.IntegrationEndUtc);
        CollectionAssert.AreEqual(encoded, CaptureContractJson.Serialize(parsed.Document.Manifest));
        var nonCloudScene = enriched with { Scene = scene with { CloudScenario = null } };
        Assert.IsFalse(Encoding.UTF8.GetString(CaptureContractJson.Serialize(nonCloudScene))
            .Contains("cloudScenario", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ManifestV1_CloudScenarioProvenanceRoundTripsAsOptionalLegacyScene()
    {
        var parameters = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "virtual-cloud-scenario-v1",
            scenarioId = "scn-legacy-cloud",
            seed = 104
        }));
        var parametersSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(parameters);
        var scene = new SceneProvenance(
            "scene-v1-cloud",
            "rig-v1",
            "HYG",
            "4.2",
            new string('A', 64),
            "EquidistantFisheye",
            "projection-v1",
            "astronomy-v1",
            "sensor-v1",
            CloudScenario: new CloudScenarioProvenance(
                "virtual-cloud-scenario-v1",
                "scn-legacy-cloud",
                "1",
                "virtual-cloud-value-field-v1",
                parametersSha256,
                104,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(10),
                DateTimeOffset.UnixEpoch.AddSeconds(14),
                4,
                parameters));
        var legacy = new ArtifactUploadManifest(
            ArtifactUploadManifest.CurrentSchemaVersion,
            "agent-a",
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            FrameArtifactRole.Raw,
            "application/octet-stream",
            4,
            new string('B', 64),
            DateTimeOffset.UnixEpoch,
            "raw-v1",
            "frames/raw.bin",
            scene);

        var result = CaptureContractJson.ParseManifest(JsonSerializer.SerializeToUtf8Bytes(legacy, WebJsonOptions));
        var cloud = result.Document!.LegacyManifest!.Scene!.CloudScenario!;

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(CaptureManifestCompleteness.LegacyIncomplete, result.Document.Completeness);
        Assert.AreEqual(parametersSha256, cloud.ParametersSha256);
        Assert.AreEqual("scn-legacy-cloud", cloud.ScenarioId);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddSeconds(14), cloud.IntegrationEndUtc);
    }

    [TestMethod]
    public void TransientScenarioProvenanceRoundTripsWithoutChangingDescriptorIdentity()
    {
        var parameters = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "virtual-transient-scenario-v1",
            scenarioId = "scn-transient",
            scenarioVersion = "1",
            seed = 61,
            epochUtc = DateTimeOffset.UnixEpoch,
            temporalSampleCount = 8,
            skyTracks = new[] { new { primitiveId = "p-001" } },
            sensorTracks = new[] { new { primitiveId = "s-001" } }
        }));
        var transient = new TransientScenarioProvenance(
            "virtual-transient-scenario-v1",
            "scn-transient",
            "1",
            "virtual-transient-raster-v1",
            CaptureContractJson.ComputeCanonicalJsonSha256(parameters),
            61,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(10),
            DateTimeOffset.UnixEpoch.AddSeconds(11),
            8,
            1,
            1,
            parameters);
        var scene = new SceneProvenance(
            "scene-transient",
            "rig-v1",
            "HYG",
            "4.2",
            new string('A', 64),
            "EquidistantFisheye",
            "projection-v1",
            "astronomy-v1",
            "sensor-v1",
            TransientScenario: transient);
        var original = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        var enriched = original with { Scene = scene };

        var v2 = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(enriched));
        Assert.IsTrue(v2.IsValid);
        Assert.AreEqual(original.IdempotencyKey, enriched.IdempotencyKey);
        Assert.AreEqual(transient.ParametersSha256,
            v2.Document!.Manifest!.Scene!.TransientScenario!.ParametersSha256);
        Assert.AreNotEqual(
            CaptureContractJson.ComputeManifestSha256(original),
            CaptureContractJson.ComputeManifestSha256(enriched));
        var withoutTransient = enriched with { Scene = scene with { TransientScenario = null } };
        Assert.IsFalse(Encoding.UTF8.GetString(CaptureContractJson.Serialize(withoutTransient))
            .Contains("transientScenario", StringComparison.Ordinal));

        var legacy = new ArtifactUploadManifest(
            ArtifactUploadManifest.CurrentSchemaVersion,
            "agent-a",
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            FrameArtifactRole.Raw,
            "application/octet-stream",
            4,
            new string('B', 64),
            DateTimeOffset.UnixEpoch,
            "raw-v1",
            "frames/raw.bin",
            scene);
        var v1 = CaptureContractJson.ParseManifest(JsonSerializer.SerializeToUtf8Bytes(legacy, WebJsonOptions));
        Assert.IsTrue(v1.IsValid);
        Assert.AreEqual(transient.ParametersSha256,
            v1.Document!.LegacyManifest!.Scene!.TransientScenario!.ParametersSha256);

        var tampered = enriched with
        {
            Scene = scene with
            {
                TransientScenario = transient with { ParametersSha256 = new string('0', 64) }
            }
        };
        var rejectedV2 = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(tampered));
        Assert.IsFalse(rejectedV2.IsValid);
        Assert.AreEqual("scene.transientScenario", rejectedV2.Validation.FieldPath);
        var rejectedV1 = CaptureContractJson.ParseManifest(JsonSerializer.SerializeToUtf8Bytes(
            legacy with { Scene = tampered.Scene }, WebJsonOptions));
        Assert.IsFalse(rejectedV1.IsValid);
        var nullHash = enriched with
        {
            Scene = scene with
            {
                TransientScenario = transient with { ParametersSha256 = null! }
            }
        };
        Assert.IsFalse(CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(nullHash)).IsValid);
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, 3, 2, 8,
        "925DB25F0A90DB14EDD8C8A47ECFF5C1B226A5E25C5DDACB6A21F469F9C1BD64",
        "EA0E62A3870BDAD3EF760AAAD03B542246ED7DB286AA7C83665B7BEF6655499D")]
    [DataRow(CameraPixelFormat.Rgb24, 2, 2, 8,
        "4E3DA2AE0C246503682E10C16BD2AEDEC6DCB37EC2045F347A4B92798F3E1724",
        "7E6A77AF689D63BEBB1BF93844A1032383EAEC3476831F75F858BAF553F7F3A8")]
    [DataRow(CameraPixelFormat.BayerRggb16, 3, 2, 8,
        "A3745287E291AFE11547F7D6E13350A6F4BAA1ED047C513D675AAF88D35EC8DF",
        "3A2157CB09033AD66E5460C4977E666762966CEAA225A8A60A8D52C050D06671")]
    [DataRow(CameraPixelFormat.Mono8, 3, 2, 4,
        "CF487B545104026DCCC2F9844FB5F0A58BDC9673D8192E14F7B8F15FFB1E624B",
        "F94FB322447BA346B3505D989DCBC1E055CA0B38EA3C4E8EED9EB9DB03269B06")]
    public void ManifestV2_RoundTripsAndReconstructsOriginalBytes(
        CameraPixelFormat format,
        int width,
        int height,
        int stride,
        string expectedDescriptorSha256,
        string expectedManifestSha256)
    {
        var payload = Enumerable.Range(0, stride * height).Select(static value => (byte)value).ToArray();
        var manifest = CreateManifest(format, width, height, stride, payload);

        var firstJson = CaptureContractJson.Serialize(manifest);
        var parse = CaptureContractJson.ParseManifest(firstJson);
        var parsed = parse.Document?.Manifest;
        var secondJson = CaptureContractJson.Serialize(parsed!);
        var result = FrameReconstructor.TryReconstruct(parsed!.Descriptor, payload, out var frame);

        Assert.IsTrue(parse.IsValid);
        Assert.AreEqual(CaptureManifestCompleteness.Complete, parse.Document!.Completeness);
        CollectionAssert.AreEqual(firstJson, secondJson);
        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(frame);
        Assert.AreEqual(format, frame.PixelFormat);
        Assert.AreEqual(stride, frame.StrideBytes);
        Assert.IsNull(parsed.Descriptor.CycleEvidence);
        Assert.IsNull(parsed.Descriptor.Timing.SetpointAppliedUtc);
        Assert.IsFalse(Encoding.UTF8.GetString(firstJson).Contains("cycleEvidence", StringComparison.Ordinal));
        Assert.IsFalse(Encoding.UTF8.GetString(firstJson).Contains("setpointAppliedUtc", StringComparison.Ordinal));
        Assert.AreEqual(expectedDescriptorSha256, manifest.IdempotencyKey);
        Assert.AreEqual(expectedManifestSha256, CaptureContractJson.ComputeManifestSha256(manifest));
        CollectionAssert.AreEqual(payload, frame.PixelData.ToArray());

        payload[0] = 255;
        Assert.AreEqual(255, frame.PixelData.Span[0], "Reconstruction must retain the caller-owned payload without copying it.");
    }

    [TestMethod]
    public void ManifestV2_PreservesVariantOrderedLineageAndCaptureProfiles()
    {
        var payload = new byte[8];
        var firstSource = Guid.Parse("00000000-0000-0000-0000-000000000011");
        var secondSource = Guid.Parse("00000000-0000-0000-0000-000000000012");
        var raw = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var manifest = raw with
        {
            Descriptor = raw.Descriptor with
            {
                Artifact = raw.Descriptor.Artifact with
                {
                    Role = FrameArtifactRole.Combined,
                    Variant = "rolling-5",
                    SourceArtifactIds = [firstSource, secondSource]
                }
            }
        };

        var parse = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(manifest));
        var descriptor = parse.Document!.Manifest!.Descriptor;

        Assert.IsTrue(parse.IsValid);
        Assert.AreEqual("rolling-5", descriptor.Artifact.Variant);
        CollectionAssert.AreEqual(new[] { firstSource, secondSource }, descriptor.Artifact.SourceArtifactIds.ToArray());
        Assert.AreEqual("rig-a", descriptor.Profiles.Rig.Name);
        Assert.AreEqual(42L, descriptor.Capture.CaptureSequence);
    }

    [TestMethod]
    public void ManifestV2_WithCycleEvidence_RoundTripsAllFieldsAndUsesStringEnums()
    {
        var legacy = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        var activeSetpointAppliedUtc = legacy.Descriptor.Timing.RequestedStartUtc.AddMilliseconds(100);
        var decidedSetpointAppliedUtc = legacy.Descriptor.Timing.ReadoutCompletedUtc.AddMilliseconds(450);
        var evidence = CreateCycleEvidence(legacy.Descriptor);
        evidence = evidence with
        {
            Decision = evidence.Decision with
            {
                SetpointAppliedUtc = decidedSetpointAppliedUtc,
                ActiveTargetFps = 10,
                DecidedTargetFps = 12
            }
        };
        var enriched = legacy with
        {
            Descriptor = legacy.Descriptor with
            {
                Timing = legacy.Descriptor.Timing with { SetpointAppliedUtc = activeSetpointAppliedUtc },
                CycleEvidence = evidence
            }
        };

        var json = CaptureContractJson.Serialize(enriched);
        var parse = CaptureContractJson.ParseManifest(json);
        var parsed = parse.Document?.Manifest;
        var parsedEvidence = parsed?.Descriptor.CycleEvidence;
        var root = JsonNode.Parse(json)!.AsObject();
        var evidenceNode = root["descriptor"]!["cycleEvidence"]!.AsObject();

        Assert.IsTrue(enriched.Validate().IsValid);
        Assert.IsTrue(parse.IsValid);
        Assert.IsNotNull(parsed);
        Assert.AreEqual(evidence, parsedEvidence);
        Assert.AreEqual(activeSetpointAppliedUtc, parsed!.Descriptor.Timing.SetpointAppliedUtc);
        Assert.AreEqual(decidedSetpointAppliedUtc, parsedEvidence!.Decision.SetpointAppliedUtc);
        Assert.AreEqual(10d, parsedEvidence.Decision.ActiveTargetFps);
        Assert.AreEqual(12d, parsedEvidence.Decision.DecidedTargetFps);
        CollectionAssert.AreEqual(json, CaptureContractJson.Serialize(parsed));
        Assert.AreNotEqual(legacy.IdempotencyKey, enriched.IdempotencyKey);
        Assert.AreEqual("MinimumStartInterval", evidenceNode["cadenceMode"]!.GetValue<string>());
        Assert.AreEqual("DeadlineReached", evidenceNode["startReason"]!.GetValue<string>());
        Assert.AreEqual("HostMetered", evidenceNode["exposureControl"]!.GetValue<string>());
        Assert.AreEqual("HostMetered", evidenceNode["gainControl"]!.GetValue<string>());
        Assert.AreEqual("Twilight", evidenceNode["solarRegime"]!.GetValue<string>());
        Assert.AreEqual("Measured", evidenceNode["metering"]!["outcome"]!.GetValue<string>());
        Assert.AreEqual("ExposureAndGainAdjusted", evidenceNode["decision"]!["reason"]!.GetValue<string>());
        Assert.AreEqual(10d, evidenceNode["decision"]!["activeTargetFps"]!.GetValue<double>());
        Assert.AreEqual(12d, evidenceNode["decision"]!["decidedTargetFps"]!.GetValue<double>());

        var withoutTargetFps = JsonNode.Parse(CaptureContractJson.Serialize(legacy with
        {
            Descriptor = legacy.Descriptor with { CycleEvidence = CreateCycleEvidence(legacy.Descriptor) }
        }))!;
        var decisionWithoutTargetFps = withoutTargetFps["descriptor"]!["cycleEvidence"]!["decision"]!.AsObject();
        Assert.IsFalse(decisionWithoutTargetFps.ContainsKey("activeTargetFps"));
        Assert.IsFalse(decisionWithoutTargetFps.ContainsKey("decidedTargetFps"));
    }

    [TestMethod]
    public void ParseManifest_WithNumericCycleEvidenceEnums_ReturnsInvalidJson()
    {
        var manifest = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        manifest = manifest with
        {
            Descriptor = manifest.Descriptor with { CycleEvidence = CreateCycleEvidence(manifest.Descriptor) }
        };
        var cases = new (string Name, Action<JsonObject> Mutate)[]
        {
            ("start reason", cycle => cycle["startReason"] = 1),
            ("solar regime", cycle => cycle["solarRegime"] = 1),
            ("metering outcome", cycle => cycle["metering"]!.AsObject()["outcome"] = 1),
            ("decision reason", cycle => cycle["decision"]!.AsObject()["reason"] = 1)
        };

        foreach (var testCase in cases)
        {
            var root = JsonNode.Parse(CaptureContractJson.Serialize(manifest))!.AsObject();
            testCase.Mutate(root["descriptor"]!["cycleEvidence"]!.AsObject());

            var result = CaptureContractJson.ParseManifest(Encoding.UTF8.GetBytes(root.ToJsonString()));

            Assert.AreEqual(CaptureContractReasonCodes.InvalidJson, result.Validation.ReasonCode, testCase.Name);
        }
    }

    [TestMethod]
    public void ParseManifest_WithMissingRequiredCycleEvidenceMembers_ReturnsInvalidJson()
    {
        var manifest = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        manifest = manifest with
        {
            Descriptor = manifest.Descriptor with { CycleEvidence = CreateCycleEvidence(manifest.Descriptor) }
        };
        var cases = new (string Scope, string Property)[]
        {
            ("cycle", "cadenceMode"),
            ("cycle", "startReason"),
            ("cycle", "exposureControl"),
            ("cycle", "gainControl"),
            ("cycle", "moduleCallStartedUtc"),
            ("cycle", "decision"),
            ("cycle", "ingressHandoffStartedUtc"),
            ("cycle", "monotonicStartJitter"),
            ("metering", "startedUtc"),
            ("metering", "completedUtc"),
            ("metering", "consideredSampleCount"),
            ("metering", "acceptedSampleCount"),
            ("metering", "saturatedSampleCount"),
            ("metering", "scannedBytes"),
            ("metering", "outcome"),
            ("decision", "startedUtc"),
            ("decision", "completedUtc"),
            ("decision", "activeExposure"),
            ("decision", "activeGain"),
            ("decision", "decidedExposure"),
            ("decision", "decidedGain"),
            ("decision", "reason")
        };

        foreach (var testCase in cases)
        {
            var root = JsonNode.Parse(CaptureContractJson.Serialize(manifest))!.AsObject();
            var cycle = root["descriptor"]!["cycleEvidence"]!.AsObject();
            var target = testCase.Scope switch
            {
                "metering" => cycle["metering"]!.AsObject(),
                "decision" => cycle["decision"]!.AsObject(),
                _ => cycle
            };
            Assert.IsTrue(target.Remove(testCase.Property), $"{testCase.Scope}.{testCase.Property}");

            var result = CaptureContractJson.ParseManifest(Encoding.UTF8.GetBytes(root.ToJsonString()));

            Assert.AreEqual(
                CaptureContractReasonCodes.InvalidJson,
                result.Validation.ReasonCode,
                $"{testCase.Scope}.{testCase.Property}");
        }
    }

    [TestMethod]
    public void ValidateCycleEvidence_WithInvalidCadenceOrStartReason_ReturnsStableReason()
    {
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        var evidence = CreateCycleEvidence(descriptor);

        var invalidCadence = descriptor with
        {
            CycleEvidence = evidence with { CadenceMode = (CaptureCadenceMode)999 }
        };
        var incompatibleStartReason = descriptor with
        {
            CycleEvidence = evidence with { StartReason = CaptureStartReason.ContinuousReady }
        };

        Assert.AreEqual(CaptureContractReasonCodes.InvalidCadence, invalidCadence.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidCadence, incompatibleStartReason.Validate().ReasonCode);
    }

    [TestMethod]
    public void ValidateCycleEvidence_WithInvalidTimingMeteringOrControlFacts_ReturnsStableReasons()
    {
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        var evidence = CreateCycleEvidence(descriptor);

        var invalidTiming = descriptor with
        {
            CycleEvidence = evidence with
            {
                IngressHandoffStartedUtc = descriptor.Timing.DurableIngressUtc.AddMilliseconds(1)
            }
        };
        var invalidMeteringCount = descriptor with
        {
            CycleEvidence = evidence with
            {
                Metering = evidence.Metering! with { AcceptedSampleCount = 121 }
            }
        };
        var invalidMeteringOutcome = descriptor with
        {
            CycleEvidence = evidence with
            {
                Metering = evidence.Metering! with
                {
                    NormalizedLevel = null,
                    Outcome = CaptureMeteringOutcome.Measured
                }
            }
        };
        var ownershipMeterMismatch = descriptor with
        {
            CycleEvidence = evidence with { Metering = null }
        };
        var activeControlMismatch = descriptor with
        {
            CycleEvidence = evidence with
            {
                Decision = evidence.Decision with { ActiveGain = descriptor.Controls.EffectiveGain + 1 }
            }
        };
        var disabledButChanged = descriptor with
        {
            CycleEvidence = evidence with
            {
                ExposureControl = AutomaticControlOwnership.Disabled,
                GainControl = AutomaticControlOwnership.Disabled,
                SolarRegime = null,
                Metering = null,
                Decision = evidence.Decision with
                {
                    DecidedExposure = evidence.Decision.ActiveExposure + TimeSpan.FromMilliseconds(1),
                    DecidedGain = evidence.Decision.ActiveGain,
                    Reason = CaptureControlDecisionReason.Disabled
                }
            }
        };

        Assert.AreEqual(CaptureContractReasonCodes.InvalidTimingOrder, invalidTiming.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidMetering, invalidMeteringCount.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidMetering, invalidMeteringOutcome.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidMetering, ownershipMeterMismatch.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidControls, activeControlMismatch.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidControls, disabledButChanged.Validate().ReasonCode);
    }

    [TestMethod]
    public void ValidateCycleEvidence_CadenceStartReasonMatrix_AcceptsOnlyCompatibleReasons()
    {
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        var evidence = CreateCycleEvidence(descriptor);
        var validCases = new (CaptureCadenceMode Cadence, CaptureStartReason Reason)[]
        {
            (CaptureCadenceMode.MinimumStartInterval, CaptureStartReason.Initial),
            (CaptureCadenceMode.MinimumStartInterval, CaptureStartReason.DeadlineReached),
            (CaptureCadenceMode.MinimumStartInterval, CaptureStartReason.DeadlineOverrun),
            (CaptureCadenceMode.MinimumStartInterval, CaptureStartReason.FailureRecovery),
            (CaptureCadenceMode.Continuous, CaptureStartReason.Initial),
            (CaptureCadenceMode.Continuous, CaptureStartReason.ContinuousReady),
            (CaptureCadenceMode.Continuous, CaptureStartReason.FailureRecovery)
        };

        foreach (var testCase in validCases)
        {
            AssertValidCycleEvidence(
                descriptor,
                evidence with { CadenceMode = testCase.Cadence, StartReason = testCase.Reason },
                $"{testCase.Cadence}/{testCase.Reason}");
        }

        var invalidCases = new (string Name, CaptureCadenceMode Cadence, CaptureStartReason Reason)[]
        {
            ("minimum/continuous-ready", CaptureCadenceMode.MinimumStartInterval, CaptureStartReason.ContinuousReady),
            ("continuous/deadline-reached", CaptureCadenceMode.Continuous, CaptureStartReason.DeadlineReached),
            ("continuous/deadline-overrun", CaptureCadenceMode.Continuous, CaptureStartReason.DeadlineOverrun),
            ("invalid cadence", (CaptureCadenceMode)999, CaptureStartReason.Initial),
            ("invalid start reason", CaptureCadenceMode.MinimumStartInterval, (CaptureStartReason)999)
        };

        foreach (var testCase in invalidCases)
        {
            AssertInvalidCycleEvidence(
                descriptor,
                evidence with { CadenceMode = testCase.Cadence, StartReason = testCase.Reason },
                CaptureContractReasonCodes.InvalidCadence,
                testCase.Name);
        }
    }

    [TestMethod]
    public void ValidateCycleEvidence_MeteringOutcomeMatrix_AcceptsEveryConsistentOutcome()
    {
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        var evidence = CreateCycleEvidence(descriptor);
        var cases = new (CaptureMeteringOutcome Outcome, CaptureControlDecisionReason Reason)[]
        {
            (CaptureMeteringOutcome.Measured, CaptureControlDecisionReason.WithinHysteresis),
            (CaptureMeteringOutcome.NoFrame, CaptureControlDecisionReason.NoSample),
            (CaptureMeteringOutcome.UnsupportedFormat, CaptureControlDecisionReason.NoSample),
            (CaptureMeteringOutcome.NoEligibleSamples, CaptureControlDecisionReason.NoSample),
            (CaptureMeteringOutcome.SaturationRejected, CaptureControlDecisionReason.SaturationRejected)
        };

        foreach (var testCase in cases)
        {
            var candidate = CloneControlEvidence(
                evidence,
                AutomaticControlOwnership.HostMetered,
                AutomaticControlOwnership.HostMetered,
                testCase.Reason,
                outcome: testCase.Outcome);

            AssertValidCycleEvidence(descriptor, candidate, testCase.Outcome.ToString());
        }
    }

    [TestMethod]
    public void ValidateCycleEvidence_InvalidMeteringMatrix_ReturnsStableReason()
    {
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        var evidence = CreateCycleEvidence(descriptor);
        var metering = evidence.Metering!;
        var invalidCases = new (string Name, Func<CaptureMeteringEvidence, CaptureMeteringEvidence> Mutate)[]
        {
            ("invalid outcome", value => value with { Outcome = (CaptureMeteringOutcome)999 }),
            ("started non-UTC", value => value with { StartedUtc = value.StartedUtc.ToOffset(TimeSpan.FromHours(1)) }),
            ("completed non-UTC", value => value with { CompletedUtc = value.CompletedUtc.ToOffset(TimeSpan.FromHours(1)) }),
            ("started before readout", value => value with { StartedUtc = descriptor.Timing.ReadoutCompletedUtc.AddTicks(-1) }),
            ("completed before started", value => value with { CompletedUtc = value.StartedUtc.AddTicks(-1) }),
            ("completed after decision start", value => value with { CompletedUtc = evidence.Decision.StartedUtc.AddTicks(1) }),
            ("negative considered", value => value with { ConsideredSampleCount = -1 }),
            ("negative accepted", value => value with { AcceptedSampleCount = -1 }),
            ("negative saturated", value => value with { SaturatedSampleCount = -1 }),
            ("negative scanned bytes", value => value with { ScannedBytes = -1 }),
            ("accepted exceeds considered", value => value with { AcceptedSampleCount = value.ConsideredSampleCount + 1 }),
            ("saturated count too low", value => value with { SaturatedSampleCount = value.SaturatedSampleCount - 1 }),
            ("saturated count too high", value => value with { SaturatedSampleCount = value.SaturatedSampleCount + 1 }),
            ("normalized NaN", value => value with { NormalizedLevel = double.NaN }),
            ("normalized below zero", value => value with { NormalizedLevel = -0.01 }),
            ("normalized above one", value => value with { NormalizedLevel = 1.01 }),
            ("measured level missing", value => value with { NormalizedLevel = null }),
            ("measured considered count missing", value => value with
            {
                ConsideredSampleCount = 0,
                AcceptedSampleCount = 0,
                SaturatedSampleCount = 0
            }),
            ("measured accepted count missing", value => value with
            {
                AcceptedSampleCount = 0,
                SaturatedSampleCount = value.ConsideredSampleCount
            }),
            ("measured scanned bytes missing", value => value with { ScannedBytes = 0 }),
            ("no-frame level present", value => CloneMetering(value, CaptureMeteringOutcome.NoFrame) with { NormalizedLevel = 0.1 }),
            ("no-frame counts present", value => CloneMetering(value, CaptureMeteringOutcome.NoFrame) with
            {
                ConsideredSampleCount = 1,
                AcceptedSampleCount = 1
            }),
            ("no-frame scanned bytes present", value => CloneMetering(value, CaptureMeteringOutcome.NoFrame) with { ScannedBytes = 1 }),
            ("unsupported level present", value => CloneMetering(value, CaptureMeteringOutcome.UnsupportedFormat) with { NormalizedLevel = 0.1 }),
            ("no-eligible level present", value => CloneMetering(value, CaptureMeteringOutcome.NoEligibleSamples) with { NormalizedLevel = 0.1 }),
            ("no-eligible scanned bytes present", value => CloneMetering(value, CaptureMeteringOutcome.NoEligibleSamples) with { ScannedBytes = 1 }),
            ("saturation level present", value => CloneMetering(value, CaptureMeteringOutcome.SaturationRejected) with { NormalizedLevel = 0.1 }),
            ("saturation accepted samples present", value => CloneMetering(value, CaptureMeteringOutcome.SaturationRejected) with
            {
                AcceptedSampleCount = 1,
                SaturatedSampleCount = value.ConsideredSampleCount - 1
            }),
            ("saturation considered samples missing", value => CloneMetering(value, CaptureMeteringOutcome.SaturationRejected) with
            {
                ConsideredSampleCount = 0,
                AcceptedSampleCount = 0,
                SaturatedSampleCount = 0
            }),
            ("saturation count missing", value => CloneMetering(value, CaptureMeteringOutcome.SaturationRejected) with
            {
                ConsideredSampleCount = 1,
                AcceptedSampleCount = 1,
                SaturatedSampleCount = 0
            }),
            ("saturation scanned bytes missing", value => CloneMetering(value, CaptureMeteringOutcome.SaturationRejected) with { ScannedBytes = 0 })
        };

        foreach (var testCase in invalidCases)
        {
            AssertInvalidCycleEvidence(
                descriptor,
                evidence with { Metering = testCase.Mutate(metering) },
                CaptureContractReasonCodes.InvalidMetering,
                testCase.Name);
        }
    }

    [TestMethod]
    public void ValidateCycleEvidence_ControlOwnershipMatrix_AcceptsValidOwnershipAndReasons()
    {
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        var evidence = CreateCycleEvidence(descriptor);
        var lessExposure = evidence.Decision.ActiveExposure - TimeSpan.FromMilliseconds(100);
        var moreExposure = evidence.Decision.ActiveExposure + TimeSpan.FromMilliseconds(100);
        var lessGain = evidence.Decision.ActiveGain - 1;
        var moreGain = evidence.Decision.ActiveGain + 1;
        var validCases = new (string Name, CaptureCycleEvidence Evidence)[]
        {
            ("disabled", CloneControlEvidence(evidence, AutomaticControlOwnership.Disabled, AutomaticControlOwnership.Disabled,
                CaptureControlDecisionReason.Disabled)),
            ("camera native", CloneControlEvidence(evidence, AutomaticControlOwnership.CameraNative, AutomaticControlOwnership.CameraNative,
                CaptureControlDecisionReason.CameraNative, moreExposure, moreGain)),
            ("disabled exposure/native gain", CloneControlEvidence(evidence, AutomaticControlOwnership.Disabled, AutomaticControlOwnership.CameraNative,
                CaptureControlDecisionReason.CameraNative, decidedGain: moreGain)),
            ("native exposure/disabled gain", CloneControlEvidence(evidence, AutomaticControlOwnership.CameraNative, AutomaticControlOwnership.Disabled,
                CaptureControlDecisionReason.CameraNative, moreExposure)),
            ("host exposure only", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.Disabled,
                CaptureControlDecisionReason.ExposureAdjusted, lessExposure)),
            ("host gain only", CloneControlEvidence(evidence, AutomaticControlOwnership.Disabled, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.GainAdjusted, decidedGain: lessGain)),
            ("no frame", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.NoSample, outcome: CaptureMeteringOutcome.NoFrame)),
            ("unsupported format", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.NoSample, outcome: CaptureMeteringOutcome.UnsupportedFormat)),
            ("no eligible samples", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.NoSample, outcome: CaptureMeteringOutcome.NoEligibleSamples)),
            ("saturation unchanged", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.SaturationRejected, outcome: CaptureMeteringOutcome.SaturationRejected)),
            ("saturation exposure downward", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.SaturationRejected, lessExposure, outcome: CaptureMeteringOutcome.SaturationRejected)),
            ("saturation gain downward", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.SaturationRejected, decidedGain: lessGain, outcome: CaptureMeteringOutcome.SaturationRejected)),
            ("saturation both downward", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.SaturationRejected, lessExposure, lessGain, CaptureMeteringOutcome.SaturationRejected)),
            ("within hysteresis", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.WithinHysteresis)),
            ("exposure adjusted", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.ExposureAdjusted, lessExposure)),
            ("gain adjusted", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.GainAdjusted, decidedGain: lessGain)),
            ("both adjusted", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.ExposureAndGainAdjusted, lessExposure, lessGain)),
            ("lower limit", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.LowerLimitReached)),
            ("upper limit", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.UpperLimitReached))
        };

        foreach (var testCase in validCases)
        {
            AssertValidCycleEvidence(descriptor, testCase.Evidence, testCase.Name);
        }
    }

    [TestMethod]
    public void ValidateCycleEvidence_SetpointApplicationFailure_RequiresChangedOwnedControlAndNoAppliedTimestamp()
    {
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        var evidence = CreateCycleEvidence(descriptor);
        var moreExposure = evidence.Decision.ActiveExposure + TimeSpan.FromMilliseconds(100);
        var lessExposure = evidence.Decision.ActiveExposure - TimeSpan.FromMilliseconds(100);
        var moreGain = evidence.Decision.ActiveGain + 1;
        var nativeFailure = CloneControlEvidence(
            evidence,
            AutomaticControlOwnership.CameraNative,
            AutomaticControlOwnership.Disabled,
            CaptureControlDecisionReason.SetpointApplicationFailed,
            moreExposure);
        var hostFailure = CloneControlEvidence(
            evidence,
            AutomaticControlOwnership.HostMetered,
            AutomaticControlOwnership.Disabled,
            CaptureControlDecisionReason.SetpointApplicationFailed,
            lessExposure);
        var targetFpsFailure = CloneControlEvidence(
            evidence,
            AutomaticControlOwnership.Disabled,
            AutomaticControlOwnership.Disabled,
            CaptureControlDecisionReason.SetpointApplicationFailed) with
        {
            Decision = evidence.Decision with
            {
                DecidedExposure = evidence.Decision.ActiveExposure,
                DecidedGain = evidence.Decision.ActiveGain,
                ActiveTargetFps = null,
                DecidedTargetFps = 12,
                Reason = CaptureControlDecisionReason.SetpointApplicationFailed
            }
        };

        AssertValidCycleEvidence(descriptor, nativeFailure, "camera-native failure");
        AssertValidCycleEvidence(descriptor, hostFailure, "host-metered failure");
        AssertValidCycleEvidence(descriptor, targetFpsFailure, "disabled controls target-FPS-only failure");

        var invalidCases = new (string Name, CaptureCycleEvidence Evidence)[]
        {
            ("camera-native changed disabled control", nativeFailure with
            {
                Decision = nativeFailure.Decision with { DecidedGain = moreGain }
            }),
            ("camera-native no change", CloneControlEvidence(
                evidence,
                AutomaticControlOwnership.CameraNative,
                AutomaticControlOwnership.Disabled,
                CaptureControlDecisionReason.SetpointApplicationFailed)),
            ("camera-native applied timestamp", nativeFailure with
            {
                Decision = nativeFailure.Decision with { SetpointAppliedUtc = nativeFailure.Decision.CompletedUtc }
            }),
            ("host-metered no change", CloneControlEvidence(
                evidence,
                AutomaticControlOwnership.HostMetered,
                AutomaticControlOwnership.Disabled,
                CaptureControlDecisionReason.SetpointApplicationFailed)),
            ("host-metered changed disabled control", hostFailure with
            {
                Decision = hostFailure.Decision with { DecidedGain = moreGain }
            }),
            ("host-metered applied timestamp", hostFailure with
            {
                Decision = hostFailure.Decision with { SetpointAppliedUtc = hostFailure.Decision.CompletedUtc }
            }),
            ("target FPS applied timestamp", targetFpsFailure with
            {
                Decision = targetFpsFailure.Decision with
                {
                    SetpointAppliedUtc = targetFpsFailure.Decision.CompletedUtc
                }
            })
        };

        foreach (var testCase in invalidCases)
        {
            AssertInvalidCycleEvidence(
                descriptor,
                testCase.Evidence,
                CaptureContractReasonCodes.InvalidControls,
                testCase.Name);
        }
    }

    [TestMethod]
    public void ValidateCycleEvidence_InvalidOwnershipAndControlMatrix_ReturnsStableReasons()
    {
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        var evidence = CreateCycleEvidence(descriptor);
        var activeExposure = evidence.Decision.ActiveExposure;
        var activeGain = evidence.Decision.ActiveGain;
        var lessExposure = activeExposure - TimeSpan.FromMilliseconds(100);
        var moreExposure = activeExposure + TimeSpan.FromMilliseconds(100);
        var lessGain = activeGain - 1;
        var moreGain = activeGain + 1;
        var noHost = CloneControlEvidence(
            evidence,
            AutomaticControlOwnership.Disabled,
            AutomaticControlOwnership.Disabled,
            CaptureControlDecisionReason.Disabled);
        AssertValidCycleEvidence(
            descriptor,
            noHost with { Decision = noHost.Decision with { DecidedTargetFps = 12 } },
            "disabled exposure and gain with module-provided target FPS");
        var invalidCases = new (string Name, CaptureCycleEvidence Evidence, string ReasonCode)[]
        {
            ("invalid exposure ownership", evidence with { ExposureControl = (AutomaticControlOwnership)999 }, CaptureContractReasonCodes.InvalidControls),
            ("invalid gain ownership", evidence with { GainControl = (AutomaticControlOwnership)999 }, CaptureContractReasonCodes.InvalidControls),
            ("unspecified exposure ownership", evidence with { ExposureControl = AutomaticControlOwnership.Unspecified }, CaptureContractReasonCodes.InvalidControls),
            ("unspecified gain ownership", evidence with { GainControl = AutomaticControlOwnership.Unspecified }, CaptureContractReasonCodes.InvalidControls),
            ("invalid solar regime", evidence with { SolarRegime = (CaptureSolarRegime)999 }, CaptureContractReasonCodes.InvalidMetering),
            ("missing host meter", evidence with { Metering = null }, CaptureContractReasonCodes.InvalidMetering),
            ("missing host solar regime", evidence with { SolarRegime = null }, CaptureContractReasonCodes.InvalidMetering),
            ("unexpected native meter", noHost with { Metering = evidence.Metering }, CaptureContractReasonCodes.InvalidMetering),
            ("unexpected native solar regime", noHost with { SolarRegime = CaptureSolarRegime.Day }, CaptureContractReasonCodes.InvalidMetering),
            ("missing decision", evidence with { Decision = null! }, CaptureContractReasonCodes.InvalidControls),
            ("invalid decision reason", evidence with
            {
                Decision = evidence.Decision with { Reason = (CaptureControlDecisionReason)999 }
            }, CaptureContractReasonCodes.InvalidControls),
            ("negative active exposure", evidence with { Decision = evidence.Decision with { ActiveExposure = TimeSpan.FromTicks(-1) } }, CaptureContractReasonCodes.InvalidControls),
            ("negative decided exposure", evidence with { Decision = evidence.Decision with { DecidedExposure = TimeSpan.FromTicks(-1) } }, CaptureContractReasonCodes.InvalidControls),
            ("active gain NaN", evidence with { Decision = evidence.Decision with { ActiveGain = double.NaN } }, CaptureContractReasonCodes.InvalidControls),
            ("negative active gain", evidence with { Decision = evidence.Decision with { ActiveGain = -1 } }, CaptureContractReasonCodes.InvalidControls),
            ("decided gain NaN", evidence with { Decision = evidence.Decision with { DecidedGain = double.NaN } }, CaptureContractReasonCodes.InvalidControls),
            ("negative decided gain", evidence with { Decision = evidence.Decision with { DecidedGain = -1 } }, CaptureContractReasonCodes.InvalidControls),
            ("active target FPS NaN", evidence with { Decision = evidence.Decision with { ActiveTargetFps = double.NaN } }, CaptureContractReasonCodes.InvalidControls),
            ("active target FPS zero", evidence with { Decision = evidence.Decision with { ActiveTargetFps = 0 } }, CaptureContractReasonCodes.InvalidControls),
            ("decided target FPS infinity", evidence with { Decision = evidence.Decision with { DecidedTargetFps = double.PositiveInfinity } }, CaptureContractReasonCodes.InvalidControls),
            ("negative decided target FPS", evidence with { Decision = evidence.Decision with { DecidedTargetFps = -1 } }, CaptureContractReasonCodes.InvalidControls),
            ("active exposure mismatch", evidence with { Decision = evidence.Decision with { ActiveExposure = moreExposure } }, CaptureContractReasonCodes.InvalidControls),
            ("active gain mismatch", evidence with { Decision = evidence.Decision with { ActiveGain = moreGain } }, CaptureContractReasonCodes.InvalidControls),
            ("host disabled reason", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.Disabled), CaptureContractReasonCodes.InvalidControls),
            ("host camera-native reason", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.CameraNative), CaptureContractReasonCodes.InvalidControls),
            ("no-sample with measurement", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.NoSample), CaptureContractReasonCodes.InvalidControls),
            ("no-sample with saturation", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.NoSample, outcome: CaptureMeteringOutcome.SaturationRejected), CaptureContractReasonCodes.InvalidControls),
            ("saturation reason with measurement", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.SaturationRejected), CaptureContractReasonCodes.InvalidControls),
            ("saturation reason with no frame", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.SaturationRejected, outcome: CaptureMeteringOutcome.NoFrame), CaptureContractReasonCodes.InvalidControls),
            ("within hysteresis with no frame", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.WithinHysteresis, outcome: CaptureMeteringOutcome.NoFrame), CaptureContractReasonCodes.InvalidControls),
            ("within hysteresis changed", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.WithinHysteresis, lessExposure), CaptureContractReasonCodes.InvalidControls),
            ("exposure adjustment unchanged", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.ExposureAdjusted), CaptureContractReasonCodes.InvalidControls),
            ("exposure adjustment changed gain", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.ExposureAdjusted, lessExposure, lessGain), CaptureContractReasonCodes.InvalidControls),
            ("exposure adjustment without ownership", CloneControlEvidence(evidence, AutomaticControlOwnership.Disabled, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.ExposureAdjusted, lessExposure), CaptureContractReasonCodes.InvalidControls),
            ("gain adjustment unchanged", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.GainAdjusted), CaptureContractReasonCodes.InvalidControls),
            ("gain adjustment changed exposure", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.GainAdjusted, lessExposure, lessGain), CaptureContractReasonCodes.InvalidControls),
            ("gain adjustment without ownership", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.Disabled,
                CaptureControlDecisionReason.GainAdjusted, decidedGain: lessGain), CaptureContractReasonCodes.InvalidControls),
            ("both adjustment changed exposure only", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.ExposureAndGainAdjusted, lessExposure), CaptureContractReasonCodes.InvalidControls),
            ("both adjustment changed gain only", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.ExposureAndGainAdjusted, decidedGain: lessGain), CaptureContractReasonCodes.InvalidControls),
            ("both adjustment without gain ownership", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.Disabled,
                CaptureControlDecisionReason.ExposureAndGainAdjusted, lessExposure, lessGain), CaptureContractReasonCodes.InvalidControls),
            ("saturation exposure upward", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.SaturationRejected, moreExposure, outcome: CaptureMeteringOutcome.SaturationRejected), CaptureContractReasonCodes.InvalidControls),
            ("saturation gain upward", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.SaturationRejected, decidedGain: moreGain, outcome: CaptureMeteringOutcome.SaturationRejected), CaptureContractReasonCodes.InvalidControls),
            ("saturation changed non-host exposure", CloneControlEvidence(evidence, AutomaticControlOwnership.Disabled, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.SaturationRejected, lessExposure, outcome: CaptureMeteringOutcome.SaturationRejected), CaptureContractReasonCodes.InvalidControls),
            ("lower limit changed", CloneControlEvidence(evidence, AutomaticControlOwnership.HostMetered, AutomaticControlOwnership.HostMetered,
                CaptureControlDecisionReason.LowerLimitReached, lessExposure), CaptureContractReasonCodes.InvalidControls),
            ("disabled wrong reason", noHost with { Decision = noHost.Decision with { Reason = CaptureControlDecisionReason.CameraNative } }, CaptureContractReasonCodes.InvalidControls),
            ("disabled changed", noHost with { Decision = noHost.Decision with { DecidedGain = lessGain } }, CaptureContractReasonCodes.InvalidControls),
            ("native wrong reason", CloneControlEvidence(evidence, AutomaticControlOwnership.CameraNative, AutomaticControlOwnership.CameraNative,
                CaptureControlDecisionReason.Disabled), CaptureContractReasonCodes.InvalidControls),
            ("disabled exposure changed under native gain", CloneControlEvidence(evidence, AutomaticControlOwnership.Disabled, AutomaticControlOwnership.CameraNative,
                CaptureControlDecisionReason.CameraNative, lessExposure), CaptureContractReasonCodes.InvalidControls),
            ("disabled gain changed under native exposure", CloneControlEvidence(evidence, AutomaticControlOwnership.CameraNative, AutomaticControlOwnership.Disabled,
                CaptureControlDecisionReason.CameraNative, decidedGain: lessGain), CaptureContractReasonCodes.InvalidControls)
        };

        foreach (var testCase in invalidCases)
        {
            AssertInvalidCycleEvidence(descriptor, testCase.Evidence, testCase.ReasonCode, testCase.Name);
        }
    }

    [TestMethod]
    public void ValidateCycleEvidence_TimingMatrix_ValidatesUtcOrderGapAndAppliedBoundaries()
    {
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        var evidence = CreateCycleEvidence(descriptor);
        var validAppliedCases = new[]
        {
            evidence.Decision.CompletedUtc,
            evidence.IngressHandoffStartedUtc
        };

        foreach (var appliedUtc in validAppliedCases)
        {
            AssertValidCycleEvidence(
                descriptor,
                evidence with { Decision = evidence.Decision with { SetpointAppliedUtc = appliedUtc } },
                $"applied {appliedUtc:O}");
        }

        var invalidCases = new (string Name, CaptureCycleEvidence Evidence)[]
        {
            ("negative observed gap", evidence with { ObservedInterExposureGap = TimeSpan.FromTicks(-1) }),
            ("module call non-UTC", evidence with { ModuleCallStartedUtc = evidence.ModuleCallStartedUtc.ToOffset(TimeSpan.FromHours(1)) }),
            ("handoff non-UTC", evidence with { IngressHandoffStartedUtc = evidence.IngressHandoffStartedUtc.ToOffset(TimeSpan.FromHours(1)) }),
            ("decision start non-UTC", evidence with
            {
                Decision = evidence.Decision with { StartedUtc = evidence.Decision.StartedUtc.ToOffset(TimeSpan.FromHours(1)) }
            }),
            ("decision completion non-UTC", evidence with
            {
                Decision = evidence.Decision with { CompletedUtc = evidence.Decision.CompletedUtc.ToOffset(TimeSpan.FromHours(1)) }
            }),
            ("applied non-UTC", evidence with
            {
                Decision = evidence.Decision with
                {
                    SetpointAppliedUtc = evidence.Decision.CompletedUtc.ToOffset(TimeSpan.FromHours(1))
                }
            }),
            ("module after decision start", evidence with { ModuleCallStartedUtc = evidence.Decision.StartedUtc.AddTicks(1) }),
            ("decision before readout", evidence with
            {
                Decision = evidence.Decision with { StartedUtc = descriptor.Timing.ReadoutCompletedUtc.AddTicks(-1) }
            }),
            ("decision completed before start", evidence with
            {
                Decision = evidence.Decision with { CompletedUtc = evidence.Decision.StartedUtc.AddTicks(-1) }
            }),
            ("decision completed after handoff", evidence with
            {
                Decision = evidence.Decision with { CompletedUtc = evidence.IngressHandoffStartedUtc.AddTicks(1) }
            }),
            ("handoff after durable ingress", evidence with
            {
                IngressHandoffStartedUtc = descriptor.Timing.DurableIngressUtc.AddTicks(1)
            }),
            ("applied before decision completion", evidence with
            {
                Decision = evidence.Decision with
                {
                    SetpointAppliedUtc = evidence.Decision.CompletedUtc.AddTicks(-1)
                }
            }),
            ("applied after handoff", evidence with
            {
                Decision = evidence.Decision with
                {
                    SetpointAppliedUtc = evidence.IngressHandoffStartedUtc.AddTicks(1)
                }
            })
        };

        foreach (var testCase in invalidCases)
        {
            AssertInvalidCycleEvidence(
                descriptor,
                testCase.Evidence,
                CaptureContractReasonCodes.InvalidTimingOrder,
                testCase.Name);
        }

        var correctedClock = descriptor with
        {
            Timing = descriptor.Timing with
            {
                SetpointAppliedUtc = descriptor.Timing.RequestedStartUtc.AddSeconds(-1)
            },
            CycleEvidence = evidence with
            {
                ModuleCallStartedUtc = descriptor.Timing.RequestedStartUtc.AddTicks(-1)
            }
        };
        Assert.IsTrue(correctedClock.Validate().IsValid);
    }

    [TestMethod]
    public void ParseManifest_WithV1_ReturnsLegacyIncompleteWithoutDescriptor()
    {
        var legacy = new ArtifactUploadManifest(
            ArtifactUploadManifest.CurrentSchemaVersion,
            "agent-a",
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            FrameArtifactRole.Raw,
            "application/octet-stream",
            4,
            new string('A', 64),
            DateTimeOffset.UnixEpoch,
            "raw-v1",
            "frames/raw.bin");
        var json = JsonSerializer.SerializeToUtf8Bytes(legacy, WebJsonOptions);

        var result = CaptureContractJson.ParseManifest(json);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(CaptureManifestCompleteness.LegacyIncomplete, result.Document!.Completeness);
        Assert.IsNotNull(result.Document.LegacyManifest);
        Assert.IsNull(result.Document.LegacyManifest.Scene);
        Assert.IsNull(result.Document.Manifest);
    }

    [TestMethod]
    public void ParseManifest_WithUnknownOrMalformedVersion_ReturnsStableReason()
    {
        var unknown = CaptureContractJson.ParseManifest("{\"schemaVersion\":\"v3\"}"u8.ToArray());
        var missing = CaptureContractJson.ParseManifest("{}"u8.ToArray());
        var malformed = CaptureContractJson.ParseManifest("{"u8.ToArray());

        Assert.AreEqual(CaptureContractReasonCodes.UnsupportedSchema, unknown.Validation.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.UnsupportedSchema, missing.Validation.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidJson, malformed.Validation.ReasonCode);
    }

    [TestMethod]
    public void ParseManifest_WithInvalidV1OrV2_ReturnsStableReason()
    {
        var invalidLegacy = CaptureContractJson.ParseManifest(
            "{\"schemaVersion\":\"v1\",\"agentId\":\"\"}"u8.ToArray());
        var payload = new byte[8];
        var current = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var invalidCurrent = current with
        {
            Descriptor = current.Descriptor with
            {
                Capture = current.Descriptor.Capture with
                {
                    CaptureId = Guid.Empty
                }
            }
        };

        var currentResult = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(invalidCurrent));

        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity, invalidLegacy.Validation.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity, currentResult.Validation.ReasonCode);
    }

    [TestMethod]
    public void ParseManifest_WithMissingNullOrNumericRequiredFields_ReturnsStableReason()
    {
        var manifest = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        var missing = CaptureContractJson.ParseManifest(
            "{\"schemaVersion\":\"v2\",\"relativeArtifactPath\":\"frames/raw.bin\"}"u8.ToArray());

        var nullNode = JsonNode.Parse(CaptureContractJson.Serialize(manifest))!.AsObject();
        nullNode["descriptor"]!.AsObject()["timing"] = null;
        var nestedNull = CaptureContractJson.ParseManifest(Encoding.UTF8.GetBytes(nullNode.ToJsonString()));

        var numericNode = JsonNode.Parse(CaptureContractJson.Serialize(manifest))!.AsObject();
        numericNode["descriptor"]!["layout"]!.AsObject()["pixelFormat"] = 1;
        var numericEnum = CaptureContractJson.ParseManifest(Encoding.UTF8.GetBytes(numericNode.ToJsonString()));

        Assert.AreEqual(CaptureContractReasonCodes.InvalidJson, missing.Validation.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidTimingOrder, nestedNull.Validation.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidJson, numericEnum.Validation.ReasonCode);
    }

    [TestMethod]
    public void RecipeOptionsHash_IsCanonicalAcrossPropertyOrder()
    {
        using var first = JsonDocument.Parse("{\"z\":1,\"name\":\"fixture\",\"nested\":{\"b\":true,\"a\":null},\"array\":[2,1]}");
        using var second = JsonDocument.Parse("{\"array\":[2,1],\"nested\":{\"a\":null,\"b\":true},\"name\":\"fixture\",\"z\":1}");

        var firstHash = CaptureContractJson.ComputeCanonicalJsonSha256(first.RootElement);
        var secondHash = CaptureContractJson.ComputeCanonicalJsonSha256(second.RootElement);

        Assert.AreEqual(firstHash, secondHash);
        Assert.AreEqual(
            "{\"array\":[2,1],\"name\":\"fixture\",\"nested\":{\"a\":null,\"b\":true},\"z\":1}",
            CaptureContractJson.Canonicalize(first.RootElement).GetRawText());
    }

    [TestMethod]
    public void ManifestIdempotency_IgnoresStoragePathButChangesWithVariant()
    {
        var payload = new byte[8];
        var first = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var moved = first with { RelativeArtifactPath = "archive/raw.bin" };
        var variant = first with
        {
            Descriptor = first.Descriptor with
            {
                Artifact = first.Descriptor.Artifact with { Variant = "alternate" }
            }
        };

        Assert.AreEqual(first.IdempotencyKey, moved.IdempotencyKey);
        Assert.AreNotEqual(first.IdempotencyKey, variant.IdempotencyKey);
        Assert.AreNotEqual(first.IdempotencyKey, CaptureContractJson.ComputeManifestSha256(moved));
    }

    [TestMethod]
    public void ManifestIdempotency_NormalizesChecksumCasing()
    {
        var upper = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        var profiles = upper.Descriptor.Profiles;
        var lower = upper with
        {
            Descriptor = upper.Descriptor with
            {
                Profiles = profiles with
                {
                    Rig = profiles.Rig with { Sha256 = new string('a', 64) },
                    Calibration = profiles.Calibration with { Sha256 = new string('a', 64) },
                    Mask = profiles.Mask with { Sha256 = new string('a', 64) },
                    Sensor = profiles.Sensor with { Sha256 = new string('a', 64) },
                    Processing = profiles.Processing with { Sha256 = new string('a', 64) }
                },
                Artifact = upper.Descriptor.Artifact with
                {
                    ChecksumSha256 = "af5570f5a1810b7af78caf4bc70a660f0df51e42baf91d4de5b2328de0e83dfc",
                    Recipe = upper.Descriptor.Artifact.Recipe with
                    {
                        OptionsSha256 = "18de2b6210171497ea31bddec3fe87364962d62ade091704355dfee11873b768"
                    }
                }
            }
        };

        Assert.AreEqual(upper.IdempotencyKey, lower.IdempotencyKey);
        CollectionAssert.AreEqual(CaptureContractJson.Serialize(upper), CaptureContractJson.Serialize(lower));
    }

    [TestMethod]
    public async Task PayloadChecksum_StreamAndSpanProduceSameValue()
    {
        var payload = Enumerable.Range(0, 8193).Select(static value => (byte)value).ToArray();
        using var stream = new MemoryStream(payload, writable: false);

        var streamHash = await PayloadChecksum.ComputeSha256Async(stream).ConfigureAwait(false);

        Assert.AreEqual(PayloadChecksum.ComputeSha256(payload), streamHash);
    }

    [TestMethod]
    public void Reconstruct_WithLengthOrChecksumMismatch_ReturnsStableReason()
    {
        var payload = new byte[8];
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload).Descriptor;

        var length = FrameReconstructor.TryReconstruct(descriptor, payload.AsMemory(0, 7), out _);
        payload[0] = 1;
        var checksum = FrameReconstructor.TryReconstruct(descriptor, payload, out _);

        Assert.AreEqual(CaptureContractReasonCodes.PayloadLengthMismatch, length.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.PayloadChecksumMismatch, checksum.ReasonCode);
    }

    [TestMethod]
    public void Validate_WithInvalidIdentityTimingProfileRecipeLayoutAndLineage_ReturnsStableReasons()
    {
        var payload = new byte[8];
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload).Descriptor;
        var invalidIdentity = descriptor with { Capture = descriptor.Capture with { CaptureId = Guid.Empty } };
        var invalidSequence = descriptor with { Capture = descriptor.Capture with { CaptureSequence = 0 } };
        var invalidTiming = descriptor with
        {
            Timing = descriptor.Timing with { ReadoutCompletedUtc = descriptor.Timing.ExposureStartedUtc.AddSeconds(-1) }
        };
        var invalidProfile = descriptor with
        {
            Profiles = descriptor.Profiles with { Mask = descriptor.Profiles.Mask with { Sha256 = "bad" } }
        };
        var invalidRecipe = descriptor with
        {
            Artifact = descriptor.Artifact with
            {
                Recipe = descriptor.Artifact.Recipe with { OptionsSha256 = new string('0', 64) }
            }
        };
        var invalidLayout = descriptor with { Layout = descriptor.Layout with { StrideBytes = 3 } };
        var invalidLineage = descriptor with
        {
            Artifact = descriptor.Artifact with { SourceArtifactIds = [Guid.Empty] }
        };

        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity, invalidIdentity.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidCaptureSequence, invalidSequence.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidTimingOrder, invalidTiming.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidProfile, invalidProfile.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.RecipeHashMismatch, invalidRecipe.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidStride, invalidLayout.Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLineage, invalidLineage.Validate().ReasonCode);
    }

    [TestMethod]
    public void Validate_WithFormatSpecificLayoutErrors_ReturnsStableReasons()
    {
        var descriptor = CreateManifest(CameraPixelFormat.BayerRggb16, 2, 2, 4, new byte[8]).Descriptor;

        Assert.AreEqual(CaptureContractReasonCodes.InvalidSampleDepth,
            (descriptor with { Layout = descriptor.Layout with { SampleDepthBits = 11 } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidByteOrder,
            (descriptor with { Layout = descriptor.Layout with { ByteOrder = FrameByteOrder.NotApplicable } }).Validate().ReasonCode);
        Assert.IsTrue((descriptor with { Layout = descriptor.Layout with { ByteOrder = FrameByteOrder.BigEndian } }).Validate().IsValid);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidPacking,
            (descriptor with { Layout = descriptor.Layout with { Packing = FrameSamplePacking.Packed } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidCfa,
            (descriptor with { Layout = descriptor.Layout with { CfaPattern = ColorFilterArrayPattern.None } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLevels,
            (descriptor with { Layout = descriptor.Layout with { BlackLevel = 10, WhiteLevel = 5 } }).Validate().ReasonCode);
    }

    [TestMethod]
    public void ManifestV2_WithExplicitLowerDepthReadout_RoundTripsAsComplete()
    {
        var payload = new byte[160 * 120 * 2];
        var legacy = CreateManifest(CameraPixelFormat.Mono16, 160, 120, 320, payload);
        var layout = legacy.Descriptor.Layout with
        {
            SampleDepthBits = 10,
            WhiteLevel = 1023,
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            Readout = new FrameReadoutDescriptor(
                1936,
                1216,
                648,
                368,
                640,
                480,
                4,
                4,
                FrameBinningAlgorithm.DigitalAverageV1,
                null,
                null)
        };
        var manifest = legacy with { Descriptor = legacy.Descriptor with { Layout = layout } };

        var encoded = CaptureContractJson.Serialize(manifest);
        var parsed = CaptureContractJson.ParseManifest(encoded);
        var result = FrameReconstructor.TryReconstruct(parsed.Document!.Manifest!.Descriptor, payload, out var frame);

        Assert.IsTrue(parsed.IsValid);
        Assert.AreEqual(CaptureManifestCompleteness.Complete, parsed.Document.Completeness);
        Assert.AreEqual(layout, parsed.Document.Manifest.Descriptor.Layout);
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(layout, frame!.Layout);
        CollectionAssert.AreEqual(encoded, CaptureContractJson.Serialize(parsed.Document.Manifest));
    }

    [TestMethod]
    public void ParseManifest_WithAmbiguousLowerDepthV2_PreservesBytesAsLegacyIncomplete()
    {
        var payload = new byte[8];
        var legacy = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var manifest = legacy with
        {
            Descriptor = legacy.Descriptor with
            {
                Layout = legacy.Descriptor.Layout with { SampleDepthBits = 12, WhiteLevel = 65535 }
            }
        };
        var encoded = CaptureContractJson.Serialize(manifest);

        var parsed = CaptureContractJson.ParseManifest(encoded);
        var result = FrameReconstructor.TryReconstruct(parsed.Document!.Manifest!.Descriptor, payload, out var frame);

        Assert.IsTrue(parsed.IsValid);
        Assert.AreEqual(CaptureManifestCompleteness.LegacyIncomplete, parsed.Document.Completeness);
        Assert.IsTrue(result.IsValid);
        CollectionAssert.AreEqual(payload, frame!.PixelData.ToArray());
        CollectionAssert.AreEqual(encoded, CaptureContractJson.Serialize(parsed.Document.Manifest));
    }

    [TestMethod]
    public void Validate_WithPartialOrIncompatibleStoredCodeSemantics_ReturnsStableReason()
    {
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;

        var missingLevelSpace = descriptor.Layout with
        {
            SampleDepthBits = 12,
            WhiteLevel = 4095,
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1
        };
        var invalidIdentity = missingLevelSpace with
        {
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            StoredCodeTransform = FrameStoredCodeTransform.IdentityV1
        };

        Assert.AreEqual(CaptureContractReasonCodes.InvalidStoredCode,
            (descriptor with { Layout = missingLevelSpace }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidStoredCode,
            (descriptor with { Layout = invalidIdentity }).Validate().ReasonCode);
    }

    [TestMethod]
    public void Validate_WithInvalidReadoutGeometryOrCfaPhase_ReturnsStableReason()
    {
        var mono = CreateManifest(CameraPixelFormat.Mono16, 160, 120, 320, new byte[38400]).Descriptor;
        var readout = new FrameReadoutDescriptor(
            1936, 1216, 648, 368, 640, 480, 4, 4, FrameBinningAlgorithm.DigitalAverageV1, null, null);
        var bayer = CreateManifest(CameraPixelFormat.BayerRggb16, 2, 2, 4, new byte[8]).Descriptor;
        var shiftedCfa = new FrameReadoutDescriptor(
            4, 4, 1, 0, 2, 2, 1, 1, FrameBinningAlgorithm.IdentityV1, 0, 0);

        Assert.AreEqual(CaptureContractReasonCodes.InvalidReadout,
            (mono with { Layout = mono.Layout with { Readout = readout with { RoiWidth = 641 } } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidCfa,
            (bayer with { Layout = bayer.Layout with { Readout = shiftedCfa } }).Validate().ReasonCode);
    }

    [TestMethod]
    public void Validate_WithInvalidControlsArtifactRecipeChecksumAndSchema_ReturnsStableReasons()
    {
        var payload = new byte[8];
        var manifest = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var descriptor = manifest.Descriptor;

        Assert.AreEqual(CaptureContractReasonCodes.InvalidControls,
            (descriptor with { Controls = descriptor.Controls with { EffectiveGain = double.NaN } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidDimensions,
            (descriptor with { Layout = descriptor.Layout with { Width = 0 } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.UnsupportedFormat,
            (descriptor with { Layout = descriptor.Layout with { PixelFormat = (CameraPixelFormat)999 } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidArtifactRole,
            (descriptor with { Artifact = descriptor.Artifact with { Role = (FrameArtifactRole)999 } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidArtifactVariant,
            (descriptor with { Artifact = descriptor.Artifact with { Variant = " " } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidRecipe,
            (descriptor with { Artifact = descriptor.Artifact with { Recipe = descriptor.Artifact.Recipe with { Name = "" } } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidChecksum,
            (descriptor with { Artifact = descriptor.Artifact with { ChecksumSha256 = "bad" } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.UnsupportedSchema,
            (manifest with { SchemaVersion = "v3" }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidPath,
            (manifest with { RelativeArtifactPath = "../raw.bin" }).Validate().ReasonCode);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("/raw.bin")]
    [DataRow("\\raw.bin")]
    [DataRow("C:\\raw.bin")]
    [DataRow("frames/./raw.bin")]
    [DataRow("frames/\0/raw.bin")]
    public void Validate_WithUnsafeManifestPaths_ReturnsStableReason(string path)
    {
        var manifest = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]) with
        {
            RelativeArtifactPath = path
        };

        Assert.AreEqual(CaptureContractReasonCodes.InvalidPath, manifest.Validate().ReasonCode);
    }

    [TestMethod]
    public void Validate_WithArtifactIdentityTimingLineageAndMediaErrors_ReturnsStableReasons()
    {
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor;
        var source = Guid.Parse("30000000-0000-0000-0000-000000000001");

        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity,
            (descriptor with { Artifact = null! }).Validate().ReasonCode);
        var invalidSource = (descriptor with
        {
            Artifact = descriptor.Artifact with { SourceId = "" }
        }).Validate();
        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity, invalidSource.ReasonCode);
        Assert.AreEqual("descriptor.artifact.sourceId", invalidSource.FieldPath);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidTimingOrder,
            (descriptor with { Artifact = descriptor.Artifact with { CreatedUtc = descriptor.Artifact.CreatedUtc.ToOffset(TimeSpan.FromHours(1)) } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLineage,
            (descriptor with { Artifact = descriptor.Artifact with { SourceArtifactIds = [source, source] } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLineage,
            (descriptor with { Artifact = descriptor.Artifact with { Role = FrameArtifactRole.Preview, SourceArtifactIds = [] } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidArtifactRole,
            (descriptor with { Artifact = descriptor.Artifact with { MediaType = "" } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity,
            (descriptor with { Capture = descriptor.Capture with { CaptureId = descriptor.Artifact.ArtifactId } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLineage,
            (descriptor with { Artifact = descriptor.Artifact with { SourceArtifactIds = [descriptor.Artifact.ArtifactId] } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLineage,
            (descriptor with { Artifact = descriptor.Artifact with { SourceArtifactIds = [descriptor.Capture.CaptureId] } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLevels,
            (descriptor with { Layout = descriptor.Layout with { WhiteLevel = double.NaN } }).Validate().ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidLevels,
            (descriptor with { Layout = descriptor.Layout with { WhiteLevel = 65536 } }).Validate().ReasonCode);
    }

    [TestMethod]
    public void Reconstruct_WithoutChecksumVerification_WrapsPayload()
    {
        var payload = new byte[8];
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload).Descriptor;
        payload[0] = 42;

        var result = FrameReconstructor.TryReconstruct(descriptor, payload, out var frame, verifyChecksum: false);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(42, frame!.PixelData.Span[0]);
    }

    [TestMethod]
    public void Reconstruct_WithNullOrInvalidDescriptor_ReturnsStableReason()
    {
        var nullResult = FrameReconstructor.TryReconstruct(null!, ReadOnlyMemory<byte>.Empty, out var nullFrame);
        var descriptor = CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]).Descriptor with
        {
            Capture = new CaptureIdentityDescriptor("", "rig", 1, Guid.NewGuid())
        };
        var invalidResult = FrameReconstructor.TryReconstruct(descriptor, new byte[8], out var invalidFrame);

        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity, nullResult.ReasonCode);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidIdentity, invalidResult.ReasonCode);
        Assert.IsNull(nullFrame);
        Assert.IsNull(invalidFrame);
    }

    internal static ArtifactManifestV2 CreateManifest(
        CameraPixelFormat format,
        int width,
        int height,
        int stride,
        byte[] payload)
    {
        using var options = JsonDocument.Parse("{\"stretch\":{\"white\":65535,\"black\":0},\"enabled\":true}");
        var hash = new string('A', 64);
        var sampleDepth = format is CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24 ? 8 : 16;
        var requestedStart = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var descriptor = new ReconstructionDescriptor(
            new CaptureIdentityDescriptor(
                "agent-a", "rig-a", 42,
                Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new CaptureTimingDescriptor(
                requestedStart,
                requestedStart.AddSeconds(1),
                requestedStart.AddSeconds(2),
                requestedStart.AddSeconds(3),
                requestedStart.AddSeconds(4)),
            new CaptureControlDescriptor(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 100, 99.5,
                10, 10, -10, -9.5),
            new CaptureProfileSet(
                new("rig-a", "1.2.3", hash),
                new("dark-library", "2.0.0", hash),
                new("sensor-mask", "1.0.0", hash),
                new("asi-sensor", "3.0.0", hash),
                new("capture-profile", "4.0.0", hash)),
            new FrameLayoutDescriptor(
                width,
                height,
                stride,
                format,
                sampleDepth == 16 ? FrameByteOrder.LittleEndian : FrameByteOrder.NotApplicable,
                sampleDepth,
                sampleDepth,
                FrameSamplePacking.ByteAligned,
                format == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
                0,
                sampleDepth == 16 ? 65535 : 255,
                payload.LongLength),
            new ArtifactDescriptor(
                Guid.Parse("00000000-0000-0000-0000-000000000002"),
                FrameArtifactRole.Raw,
                "VirtualSky",
                "native",
                requestedStart.AddSeconds(4),
                [],
                RecipeIdentityDescriptor.Create("capture-raw", "1.0.0", "build-1", options.RootElement),
                "application/octet-stream",
                PayloadChecksum.ComputeSha256(payload)));
        return new ArtifactManifestV2(
            ArtifactManifestV2.CurrentSchemaVersion,
            descriptor,
            "frames/raw.bin");
    }

    private static CaptureCycleEvidence CreateCycleEvidence(ReconstructionDescriptor descriptor)
    {
        var readoutCompletedUtc = descriptor.Timing.ReadoutCompletedUtc;
        return new CaptureCycleEvidence(
            CaptureCadenceMode.MinimumStartInterval,
            CaptureStartReason.DeadlineReached,
            AutomaticControlOwnership.HostMetered,
            AutomaticControlOwnership.HostMetered,
            CaptureSolarRegime.Twilight,
            descriptor.Timing.RequestedStartUtc.AddMilliseconds(250),
            TimeSpan.FromMilliseconds(750),
            new CaptureMeteringEvidence(
                readoutCompletedUtc.AddMilliseconds(100),
                readoutCompletedUtc.AddMilliseconds(200),
                120,
                100,
                20,
                240,
                0.42,
                CaptureMeteringOutcome.Measured),
            new CaptureControlDecisionEvidence(
                readoutCompletedUtc.AddMilliseconds(300),
                readoutCompletedUtc.AddMilliseconds(400),
                descriptor.Controls.EffectiveExposure,
                descriptor.Controls.EffectiveGain,
                TimeSpan.FromMilliseconds(800),
                90,
                CaptureControlDecisionReason.ExposureAndGainAdjusted),
            readoutCompletedUtc.AddMilliseconds(600));
    }

    private static CaptureMeteringEvidence CloneMetering(
        CaptureMeteringEvidence metering,
        CaptureMeteringOutcome outcome)
        => outcome switch
        {
            CaptureMeteringOutcome.Measured => metering with { Outcome = outcome },
            CaptureMeteringOutcome.NoFrame or CaptureMeteringOutcome.UnsupportedFormat or CaptureMeteringOutcome.NoEligibleSamples =>
                metering with
                {
                    ConsideredSampleCount = 0,
                    AcceptedSampleCount = 0,
                    SaturatedSampleCount = 0,
                    ScannedBytes = 0,
                    NormalizedLevel = null,
                    Outcome = outcome
                },
            CaptureMeteringOutcome.SaturationRejected => metering with
            {
                AcceptedSampleCount = 0,
                SaturatedSampleCount = metering.ConsideredSampleCount,
                NormalizedLevel = null,
                Outcome = outcome
            },
            _ => metering with { Outcome = outcome }
        };

    private static CaptureCycleEvidence CloneControlEvidence(
        CaptureCycleEvidence evidence,
        AutomaticControlOwnership exposureControl,
        AutomaticControlOwnership gainControl,
        CaptureControlDecisionReason reason,
        TimeSpan? decidedExposure = null,
        double? decidedGain = null,
        CaptureMeteringOutcome outcome = CaptureMeteringOutcome.Measured)
    {
        var hasHostMeteredControl = exposureControl == AutomaticControlOwnership.HostMetered ||
            gainControl == AutomaticControlOwnership.HostMetered;
        return evidence with
        {
            ExposureControl = exposureControl,
            GainControl = gainControl,
            SolarRegime = hasHostMeteredControl ? CaptureSolarRegime.Twilight : null,
            Metering = hasHostMeteredControl ? CloneMetering(evidence.Metering!, outcome) : null,
            Decision = evidence.Decision with
            {
                DecidedExposure = decidedExposure ?? evidence.Decision.ActiveExposure,
                DecidedGain = decidedGain ?? evidence.Decision.ActiveGain,
                Reason = reason
            }
        };
    }

    private static void AssertValidCycleEvidence(
        ReconstructionDescriptor descriptor,
        CaptureCycleEvidence evidence,
        string caseName)
    {
        var result = (descriptor with { CycleEvidence = evidence }).Validate();

        Assert.IsTrue(result.IsValid, $"{caseName}: {result.ReasonCode} at {result.FieldPath}");
    }

    private static void AssertInvalidCycleEvidence(
        ReconstructionDescriptor descriptor,
        CaptureCycleEvidence evidence,
        string expectedReasonCode,
        string caseName)
    {
        var result = (descriptor with { CycleEvidence = evidence }).Validate();

        Assert.IsFalse(result.IsValid, $"{caseName}: expected failure");
        Assert.AreEqual(expectedReasonCode, result.ReasonCode, caseName);
    }
}
