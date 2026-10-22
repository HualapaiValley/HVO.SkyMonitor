namespace HVO.SkyMonitor.CameraAgent.Common.SkyMap;

/// <summary>
/// The operator's scene object limit, <c>CameraAgent:SkyMap:MaximumObjects</c>, as the host configuration holds it
/// now. It bounds the brightest catalog stars that both the sky map and a VirtualSky capture select; constellation
/// figure stars and solar-system bodies are added on top of it.
/// </summary>
/// <remarks>
/// Read <see cref="Current"/> once per projection or capture, so a value saved on the Observatory &amp; location page
/// applies to the next one without a restart.
/// </remarks>
public interface ISceneObjectBound
{
    /// <summary>Gets the current limit, from 1 to <see cref="CameraAgentSkyMapProjection.MaximumConfigurableObjects"/>.</summary>
    int Current { get; }
}

/// <summary>Reads the scene object limit from the live host configuration on every access.</summary>
internal sealed class ConfigurationSceneObjectBound(Func<int> read) : ISceneObjectBound
{
    /// <inheritdoc />
    public int Current => read();
}
