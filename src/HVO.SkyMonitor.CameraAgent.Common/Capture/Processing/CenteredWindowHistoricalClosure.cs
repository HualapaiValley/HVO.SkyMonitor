using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

// The earlier five-field pin remains readable under the original closure hash.
// Its missing envelope never participates in a new registration invocation.
internal static class CenteredWindowHistoricalClosure
{
    internal static CenteredWindowClosure RestoreIdentity(CenteredWindowClosure closure, byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        var slots = json.RootElement.GetProperty("slots");
        var historical = new
        {
            closure.Schedule,
            closure.EvidenceCutoffUtc,
            Slots = closure.Slots.Select((slot, index) => new
            {
                slot.Offset,
                slot.Time,
                slot.State,
                slot.Reason,
                slot.Input,
                Products = slot.Products is { } products ? new
                {
                    products.LinearSettledUtc,
                    products.Disposition,
                    products.Linear,
                    products.Measurement,
                    products.Plate,
                    products.RejectionReason,
                    RegistrationPin = HistoricalExposurePin(products.RegistrationPin, slots[index])
                } : null,
                slot.Attempt
            }).ToArray()
        };
        return closure with { HistoricalIdentity = PayloadChecksum.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(historical)) };
    }

    private static object? HistoricalExposurePin(CenteredStackExposurePin? pin, JsonElement slot) => pin switch
    {
        null => null,
        { Raw.EnvelopeSha256: null } => WithFiveFieldPins(pin, slot),
        { LinearLayout: null } => new
        {
            pin.Key,
            pin.CaptureSequence,
            pin.ExposureStartedUtc,
            pin.ExposureEndedUtc,
            pin.IntegrationTicks,
            pin.Raw,
            pin.Linear,
            pin.ValidityMask,
            pin.Measurement,
            pin.Plate,
            pin.GeometrySha256,
            pin.LinearProvenanceSha256,
            pin.MeasurementBindingSha256
        },
        _ => pin
    };

    private static object WithFiveFieldPins(CenteredStackExposurePin pin, JsonElement slot) => new
    {
        pin.Key,
        pin.CaptureSequence,
        pin.ExposureStartedUtc,
        pin.ExposureEndedUtc,
        pin.IntegrationTicks,
        Raw = HistoricalArtifactPin(pin.Raw),
        Linear = HistoricalArtifactPin(pin.Linear),
        ValidityMask = HistoricalArtifactPin(pin.ValidityMask),
        Measurement = HistoricalArtifactPin(pin.Measurement),
        Plate = pin.Plate is { } plate ? new
        {
            Associations = HistoricalArtifactPin(plate.Associations),
            Scene = HistoricalArtifactPin(plate.Scene),
            Context = HistoricalArtifactPin(plate.Context),
            Solution = HistoricalArtifactPin(plate.Solution)
        } : null,
        pin.GeometrySha256,
        pin.LinearProvenanceSha256,
        MeasurementGeometrySha256 = ReadGeometryHash(slot)
    };

    private static string ReadGeometryHash(JsonElement slot)
    {
        if (!slot.GetProperty("products").GetProperty("registrationPin")
                .TryGetProperty("measurementGeometrySha256", out var value) ||
            value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: 64 } hash ||
            !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("The historical centered pin has no valid measurement geometry identity.");
        return hash;
    }

    private static object? HistoricalArtifactPin(CenteredStackArtifactPin? pin) => pin is null ? null : new
    {
        pin.ArtifactId,
        pin.ProducerSha256,
        pin.PayloadSha256,
        pin.DescriptorSha256,
        pin.ContentSha256
    };
}
