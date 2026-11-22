using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>One exact product a centered base binds, with the bytes its payload digest names.</summary>
internal sealed record SiloViewProduct(CenteredViewArtifact Artifact, byte[] Bytes, string Role, SiloRetentionClass RetentionClass, string MediaType)
{
    public string ArtifactId => Artifact.ArtifactId.ToString("D");

    /// <summary>The binding a section projects for this product.</summary>
    public SiloViewEvidence Binding => new(ArtifactId, Artifact.IdentitySha256, Artifact.PayloadSha256);

    public SiloDraftMember Member(SiloTestPayloads payloads, bool required = true) =>
        SiloDrafts.Member(payloads, ArtifactId, Role, RetentionClass, MediaType, Bytes, required, Artifact.IdentitySha256);
}

/// <summary>A stack base definition and the preview, science and closed-window products it binds.</summary>
internal sealed record SiloStackView(
    CenteredViewBaseDefinition Definition, SiloViewProduct Preview, SiloViewProduct Science, SiloViewProduct ClosedWindow)
{
    public IReadOnlyList<SiloViewProduct> Products => [Preview, Science, ClosedWindow];
}

/// <summary>
/// True typed centered view documents for the Silo tests. Each base and revision ID comes from the store's own
/// derivation, so every view built here is one the store could have written, and every value is seeded from its name so
/// the golden manifest is stable.
/// </summary>
internal static class SiloTypedViews
{
    public static readonly DateTimeOffset Registered = new(2026, 11, 22, 3, 30, 0, TimeSpan.Zero);

    public static Guid Id(string seed) => new(SiloDrafts.Bytes("id-" + seed, 16));

    /// <summary>A canonical uppercase SHA-256 identity, the form the store validates.</summary>
    public static string Identity(string seed) => Convert.ToHexString(SiloDrafts.Bytes("identity-" + seed, 32));

    public static CenteredViewArtifact Artifact(string name, byte[] payload) =>
        new(Id(name), Identity(name), Convert.ToHexString(SHA256.HashData(payload)));

    public static CenteredViewStream Stream(string seed) => new(new("agent-a", "camera-1"), Id("run-" + seed), "generation-1");

    public static string StreamId(string seed) => SqliteCenteredViews.StreamIdentity(Stream(seed));

    public static Guid ReferenceCapture(string seed) => Id("reference-" + seed);

    /// <summary>An evidence product as its own artifact revision; <see cref="SiloDrafts.Evidence"/> publishes it.</summary>
    public static SiloViewProduct Evidence(string name)
    {
        var bytes = SiloDrafts.Bytes("evidence-" + name, 200);
        return new(Artifact("evidence-" + name, bytes), bytes, "evidence", SiloRetentionClass.Metadata, "application/json");
    }

    public static string EvidenceId(string name) => Evidence(name).ArtifactId;

    /// <summary>
    /// A two-member stack on its reference: a registered-linear preview, a science product and the closed window that a
    /// stack base must bind.
    /// </summary>
    public static SiloStackView Stack(string seed)
    {
        var stream = Stream(seed);
        var reference = Exposure(stream, "reference-" + seed, 3);
        var contributor = Exposure(stream, "contributor-" + seed, 4);
        var previewBytes = SiloDrafts.Bytes("view-preview-" + seed, 900);
        var preview = new SiloViewProduct(Artifact("view-preview-" + seed, previewBytes), previewBytes, "preview", SiloRetentionClass.Display, "image/jpeg");
        var scienceBytes = SiloDrafts.Bytes("view-science-" + seed, 1200);
        var science = new SiloViewProduct(Artifact("view-science-" + seed, scienceBytes), scienceBytes, "science", SiloRetentionClass.Full,
            "application/octet-stream");
        var windowBytes = SiloDrafts.Bytes("view-window-" + seed, 300);
        var window = new SiloViewProduct(Artifact("view-window-" + seed, windowBytes), windowBytes, "closed-window", SiloRetentionClass.Metadata,
            "application/json");
        var definition = new CenteredViewBaseDefinition(stream, reference, 3, CenteredViewKind.Stack, science.Artifact,
            new CenteredViewPreview(
                new(preview.Artifact.ArtifactId, preview.Artifact.IdentitySha256, "image/jpeg", Compatibility),
                preview.Artifact.PayloadSha256,
                RecipeIdentityDescriptor.Create("preview", "1.0.0", "preview-v1", JsonSerializer.SerializeToElement(new { })),
                "JPEG", "jpeg-v1", new(16, 12, 0, 0, 16, 12, 16, 12))
            {
                Source = CenteredViewImageSource.RegisteredLinear
            },
            [
                new(reference, "ReferenceIdentity", JsonSerializer.SerializeToElement(new { method = "identity" })),
                new(contributor, "Homography", JsonSerializer.SerializeToElement(new { method = "homography", scale = 1 }))
            ],
            window.Artifact);
        return new(definition, preview, science, window);
    }

