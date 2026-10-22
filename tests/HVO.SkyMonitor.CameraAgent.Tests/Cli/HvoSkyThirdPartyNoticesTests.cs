using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli;

/// <summary>
/// hvo-sky ships as one self-contained file, <c>hvo-sky-&lt;rid&gt;</c>, holding every package it depends on.
/// <c>THIRD-PARTY-NOTICES.md</c>, which every release ships as its own asset, names each of them, as it names the
/// installer's (the installer's closure is checked by Deployment.Cli.Tests, which may not reach this client). The
/// packages are read from this test's dependency graph, from the client's project down, so a package the client starts
/// to depend on fails here until the notices name it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class HvoSkyThirdPartyNoticesTests
{
    private const string Client = "HVO.SkyMonitor.CameraAgent.Cli";

    private static readonly string Notices = File.ReadAllText(Path.Combine(RepositoryRoot(), "THIRD-PARTY-NOTICES.md"));

    [TestMethod]
    public void EveryPackageTheClientShipsWith_IsNamed()
    {
        var packages = ClientClosure();

        CollectionAssert.Contains(packages.ToArray(), "System.CommandLine", "the dependency graph was read from the client's project");
        var missing = packages.Where(package => !Notices.Contains($"`{package}`", StringComparison.Ordinal)).ToArray();
        Assert.IsEmpty(missing, $"THIRD-PARTY-NOTICES.md does not name: {string.Join(", ", missing)}");
    }

    // The packages the client depends on, directly, through a project it references, or through another package, by
    // name, as this test's deps.json records them. Packages a project leaves out (ExcludeAssets) are not in it.
    private static SortedSet<string> ClientClosure()
    {
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "HVO.SkyMonitor.CameraAgent.Tests.deps.json")));
        var target = deps.RootElement.GetProperty("targets").EnumerateObject().First().Value;
        var libraries = deps.RootElement.GetProperty("libraries");
        var byName = target.EnumerateObject().ToDictionary(entry => entry.Name.Split('/')[0], entry => entry, StringComparer.Ordinal);
        Assert.IsTrue(byName.ContainsKey(Client), $"{Client} is in this test's dependency graph");
        var packages = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([Client]);
        while (pending.TryPop(out var name))
        {
            if (!byName.TryGetValue(name, out var entry) || !seen.Add(name))
            {
                continue;
            }

            if (libraries.GetProperty(entry.Name).GetProperty("type").GetString() == "package")
            {
                packages.Add(name);
            }

            if (entry.Value.TryGetProperty("dependencies", out var dependencies))
            {
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    pending.Push(dependency.Name);
                }
            }
        }

        return packages;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root holds HVO.SkyMonitor.v9.slnx.");
    }
}
