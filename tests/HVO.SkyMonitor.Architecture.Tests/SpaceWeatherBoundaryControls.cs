global using HVO.SkyMonitor.Processing.SpaceWeather;

// Deliberate violations compiled into this test assembly only. The capture
// control uses a global import, so its method never spells the target namespace.
namespace HVO.SkyMonitor.Processing.SpaceWeather
{
    internal sealed class SwpcMessageRecord;

    [System.AttributeUsage(System.AttributeTargets.Class)]
    internal sealed class TypeDependencyAttribute(System.Type dependency) : System.Attribute
    {
        public System.Type Dependency { get; } = dependency;
    }
}

namespace HVO.SkyMonitor.Processing.SpaceWeather.Internal
{
    internal static class FileDependencyControl
    {
        internal static string Read(string path) => System.IO.File.ReadAllText(path);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Compiled metadata fixture; never executed.")]
    [TypeDependency(typeof(System.Net.Http.HttpClient))]
    internal sealed class SignatureDependencyControl : System.IO.MemoryStream
    {
        internal System.IO.FileInfo? File { get; set; }
        internal static System.IO.DirectoryInfo? Convert(System.IO.BinaryReader? reader) => null;
    }
}

namespace HVO.SkyMonitor.Capture
{
    internal static class GlobalImportDependencyControl
    {
        internal static object Create() => new SwpcMessageRecord();
    }
}
