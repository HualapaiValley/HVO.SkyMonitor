namespace HVO.SkyMonitor.Tests.Flat;

// Path-only owners: neither namespace nor type name selects these dependencies.
internal static class FlatDependencyControl
{
    internal static object Create() => new HVO.SkyMonitor.Processing.SpaceWeather.SwpcMessageRecord();

    // No sequence points of its own; inherits its declaring type's documents.
    internal interface INested
    {
        HVO.SkyMonitor.Processing.SpaceWeather.SwpcMessageRecord Read();
    }
}

// No method bodies: requires portable PDB TypeDefinitionDocuments.
internal interface IFlatContract
{
    HVO.SkyMonitor.Processing.SpaceWeather.SwpcMessageRecord Read();
}
