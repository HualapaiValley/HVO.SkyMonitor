using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>
/// Identifies the role of a capture artifact within a frame artifact set.
/// </summary>
public enum FrameArtifactRole
{
    /// <summary>
    /// The immutable bytes emitted by the camera module.
    /// </summary>
    Raw,

    /// <summary>
    /// A linear image after calibration has been applied.
    /// </summary>
    Calibrated,

    /// <summary>
    /// A linear image combined from multiple compatible captures.
    /// </summary>
    Combined,

    /// <summary>
    /// A display-ready image derived from a linear artifact.
    /// </summary>
    Preview,

    /// <summary>
    /// A display-ready image with celestial annotations.
    /// </summary>
    AnnotatedPreview,

    /// <summary>
    /// Metadata describing the capture and its artifacts.
    /// </summary>
    Metadata
}

/// <summary>
/// Describes one immutable artifact associated with a capture.
/// </summary>
/// <param name="ArtifactId">Stable identifier for the artifact.</param>
/// <param name="Role">The artifact's role in its containing frame artifact set.</param>
/// <param name="Frame">The frame data represented by the artifact.</param>
/// <param name="SourceArtifactIds">Identifiers of artifacts used to create this artifact.</param>
/// <param name="RecipeVersion">The processing recipe version that created this artifact, when applicable.</param>
public sealed record FrameArtifact
{
    /// <summary>
    /// Initializes an immutable artifact contract.
    /// </summary>
    public FrameArtifact(
        Guid artifactId,
        FrameArtifactRole role,
        CameraFrame frame,
        IReadOnlyList<Guid>? sourceArtifactIds = null,
        string? recipeVersion = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArtifactId = artifactId;
        Role = role;
        Frame = frame;
        SourceArtifactIds = sourceArtifactIds;
        RecipeVersion = recipeVersion;
    }

    /// <summary>
    /// Gets the stable identifier for the artifact.
    /// </summary>
    public Guid ArtifactId { get; }

    /// <summary>
    /// Gets the artifact's role in its containing frame artifact set.
    /// </summary>
    public FrameArtifactRole Role { get; }

    /// <summary>
    /// Gets the frame data represented by the artifact.
    /// </summary>
    public CameraFrame Frame { get; }

    /// <summary>
    /// Gets identifiers of artifacts used to create this artifact.
    /// </summary>
    public IReadOnlyList<Guid>? SourceArtifactIds { get; }

    /// <summary>
    /// Gets the processing recipe version that created this artifact, when applicable.
    /// </summary>
    public string? RecipeVersion { get; }
}

/// <summary>
/// Immutable, thread-safe collection of artifacts derived from one camera capture.
/// The set always retains its raw artifact; replacement operations may only add or
/// replace derivative roles.
/// </summary>
public sealed class FrameArtifactSet
{
    private readonly IReadOnlyDictionary<FrameArtifactRole, FrameArtifact> _artifacts;

    /// <summary>
    /// Creates an artifact set containing the specified raw camera frame.
    /// </summary>
    /// <param name="rawFrame">The original, unmodified camera output.</param>
    public FrameArtifactSet(CameraFrame rawFrame)
        : this(new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, rawFrame))
    {
    }

    /// <summary>
    /// Creates an artifact set containing the specified raw artifact.
    /// </summary>
    /// <param name="rawArtifact">The original, unmodified camera output artifact.</param>
    public FrameArtifactSet(FrameArtifact rawArtifact)
    {
        ArgumentNullException.ThrowIfNull(rawArtifact);
        if (rawArtifact.Role != FrameArtifactRole.Raw)
        {
            throw new ArgumentException("The initial artifact must have the Raw role.", nameof(rawArtifact));
        }

        _artifacts = new ReadOnlyDictionary<FrameArtifactRole, FrameArtifact>(
            new Dictionary<FrameArtifactRole, FrameArtifact>
            {
                [FrameArtifactRole.Raw] = rawArtifact
            });
    }

    private FrameArtifactSet(IReadOnlyDictionary<FrameArtifactRole, FrameArtifact> artifacts)
    {
        _artifacts = artifacts;
    }

    /// <summary>
    /// Gets the original, unmodified camera artifact.
    /// </summary>
    public FrameArtifact Raw => _artifacts[FrameArtifactRole.Raw];

    /// <summary>
    /// Gets all artifacts keyed by role.
    /// </summary>
    public IReadOnlyDictionary<FrameArtifactRole, FrameArtifact> Artifacts => _artifacts;

    /// <summary>
    /// Gets an artifact by role.
    /// </summary>
    /// <param name="role">The role to retrieve.</param>
    /// <returns>The matching artifact.</returns>
    public FrameArtifact this[FrameArtifactRole role] => _artifacts[role];

    /// <summary>
    /// Returns a new set with a derivative frame assigned to <paramref name="role" />.
    /// The raw artifact remains unchanged and is recorded as the default source.
    /// </summary>
    /// <param name="role">A non-raw derivative role.</param>
    /// <param name="frame">The derived frame.</param>
    /// <param name="recipeVersion">The processing recipe version, when applicable.</param>
    /// <returns>A new set containing the derivative.</returns>
    public FrameArtifactSet WithDerivative(
        FrameArtifactRole role,
        CameraFrame frame,
        string? recipeVersion = null,
        IReadOnlyList<Guid>? sourceArtifactIds = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (role == FrameArtifactRole.Raw)
        {
            throw new ArgumentOutOfRangeException(nameof(role), "Raw artifacts cannot be replaced.");
        }

        var artifacts = _artifacts.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        artifacts[role] = new FrameArtifact(
            Guid.NewGuid(),
            role,
            frame,
            sourceArtifactIds ?? [Raw.ArtifactId],
            recipeVersion);

        return new FrameArtifactSet(new ReadOnlyDictionary<FrameArtifactRole, FrameArtifact>(artifacts));
    }
}
