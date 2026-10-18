using System.Text.Json;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.TerminalUi;

/// <summary>
/// The installer is one file that bundles Terminal.Gui and everything it brings in, Onigwrap's native Oniguruma library
/// among them (PR-1371-R1 F7). <c>THIRD-PARTY-NOTICES.md</c>, which every release ships as its own asset, names each of
/// those packages and carries the terms their licences require. The packages are read from this test's own dependency
/// graph, so a package Terminal.Gui starts to bring in fails here until the notices name it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ThirdPartyNoticesTests
{
    private static readonly string Notices = File.ReadAllText(Path.Combine(RepositoryRoot(), "THIRD-PARTY-NOTICES.md"));

    [TestMethod]
    public void EveryPackageTerminalGuiBringsIn_IsNamed()
    {
        var packages = Closure("Terminal.Gui", "Wcwidth");

        Assert.IsGreaterThan(10, packages.Count, "the dependency graph was read");
        CollectionAssert.Contains(packages.ToArray(), "Onigwrap");
        var missing = packages.Where(package => !Notices.Contains($"`{package}`", StringComparison.Ordinal)).ToArray();
        Assert.IsEmpty(missing, $"THIRD-PARTY-NOTICES.md does not name: {string.Join(", ", missing)}");
    }

    [TestMethod]
    [DataRow("Copyright (c) 2002-2021  K.Kosako  <kkosako0@gmail.com>")]
    [DataRow("2. Redistributions in binary form must reproduce the above copyright")]
    [DataRow("Copyright (c) 2016-2026, Alexandre Mutel")]
    [DataRow("Copyright 2007-2011 Novell Inc")]
    [DataRow("Copyright (c) 2024 Aikawa Yataro")]
    [DataRow("Copyright (c) 2015 Fluent Solutions")]
    [DataRow("Copyright (c) 2021 Daniel Peñalba")]
    [DataRow("Apache License")]
    [DataRow("Version 2.0, January 2004")]
    public void TheTermsTheLicencesRequire_AreThere(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringAssert.Contains(Notices.Replace("\r\n", "\n", StringComparison.Ordinal), text, StringComparison.Ordinal);
    }

    // The packages <paramref name="roots"/> depend on, directly or not, by name, as this test's deps.json records them.
    private static SortedSet<string> Closure(params string[] roots)
    {
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "HVO.SkyMonitor.Deployment.Cli.Tests.deps.json")));
        var target = deps.RootElement.GetProperty("targets").EnumerateObject().First().Value;
        var libraries = deps.RootElement.GetProperty("libraries");
        var byName = target.EnumerateObject().ToDictionary(entry => entry.Name.Split('/')[0], entry => entry, StringComparer.Ordinal);
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots);
        while (pending.TryPop(out var name))
        {
            if (!byName.TryGetValue(name, out var entry) || !found.Add(name))
            {
                continue;
            }

            Assert.AreEqual("package", libraries.GetProperty(entry.Name).GetProperty("type").GetString(), $"{name} is a package");
            if (entry.Value.TryGetProperty("dependencies", out var dependencies))
            {
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    pending.Push(dependency.Name);
                }
            }
        }

        return found;
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
