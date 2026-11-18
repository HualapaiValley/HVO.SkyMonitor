namespace HVO.SkyMonitor.CameraAgent.Components.Shared;

internal static class SamePageFragmentLink
{
    public static string Build(string currentUri, string fragment)
    {
        var uri = new Uri(currentUri, UriKind.Absolute);
        return new UriBuilder(uri) { Fragment = fragment.TrimStart('#') }.Uri.AbsoluteUri;
    }
}
