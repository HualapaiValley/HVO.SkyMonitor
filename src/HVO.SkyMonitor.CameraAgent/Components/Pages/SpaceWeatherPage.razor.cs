using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// Space weather: the retained SWPC notices and the local refresh state of this CameraAgent. The embedded panel owns
/// its read and its bounded commands; the page adds only the Operations heading.
/// </summary>
public sealed partial class SpaceWeatherPage : ComponentBase
{
    internal const string Description =
        "Review retained SWPC notices with their source wording kept apart from this site's estimate, and the local refresh state, without assessing cloud.";
}
