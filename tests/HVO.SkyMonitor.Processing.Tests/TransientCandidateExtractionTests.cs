using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientCandidateExtractionTests
{
    private const string ExtractionReceiptSha256 = "EFE7DEB7C9501F9F57B14EE102E6CC8B7A2D469C80CCA51C57D7728D0B13354C";
    private static readonly TransientCandidateExtractionOptionsV1 Options = new(
        MinimumResidualAdu: 20,
        MinimumComponentPixels: 2,
        MinimumIntegratedSignalAdu: 40,
        MaximumCandidates: 4,
        ProfileSampleCount: 4,
        MaximumSaturationBridgePixels: 16,
        MaximumForegroundPixels: 1_000,
        MaximumFragmentGapPixels: 0,
        MinimumFragmentAlignmentCosine: 0.95);

    [TestMethod]
    public void CenteredConvergedExtractionProducesCompleteCandidateAndStrictReceipt()
    {
        var request = CreateRequest(TransientTemporalBackgroundKind.CenteredFinal, [7, 8, 9, 10], true);

        var outcome = TransientCandidateExtractionFactory.Create(request);

        Assert.AreEqual(TransientCandidateExtractionStatus.Produced, outcome.Status, outcome.ReasonCode);
        Assert.HasCount(1, outcome.Candidates);
        var candidate = outcome.Candidates[0];
        Assert.AreEqual(TransientCandidateState.Complete, candidate.State);
        Assert.AreEqual(request.IdentitySlots[0].CandidateId, candidate.CandidateId);
        Assert.AreEqual(request.Target.Input.Descriptor.Source.EvidenceId, candidate.Geometry!.SourceEvidenceId);
        Assert.IsGreaterThan(3, candidate.Features!.LengthPixels);
        Assert.AreEqual(1, candidate.Features.FragmentCount);
        Assert.IsTrue(candidate.Reasons.Any(static reason => reason.Code == "transient.residual-component"));
        Assert.IsNotNull(outcome.Descriptor);
        Assert.AreEqual(request.Background.Descriptor.BackgroundIdentitySha256,
            outcome.Descriptor.Background.BackgroundIdentitySha256);
        Assert.AreEqual(request.Background.Descriptor.Sources.Count, outcome.Descriptor.OrderedSources.Count);

        var observation = TransientObservationFactory.Create(new TransientObservationPromotionRequest(
            candidate.CandidateId,
            Guid.Parse("85000000-0000-0000-0000-000000000001"),
            0,
            outcome.Descriptor));
        Assert.AreSame(candidate.Geometry, observation.Geometry);
        Assert.AreSame(candidate.Features, observation.Features);
        Assert.AreEqual(candidate.CandidateId, observation.Extraction.OriginatingCandidateId);
        Assert.IsTrue(observation.BackgroundArtifacts.Select(static artifact => artifact.ArtifactId).SequenceEqual(
            outcome.Descriptor.Background.Sources
                .Where(static source => source.Disposition == TransientTemporalSourceDisposition.Included)
                .Select(source => outcome.Descriptor.OrderedSources.Single(
                    evidence => evidence.Source.EvidenceId == source.EvidenceId).Source.Locator.Artifact.ArtifactId)));

        var json = TransientCandidateExtractionJson.Serialize(outcome.Descriptor);
        var receiptSha256 = Convert.ToHexString(SHA256.HashData(json));
        TestContext.WriteLine($"extraction-receipt-sha256={receiptSha256}");
        Assert.AreEqual(ExtractionReceiptSha256, receiptSha256);
        var parsed = TransientCandidateExtractionJson.Parse(json);
        CollectionAssert.AreEqual(json, TransientCandidateExtractionJson.Serialize(parsed));
        Assert.AreEqual(outcome.Descriptor.ExtractionIdentitySha256, parsed.ExtractionIdentitySha256);

        var invalidProfiles = outcome.Descriptor with
        {
            Candidates = [candidate with
            {
                Features = candidate.Features! with
                {
                    WidthProfile = candidate.Features.WidthProfile.Skip(1).ToArray()
                }
            }]
        };
        invalidProfiles = invalidProfiles with
        {
            ExtractionIdentitySha256 = TransientCandidateExtractionFactory.ComputeExtractionIdentitySha256(invalidProfiles)
        };
        Assert.ThrowsExactly<ArgumentException>(() => TransientCandidateExtractionJson.Serialize(invalidProfiles));

        var conflictingNoSupport = outcome.Descriptor with { NoSupportMaskChecksumSha256 = new string('A', 64) };
        conflictingNoSupport = conflictingNoSupport with
        {
            ExtractionIdentitySha256 = TransientCandidateExtractionFactory.ComputeExtractionIdentitySha256(conflictingNoSupport)
        };
        Assert.ThrowsExactly<ArgumentException>(() =>
            TransientCandidateExtractionJson.Serialize(conflictingNoSupport));

        var nullSource = outcome.Descriptor with
        {
            OrderedSources = [null!, .. outcome.Descriptor.OrderedSources.Skip(1)]
        };
        nullSource = nullSource with
        {
            ExtractionIdentitySha256 = TransientCandidateExtractionFactory.ComputeExtractionIdentitySha256(nullSource)
        };
        Assert.ThrowsExactly<ArgumentException>(() => TransientCandidateExtractionJson.Serialize(nullSource));

        var text = Encoding.UTF8.GetString(json);
        foreach (var invalid in new[]
        {
            text.Insert(1, "\"unknown\":true,"),
            text.Insert(1, "\"schemaVersion\":\"transient-candidate-extraction-v1\","),
            text.Replace("\"complete\"", "2", StringComparison.Ordinal)
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                TransientCandidateExtractionJson.Parse(Encoding.UTF8.GetBytes(invalid)));
        }
    }

    [TestMethod]
    public void CausalExtractionIsProvisionalAndNoEventReturnsAuditableNoCandidate()
    {
        var provisional = TransientCandidateExtractionFactory.Create(
            CreateRequest(TransientTemporalBackgroundKind.CausalProvisional, [7, 8, 9, 10], false));

        Assert.AreEqual(TransientCandidateExtractionStatus.Produced, provisional.Status);
        Assert.AreEqual(TransientCandidateState.Provisional, provisional.Candidates[0].State);
        Assert.IsTrue(provisional.Candidates[0].Reasons.Any(
            static reason => reason.Code == "transient.pending-centered-context"));

        var noEvent = TransientCandidateExtractionFactory.Create(
            CreateRequest(TransientTemporalBackgroundKind.CenteredFinal, [], true));
        Assert.AreEqual(TransientCandidateExtractionStatus.NoCandidate, noEvent.Status);
        Assert.AreEqual(TransientCandidateExtractionReasonCodes.NoCandidate, noEvent.ReasonCode);
        Assert.IsNotNull(noEvent.Descriptor);
        Assert.IsEmpty(noEvent.Descriptor.Candidates);
        Assert.IsNotEmpty(TransientCandidateExtractionJson.Serialize(noEvent.Descriptor));

        foreach (var kind in new[]
        {
            TransientDetectorMaskKind.Sky,
            TransientDetectorMaskKind.ImageCircle,
            TransientDetectorMaskKind.Horizon,
            TransientDetectorMaskKind.Obstruction,
            TransientDetectorMaskKind.BadPixel,
            TransientDetectorMaskKind.Star
        })
        {
            var masked = TransientCandidateExtractionFactory.Create(
                CreateRequest(TransientTemporalBackgroundKind.CenteredFinal, [7, 8, 9, 10], true, kind));
            Assert.AreEqual(TransientCandidateExtractionStatus.NoCandidate, masked.Status, kind.ToString());
            Assert.IsGreaterThanOrEqualTo(4, masked.HardMaskedPixelCount, kind.ToString());
            Assert.HasCount(7, masked.Descriptor!.Background.Sources.Single(
                static source => source.Position == TransientTemporalPosition.N).MaskIdentitySha256s);
        }

        var cloudLike = TransientCandidateExtractionFactory.Create(
            CreateRequest(TransientTemporalBackgroundKind.CenteredFinal, [7, 8, 9, 10], true, broad: true));
        Assert.AreEqual(TransientCandidateExtractionStatus.Produced, cloudLike.Status);
        var cloudObservation = TransientObservationFactory.CreateAssessmentObservation(new TransientObservationPromotionRequest(
            cloudLike.Candidates[0].CandidateId,
            Guid.Parse("85000000-0000-0000-0000-000000000002"),
            0,
            cloudLike.Descriptor!));
        var cloudAssessment = TransientAssessmentFactory.Create(new TransientAssessmentExecutionRequest(
            cloudLike.Candidates[0].EventId,
            Guid.Parse("86000000-0000-0000-0000-000000000001"),
            TransientTestData.Epoch.AddMinutes(11),
            TransientAssessmentAuthority.Provisional,
            [cloudObservation],
            new TransientDeterministicAssessmentOptionsV1(
                5, 3, 4, 3, 1.8, 0.5, 2.5, 1_000, 3, 3, 3, 100, 20, 2),
            []));
        Assert.AreEqual(
            TransientClassification.EnvironmentalArtifact,
            cloudAssessment.Descriptor!.Assessment.Classification);
    }

    [TestMethod]
    public void RejectsTamperedBackgroundLineageAndIdentitySlotsWithoutPartialCandidates()
    {
        var request = CreateRequest(TransientTemporalBackgroundKind.CenteredFinal, [7, 8, 9, 10], true);
        var tamperedBackground = request with
        {
            Background = request.Background with { Pixels = request.Background.Pixels.ToArray().AsMemory(2) }
        };
        AssertInvalid(TransientCandidateExtractionReasonCodes.InvalidBackground, tamperedBackground);
        AssertInvalid(TransientCandidateExtractionReasonCodes.InvalidBackground, request with
        {
            Background = request.Background with { NoSupportMask = Mask(8, 3, 0) }
        });

        var reversedLineage = request with { OrderedSources = request.OrderedSources.Reverse().ToArray() };
        AssertInvalid(TransientCandidateExtractionReasonCodes.InvalidLineage, reversedLineage);

        var targetMasks = request.Target.Masks.ToArray();
        targetMasks[0] = targetMasks[0] with { Mask = Mask(8, 3, 0) };
        AssertInvalid(TransientCandidateExtractionReasonCodes.InvalidMask, request with
        {
            Target = request.Target with { Masks = targetMasks }
        });

        var duplicateSlots = request with
        {
            IdentitySlots = request.IdentitySlots.Select(static slot => slot with
            {
                CandidateId = Guid.Parse("81000000-0000-0000-0000-000000000001")
            }).ToArray()
        };
        AssertInvalid(TransientCandidateExtractionReasonCodes.InvalidRequest, duplicateSlots);
    }

    [TestMethod]
    public void CandidateLimitIsAtomicAndCancellationThrowsBeforeWork()
    {
        var request = CreateRequest(TransientTemporalBackgroundKind.CenteredFinal, [7, 8, 0, 0, 0, 9, 10], true) with
        {
            Options = Options with { MaximumCandidates = 1 },
            IdentitySlots = [new TransientCandidateIdentitySlot(
                Guid.Parse("81000000-0000-0000-0000-000000000001"),
                Guid.Parse("82000000-0000-0000-0000-000000000001"))]
        };

        var limited = TransientCandidateExtractionFactory.Create(request);

        Assert.AreEqual(TransientCandidateExtractionStatus.LimitExceeded, limited.Status);
        Assert.IsEmpty(limited.Candidates);
        Assert.IsNull(limited.Descriptor);

        var foregroundLimited = TransientCandidateExtractionFactory.Create(
            CreateRequest(TransientTemporalBackgroundKind.CenteredFinal, [7, 8, 9, 10], true) with
            {
                Options = Options with { MaximumForegroundPixels = 1 }
            });
        Assert.AreEqual(TransientCandidateExtractionStatus.LimitExceeded, foregroundLimited.Status);
        Assert.AreEqual(TransientCandidateExtractionReasonCodes.ResourceLimit, foregroundLimited.ReasonCode);
        Assert.AreEqual("options.maximumForegroundPixels", foregroundLimited.Field);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            TransientCandidateExtractionFactory.Create(request, cancellation.Token));
    }

    [TestMethod]
    public void CanonicalOptionsChangeRecipeAndExtractionIdentities()
    {
        var first = TransientCandidateExtractionFactory.Create(
            CreateRequest(TransientTemporalBackgroundKind.CenteredFinal, [7, 8, 9, 10], true));
        var secondRequest = CreateRequest(TransientTemporalBackgroundKind.CenteredFinal, [7, 8, 9, 10], true);
        secondRequest = secondRequest with
        {
            Options = secondRequest.Options with { MinimumResidualAdu = 21 }
        };
        var second = TransientCandidateExtractionFactory.Create(secondRequest);

        Assert.AreEqual(TransientCandidateExtractionStatus.Produced, first.Status);
        Assert.AreEqual(TransientCandidateExtractionStatus.Produced, second.Status);
        Assert.AreNotEqual(first.Descriptor!.OptionsIdentitySha256, second.Descriptor!.OptionsIdentitySha256);
        Assert.AreNotEqual(first.Descriptor.ExtractionIdentitySha256, second.Descriptor.ExtractionIdentitySha256);
        Assert.AreNotEqual(
            first.Candidates[0].Extraction.RecipeIdentitySha256,
            second.Candidates[0].Extraction.RecipeIdentitySha256);
    }

    private static TransientCandidateExtractionRequest CreateRequest(
        TransientTemporalBackgroundKind kind,
        IReadOnlyList<ushort> targetSignal,
        bool converged,
        TransientDetectorMaskKind? maskedKind = null,
        bool broad = false)
    {
        var positions = kind == TransientTemporalBackgroundKind.CausalProvisional
            ? new[] { TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1, TransientTemporalPosition.N }
            : new[] { TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1,
                TransientTemporalPosition.N, TransientTemporalPosition.NPlus1, TransientTemporalPosition.NPlus2 };
        var window = positions.ToDictionary(
            static position => position,
            position => CreateSource(position, targetSignal, maskedKind, broad));
        var context = window.Values.Where(static source => source.Position != TransientTemporalPosition.N).ToArray();
        var background = TransientTemporalBackgroundFactory.Create(new TransientTemporalBackgroundRequest(
            kind,
            window[TransientTemporalPosition.N],
            context,
            [],
            TimeSpan.FromSeconds(30)));
        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, background.Status, background.ReasonCode);
        var byEvidence = window.Values.ToDictionary(static source => source.Input.Descriptor.Source.EvidenceId);
        var orderedSources = background.Product!.Descriptor.Sources
            .Select(source => byEvidence[source.EvidenceId])
            .ToArray();
        var identities = Enumerable.Range(1, Options.MaximumCandidates).Select(index =>
            new TransientCandidateIdentitySlot(
                Guid.Parse($"81000000-0000-0000-0000-{index:D12}"),
                Guid.Parse($"82000000-0000-0000-0000-{index:D12}"))).ToArray();
        return new TransientCandidateExtractionRequest(
            "agent-121",
            TransientTestData.Epoch.AddMinutes(10),
            window[TransientTemporalPosition.N],
            background.Product,
            orderedSources,
            identities,
            Options,
            converged);
    }

    private static TransientTemporalSource CreateSource(
        TransientTemporalPosition position,
        IReadOnlyList<ushort> targetSignal,
        TransientDetectorMaskKind? maskedKind,
        bool broad)
    {
        const int width = 8;
        const int height = 3;
        var sequence = 100 + (int)position;
        var values = new ushort[width * height];
        if (position == TransientTemporalPosition.N)
        {
            var firstRow = broad ? 0 : 1;
            var lastRow = broad ? height - 1 : 1;
            for (var row = firstRow; row <= lastRow; row++)
            {
                for (var index = 0; index < targetSignal.Count; index++)
                {
                    values[row * width + index] = (ushort)(targetSignal[index] * 10);
                }
            }
        }
        var payload = U16(values);
        var template = TransientTestData.CreateDetectorSource(CameraPixelFormat.Mono16);
        var started = TransientTestData.Epoch.AddSeconds((int)position * 20 + 40);
        var ended = started.AddSeconds(10);
        var artifactId = Guid.Parse($"83000000-0000-0000-0000-{sequence:D12}");
        var artifact = template.Artifact with
        {
            ArtifactId = artifactId,
            Payload = payload,
            CaptureSequence = sequence,
            CreatedUtc = ended,
            Integration = TimeSpan.FromSeconds(10),
            Layout = template.Artifact.Layout! with
            {
                Width = width,
                Height = height,
                StrideBytes = width * 2,
                ByteLength = payload.Length
            },
            ObservationStartedUtc = started,
            ObservationEndedUtc = ended
        };
        var source = template.Source with
        {
            EvidenceId = Guid.Parse($"84000000-0000-0000-0000-{sequence:D12}"),
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
        var input = TransientDetectorInputFactory.Create(artifact, source, template.Levels);
        Assert.IsTrue(input.Validation.IsValid, input.Validation.ReasonCode);
        var masks = new[]
        {
            TransientDetectorMaskKind.Sky,
            TransientDetectorMaskKind.ImageCircle,
            TransientDetectorMaskKind.Horizon,
            TransientDetectorMaskKind.Obstruction,
            TransientDetectorMaskKind.BadPixel,
            TransientDetectorMaskKind.Star
        }.Select(maskKind => TransientDetectorMask.Create(
            maskKind,
            new ProcessingAlgorithmIdentity($"test-{maskKind.ToString().ToUpperInvariant()}-mask", "v1"),
            maskKind == maskedKind
                ? Mask(width, height, Enumerable.Range(width, targetSignal.Count).ToArray())
                : Linear16MaskOperations.Empty(width, height))).ToArray();
        return new TransientTemporalSource(
            position,
            sequence,
            input.Input!,
            new TransientSensitivityV1("response-v1", 1, 1),
            masks);
    }

    private static byte[] U16(IEnumerable<ushort> values)
    {
        var samples = values.ToArray();
        var output = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            output[index * 2] = (byte)samples[index];
            output[index * 2 + 1] = (byte)(samples[index] >> 8);
        }
        return output;
    }

    private static Linear16PixelMask Mask(int width, int height, params int[] pixels)
    {
        var bits = new byte[Linear16MaskOperations.RequiredByteLength(width, height)];
        foreach (var pixel in pixels)
        {
            bits[pixel >> 3] |= (byte)(1 << (pixel & 7));
        }
        return new Linear16PixelMask(width, height, bits);
    }

    private static void AssertInvalid(string reason, TransientCandidateExtractionRequest request)
    {
        var outcome = TransientCandidateExtractionFactory.Create(request);
        Assert.AreEqual(TransientCandidateExtractionStatus.Invalid, outcome.Status);
        Assert.AreEqual(reason, outcome.ReasonCode);
        Assert.IsEmpty(outcome.Candidates);
    }

    public TestContext TestContext { get; set; } = null!;
}
