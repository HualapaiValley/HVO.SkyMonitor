using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class EnvironmentalObservationContractTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse(
        "2026-01-15T06:00:00Z",
        CultureInfo.InvariantCulture);

    [TestMethod]
    public void CanonicalRoundTripAndContentIdentityAreStable()
    {
        var observation = CreateObservation();
        var reorderedParameters = Json("""{"offset":1,"model":"weather-v1"}""");
        var equivalent = observation with
        {
            Source = observation.Source with
            {
                Provenance = observation.Source.Provenance with
                {
                    Parameters = reorderedParameters,
                    ParametersSha256 = LowerHex(CaptureContractJson.ComputeCanonicalJsonSha256(reorderedParameters))
                }
            }
        };

        var bytes = EnvironmentalObservationJson.Serialize(observation);
        var parsed = EnvironmentalObservationJson.Parse(bytes);

        Assert.IsTrue(parsed.Validation.IsValid);
        Assert.IsNotNull(parsed.Observation);
        CollectionAssert.AreEqual(bytes, EnvironmentalObservationJson.Serialize(parsed.Observation));
        Assert.AreEqual(
            EnvironmentalObservationJson.ComputeContentSha256(observation),
            EnvironmentalObservationJson.ComputeContentSha256(equivalent));
        Assert.AreEqual(
            EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation),
            EnvironmentalObservationJson.ComputeSourceIdentitySha256(equivalent));
        Assert.AreEqual(
            EnvironmentalObservationJson.ComputeSourceContentSha256(observation),
            EnvironmentalObservationJson.ComputeSourceContentSha256(equivalent));
        Assert.AreEqual(64, EnvironmentalObservationJson.ComputeContentSha256(observation).Length);

        var receivedLater = new ReceivedEnvironmentalObservationV1(
            observation,
            Epoch.AddHours(2),
            EnvironmentalObservationJson.ComputeContentSha256(observation));
        Assert.AreEqual(
            receivedLater.ContentSha256,
            EnvironmentalObservationJson.ComputeContentSha256(receivedLater.Observation));
        Assert.IsTrue(EnvironmentalObservationJson.Validate(receivedLater).IsValid);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidTime,
            EnvironmentalObservationJson.Validate(receivedLater with { ReceivedAtUtc = default }).ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidIdentity,
            EnvironmentalObservationJson.Validate(receivedLater with { ContentSha256 = new string('A', 64) }).ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidJson,
            EnvironmentalObservationJson.Parse("null"u8.ToArray()).Validation.ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidIdentity,
            EnvironmentalObservationJson.Validate(receivedLater with { Observation = null! }).ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidValue,
            EnvironmentalObservationJson.Validate(receivedLater with
            {
                Observation = observation with { Value = null! }
            }).ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidIdentity,
            EnvironmentalObservationJson.Validate(receivedLater with { ContentSha256 = "not-a-sha256" }).ReasonCode);
    }

    [TestMethod]
    public void EverySupportedKindRequiresItsCanonicalUnitAndRange()
    {
        var valid = new[]
        {
            Value(EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationUnit.DegreesCelsius, -273.15),
            Value(EnvironmentalObservationKind.RelativeHumidity, EnvironmentalObservationUnit.Percent, 100),
            Value(EnvironmentalObservationKind.AtmosphericPressure, EnvironmentalObservationUnit.Pascals, 101_325),
            Value(EnvironmentalObservationKind.WindSpeed, EnvironmentalObservationUnit.MetersPerSecond, 0),
            Value(EnvironmentalObservationKind.WindDirection, EnvironmentalObservationUnit.DegreesTrue, 359.999),
            Value(EnvironmentalObservationKind.WindGust, EnvironmentalObservationUnit.MetersPerSecond, 4),
            Value(EnvironmentalObservationKind.PrecipitationRate, EnvironmentalObservationUnit.MillimetersPerHour, 0),
            new EnvironmentalObservationValue(
                EnvironmentalObservationKind.RainState,
                EnvironmentalObservationUnit.Boolean,
                null,
                false,
                EnvironmentalObservationQuality.Good),
            Value(EnvironmentalObservationKind.SkyBrightness, EnvironmentalObservationUnit.MagnitudesPerSquareArcsecond, 20.5),
            Value(EnvironmentalObservationKind.SkyQuality, EnvironmentalObservationUnit.MagnitudesPerSquareArcsecond, 21),
            Value(EnvironmentalObservationKind.CloudCover, EnvironmentalObservationUnit.Fraction, 1)
        };

        foreach (var value in valid)
        {
            Assert.IsTrue(EnvironmentalObservationJson.Validate(CreateObservation(value: value)).IsValid, value.Kind.ToString());
            var wrongUnit = value with { Unit = EnvironmentalObservationUnit.Pascals };
            if (value.Kind != EnvironmentalObservationKind.AtmosphericPressure)
            {
                Assert.AreEqual(
                    EnvironmentalObservationReasonCodes.InvalidUnit,
                    EnvironmentalObservationJson.Validate(CreateObservation(value: wrongUnit)).ReasonCode,
                    value.Kind.ToString());
            }
        }

        var invalid = new[]
        {
            Value(EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationUnit.DegreesCelsius, -273.151),
            Value(EnvironmentalObservationKind.RelativeHumidity, EnvironmentalObservationUnit.Percent, 100.1),
            Value(EnvironmentalObservationKind.AtmosphericPressure, EnvironmentalObservationUnit.Pascals, 0),
            Value(EnvironmentalObservationKind.WindSpeed, EnvironmentalObservationUnit.MetersPerSecond, -1),
            Value(EnvironmentalObservationKind.WindDirection, EnvironmentalObservationUnit.DegreesTrue, 360),
            Value(EnvironmentalObservationKind.PrecipitationRate, EnvironmentalObservationUnit.MillimetersPerHour, -1),
            Value(EnvironmentalObservationKind.CloudCover, EnvironmentalObservationUnit.Fraction, 1.01)
        };
        foreach (var value in invalid)
        {
            Assert.IsFalse(EnvironmentalObservationJson.Validate(CreateObservation(value: value)).IsValid, value.Kind.ToString());
        }
    }

    [TestMethod]
    public void V2AddsOnlyEffectiveCameraSensorTemperatureWithoutChangingV1()
    {
        var cameraTemperature = Value(
            EnvironmentalObservationKind.CameraSensorTemperature,
            EnvironmentalObservationUnit.DegreesCelsius,
            -5);
        var v1 = CreateObservation(value: cameraTemperature);
        var v2 = v1 with
        {
            SchemaVersion = EnvironmentalObservationSchemaVersions.V2,
            Target = v1.Target with { RigId = "camera-1" }
        };

        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidUnit,
            EnvironmentalObservationJson.Validate(v1).ReasonCode);
        Assert.IsTrue(EnvironmentalObservationJson.Validate(v2).IsValid);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidTarget,
            EnvironmentalObservationJson.Validate(v2 with { Target = v2.Target with { RigId = null } }).ReasonCode);
        Assert.IsNotNull(EnvironmentalObservationJson.Parse(EnvironmentalObservationJson.Serialize(v2)).Observation);
        Assert.IsFalse(EnvironmentalObservationJson.Validate(v2 with
        {
            Value = cameraTemperature with { NumericValue = -273.151 }
        }).IsValid);
        Assert.IsFalse(EnvironmentalObservationJson.Validate(v2 with
        {
            Value = cameraTemperature with { Unit = EnvironmentalObservationUnit.Pascals }
        }).IsValid);
        var alternativeKind = CreateObservation(value: Value(
            EnvironmentalObservationKind.AirTemperature,
            EnvironmentalObservationUnit.DegreesCelsius,
            -5)) with
        {
            SchemaVersion = EnvironmentalObservationSchemaVersions.V2,
            Target = v2.Target
        };
        var cameraKind = alternativeKind with
        {
            Value = alternativeKind.Value with { Kind = EnvironmentalObservationKind.CameraSensorTemperature }
        };
        var alternativeReading = alternativeKind with
        {
            Value = alternativeKind.Value with { NumericValue = -4 }
        };
        Assert.IsTrue(EnvironmentalObservationJson.Validate(cameraKind).IsValid);
        Assert.IsTrue(EnvironmentalObservationJson.Validate(alternativeKind).IsValid);
        Assert.AreNotEqual(
            EnvironmentalObservationJson.ComputeSourceIdentitySha256(cameraKind),
            EnvironmentalObservationJson.ComputeSourceIdentitySha256(alternativeKind));
        Assert.AreNotEqual(
            EnvironmentalObservationJson.ComputeSourceContentSha256(cameraKind),
            EnvironmentalObservationJson.ComputeSourceContentSha256(alternativeKind));
        Assert.AreEqual(
            EnvironmentalObservationJson.ComputeSourceIdentitySha256(alternativeKind),
            EnvironmentalObservationJson.ComputeSourceIdentitySha256(alternativeReading));
        Assert.AreEqual(
            EnvironmentalObservationJson.ComputeSourceContentSha256(alternativeKind),
            EnvironmentalObservationJson.ComputeSourceContentSha256(alternativeReading));
    }

    [TestMethod]
    public void ValuesRejectNonfiniteAmbiguousAndInvalidUncertainty()
    {
        var source = CreateObservation();
        var invalidValues = new[]
        {
            source.Value with { NumericValue = double.NaN },
            source.Value with { NumericValue = double.PositiveInfinity },
            source.Value with { BooleanValue = true },
            source.Value with { NumericValue = null },
            source.Value with { Uncertainty = -0.1 },
            source.Value with { Uncertainty = double.NaN },
            source.Value with { SubmittedNumericValue = 45 },
            source.Value with { SubmittedUnit = "fahrenheit" },
            source.Value with { SubmittedNumericValue = double.NegativeInfinity, SubmittedUnit = "fahrenheit" },
            source.Value with { Kind = (EnvironmentalObservationKind)(-1) },
            source.Value with { Unit = (EnvironmentalObservationUnit)(-1) },
            source.Value with { Quality = (EnvironmentalObservationQuality)(-1) }
        };

        foreach (var value in invalidValues)
        {
            Assert.IsFalse(EnvironmentalObservationJson.Validate(source with { Value = value }).IsValid);
        }
    }

    [TestMethod]
    public void SourceKindsAndDerivedLineageRemainDistinct()
    {
        var source = CreateObservation();
        var hashes = Enum.GetValues<EnvironmentalObservationSourceKind>()
            .Select(kind =>
            {
                var sourceIds = kind == EnvironmentalObservationSourceKind.Derived
                    ? new[]
                    {
                        new EnvironmentalObservationReference(
                            new string('A', 64),
                            Guid.Parse("22222222-2222-2222-2222-222222222222"))
                    }
                    : Array.Empty<EnvironmentalObservationReference>();
                var observation = source with
                {
                    Source = source.Source with
                    {
                        Kind = kind
                    },
                    Lineage = sourceIds
                };
                Assert.IsTrue(EnvironmentalObservationJson.Validate(observation).IsValid);
                return EnvironmentalObservationJson.ComputeContentSha256(observation);
            })
            .ToArray();

        Assert.AreEqual(hashes.Length, hashes.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidProvenance,
            EnvironmentalObservationJson.Validate(source with
            {
                Source = source.Source with { Kind = EnvironmentalObservationSourceKind.Derived }
            }).ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidProvenance,
            EnvironmentalObservationJson.Validate(source with
            {
                Source = source.Source with
                {
                    Kind = EnvironmentalObservationSourceKind.Derived
                },
                Lineage =
                [
                    new EnvironmentalObservationReference(
                        EnvironmentalObservationJson.ComputeSourceIdentitySha256(source),
                        source.ObservationId)
                ]
            }).ReasonCode);
    }

    [TestMethod]
    public void TimeValidationUsesUtcAndExplicitHalfOpenValidity()
    {
        var source = CreateObservation();
        var invalid = new[]
        {
            source with { ObservedAtUtc = Epoch.ToOffset(TimeSpan.FromHours(1)) },
            source with { ValidThroughUtc = source.ValidFromUtc },
            source with { StaleAfterUtc = source.ValidFromUtc.AddTicks(-1) },
            source with { StaleAfterUtc = source.ValidThroughUtc.AddTicks(1) },
            source with { ObservedFromUtc = Epoch.AddSeconds(-1), ObservedThroughUtc = null },
            source with { ObservedFromUtc = Epoch.AddSeconds(1), ObservedThroughUtc = Epoch.AddSeconds(2) },
            source with { ObservedFromUtc = Epoch.AddSeconds(1), ObservedThroughUtc = Epoch.AddSeconds(-1) }
        };

        foreach (var observation in invalid)
        {
            Assert.AreEqual(
                EnvironmentalObservationReasonCodes.InvalidTime,
                EnvironmentalObservationJson.Validate(observation).ReasonCode);
        }

        Assert.IsTrue(EnvironmentalObservationJson.Validate(source with
        {
            ObservedFromUtc = Epoch,
            ObservedThroughUtc = Epoch
        }).IsValid);
    }

    [TestMethod]
    public void TargetAndProvenanceValidationRejectAmbiguousIdentity()
    {
        var source = CreateObservation();
        var duplicateParameters = Json("""{"calibration":1,"calibration":2}""");
        var nestedDuplicateParameters = Json("""{"items":[{"calibration":1,"calibration":2}]}""");
        var invalid = new[]
        {
            source with { ObservationId = Guid.Empty },
            source with { Target = null! },
            source with { Target = source.Target with { SiteId = Guid.Empty } },
            source with { Target = source.Target with { AgentId = Guid.Empty } },
            source with { Target = source.Target with { RigId = new string('r', 129) } },
            source with { Target = source.Target with { AgentId = null, RigId = "rig-1" } },
            source with { Source = source.Source with { Provider = " provider" } },
            source with { Source = source.Source with { Version = new string('v', 65) } },
            source with { Source = source.Source with { Provenance = null! } },
            source with
            {
                Source = source.Source with
                {
                    Provenance = source.Source.Provenance with { Method = null! }
                }
            },
            source with
            {
                Source = source.Source with
                {
                    Provenance = source.Source.Provenance with
                    {
                        Method = source.Source.Provenance.Method with { Name = " " }
                    }
                }
            },
            source with
            {
                Source = source.Source with
                {
                    Provenance = source.Source.Provenance with
                    {
                        Method = source.Source.Provenance.Method with { Version = new string('v', 65) }
                    }
                }
            },
            source with
            {
                Source = source.Source with
                {
                    Provenance = source.Source.Provenance with { Parameters = Json("[]") }
                }
            },
            source with
            {
                Source = source.Source with
                {
                    Provenance = source.Source.Provenance with { ParametersSha256 = "not-a-sha256" }
                }
            },
            source with
            {
                Source = source.Source with
                {
                    Provenance = source.Source.Provenance with { ParametersSha256 = new string('A', 64) }
                }
            },
            source with
            {
                Source = source.Source with
                {
                    Provenance = source.Source.Provenance with
                    {
                        Parameters = duplicateParameters,
                        ParametersSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(duplicateParameters)
                    }
                }
            },
            source with
            {
                Source = source.Source with
                {
                    Provenance = source.Source.Provenance with
                    {
                        Parameters = nestedDuplicateParameters,
                        ParametersSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(nestedDuplicateParameters)
                    }
                }
            }
        };

        foreach (var observation in invalid)
        {
            Assert.IsFalse(EnvironmentalObservationJson.Validate(observation).IsValid);
        }
    }

    [TestMethod]
    public void ParseRejectsMalformedNumericEnumsAndMissingRequiredMembers()
    {
        var source = Encoding.UTF8.GetString(EnvironmentalObservationJson.Serialize(CreateObservation()));
        var numericEnum = source.Replace("\"Good\"", "1", StringComparison.Ordinal);
        var missingSchema = source.Replace(
            "\"schemaVersion\":\"environmental-observation-v1\",",
            string.Empty,
            StringComparison.Ordinal);
        var unknownMember = source.Insert(1, "\"unknown\":true,");
        var duplicateMember = source.Insert(1, "\"schemaVersion\":\"other\",");
        var caseCollidingMember = source.Insert(1, "\"SchemaVersion\":\"other\",");
        var unsupportedSchema = source.Replace(
            "\"schemaVersion\":\"environmental-observation-v1\"",
            "\"schemaVersion\":\"environmental-observation-v3\"",
            StringComparison.Ordinal);
        var oversized = Encoding.UTF8.GetBytes(source + new string(' ', EnvironmentalObservationJson.MaximumPayloadBytes));

        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidJson,
            EnvironmentalObservationJson.Parse(Encoding.UTF8.GetBytes("not-json")).Validation.ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidJson,
            EnvironmentalObservationJson.Parse(Encoding.UTF8.GetBytes(numericEnum)).Validation.ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidJson,
            EnvironmentalObservationJson.Parse(Encoding.UTF8.GetBytes(missingSchema)).Validation.ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidJson,
            EnvironmentalObservationJson.Parse(Encoding.UTF8.GetBytes(unknownMember)).Validation.ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidJson,
            EnvironmentalObservationJson.Parse(Encoding.UTF8.GetBytes(duplicateMember)).Validation.ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidJson,
            EnvironmentalObservationJson.Parse(Encoding.UTF8.GetBytes(caseCollidingMember)).Validation.ReasonCode);
        var unsupported = EnvironmentalObservationJson.Parse(Encoding.UTF8.GetBytes(unsupportedSchema));
        Assert.IsNull(unsupported.Observation);
        Assert.AreEqual(EnvironmentalObservationReasonCodes.UnsupportedSchema, unsupported.Validation.ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.PayloadTooLarge,
            EnvironmentalObservationJson.Parse(oversized).Validation.ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.UnsupportedSchema,
            EnvironmentalObservationJson.Validate(CreateObservation() with { SchemaVersion = "v2" }).ReasonCode);
    }

    [TestMethod]
    public void CanonicalOperationsAreCultureAndAmbientTimeIndependent()
    {
        var observation = CreateObservation(value: Value(
            EnvironmentalObservationKind.AirTemperature,
            EnvironmentalObservationUnit.DegreesCelsius,
            12.5) with
        {
            SubmittedNumericValue = 54.5,
            SubmittedUnit = "degrees-fahrenheit"
        });
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var first = EnvironmentalObservationJson.ComputeContentSha256(observation);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var second = EnvironmentalObservationJson.ComputeContentSha256(observation);
            Assert.AreEqual(first, second);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [TestMethod]
    public void TargetlessFactEnrichmentPreservesEveryProducerField()
    {
        var observation = CreateObservation();
        var fact = new EnvironmentalObservationFactV1(
            observation.SchemaVersion,
            observation.ObservationId,
            observation.Source,
            observation.ObservedAtUtc,
            observation.ObservedFromUtc,
            observation.ObservedThroughUtc,
            observation.ValidFromUtc,
            observation.ValidThroughUtc,
            observation.StaleAfterUtc,
            observation.Value,
            observation.Lineage);

        var enriched = fact.Enrich(observation.Target);

        Assert.AreEqual(observation, enriched);
        Assert.IsTrue(EnvironmentalObservationJson.Validate(enriched).IsValid);
        Assert.IsFalse(typeof(EnvironmentalObservationFactV1).GetProperties().Any(
            property => property.PropertyType == typeof(EnvironmentalObservationTarget)));
    }

    [TestMethod]
    public void TargetlessFactCanonicalizationIsStrictAndTargetIndependent()
    {
        var observation = CreateObservation();
        var fact = CreateFact(observation);

        var payload = EnvironmentalObservationFactJson.Serialize(fact);
        var parsed = EnvironmentalObservationFactJson.Parse(payload);

        Assert.IsTrue(parsed.Validation.IsValid, parsed.Validation.ReasonCode);
        CollectionAssert.AreEqual(payload, EnvironmentalObservationFactJson.Serialize(parsed.Fact!));
        Assert.AreEqual(
            EnvironmentalObservationFactJson.ComputeContentSha256(fact),
            EnvironmentalObservationFactJson.ComputeContentSha256(parsed.Fact!));
        Assert.AreEqual(
            EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(fact),
            EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(parsed.Fact!));
        Assert.AreNotEqual(
            EnvironmentalObservationJson.ComputeSourceIdentitySha256(fact.Enrich(observation.Target)),
            EnvironmentalObservationJson.ComputeSourceIdentitySha256(fact.Enrich(
                observation.Target with { SiteId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc") })));
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidJson,
            EnvironmentalObservationFactJson.Parse(
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).Insert(1, "\"schemaVersion\":\"duplicate\",")))
                .Validation.ReasonCode);
    }

    [TestMethod]
    public void TargetlessFactValidationCoversStrictBoundsVersionsAndLineage()
    {
        var fact = CreateFact(CreateObservation());
        var unsupported = fact with { SchemaVersion = "environmental-observation-v3" };
        var v2 = fact with { SchemaVersion = EnvironmentalObservationSchemaVersions.V2 };
        var derived = v2 with
        {
            Source = v2.Source with { Kind = EnvironmentalObservationSourceKind.Derived },
            Lineage =
            [
                new EnvironmentalObservationReference(
                    LowerHex(new string('A', 64)),
                    Guid.Parse("22222222-2222-2222-2222-222222222222"))
            ]
        };

        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.PayloadTooLarge,
            EnvironmentalObservationFactJson.Parse(
                new byte[EnvironmentalObservationJson.MaximumPayloadBytes + 1]).Validation.ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidJson,
            EnvironmentalObservationFactJson.Parse(Encoding.UTF8.GetBytes("null")).Validation.ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidJson,
            EnvironmentalObservationFactJson.Parse(Encoding.UTF8.GetBytes("not-json")).Validation.ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.UnsupportedSchema,
            EnvironmentalObservationFactJson.Parse(
                EnvironmentalObservationFactJson.Serialize(unsupported)).Validation.ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.UnsupportedSchema,
            EnvironmentalObservationFactJson.Validate(unsupported).ReasonCode);
        var nestedDuplicate = Encoding.UTF8.GetString(EnvironmentalObservationFactJson.Serialize(derived)).Replace(
            $"\"sourceIdentitySha256\":\"{new string('A', 64)}\"",
            $"\"sourceIdentitySha256\":\"{new string('A', 64)}\",\"sourceIdentitySha256\":\"{new string('B', 64)}\"",
            StringComparison.Ordinal);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidJson,
            EnvironmentalObservationFactJson.Parse(Encoding.UTF8.GetBytes(nestedDuplicate)).Validation.ReasonCode);

        Assert.AreNotEqual(
            EnvironmentalObservationFactJson.ComputeContentSha256(fact),
            EnvironmentalObservationFactJson.ComputeContentSha256(v2));
        Assert.AreNotEqual(
            EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(fact),
            EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(v2));
        Assert.AreNotEqual(
            EnvironmentalObservationFactJson.ComputeSourceContentSha256(fact),
            EnvironmentalObservationFactJson.ComputeSourceContentSha256(v2));
        var alternativeKind = v2 with
        {
            RigId = "rig-1",
            Value = Value(
                EnvironmentalObservationKind.AirTemperature,
                EnvironmentalObservationUnit.DegreesCelsius,
                -5)
        };
        var cameraKind = alternativeKind with
        {
            Value = alternativeKind.Value with { Kind = EnvironmentalObservationKind.CameraSensorTemperature }
        };
        var alternativeReading = alternativeKind with
        {
            Value = alternativeKind.Value with { NumericValue = -4 }
        };
        Assert.IsTrue(EnvironmentalObservationFactJson.Validate(cameraKind).IsValid);
        Assert.IsTrue(EnvironmentalObservationFactJson.Validate(alternativeKind).IsValid);
        Assert.AreNotEqual(
            EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(cameraKind),
            EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(alternativeKind));
        Assert.AreNotEqual(
            EnvironmentalObservationFactJson.ComputeSourceContentSha256(cameraKind),
            EnvironmentalObservationFactJson.ComputeSourceContentSha256(alternativeKind));
        Assert.AreEqual(
            EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(alternativeKind),
            EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(alternativeReading));
        Assert.AreEqual(
            EnvironmentalObservationFactJson.ComputeSourceContentSha256(alternativeKind),
            EnvironmentalObservationFactJson.ComputeSourceContentSha256(alternativeReading));
        var parsed = EnvironmentalObservationFactJson.Parse(EnvironmentalObservationFactJson.Serialize(derived));

        Assert.IsTrue(parsed.Validation.IsValid, parsed.Validation.ReasonCode);
        Assert.AreEqual(new string('A', 64), parsed.Fact!.Lineage.Single().SourceIdentitySha256);
    }

    [TestMethod]
    public void DeliveryEnvelopeAndAcknowledgementUseStrictBoundedIdentity()
    {
        var observation = CreateObservation();
        var envelope = new EnvironmentalObservationDeliveryEnvelope(
            EnvironmentalObservationDeliveryEnvelope.CurrentSchemaVersion,
            "device-1",
            "secret",
            observation);
        var acknowledgement = new EnvironmentalObservationAcknowledgement(
            EnvironmentalObservationAcknowledgement.CurrentSchemaVersion,
            observation.ObservationId,
            EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation),
            EnvironmentalObservationJson.ComputeContentSha256(observation),
            Epoch.AddMinutes(1),
            EnvironmentalObservationDeliveryDisposition.Accepted);

        var parsedEnvelope = EnvironmentalObservationDeliveryJson.ParseEnvelope(
            EnvironmentalObservationDeliveryJson.Serialize(envelope));
        var parsedAcknowledgement = EnvironmentalObservationDeliveryJson.ParseAcknowledgement(
            EnvironmentalObservationDeliveryJson.Serialize(acknowledgement));

        Assert.IsTrue(parsedEnvelope.Validation.IsValid);
        Assert.AreEqual(envelope.DeviceId, parsedEnvelope.Value!.DeviceId);
        CollectionAssert.AreEqual(
            EnvironmentalObservationJson.Serialize(envelope.Observation),
            EnvironmentalObservationJson.Serialize(parsedEnvelope.Value.Observation));
        Assert.IsTrue(parsedAcknowledgement.Validation.IsValid);
        Assert.IsTrue(EnvironmentalObservationDeliveryJson.Matches(parsedAcknowledgement.Value!, observation));
        Assert.IsFalse(EnvironmentalObservationDeliveryJson.Matches(
            acknowledgement with { ObservationId = Guid.NewGuid() },
            observation));
        var duplicateOuterMember = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(EnvironmentalObservationDeliveryJson.Serialize(envelope)).Insert(1, "\"deviceId\":\"other\","));
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidJson,
            EnvironmentalObservationDeliveryJson.ParseEnvelope(duplicateOuterMember).Validation.ReasonCode);
        Assert.AreEqual(
            EnvironmentalObservationReasonCodes.InvalidJson,
            EnvironmentalObservationDeliveryJson.ParseAcknowledgement(Encoding.UTF8.GetBytes("{\"unknown\":true}")).Validation.ReasonCode);
    }

    private static EnvironmentalObservationV1 CreateObservation(
        EnvironmentalObservationValue? value = null)
    {
        var parameters = Json("""{"model":"weather-v1","offset":1}""");
        return new EnvironmentalObservationV1(
            EnvironmentalObservationV1.CurrentSchemaVersion,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new EnvironmentalObservationTarget(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                "rig-1"),
            new EnvironmentalObservationSource(
                "virtual-sky",
                "weather-primary",
                "1.0.0",
                EnvironmentalObservationSourceKind.Simulated,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("virtual-weather", "1.0.0"),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            Epoch,
            Epoch.AddSeconds(-1),
            Epoch.AddSeconds(1),
            Epoch.AddMinutes(-1),
            Epoch.AddMinutes(10),
            Epoch.AddMinutes(5),
            value ?? Value(
                EnvironmentalObservationKind.RelativeHumidity,
                EnvironmentalObservationUnit.Percent,
                45),
            []);
    }

    private static EnvironmentalObservationFactV1 CreateFact(EnvironmentalObservationV1 observation)
        => new(
            observation.SchemaVersion,
            observation.ObservationId,
            observation.Source,
            observation.ObservedAtUtc,
            observation.ObservedFromUtc,
            observation.ObservedThroughUtc,
            observation.ValidFromUtc,
            observation.ValidThroughUtc,
            observation.StaleAfterUtc,
            observation.Value,
            observation.Lineage);

    private static EnvironmentalObservationValue Value(
        EnvironmentalObservationKind kind,
        EnvironmentalObservationUnit unit,
        double numeric)
        => new(kind, unit, numeric, null, EnvironmentalObservationQuality.Good, 0.1);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string LowerHex(string value)
        => string.Create(value.Length, value, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                span[index] = source[index] is >= 'A' and <= 'F'
                    ? (char)(source[index] + ('a' - 'A'))
                    : source[index];
            }
        });
}
