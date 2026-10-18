using System.Reflection;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The installer's build identity, <c>&lt;version&gt;+&lt;40-character commit&gt;</c> (install contract section 1), which
/// <c>--version</c> prints. The build stamps it into the assembly's informational version: a publish not given a
/// version is the product version's development build, <c>X.Y.Z-dev</c> (Directory.Build.props), and one built without a
/// checkout or a supplied commit names its commit <c>unknown</c>.
/// </summary>
internal static class InstallerBuildIdentity
{
    /// <summary>What an assembly carrying no stamp reports, so an unknown build still says so.</summary>
    public const string Unknown = "0.0.0-dev+unknown";

    /// <summary>This installer's identity.</summary>
    public static string Current { get; } = Of(typeof(InstallerBuildIdentity).Assembly);

    /// <summary>The identity stamped into <paramref name="assembly"/>.</summary>
    internal static string Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Unknown;
    }
}
