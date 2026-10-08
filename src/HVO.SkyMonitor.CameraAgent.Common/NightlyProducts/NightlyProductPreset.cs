using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

/// <summary>Registered preset identities and the immutable occurrence bound into every recipe output.</summary>
internal static class NightlyProductPreset
{
    internal const string OccurrenceInputName = "still-product-occurrence";
    internal const string OccurrenceInputVersion = "hvo-still-product-occurrence-input-v1";

    internal static string Target(NightlyProductKind kind, NightlyProductOptions options) =>
        string.Concat(NightlyProductContract.TargetFor(kind), ":", CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            version = "hvo-still-product-preset-v1",
            kind,
            options.SourceNodeId,
            SourceRecipeIdentitySha256 = options.SourceRecipeIdentitySha256?.ToUpperInvariant(),
            RigProfileSha256 = options.RigProfileSha256?.ToUpperInvariant(),
            options.KeogramColumnSeconds,
            options.KeogramMaximumGapSeconds,
            options.KeogramMaximumGapColumnCount,
            options.KeogramMaximumColumnCount,
            options.MaximumSegmentSources,
            options.MaximumSegmentsPerRun,
            options.RenditionJpegQuality
        }));

    internal static bool Matches(string target, NightlyProductKind kind, NightlyProductOptions options) =>
        string.Equals(target, Target(kind, options), StringComparison.Ordinal);

    internal static bool TryParseTarget(string target, out NightlyProductKind kind)
    {
        var separator = target.IndexOf(':', StringComparison.Ordinal);
        kind = default;
        return separator > 0 && target.Length == separator + 65 &&
            target.AsSpan(separator + 1).ToString().All(Uri.IsHexDigit) &&
            NightlyProductContract.TryParseTarget(target[..separator], out kind);
    }

    internal static ProcessingAuxiliaryInput BindOccurrence(LocalAutomationOccurrence occurrence)
    {
        var canonical = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(occurrence));
        var payload = Encoding.UTF8.GetBytes(canonical.GetRawText());
        return new(OccurrenceInputName, ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: OccurrenceInputVersion, IdentitySha256: ProcessingIdentity.ComputePayloadSha256(payload), Payload: payload);
    }

    internal static bool IsBound(ProcessingRecipeIdentity recipe, LocalAutomationOccurrence occurrence)
    {
        var expected = BindOccurrence(occurrence);
        if (!recipe.Descriptor.Options.TryGetProperty("auxiliaryInputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array)
            return false;
        var matches = inputs.EnumerateArray().Where(input => input.TryGetProperty("name", out var name) &&
            name.GetString() == OccurrenceInputName).ToArray();
        return matches.Length == 1 && matches[0].GetProperty("identitySha256").GetString() == expected.IdentitySha256 &&
            matches[0].GetProperty("schemaVersion").GetString() == OccurrenceInputVersion;
    }
}
