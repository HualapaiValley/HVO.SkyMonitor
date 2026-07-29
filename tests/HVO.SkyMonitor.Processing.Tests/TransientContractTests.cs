using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientContractTests
{
    private const string EventSha256 = "33EAB7513A13B19E7584AA8B20666D7757AF85BC8235F172DCDD33DF908924D2";
    private const string CandidateSha256 = "3D7FFA7395EEFF64093AC8B97B9D90666E2A13E0FC3EA96A32C71C89EEBD75DA";
    private const string MonoDescriptorSha256 = "DAA86B5D5B0F74A3DC01FCDAAA4B5B2B23C2B2EE2D20854620F0C4C1CB754FDA";
    private const string RggbDescriptorSha256 = "EF5B610A77E14E0156B0BC4D0A1B1BBD0FF1C49FCDE4B0B8167ADDF5F5601509";
    private const string LegacyMonoInputIdentitySha256 = "D10574443D2F8109827AFAE454A8965D35145D755D14267F21AC9B065CFCE213";

    [TestMethod]
    public async Task GoldenContractsRoundTripExactCanonicalBytesAndPinnedHashes()
    {
        await AssertGoldenAsync(
            "transient-event-v1.json",
            TransientContractJson.Serialize(TransientTestData.CreateEvent()),
            EventSha256,
            bytes => TransientContractJson.ParseEvent(bytes).Value,
            TransientContractJson.Serialize).ConfigureAwait(false);
        await AssertGoldenAsync(
            "transient-candidate-v1.json",
            TransientContractJson.Serialize(TransientTestData.CreateCandidate()),
            CandidateSha256,
            bytes => TransientContractJson.ParseCandidate(bytes).Value,
            TransientContractJson.Serialize).ConfigureAwait(false);
        await AssertGoldenAsync(
            "transient-detector-input-mono16-v1.json",
            TransientContractJson.Serialize(TransientTestData.CreateDetectorDescriptor(CameraPixelFormat.Mono16)),
            MonoDescriptorSha256,
            bytes => TransientContractJson.ParseDetectorInputDescriptor(bytes).Value,
            TransientContractJson.Serialize).ConfigureAwait(false);
        await AssertGoldenAsync(
            "transient-detector-input-rggb16-v1.json",
            TransientContractJson.Serialize(TransientTestData.CreateDetectorDescriptor(CameraPixelFormat.BayerRggb16)),
            RggbDescriptorSha256,
            bytes => TransientContractJson.ParseDetectorInputDescriptor(bytes).Value,
            TransientContractJson.Serialize).ConfigureAwait(false);
    }

    [TestMethod]
    public void StrictParsingRejectsUnknownDuplicateCaseCollidingNumericAndMissingMembers()
    {
        var json = Encoding.UTF8.GetString(TransientContractJson.Serialize(TransientTestData.CreateCandidate()));
        var invalid = new[]
        {
            json.Insert(1, "\"unknown\":true,"),
            json.Insert(1, "\"schemaVersion\":\"transient-candidate-v1\","),
            json.Insert(1, "\"SchemaVersion\":\"transient-candidate-v1\","),
            json.Replace("\"Provisional\"", "1", StringComparison.Ordinal),
            json.Replace("\"schemaVersion\":\"transient-candidate-v1\",", string.Empty, StringComparison.Ordinal)
        };

        foreach (var value in invalid)
        {
            var result = TransientContractJson.ParseCandidate(Encoding.UTF8.GetBytes(value));
            Assert.AreEqual(TransientContractReasonCodes.InvalidJson, result.Validation.ReasonCode);
        }
        Assert.AreEqual(
            TransientContractReasonCodes.PayloadTooLarge,
            TransientContractJson.ParseCandidate(
                new byte[TransientContractJson.MaximumCandidateBytes + 1]).Validation.ReasonCode);
    }

    [TestMethod]
    public void UnknownTopLevelLocatorAndProducerSchemasHaveSpecificReasons()
    {
        var candidate = Encoding.UTF8.GetString(TransientContractJson.Serialize(TransientTestData.CreateCandidate()));
        Assert.AreEqual(
            TransientContractReasonCodes.UnsupportedSchema,
            TransientContractJson.ParseCandidate(Encoding.UTF8.GetBytes(candidate.Replace(
                "transient-candidate-v1", "transient-candidate-v2", StringComparison.Ordinal))).Validation.ReasonCode);
        Assert.AreEqual(
            TransientContractReasonCodes.UnsupportedLocator,
            TransientContractJson.ParseCandidate(Encoding.UTF8.GetBytes(candidate.Replace(
                "transient-source-locator-v1", "transient-source-locator-v2", StringComparison.Ordinal))).Validation.ReasonCode);
        var futureLocator = candidate.Replace(
            "\"schemaVersion\":\"transient-source-locator-v1\"",
            "\"frameRange\":{},\"schemaVersion\":\"transient-source-locator-v2\"",
            StringComparison.Ordinal);
        Assert.AreEqual(
            TransientContractReasonCodes.UnsupportedLocator,
            TransientContractJson.ParseCandidate(Encoding.UTF8.GetBytes(futureLocator)).Validation.ReasonCode);
        Assert.AreEqual(
            TransientContractReasonCodes.UnsupportedProducer,
            TransientContractJson.ParseCandidate(Encoding.UTF8.GetBytes(candidate.Replace(
                "transient-extraction-producer-v1", "transient-extraction-producer-v2", StringComparison.Ordinal))).Validation.ReasonCode);
        var futureProducer = candidate.Replace(
            "\"schemaVersion\":\"transient-extraction-producer-v1\"",
            "\"modelChecksum\":\"future\",\"schemaVersion\":\"transient-extraction-producer-v2\"",
            StringComparison.Ordinal);
        Assert.AreEqual(
            TransientContractReasonCodes.UnsupportedProducer,
            TransientContractJson.ParseCandidate(Encoding.UTF8.GetBytes(futureProducer)).Validation.ReasonCode);

        foreach (var result in new[]
        {
            TransientContractJson.ParseEvent(Encoding.UTF8.GetBytes(
                """{"future":true,"producer":{"schemaVersion":"transient-assessment-producer-v2"},"schemaVersion":"transient-event-v2"}""")).Validation,
            TransientContractJson.ParseCandidate(Encoding.UTF8.GetBytes(
                """{"future":true,"locator":{"schemaVersion":"transient-source-locator-v2"},"schemaVersion":"transient-candidate-v2"}""")).Validation,
            TransientContractJson.ParseDetectorInputDescriptor(Encoding.UTF8.GetBytes(
                """{"future":true,"locator":{"schemaVersion":"transient-source-locator-v2"},"schemaVersion":"transient-detector-input-v2"}""")).Validation
        })
        {
            Assert.AreEqual(TransientContractReasonCodes.UnsupportedSchema, result.ReasonCode);
            Assert.AreEqual("schemaVersion", result.FieldPath);
        }
    }

    [TestMethod]
    public void EventRejectsInvalidIdentityLineageTimeAndGeometry()
    {
        var value = TransientTestData.CreateEvent();
        AssertReason(TransientContractReasonCodes.InvalidIdentity, value with { EventId = Guid.Empty });
        AssertReason(TransientContractReasonCodes.InvalidIdentity, value with { Version = 1 });
        AssertReason(TransientContractReasonCodes.InvalidTime, value with
        {
            LastObservedUtc = value.VersionCreatedUtc.AddTicks(1)
        });
        AssertReason(TransientContractReasonCodes.InvalidLineage, value with
        {
            Observations = [value.Observations[0], value.Observations[1] with { Ordinal = 0 }]
        });
        AssertReason(TransientContractReasonCodes.InvalidGeometry, value with
        {
            Observations =
            [
                value.Observations[0] with
                {
                    Geometry = value.Observations[0].Geometry with
                    {
                        Bounds = value.Observations[0].Geometry.Bounds with { Width = double.NaN }
                    }
                },
                value.Observations[1]
            ]
        });
        AssertReason(TransientContractReasonCodes.InvalidSource, value with
        {
            Observations =
            [
                value.Observations[0] with
                {
                    Source = value.Observations[0].Source with
                    {
                        Locator = value.Observations[0].Source.Locator with
                        {
                            Artifact = value.Observations[0].Source.Locator.Artifact with
                            {
                                Role = FrameArtifactRole.Preview
                            }
                        }
                    }
                },
                value.Observations[1]
            ]
        });

        var candidate = TransientTestData.CreateCandidate();
        Assert.AreEqual(
            TransientContractReasonCodes.InvalidSource,
            TransientContractJson.Validate(candidate with
            {
                ContextSources =
                [
                    candidate.ContextSources[0] with
                    {
                        Locator = candidate.ContextSources[0].Locator with
                        {
                            Artifact = candidate.ContextSources[0].Locator.Artifact with
                            {
                                Role = FrameArtifactRole.Metadata
                            }
                        }
                    },
                    candidate.ContextSources[1]
                ]
            }).ReasonCode);
        Assert.AreEqual(
            TransientContractReasonCodes.InvalidLineage,
            TransientContractJson.Validate(candidate with
            {
                Extraction = candidate.Extraction with { OriginatingCandidateId = Guid.NewGuid() }
            }).ReasonCode);
        Assert.AreEqual(
            TransientContractReasonCodes.InvalidLineage,
            TransientContractJson.Validate(candidate with
            {
                Extraction = candidate.Extraction with { OriginatingCandidateId = null }
            }).ReasonCode);
    }

    [TestMethod]
    public void AssessmentVersionsCoexistAndFireballRemainsMeteorSeverity()
    {
        var value = TransientTestData.CreateEvent();
        Assert.IsTrue(TransientContractJson.Validate(value).IsValid);
        Assert.AreEqual(2, value.Assessments.Count);
        Assert.AreNotEqual(value.Assessments[0].Producer.Version, value.Assessments[1].Producer.Version);
        Assert.AreEqual(value.Assessments[0].AssessmentId, value.Assessments[1].SupersedesAssessmentId);
        Assert.AreEqual(
            value.Observations[0].Extraction.OriginatingCandidateId,
            value.Observations[1].Extraction.OriginatingCandidateId);
        Assert.AreEqual(TransientTestData.CreateCandidate().Extraction, value.Observations[0].Extraction);
        Assert.AreNotEqual(
            value.Observations[0].Extraction.Producer.Version,
            value.Observations[1].Extraction.Producer.Version);
        Assert.AreNotEqual(
            typeof(TransientAssessmentProducerV1),
            value.Observations[0].Extraction.Producer.GetType());

        var invalid = value with
        {
            Assessments =
            [
                value.Assessments[0] with
                {
                    Classification = TransientClassification.Satellite,
                    MeteorSeverity = TransientMeteorSeverity.Fireball
                },
                value.Assessments[1]
            ]
        };
        AssertReason(TransientContractReasonCodes.InvalidAssessment, invalid);
    }

    [TestMethod]
    public void ReviewNotificationDerivativeAndProfileInvariantsAreStrict()
    {
        var value = TransientTestData.CreateEvent();
        AssertReason(TransientContractReasonCodes.InvalidSource, value with
        {
            Observations =
            [
                value.Observations[0] with
                {
                    Provenance = value.Observations[0].Provenance with { MaskIdentity = " " }
                },
                value.Observations[1]
            ]
        });
        AssertReason(TransientContractReasonCodes.InvalidSource, value with
        {
            Observations =
            [
                value.Observations[0] with
                {
                    BackgroundArtifacts =
                    [value.Observations[0].BackgroundArtifacts[0] with { Role = FrameArtifactRole.AnnotatedPreview }]
                },
                value.Observations[1]
            ]
        });
        AssertReason(TransientContractReasonCodes.InvalidSource, value with
        {
            Observations =
            [
                value.Observations[0] with
                {
                    Extraction = value.Observations[0].Extraction with
                    {
                        Producer = value.Observations[0].Extraction.Producer with { SchemaVersion = "v2" }
                    }
                },
                value.Observations[1]
            ]
        });
        AssertReason(TransientContractReasonCodes.InvalidSource, value with
        {
            Observations =
            [
                value.Observations[0] with
                {
                    BackgroundArtifacts = [value.Observations[0].Source.Locator.Artifact]
                },
                value.Observations[1]
            ]
        });
        AssertReason(TransientContractReasonCodes.InvalidReview, value with
        {
            Reviews = [value.Reviews[0] with { Disposition = TransientReviewDisposition.Overridden }, value.Reviews[1]]
        });
        AssertReason(TransientContractReasonCodes.InvalidNotification, value with
        {
            Notifications = [value.Notifications[0] with { State = TransientNotificationState.Failed }]
        });
        AssertReason(TransientContractReasonCodes.InvalidDerivative, value with
        {
            Derivatives = [value.Derivatives[1] with { Limitations = [] }]
        });
        AssertReason(TransientContractReasonCodes.InvalidDerivative, value with
        {
            Derivatives =
            [value.Derivatives[0] with { RecipeIdentitySha256 = new string('0', 64) }, value.Derivatives[1]]
        });
        AssertReason(TransientContractReasonCodes.InvalidFeatures, value with
        {
            Observations =
            [
                value.Observations[0] with
                {
                    Features = value.Observations[0].Features with
                    {
                        WidthProfile =
                        [
                            new TransientProfileSampleV1(500_000, 1),
                            new TransientProfileSampleV1(500_000, 2)
                        ]
                    }
                },
                value.Observations[1]
            ]
        });
    }

    [TestMethod]
    public void EventIdentityIsCallerOwnedAndIndependentOfArtifactsAndPackaging()
    {
        var value = TransientTestData.CreateEvent();
        var changed = value with
        {
            Derivatives = [],
            Observations = value.Observations.Select(observation => observation with
            {
                Source = observation.Source with
                {
                    Locator = observation.Source.Locator with
                    {
                        Artifact = observation.Source.Locator.Artifact with { ArtifactId = Guid.NewGuid() }
                    }
                }
            }).ToArray()
        };

        Assert.AreEqual(value.EventId, changed.EventId);
        Assert.IsTrue(TransientContractJson.Validate(changed).IsValid);
        Assert.AreNotEqual(
            Sha256(TransientContractJson.Serialize(value)),
            Sha256(TransientContractJson.Serialize(changed)));
    }

    [TestMethod]
    public void HistoryChainsRejectEvidenceContradictionsChronologyForksAndScopeChanges()
    {
        var value = TransientTestData.CreateEvent();
        AssertReason(TransientContractReasonCodes.InvalidAssessment, value with
        {
            Assessments =
            [
                value.Assessments[0] with
                {
                    Reasons =
                    [new TransientReasonV1(
                        "transient.out-of-scope",
                        TransientReasonKind.Supporting,
                        [value.Observations[1].ObservationId])]
                },
                value.Assessments[1]
            ]
        });
        AssertReason(TransientContractReasonCodes.InvalidAssessment, value with
        {
            Assessments =
            [value.Assessments[0], value.Assessments[1] with { CreatedUtc = value.Assessments[0].CreatedUtc }]
        });
        var forkedAssessment = value.Assessments[1] with
        {
            AssessmentId = Guid.Parse("40000000-0000-0000-0000-000000000003"),
            CreatedUtc = value.Assessments[1].CreatedUtc.AddSeconds(1),
            Producer = value.Assessments[1].Producer with { Version = "detector-v3" }
        };
        AssertReason(TransientContractReasonCodes.InvalidAssessment, value with
        {
            Assessments = [value.Assessments[0], value.Assessments[1], forkedAssessment]
        });
        AssertReason(TransientContractReasonCodes.InvalidReview, value with
        {
            Reviews =
            [value.Reviews[0], value.Reviews[1] with { AssessmentId = value.Assessments[0].AssessmentId }]
        });
        AssertReason(TransientContractReasonCodes.InvalidReview, value with
        {
            Reviews = [value.Reviews[0], value.Reviews[1] with { CreatedUtc = value.Reviews[0].CreatedUtc }]
        });
        AssertReason(TransientContractReasonCodes.InvalidReview, value with
        {
            Reviews =
            [
                value.Reviews[0],
                value.Reviews[1],
                value.Reviews[1] with
                {
                    ReviewId = Guid.Parse("50000000-0000-0000-0000-000000000003"),
                    CreatedUtc = value.Reviews[1].CreatedUtc.AddMilliseconds(1)
                }
            ]
        });
        AssertReason(TransientContractReasonCodes.InvalidNotification, value with
        {
            Notifications =
            [value.Notifications[0], value.Notifications[1] with { Channel = "different-channel" }]
        });
        AssertReason(TransientContractReasonCodes.InvalidNotification, value with
        {
            Notifications =
            [value.Notifications[0], value.Notifications[1] with { AssessmentId = value.Assessments[0].AssessmentId }]
        });
        AssertReason(TransientContractReasonCodes.InvalidNotification, value with
        {
            Notifications =
            [
                value.Notifications[0],
                value.Notifications[1],
                value.Notifications[1] with
                {
                    NotificationId = Guid.Parse("60000000-0000-0000-0000-000000000003"),
                    CreatedUtc = value.Notifications[0].CreatedUtc.AddMilliseconds(1)
                }
            ]
        });
    }

    [TestMethod]
    public void EventAndVersionTimesBoundEveryContainedRecordAndPredecessor()
    {
        var value = TransientTestData.CreateEvent();
        var laterObservation = value.Observations[1] with
        {
            Source = value.Observations[1].Source with
            {
                ObservationStartedUtc = value.EventCreatedUtc.AddTicks(1),
                ObservationEndedUtc = value.EventCreatedUtc.AddSeconds(1)
            }
        };
        Assert.IsTrue(TransientContractJson.Validate(value with
        {
            LastObservedUtc = laterObservation.Source.ObservationEndedUtc,
            Observations = [value.Observations[0], laterObservation]
        }).IsValid);
        AssertReason(TransientContractReasonCodes.InvalidTime, value with
        {
            EventCreatedUtc = value.FirstObservedUtc.AddTicks(-1)
        });
        AssertReason(TransientContractReasonCodes.InvalidTime, value with
        {
            PreviousVersionCreatedUtc = value.VersionCreatedUtc
        });
        var invalidPredecessor = TransientContractJson.Validate(value with
        {
            PreviousEventVersionId = Guid.Empty
        });
        Assert.AreEqual(TransientContractReasonCodes.InvalidIdentity, invalidPredecessor.ReasonCode);
        Assert.AreEqual("previousEventVersionId", invalidPredecessor.FieldPath);
        AssertReason(TransientContractReasonCodes.InvalidIdentity, value with
        {
            PreviousEventVersionId = value.EventId
        });
        AssertReason(TransientContractReasonCodes.InvalidIdentity, value with
        {
            PreviousEventVersionId = value.EventVersionId
        });
        AssertReason(TransientContractReasonCodes.InvalidAssessment, value with
        {
            Assessments =
            [value.Assessments[0], value.Assessments[1] with { CreatedUtc = value.VersionCreatedUtc.AddTicks(1) }]
        });
        AssertReason(TransientContractReasonCodes.InvalidReview, value with
        {
            Reviews = [value.Reviews[0] with { CreatedUtc = value.VersionCreatedUtc.AddTicks(1) }]
        });
        AssertReason(TransientContractReasonCodes.InvalidNotification, value with
        {
            Notifications = [value.Notifications[0] with { CreatedUtc = value.VersionCreatedUtc.AddTicks(1) }]
        });
        AssertReason(TransientContractReasonCodes.InvalidReview, value with
        {
            Reviews = [value.Reviews[0] with { CreatedUtc = value.Assessments[1].CreatedUtc.AddTicks(-1) }]
        });
        AssertReason(TransientContractReasonCodes.InvalidNotification, value with
        {
            Notifications =
            [value.Notifications[0] with { CreatedUtc = value.Assessments[1].CreatedUtc.AddTicks(-1) }]
        });
        AssertReason(TransientContractReasonCodes.InvalidDerivative, value with
        {
            Derivatives = [value.Derivatives[0] with { CreatedUtc = value.VersionCreatedUtc.AddTicks(1) }]
        });
    }

    [TestMethod]
    public void SignedZeroNormalizesForGeometryBytesTransformsAndInputIdentity()
    {
        var candidate = TransientTestData.CreateCandidate();
        var positiveGeometry = candidate.Geometry! with
        {
            Bounds = candidate.Geometry.Bounds with { X = 0, Y = 0 },
            Polyline =
            [
                candidate.Geometry.Polyline[0] with { X = 0, Y = 0 },
                candidate.Geometry.Polyline[1]
            ]
        };
        var positive = candidate with { Geometry = positiveGeometry };
        var negative = candidate with
        {
            Geometry = positiveGeometry with
            {
                Bounds = positiveGeometry.Bounds with { X = -0d, Y = -0d },
                Polyline =
                [
                    positiveGeometry.Polyline[0] with { X = -0d, Y = -0d },
                    positiveGeometry.Polyline[1]
                ]
            }
        };
        CollectionAssert.AreEqual(
            TransientContractJson.Serialize(positive),
            TransientContractJson.Serialize(negative));

        var positiveFeatures = candidate.Features! with
        {
            LengthPixels = 0,
            MeanWidthPixels = 0,
            MaximumWidthPixels = 0,
            WidthProfile = [new TransientProfileSampleV1(0, 0)],
            BrightnessProfile = [new TransientProfileSampleV1(0, 0)]
        };
        CollectionAssert.AreEqual(
            TransientContractJson.Serialize(candidate with { Features = positiveFeatures }),
            TransientContractJson.Serialize(candidate with
            {
                Features = positiveFeatures with
                {
                    LengthPixels = -0d,
                    MeanWidthPixels = -0d,
                    MaximumWidthPixels = -0d,
                    WidthProfile = [new TransientProfileSampleV1(0, -0d)],
                    BrightnessProfile = [new TransientProfileSampleV1(0, -0d)]
                }
            }));

        var descriptor = TransientTestData.CreateDetectorDescriptor(CameraPixelFormat.Mono16);
        var negativeTransform = RebindIdentity(descriptor with
        {
            SourceToDetectorTransform = descriptor.SourceToDetectorTransform with { OffsetX = -0d, OffsetY = -0d }
        });
        Assert.AreEqual(descriptor.InputIdentitySha256, negativeTransform.InputIdentitySha256);
        CollectionAssert.AreEqual(
            TransientContractJson.Serialize(descriptor),
            TransientContractJson.Serialize(negativeTransform));
        var negativeLevel = RebindIdentity(descriptor with
        {
            Layout = descriptor.Layout with { BlackLevel = -0d }
        });
        Assert.AreEqual(descriptor.InputIdentitySha256, negativeLevel.InputIdentitySha256);
        CollectionAssert.AreEqual(
            TransientContractJson.Serialize(descriptor),
            TransientContractJson.Serialize(negativeLevel));
    }

    [TestMethod]
    public void DetectorDescriptorRejectsIdentityLayoutLevelsAndProfileChanges()
    {
        var descriptor = TransientTestData.CreateDetectorDescriptor(CameraPixelFormat.Mono16);
        Assert.IsTrue(TransientContractJson.Validate(descriptor).IsValid);
        Assert.AreEqual(
            TransientContractReasonCodes.InvalidIdentity,
            TransientContractJson.Validate(descriptor with { InputIdentitySha256 = new string('0', 64) }).ReasonCode);
        Assert.AreEqual(
            TransientContractReasonCodes.InvalidDetectorInput,
            TransientContractJson.Validate(descriptor with
            {
                Layout = descriptor.Layout with { PixelFormat = CameraPixelFormat.Rgb24 }
            }).ReasonCode);
        Assert.AreEqual(
            TransientContractReasonCodes.InvalidDetectorInput,
            TransientContractJson.Validate(descriptor with
            {
                Compatibility = descriptor.Compatibility with { Mask = " " }
            }).ReasonCode);
        var invalidProvenance = new[]
        {
            descriptor with { Representation = TransientDetectorRepresentation.Rggb16CellAverage },
            descriptor with { Conversion = descriptor.Conversion with { Name = "other" } },
            descriptor with { Conversion = descriptor.Conversion with { Version = "other" } },
            descriptor with
            {
                SourceToDetectorTransform = descriptor.SourceToDetectorTransform with { Version = "other" }
            },
            descriptor with
            {
                SourceToDetectorTransform = descriptor.SourceToDetectorTransform with { ScaleX = -1 }
            },
            descriptor with
            {
                SourceToDetectorTransform = descriptor.SourceToDetectorTransform with { ScaleY = 2 }
            },
            descriptor with
            {
                SourceToDetectorTransform = descriptor.SourceToDetectorTransform with { OffsetX = 1 }
            },
            descriptor with
            {
                SourceToDetectorTransform = descriptor.SourceToDetectorTransform with { OffsetY = -1 }
            }
        };
        foreach (var invalid in invalidProvenance)
        {
            Assert.AreEqual(
                TransientContractReasonCodes.InvalidDetectorInput,
                TransientContractJson.Validate(RebindIdentity(invalid)).ReasonCode);
        }
    }

    [TestMethod]
    public void LegacyV1DetectorDescriptorWithoutSaturationChecksumRetainsIdentityAndParses()
    {
        var descriptor = RebindIdentity(TransientTestData.CreateDetectorDescriptor(CameraPixelFormat.Mono16) with
        {
            SaturationMaskChecksumSha256 = null
        });

        Assert.AreEqual(LegacyMonoInputIdentitySha256, descriptor.InputIdentitySha256);
        var json = TransientContractJson.Serialize(descriptor);
        Assert.IsFalse(Encoding.UTF8.GetString(json).Contains("saturationMaskChecksumSha256", StringComparison.Ordinal));

        var parsed = TransientContractJson.ParseDetectorInputDescriptor(json);

        Assert.IsTrue(parsed.Validation.IsValid, parsed.Validation.ReasonCode);
        Assert.IsNotNull(parsed.Value);
        Assert.IsNull(parsed.Value.SaturationMaskChecksumSha256);
        Assert.AreEqual(LegacyMonoInputIdentitySha256, parsed.Value.InputIdentitySha256);
    }

    [TestMethod]
    public void PublicProcessingApiExposesNoScenarioTruthOrHostInfrastructureTypes()
    {
        var forbidden = new[]
        {
            "TransientScenarioProvenance", "VirtualSky", "SceneProvenance", "AspNetCore", "EntityFramework", "Minio"
        };
        var exposed = typeof(TransientEventV1).Assembly.ExportedTypes
            .SelectMany(type => type.GetMembers())
            .SelectMany(member => member switch
            {
                System.Reflection.MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType),
                System.Reflection.ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
                System.Reflection.PropertyInfo property => [property.PropertyType],
                System.Reflection.FieldInfo field => [field.FieldType],
                _ => []
            })
            .SelectMany(FlattenType)
            .Distinct()
            .ToArray();

        Assert.IsFalse(exposed.Any(type => forbidden.Any(name =>
            type.FullName?.Contains(name, StringComparison.Ordinal) == true)));

        var forbiddenMemberNames = new[]
        {
            "scenario", "truth", "expectedlabel", "expectedclass", "groundtruth", "simulation", "hiddenlabel"
        };
        var transientMembers = typeof(TransientEventV1).Assembly.ExportedTypes
            .Where(type => type.Namespace == typeof(TransientEventV1).Namespace &&
                type.Name.StartsWith("Transient", StringComparison.Ordinal))
            .SelectMany(type => type.GetMembers().Select(member => $"{type.Name}.{member.Name}"))
            .ToArray();
        Assert.IsFalse(transientMembers.Any(member => forbiddenMemberNames.Any(forbiddenName =>
            member.Contains(forbiddenName, StringComparison.OrdinalIgnoreCase))));
    }

    private static async Task AssertGoldenAsync<T>(
        string fileName,
        byte[] actual,
        string expectedSha256,
        Func<ReadOnlyMemory<byte>, T?> parse,
        Func<T, byte[]> serialize)
        where T : class
    {
        var fixture = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName))
            .ConfigureAwait(false);
        Assert.AreEqual((byte)'\n', fixture[^1], fileName);
        var expected = fixture[..^1];
        CollectionAssert.AreEqual(expected, actual, fileName);
        Assert.AreEqual(expectedSha256, Sha256(actual), fileName);
        var parsed = parse(expected);
        Assert.IsNotNull(parsed, fileName);
        CollectionAssert.AreEqual(expected, serialize(parsed), fileName);
    }

    private static void AssertReason(string expected, TransientEventV1 value)
        => Assert.AreEqual(expected, TransientContractJson.Validate(value).ReasonCode);

    private static string Sha256(ReadOnlySpan<byte> value)
        => Convert.ToHexString(SHA256.HashData(value));

    private static TransientDetectorInputDescriptorV1 RebindIdentity(TransientDetectorInputDescriptorV1 descriptor)
        => descriptor with
        {
            InputIdentitySha256 = TransientContractJson.ComputeDetectorInputIdentitySha256(descriptor)
        };

    private static IEnumerable<Type> FlattenType(Type type)
    {
        yield return type;
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(FlattenType))
            {
                yield return argument;
            }
        }
    }
}
