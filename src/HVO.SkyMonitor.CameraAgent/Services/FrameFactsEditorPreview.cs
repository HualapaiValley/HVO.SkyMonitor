using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>Ephemeral preview input and output. Evaluation, layout and raster semantics belong to the shared producer.</summary>
internal static class FrameFactsEditorPreview
{
    internal const int MaximumInputBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    internal static FrameFactsSnapshotV1 MissingSnapshot()
    {
        var facts = FrameFactsRetainedCatalog.Entries.OrderBy(entry => entry.Definition.Key, StringComparer.Ordinal)
            .Select(entry => new FrameFactsScalarV1(entry.Definition.Key, FrameFactsAvailability.Missing, FrameFactsValueKind.None,
                null, null, null, null, new("editor-no-evidence", "no-retained-facts-loaded", "Missing preview evidence"))).ToArray();
        var snapshot = new FrameFactsSnapshotV1(FrameFactsSnapshotV1.CurrentVersion, string.Empty, Array.AsReadOnly(facts));
        return snapshot with { IdentitySha256 = snapshot.ComputeIdentity() };
    }

    internal static FrameFactsSnapshotV1 ReadSnapshot(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumInputBytes) throw new ArgumentException("Retained facts JSON exceeds 1 MiB.", nameof(bytes));
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Retained facts JSON must be a JSON object.", nameof(bytes));
        var snapshot = document.RootElement.TryGetProperty("schemaVersion", out _)
            ? PresentationProcessingProducts.ParseMetadataFacts(bytes).FrameFacts
            : JsonSerializer.Deserialize<FrameFactsSnapshotV1>(bytes.Span, JsonOptions);
        if (snapshot is null) throw new ArgumentException("This product has no frozen Frame Facts snapshot. Load facts v3 or a snapshot v1.", nameof(bytes));
        snapshot.ToContext();
        return snapshot;
    }

    internal static (string Image, PresentationLayerPayloadV1 Payload) Render(ProcessingNodeOptionsForm form, FrameFactsSnapshotV1 snapshot)
    {
        if (!form.TryBuild(out var json, out var diagnostics))
            throw new ArgumentException(string.Join(' ', diagnostics.Select(diagnostic => diagnostic.Message)), nameof(form));
        var options = (json ?? JsonSerializer.SerializeToElement(new { })).Deserialize<FrameFactsPresentationOptions>(JsonOptions)!;
        var payload = FrameFactsPresentation.Create(snapshot.IdentitySha256, snapshot, options);
        var layout = new ImageLayout(options.WidthPixels, options.HeightPixels, CameraPixelFormat.Mono8, options.WidthPixels);
        var raster = PresentationLayerCompositor.CompositeDisplay(layout, new byte[layout.RequiredByteLength],
            [new(payload, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        return ("data:image/jpeg;base64," + Convert.ToBase64String(JpegImageCodec.EncodeToJpeg(raster.Layout, raster.Pixels, 95)), payload);
    }
}
