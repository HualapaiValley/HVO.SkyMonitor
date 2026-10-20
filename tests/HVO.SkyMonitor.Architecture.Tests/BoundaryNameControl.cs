namespace HVO.SkyMonitor.Tests.Flat;

// Neither the path nor any namespace declaration selects this owner.
internal static class RecipeNameDependencyControl
{
    internal static object Create() => new HVO.SkyMonitor.Processing.SpaceWeather.SwpcMessageRecord();
}
