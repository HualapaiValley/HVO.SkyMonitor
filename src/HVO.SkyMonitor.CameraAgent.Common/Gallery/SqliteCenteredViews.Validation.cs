using System.Collections.ObjectModel;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed partial class SqliteCenteredViews
{
    private static void ValidateBase(CenteredViewBaseDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Preview);
        ArgumentNullException.ThrowIfNull(definition.Contributors);
        ArgumentOutOfRangeException.ThrowIfNegative(definition.ReferenceOrder);
        ValidateExposure(definition.Stream, definition.Reference);
        ValidateArtifact(definition.Science);
        if (definition.RecomputedFromBaseId is { } parent)
            ValidateHash(parent);

        var contributors = definition.Contributors;
        if (definition.Kind switch
        {
            CenteredViewKind.Single => contributors.Count != 1 ||
                (definition.Preview.Source == CenteredViewImageSource.RawFallback
                    ? definition.Reference.Linear is not null || definition.Science != definition.Reference.RawArtifact()
                    : definition.Preview.Source != CenteredViewImageSource.QualifiedLinear || definition.Science != definition.Reference.Linear) ||
                definition.ClosedWindow is not null || definition.RecomputedFromBaseId is not null,
            CenteredViewKind.Stack => contributors.Count is < 2 or > 5 || definition.ClosedWindow is null ||
                definition.Preview.Source != CenteredViewImageSource.RegisteredLinear ||
                contributors.Any(member => member.Exposure.Linear is null),
            _ => true
        })
            throw new ArgumentException("Base kind, science product and frozen membership disagree.", nameof(definition));
        if (definition.ClosedWindow is { } window)
            ValidateArtifact(window);
        if (definition.Window is { } binding)
        {
            ValidateHash(binding.ScheduleIdentitySha256);
            ValidateHash(binding.ClosureIdentitySha256);
            ValidateHash(binding.RegistrationPlanIdentitySha256);
            if (definition.Kind != CenteredViewKind.Stack || binding.EvidenceCutoffUtc > binding.DeadlineUtc ||
                binding.Slots.Count != 5 || binding.Slots[2].CaptureId != definition.Reference.Capture.CaptureId ||
                binding.Slots.Where((slot, index) => slot.Offset != index - 2).Any())
                throw new ArgumentException("The centered window binding disagrees with its base.", nameof(definition));
        }

        var seen = new HashSet<Guid>();
        var containsReference = false;
        foreach (var contributor in contributors)
        {
            ArgumentNullException.ThrowIfNull(contributor);
            ValidateExposure(definition.Stream, contributor.Exposure);
            ValidateText(contributor.RegistrationMethod);
            if (contributor.SourceToReference.ValueKind != JsonValueKind.Object ||
                contributor.SourceToReference.GetRawText().Length > 64 * 1024 ||
                !seen.Add(contributor.Exposure.Capture.CaptureId))
                throw new ArgumentException("Contributor geometry must be finite evidence for a distinct source.", nameof(definition));
            if (contributor.Exposure.Capture.CaptureId == definition.Reference.Capture.CaptureId)
                containsReference = Serialize(contributor.Exposure) == Serialize(definition.Reference);
        }
        if (!containsReference)
            throw new ArgumentException("The exact reference is absent from the base membership.", nameof(definition));

        var preview = definition.Preview;
        ArgumentNullException.ThrowIfNull(preview.Transform);
        ArgumentNullException.ThrowIfNull(preview.Product);
        ArgumentNullException.ThrowIfNull(preview.Recipe);
        var transform = preview.Transform;
        var layout = definition.Reference.Input.Geometry.Layout;
        if (transform.ReferenceWidth != layout.Width || transform.ReferenceHeight != layout.Height ||
            transform.PreviewWidth != preview.Product.Compatibility.WidthPixels ||
            transform.PreviewHeight != preview.Product.Compatibility.HeightPixels ||
            transform.CropX < 0 || transform.CropY < 0 || transform.CropWidth < 1 || transform.CropHeight < 1 ||
            (long)transform.CropX + transform.CropWidth > transform.ReferenceWidth ||
            (long)transform.CropY + transform.CropHeight > transform.ReferenceHeight)
            throw new ArgumentException("Preview dimensions or crop disagree with the pinned reference raster.", nameof(definition));
        ValidateText(preview.EncoderName);
        ValidateText(preview.EncoderVersion);
        ValidateHash(preview.PayloadSha256);
        ValidateHash(preview.Recipe.OptionsSha256);
        if (preview.Recipe.Options.ValueKind != JsonValueKind.Object ||
            preview.Recipe.OptionsSha256 != CaptureContractJson.ComputeCanonicalJsonSha256(preview.Recipe.Options))
            throw new ArgumentException("Preview recipe options do not match their retained identity.", nameof(definition));
    }

    private static void ValidateExposure(CenteredViewStream stream, CenteredViewExposure exposure)
    {
        ArgumentNullException.ThrowIfNull(exposure);
        ArgumentNullException.ThrowIfNull(exposure.Input);
        ArgumentNullException.ThrowIfNull(exposure.Input.Geometry);
        ArgumentNullException.ThrowIfNull(exposure.Input.Raw);
        var raw = exposure.Input.Raw;
        var expected = new ExposureKey(stream.Camera, stream.RunId, raw.Capture.CaptureId);
        if (exposure.ExecutionId == Guid.Empty || exposure.Input.Key != expected ||
            raw.Capture.AgentId != stream.Camera.AgentId ||
            !raw.Validate().IsValid || !exposure.Input.Geometry.Layout.Validate().IsValid ||
            exposure.ExposureStartedUtc > exposure.ExposureEndedUtc)
            throw new ArgumentException("The exposure does not match the admitted camera run.", nameof(exposure));
        if (exposure.Linear is { } linear)
            ValidateArtifact(linear);
    }

    private static ReadOnlyCollection<CenteredViewLayer> NormalizeLayers(
        CenteredViewBaseDefinition definition, IEnumerable<CenteredViewLayer> source)
    {
        var layers = source.Take(LayeredPresentationJson.MaximumLayerCount + 1).ToArray();
        if (layers.Length > LayeredPresentationJson.MaximumLayerCount)
            throw new ArgumentException("The centered view has too many layer bindings.", nameof(source));
        foreach (var layer in layers)
            ValidateLayer(definition, layer);
        if (layers.Select(layer => layer.Key).Distinct().Count() != layers.Length)
            throw new ArgumentException("The centered view has duplicate layer keys.", nameof(source));

        return Array.AsReadOnly(layers.OrderBy(layer => layer.Key.Kind, StringComparer.Ordinal)
            .ThenBy(layer => layer.Key.Scope).ThenBy(layer => layer.Key.SourceCaptureId).ToArray());
    }

    private static void ValidateLayer(CenteredViewBaseDefinition definition, CenteredViewLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(layer.Key);
        ArgumentOutOfRangeException.ThrowIfNegative(layer.EvidenceOrder);
        ValidateText(layer.Key.Kind);
        var sourceId = layer.Key.SourceCaptureId;
        var inScope = layer.Key.Scope switch
        {
            CenteredViewLayerScope.Reference => sourceId == definition.Reference.Capture.CaptureId,
            CenteredViewLayerScope.Contributor => definition.Contributors.Any(member => member.Exposure.Capture.CaptureId == sourceId),
            CenteredViewLayerScope.Stack => definition.Kind == CenteredViewKind.Stack && sourceId is null,
            _ => false
        };
        if (!inScope || !Enum.IsDefined(layer.Readiness) || layer.Reason?.Length > 1024)
            throw new ArgumentException("Layer evidence is outside the exact base scope.", nameof(layer));

        if (layer.Evidence is { } evidence)
            ValidateArtifact(evidence);
        if (layer.Readiness is CenteredViewReadiness.Ready or CenteredViewReadiness.Partial or CenteredViewReadiness.AssessedEmpty &&
            (layer.Evidence is null || layer.ObservedUtc is null || layer.EvidenceOrder == 0))
            throw new ArgumentException("Assessed layers must bind to a committed evidence product and observation time.", nameof(layer));
        if (layer.Drawable is { } drawable)
        {
            if (layer.Readiness is not (CenteredViewReadiness.Ready or CenteredViewReadiness.Partial) ||
                drawable.LayerKind != layer.Key.Kind || drawable.SceneIdentitySha256 is not null ||
                drawable.SourceProduct.Compatibility != definition.Preview.Product.Compatibility)
                throw new ArgumentException("The drawable layer is incompatible with the exact preview geometry.", nameof(layer));
            LayeredPresentationJson.Validate(drawable);
        }
    }

    private static void ValidateArtifact(CenteredViewArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentOutOfRangeException.ThrowIfEqual(artifact.ArtifactId, Guid.Empty);
        ValidateHash(artifact.IdentitySha256);
        ValidateHash(artifact.PayloadSha256);
    }

    private static void ValidateHash(string value)
    {
        if (value is not { Length: 64 } || value.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException("A canonical SHA-256 identity is required.", nameof(value));
    }

    private static void ValidateText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            throw new ArgumentException("A nonblank identifier of at most 256 characters is required.", nameof(value));
    }
}
