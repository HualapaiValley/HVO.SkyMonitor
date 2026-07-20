using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientTemporalBackgroundTests
{
    [TestMethod]
    public void OffDoesNotConstructTemporalWindow()
    {
        var invoked = false;
        var window = TransientTemporalWindowActivation.CreateWhenEnabled(
            TransientDetectorExecutionMode.Off,
            () =>
            {
                invoked = true;
                return new object();
            });

        Assert.IsNull(window);
        Assert.IsFalse(invoked);
    }

    [TestMethod]
    public void CausalAndCenteredUseExactlyOrderedSourcesAndNeverTargetPixels()
    {
        var window = CreateWindow();
        var causal = TransientTemporalBackgroundFactory.Create(Request(
            TransientTemporalBackgroundKind.CausalProvisional,
            window,
            [TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1]));
        var centered = TransientTemporalBackgroundFactory.Create(Request(
            TransientTemporalBackgroundKind.CenteredFinal,
            window,
            [TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1,
             TransientTemporalPosition.NPlus1, TransientTemporalPosition.NPlus2]));

        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, causal.Status);
        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, centered.Status);
        CollectionAssert.AreEqual(U16(10, 20), causal.Product!.Pixels.ToArray());
        CollectionAssert.AreEqual(U16(20, 30), centered.Product!.Pixels.ToArray());
        CollectionAssert.AreEqual(
            new[] { -2, -1, 0 },
            causal.Sources.Select(static source => (int)source.Position).ToArray());
        CollectionAssert.AreEqual(
            new[] { -2, -1, 0, 1, 2 },
            centered.Sources.Select(static source => (int)source.Position).ToArray());
        Assert.AreEqual(TransientTemporalSourceDisposition.Target, centered.Sources[2].Disposition);
        Assert.AreEqual(4, centered.Product.Descriptor.Sources.Count(static source => source.Disposition == TransientTemporalSourceDisposition.Included));
    }

    [TestMethod]
    public void NormalizesExplicitCompatibleSetpointChangesAndRecordsExactFactor()
    {
        var window = CreateWindow();
        window[TransientTemporalPosition.NMinus1] = CreateSource(
            TransientTemporalPosition.NMinus1,
            99,
            25,
            20,
            40,
            new TransientSensitivityV1("response-v1", 2, 1),
            setpoint: "night-gain-2");

        var outcome = TransientTemporalBackgroundFactory.Create(Request(
            TransientTemporalBackgroundKind.CausalProvisional,
            window,
            [TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1]));

        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, outcome.Status);
        CollectionAssert.AreEqual(U16(10, 20), outcome.Product!.Pixels.ToArray());
        var normalized = outcome.Sources.Single(static source => source.Position == TransientTemporalPosition.NMinus1);
        Assert.AreEqual((uint)1, normalized.NormalizationNumerator);
        Assert.AreEqual((uint)2, normalized.NormalizationDenominator);
    }

    [TestMethod]
    public void ExcludesKnownEventSourceAndPreservesReasonedLineage()
    {
        var window = CreateWindow();
        var excluded = window[TransientTemporalPosition.NPlus2].Input.Descriptor.Source.EvidenceId;
        var request = Request(
            TransientTemporalBackgroundKind.CenteredFinal,
            window,
            [TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1,
             TransientTemporalPosition.NPlus1, TransientTemporalPosition.NPlus2]) with
        {
            KnownEventEvidenceIds = [excluded]
        };

        var outcome = TransientTemporalBackgroundFactory.Create(request);

        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, outcome.Status);
        CollectionAssert.AreEqual(U16(17, 27), outcome.Product!.Pixels.ToArray());
        Assert.AreEqual(
            TransientTemporalSourceDisposition.ExcludedKnownEvent,
            outcome.Sources.Single(source => source.EvidenceId == excluded).Disposition);
        Assert.AreEqual(3, outcome.Product.Descriptor.Sources.Count(static source => source.Disposition == TransientTemporalSourceDisposition.Included));
    }

    [TestMethod]
    public void IntrinsicSaturationMaskExcludesSamplesBeforeNormalizationAndMean()
    {
        var window = CreateWindow();
        window[TransientTemporalPosition.NMinus2] = CreateSource(
            TransientTemporalPosition.NMinus2,
            98,
            0,
            4000,
            10);

        var outcome = TransientTemporalBackgroundFactory.Create(Request(
            TransientTemporalBackgroundKind.CausalProvisional,
            window,
            [TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1]));

        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, outcome.Status);
        CollectionAssert.AreEqual(U16(10, 15), outcome.Product!.Pixels.ToArray());
        Assert.AreEqual(7, outcome.Sources.Single(static source => source.Position == TransientTemporalPosition.NMinus2).MaskIdentitySha256s.Count);
    }

    [TestMethod]
    public void ReturnsDistinctMissingTimeoutSequenceTemporalAndCompatibilityReasons()
    {
        var window = CreateWindow();
        var centeredPositions = new[]
        {
            TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1,
            TransientTemporalPosition.NPlus1, TransientTemporalPosition.NPlus2
        };
        var missing = Request(TransientTemporalBackgroundKind.CenteredFinal, window, centeredPositions[..^1]);
        AssertReason(TransientTemporalBackgroundReasonCodes.MissingSource, missing);
        AssertReason(TransientTemporalBackgroundReasonCodes.Timeout, missing with { DeadlineExpired = true });

        var sequence = Clone(window);
        sequence[TransientTemporalPosition.NPlus2] = CreateSource(
            TransientTemporalPosition.NPlus2,
            103,
            100,
            30,
            40);
        AssertReason(TransientTemporalBackgroundReasonCodes.SequenceGap,
            Request(TransientTemporalBackgroundKind.CenteredFinal, sequence, centeredPositions));

        var temporal = Clone(window);
        temporal[TransientTemporalPosition.NPlus2] = CreateSource(TransientTemporalPosition.NPlus2, 102, 300, 50, 60);
        AssertReason(TransientTemporalBackgroundReasonCodes.TemporalGap,
            Request(TransientTemporalBackgroundKind.CenteredFinal, temporal, centeredPositions));

        var profile = Clone(window);
        profile[TransientTemporalPosition.NPlus2] = CreateSource(
            TransientTemporalPosition.NPlus2,
            102,
            100,
            30,
            40,
            calibration: "other-calibration");
        AssertReason(TransientTemporalBackgroundReasonCodes.IncompatibleProfile,
            Request(TransientTemporalBackgroundKind.CenteredFinal, profile, centeredPositions));

        var orientation = Clone(window);
        orientation[TransientTemporalPosition.NPlus2] = CreateSource(
            TransientTemporalPosition.NPlus2,
            102,
            100,
            30,
            40,
            orientation: "south-up");
        AssertReason(TransientTemporalBackgroundReasonCodes.IncompatibleProfile,
            Request(TransientTemporalBackgroundKind.CenteredFinal, orientation, centeredPositions));

        var layout = Clone(window);
        layout[TransientTemporalPosition.NPlus2] = CreateSource(
            TransientTemporalPosition.NPlus2,
            102,
            100,
            30,
            40,
            onePixel: true);
        AssertReason(TransientTemporalBackgroundReasonCodes.IncompatibleLayout,
            Request(TransientTemporalBackgroundKind.CenteredFinal, layout, centeredPositions));

        var response = Clone(window);
        response[TransientTemporalPosition.NPlus2] = response[TransientTemporalPosition.NPlus2] with
        {
            Sensitivity = new TransientSensitivityV1("other-response", 1, 1)
        };
        AssertReason(TransientTemporalBackgroundReasonCodes.IncompatibleResponse,
            Request(TransientTemporalBackgroundKind.CenteredFinal, response, centeredPositions));
    }

    [TestMethod]
    public void AllKnownEventContextReturnsNoUsableContext()
    {
        var window = CreateWindow();
        var positions = new[] { TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1 };
        var request = Request(TransientTemporalBackgroundKind.CausalProvisional, window, positions) with
        {
            KnownEventEvidenceIds = positions.Select(position => window[position].Input.Descriptor.Source.EvidenceId).ToArray()
        };

        AssertReason(TransientTemporalBackgroundReasonCodes.NoUsableContext, request);
    }

    [TestMethod]
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Verifies equivalent lowercase external hashes normalize to canonical uppercase lineage.")]
    public void EdgeAndCentralCopiesProduceByteIdenticalBackgroundMaskAndLineage()
    {
        var window = CreateWindow();
        var positions = new[]
        {
            TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1,
            TransientTemporalPosition.NPlus1, TransientTemporalPosition.NPlus2
        };
        var localRequest = Request(TransientTemporalBackgroundKind.CenteredFinal, window, positions);
        var reconstructed = window.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value with
            {
                Input = pair.Value.Input with
                {
                    Descriptor = pair.Value.Input.Descriptor with
                    {
                        InputIdentitySha256 = pair.Value.Input.Descriptor.InputIdentitySha256.ToLowerInvariant(),
                        SaturationMaskChecksumSha256 = pair.Value.Input.Descriptor.SaturationMaskChecksumSha256!.ToLowerInvariant()
                    },
                    Pixels = pair.Value.Input.Pixels.ToArray()
                },
                Masks = pair.Value.Masks.Select(mask => mask with
                {
                    MaskIdentitySha256 = mask.MaskIdentitySha256.ToLowerInvariant(),
                    Mask = mask.Mask with { Bits = mask.Mask.Bits.ToArray() }
                }).ToArray()
            });
        var centralRequest = Request(TransientTemporalBackgroundKind.CenteredFinal, reconstructed, positions);

        var local = TransientTemporalBackgroundFactory.Create(localRequest);
        var central = TransientTemporalBackgroundFactory.Create(centralRequest);

        CollectionAssert.AreEqual(local.Product!.Pixels.ToArray(), central.Product!.Pixels.ToArray());
        CollectionAssert.AreEqual(local.Product.EffectiveMask.Bits.ToArray(), central.Product.EffectiveMask.Bits.ToArray());
        CollectionAssert.AreEqual(
            JsonSerializer.SerializeToUtf8Bytes(local.Product.Descriptor),
            JsonSerializer.SerializeToUtf8Bytes(central.Product.Descriptor));
        Assert.AreEqual(local.Product.Descriptor.BackgroundIdentitySha256, central.Product.Descriptor.BackgroundIdentitySha256);
    }

    [TestMethod]
    public void ProducedAndFailedOutcomeLineageRoundTripsStrictBoundedJson()
    {
        var window = CreateWindow();
        var produced = TransientTemporalBackgroundFactory.Create(Request(
            TransientTemporalBackgroundKind.CausalProvisional,
            window,
            [TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1]));
        var missing = TransientTemporalBackgroundFactory.Create(Request(
            TransientTemporalBackgroundKind.CenteredFinal,
            window,
            [TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1]));

        foreach (var outcome in new[] { produced, missing })
        {
            var descriptor = TransientTemporalBackgroundJson.CreateDescriptor(outcome);
            var json = TransientTemporalBackgroundJson.Serialize(descriptor);
            var parsed = TransientTemporalBackgroundJson.Parse(json);
            CollectionAssert.AreEqual(json, TransientTemporalBackgroundJson.Serialize(parsed));
        }
        var preLineageFailure = new TransientTemporalBackgroundOutcome(
            TransientTemporalBackgroundStatus.Incompatible,
            TransientTemporalBackgroundReasonCodes.InvalidRequest,
            "request",
            [],
            null);
        var failureJson = TransientTemporalBackgroundJson.Serialize(
            TransientTemporalBackgroundJson.CreateDescriptor(preLineageFailure));
        Assert.AreEqual(0, TransientTemporalBackgroundJson.Parse(failureJson).Sources.Count);

        var producedDescriptor = TransientTemporalBackgroundJson.CreateDescriptor(produced);
        Assert.ThrowsExactly<ArgumentException>(() => TransientTemporalBackgroundJson.Serialize(
            producedDescriptor with { Product = producedDescriptor.Product! with { BackgroundChecksumSha256 = new string('0', 64) } }));
        Assert.ThrowsExactly<ArgumentException>(() => TransientTemporalBackgroundJson.Serialize(
            producedDescriptor with { Sources = producedDescriptor.Sources.Take(1).ToArray() }));
        Assert.ThrowsExactly<ArgumentException>(() => TransientTemporalBackgroundJson.Serialize(
            producedDescriptor with { Sources = [null!] }));
        Assert.ThrowsExactly<ArgumentException>(() => TransientTemporalBackgroundJson.Serialize(
            producedDescriptor with
            {
                Product = producedDescriptor.Product! with { Sources = [null!] }
            }));
        Assert.ThrowsExactly<ArgumentException>(() => TransientTemporalBackgroundJson.Serialize(
            producedDescriptor with
            {
                Product = producedDescriptor.Product! with
                {
                    Sources = producedDescriptor.Product.Sources.Select(source =>
                        source.Position == TransientTemporalPosition.NMinus1
                            ? source with { CaptureSequence = source.CaptureSequence + 10 }
                            : source).ToArray()
                }
            }));
        var strictJson = Encoding.UTF8.GetString(TransientTemporalBackgroundJson.Serialize(producedDescriptor));
        foreach (var invalid in new[]
        {
            strictJson.Replace("\"status\":\"produced\"", "\"status\":0", StringComparison.Ordinal),
            strictJson.Replace("\"status\"", "\"Status\"", StringComparison.Ordinal),
            strictJson.Insert(1, "\"status\":\"produced\",")
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                TransientTemporalBackgroundJson.Parse(Encoding.UTF8.GetBytes(invalid)));
        }
    }

    [TestMethod]
    public void PersistentStarMaskExcludesMovingSupportFromBackgroundAndEffectiveMask()
    {
        var window = CreateWindow();
        var starSupport = Linear16MaskOperations.CreateCircularSupportMask(
            2,
            1,
            [new Linear16CircularMaskRegion(0.5, 0.5, 0.6), new Linear16CircularMaskRegion(1.5, 0.5, 0.6)]);
        var starMask = TransientDetectorMask.Create(
            TransientDetectorMaskKind.Star,
            new ProcessingAlgorithmIdentity("catalog-projected-star-mask", "v1"),
            starSupport);
        foreach (var position in window.Keys.ToArray())
        {
            window[position] = window[position] with
            {
                Masks = window[position].Masks
                    .Where(static mask => mask.Kind != TransientDetectorMaskKind.Star)
                    .Append(starMask)
                    .ToArray()
            };
        }

        var outcome = TransientTemporalBackgroundFactory.Create(Request(
            TransientTemporalBackgroundKind.CenteredFinal,
            window,
            [TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1,
             TransientTemporalPosition.NPlus1, TransientTemporalPosition.NPlus2]));

        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, outcome.Status);
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(outcome.Product!.EffectiveMask, 0, 0));
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(outcome.Product.EffectiveMask, 1, 0));
        CollectionAssert.AreEqual(U16(0, 0), outcome.Product.Pixels.ToArray());
    }

    [TestMethod]
    public void RejectsTamperedMaskAndHonorsCancellationBeforeWork()
    {
        var window = CreateWindow();
        var source = window[TransientTemporalPosition.NMinus1];
        var mask = source.Masks.Single(static item => item.Kind == TransientDetectorMaskKind.Star);
        window[TransientTemporalPosition.NMinus1] = source with
        {
            Masks = [mask with { MaskIdentitySha256 = new string('0', 64) }]
        };
        var request = Request(
            TransientTemporalBackgroundKind.CausalProvisional,
            window,
            [TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1]);
        AssertReason(TransientTemporalBackgroundReasonCodes.IncompatibleMask, request);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            TransientTemporalBackgroundFactory.Create(request, cancellation.Token));
    }

    [TestMethod]
    public void RejectsIncompleteOrNonPersistentMasksAndBoundsMalformedContext()
    {
        var positions = new[] { TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1 };
        var incomplete = CreateWindow();
        incomplete[TransientTemporalPosition.NMinus1] = incomplete[TransientTemporalPosition.NMinus1] with
        {
            Masks = incomplete[TransientTemporalPosition.NMinus1].Masks.Take(
                incomplete[TransientTemporalPosition.NMinus1].Masks.Count - 1).ToArray()
        };
        AssertReason(
            TransientTemporalBackgroundReasonCodes.IncompatibleMask,
            Request(TransientTemporalBackgroundKind.CausalProvisional, incomplete, positions));

        var changed = CreateWindow();
        var source = changed[TransientTemporalPosition.NMinus1];
        var changedStar = TransientDetectorMask.Create(
            TransientDetectorMaskKind.Star,
            new ProcessingAlgorithmIdentity("test-star-mask", "v1"),
            new Linear16PixelMask(2, 1, new byte[] { 1 }));
        changed[TransientTemporalPosition.NMinus1] = source with
        {
            Masks = source.Masks.Where(static mask => mask.Kind != TransientDetectorMaskKind.Star).Append(changedStar).ToArray()
        };
        AssertReason(
            TransientTemporalBackgroundReasonCodes.IncompatibleMask,
            Request(TransientTemporalBackgroundKind.CausalProvisional, changed, positions));

        var malformed = CreateWindow();
        malformed[TransientTemporalPosition.NMinus1] = malformed[TransientTemporalPosition.NMinus1] with { Input = null! };
        AssertReason(
            TransientTemporalBackgroundReasonCodes.InvalidRequest,
            Request(TransientTemporalBackgroundKind.CausalProvisional, malformed, positions));

        var duplicateEvidence = CreateWindow();
        duplicateEvidence[TransientTemporalPosition.NMinus1] = CreateSource(
            TransientTemporalPosition.NMinus1,
            99,
            25,
            10,
            20,
            evidenceId: duplicateEvidence[TransientTemporalPosition.NMinus2].Input.Descriptor.Source.EvidenceId);
        AssertReason(
            TransientTemporalBackgroundReasonCodes.InvalidRequest,
            Request(TransientTemporalBackgroundKind.CausalProvisional, duplicateEvidence, positions));

        var tamperedSaturation = CreateWindow();
        var saturationSource = tamperedSaturation[TransientTemporalPosition.NMinus1];
        tamperedSaturation[TransientTemporalPosition.NMinus1] = saturationSource with
        {
            Input = saturationSource.Input with
            {
                SaturationMask = new Linear16PixelMask(2, 1, new byte[] { 1 })
            }
        };
        AssertReason(
            TransientTemporalBackgroundReasonCodes.IncompatibleMask,
            Request(TransientTemporalBackgroundKind.CausalProvisional, tamperedSaturation, positions));

        var malformedSaturation = CreateWindow();
        saturationSource = malformedSaturation[TransientTemporalPosition.NMinus1];
        malformedSaturation[TransientTemporalPosition.NMinus1] = saturationSource with
        {
            Input = saturationSource.Input with
            {
                SaturationMask = new Linear16PixelMask(2, 1, ReadOnlyMemory<byte>.Empty)
            }
        };
        AssertReason(
            TransientTemporalBackgroundReasonCodes.IncompatibleMask,
            Request(TransientTemporalBackgroundKind.CausalProvisional, malformedSaturation, positions));

        var legacyDescriptor = saturationSource.Input.Descriptor with
        {
            InputIdentitySha256 = string.Empty,
            SaturationMaskChecksumSha256 = null
        };
        legacyDescriptor = legacyDescriptor with
        {
            InputIdentitySha256 = TransientContractJson.ComputeDetectorInputIdentitySha256(legacyDescriptor)
        };
        var legacyInput = CreateWindow();
        saturationSource = legacyInput[TransientTemporalPosition.NMinus1];
        legacyInput[TransientTemporalPosition.NMinus1] = saturationSource with
        {
            Input = saturationSource.Input with { Descriptor = legacyDescriptor }
        };
        AssertReason(
            TransientTemporalBackgroundReasonCodes.IncompatibleMask,
            Request(TransientTemporalBackgroundKind.CausalProvisional, legacyInput, positions));
    }

    private static Dictionary<TransientTemporalPosition, TransientTemporalSource> CreateWindow()
        => new()
        {
            [TransientTemporalPosition.NMinus2] = CreateSource(TransientTemporalPosition.NMinus2, 98, 0, 10, 20),
            [TransientTemporalPosition.NMinus1] = CreateSource(TransientTemporalPosition.NMinus1, 99, 25, 10, 20),
            [TransientTemporalPosition.N] = CreateSource(TransientTemporalPosition.N, 100, 50, 1000, 2000),
            [TransientTemporalPosition.NPlus1] = CreateSource(TransientTemporalPosition.NPlus1, 101, 75, 30, 40),
            [TransientTemporalPosition.NPlus2] = CreateSource(TransientTemporalPosition.NPlus2, 102, 100, 30, 40)
        };

    private static Dictionary<TransientTemporalPosition, TransientTemporalSource> Clone(
        Dictionary<TransientTemporalPosition, TransientTemporalSource> source)
        => source.ToDictionary(static pair => pair.Key, static pair => pair.Value);

    private static TransientTemporalBackgroundRequest Request(
        TransientTemporalBackgroundKind kind,
        Dictionary<TransientTemporalPosition, TransientTemporalSource> window,
        IReadOnlyList<TransientTemporalPosition> positions)
        => new(
            kind,
            window[TransientTemporalPosition.N],
            positions.Select(position => window[position]).ToArray(),
            [],
            TimeSpan.FromSeconds(30));

    private static TransientTemporalSource CreateSource(
        TransientTemporalPosition position,
        long sequence,
        int startSeconds,
        ushort first,
        ushort second,
        TransientSensitivityV1? sensitivity = null,
        string setpoint = "night-v1",
        string calibration = "calibration-v1",
        string orientation = "north-up-v1",
        bool onePixel = false,
        Guid? evidenceId = null)
    {
        var template = TransientTestData.CreateDetectorSource(CameraPixelFormat.Mono16);
        var payload = onePixel ? U16(first) : U16(first, second);
        var artifactId = Guid.Parse($"b0000000-0000-0000-0000-{sequence:D12}");
        var resolvedEvidenceId = evidenceId ?? Guid.Parse($"c0000000-0000-0000-0000-{sequence:D12}");
        var started = TransientTestData.Epoch.AddSeconds(startSeconds);
        var ended = started.AddSeconds(20);
        var artifact = template.Artifact with
        {
            ArtifactId = artifactId,
            Payload = payload,
            CreatedUtc = ended,
            Integration = TimeSpan.FromSeconds(20),
            CaptureSequence = sequence,
            Layout = template.Artifact.Layout! with
            {
                Width = onePixel ? 1 : 2,
                Height = 1,
                StrideBytes = payload.Length,
                ByteLength = payload.Length
            },
            Compatibility = template.Artifact.Compatibility with
            {
                SetpointRegime = setpoint,
                Calibration = calibration,
                Orientation = orientation
            },
            ObservationStartedUtc = started,
            ObservationEndedUtc = ended
        };
        var source = template.Source with
        {
            EvidenceId = resolvedEvidenceId,
            Locator = template.Source.Locator with
            {
                Artifact = template.Source.Locator.Artifact with
                {
                    ArtifactId = artifactId,
                    ChecksumSha256 = Convert.ToHexString(SHA256.HashData(payload))
                }
            },
            ObservationStartedUtc = started,
            ObservationEndedUtc = ended
        };
        var creation = TransientDetectorInputFactory.Create(artifact, source, template.Levels);
        Assert.IsTrue(creation.Validation.IsValid, creation.Validation.ReasonCode);
        var masks = new[]
        {
            TransientDetectorMaskKind.Sky,
            TransientDetectorMaskKind.ImageCircle,
            TransientDetectorMaskKind.Horizon,
            TransientDetectorMaskKind.Obstruction,
            TransientDetectorMaskKind.BadPixel,
            TransientDetectorMaskKind.Star
        }.Select(kind => TransientDetectorMask.Create(
            kind,
            new ProcessingAlgorithmIdentity($"test-{kind.ToString().ToUpperInvariant()}-mask", "v1"),
            Linear16MaskOperations.Empty(onePixel ? 1 : 2, 1))).ToArray();
        return new TransientTemporalSource(
            position,
            sequence,
            creation.Input!,
            sensitivity ?? new TransientSensitivityV1("response-v1", 1, 1),
            masks);
    }

    private static void AssertReason(string expected, TransientTemporalBackgroundRequest request)
    {
        var outcome = TransientTemporalBackgroundFactory.Create(request);
        Assert.AreEqual(expected, outcome.ReasonCode);
        Assert.IsNull(outcome.Product);
    }

    private static byte[] U16(params ushort[] values)
    {
        var output = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(index * 2), values[index]);
        }
        return output;
    }
}
