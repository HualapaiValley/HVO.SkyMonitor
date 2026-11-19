namespace HVO.SkyMonitor.CameraAgent.Common.SkyMap;

/// <summary>
/// The operator's scene object limit, <c>CameraAgent:SkyMap:MaximumObjects</c>, as the host configuration holds it
/// now. An explicitly configured value also bounds VirtualSky capture unless a renderer limit is set separately.
/// </summary>
/// <remarks>
/// Read <see cref="Current"/> once per projection or capture, so a value saved on the Observatory &amp; location page
/// applies to the next one without a restart.
/// </remarks>
public interface ISceneObjectBound
{
    /// <summary>Gets the current limit, from 1 to <see cref="CameraAgentSkyMapProjection.MaximumConfigurableObjects"/>.</summary>
    int Current { get; }

    /// <summary>Gets the dedicated VirtualSky renderer override, or null when capture inherits its limit.</summary>
    int? ConfiguredRendererCandidateCap => null;

    /// <summary>Gets an explicit host limit for VirtualSky capture, or null to use the camera profile.</summary>
    int? VirtualSkyOverride => Current;
}

/// <summary>Reads the scene object limit from the live host configuration on every access.</summary>
internal sealed class ConfigurationSceneObjectBound(Func<int?> readSkyMap, Func<int?> readRenderer) : ISceneObjectBound
{
    /// <inheritdoc />
    public int Current => readSkyMap() ?? CameraAgentSkyMapProjection.DefaultMaximumObjects;

    /// <inheritdoc />
    public int? ConfiguredRendererCandidateCap => readRenderer();

    /// <inheritdoc />
    public int? VirtualSkyOverride => ConfiguredRendererCandidateCap ?? readSkyMap();
}