    public static PresentationCompatibilityDescriptor Compatibility { get; } = new(16, 12, new string('B', 64), new string('C', 64));

    /// <summary>A drawable for a Ready or Partial layer, on the preview's exact geometry.</summary>
    public static PresentationLayerV1 Drawable(string kind, CenteredViewArtifact evidence) =>
        LayeredPresentationJson.CreateLayer(kind, new(evidence.ArtifactId, evidence.IdentitySha256, "application/json", Compatibility),
            null, PresentationCoordinateSpace.ScenePixels, "renderer-v1", "style-v1", 200, PresentationBlendMode.Normal, 1_000_000,
            enabledByDefault: true, default);

    /// <summary>The typed stack-wide layer a projected binding describes.</summary>
    public static CenteredViewLayer Layer(SiloViewLayerBinding binding) => new(
        new(binding.Kind, Enum.Parse<CenteredViewLayerScope>(binding.Scope),
            binding.SourceCaptureId is { } source ? Guid.ParseExact(source, "D") : null),
        binding.EvidenceOrder,
        binding.Evidence is { } evidence ? new CenteredViewArtifact(Guid.ParseExact(evidence.ArtifactId, "D"), evidence.IdentitySha256, evidence.PayloadSha256) : null,
        binding.ObservedUtc,
        Enum.Parse<CenteredViewReadiness>(binding.Readiness),
        binding.Reason,
        null);

    /// <summary>
    /// A revision exactly as the store writes it: the layers in the store's own normalized order, and the base ID, revision
    /// ID and presentation manifest derived from them.
    /// </summary>
    public static CenteredView View(CenteredViewBaseDefinition definition, string? previousRevisionId, long order, IReadOnlyList<CenteredViewLayer> layers)
    {
        var baseId = SqliteCenteredViews.BaseIdentity(definition);
        var normalized = SqliteCenteredViews.NormalizedLayers(definition, layers);
        var manifest = LayeredPresentationJson.CreateManifest(definition.Preview.Product, sceneIdentitySha256: null,
            normalized.Where(static layer => layer.Drawable is not null).Select(static layer => layer.Drawable!));
        return new(new(baseId, definition, Registered),
            new(SqliteCenteredViews.RevisionIdentity(baseId, previousRevisionId, normalized), baseId, order, Registered.AddSeconds(order), manifest,
                normalized));
    }

    /// <summary>
    /// A view group revision built as a producer builds one: published under the store-issued base and revision IDs, with
    /// the section and reopen dependencies from the typed view, and any member published in the group listed there instead
    /// of as a dependency.
    /// </summary>
    public static SiloGroupDraft Draft(
        CenteredView view, string? previousRevisionId, IReadOnlyList<SiloDraftMember> members,
        IReadOnlyList<SiloManifestDependency> computeDependencies, long order)
    {
        var anchor = SiloHarness.Start.AddMinutes(30);
        var published = members.Select(static member => (member.ArtifactId, member.ArtifactRevision)).ToHashSet();
        var reopen = SiloCenteredViews.ReopenDependencies(view, scienceDisplayed: true, published);
        return new SiloGroupDraft(SiloJson.CenteredViewGroupKind, view.Base.BaseId, view.Revision.RevisionId, anchor.AddSeconds(order), anchor,
            anchor.AddSeconds(100), members, [.. reopen, .. computeDependencies], new SiloManifestProvenance([]), previousRevisionId, "camera-1",
            SiloCenteredViews.Section(view, previousRevisionId));
    }

    private static CenteredViewExposure Exposure(CenteredViewStream stream, string name, long sequence)
    {
        var descriptor = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono8, 16, 12, 16, new byte[16 * 12]).Descriptor;
        var capture = descriptor.Capture with { CaptureId = Id(name), CaptureSequence = sequence };
        descriptor = descriptor with { Capture = capture, Artifact = descriptor.Artifact with { ArtifactId = Id(name + "-raw") } };
        var input = new ExposureInputV1(new(stream.Camera, stream.RunId, capture.CaptureId), descriptor,
            new(descriptor.Layout, new RigOrientation(0, 0, 0), new string('A', 64), "optics", null));
        return new(input, Id(name + "-execution"), new(Id(name + "-linear"), Identity(name + "-linear"), Identity(name + "-linear-payload")));
    }
}
