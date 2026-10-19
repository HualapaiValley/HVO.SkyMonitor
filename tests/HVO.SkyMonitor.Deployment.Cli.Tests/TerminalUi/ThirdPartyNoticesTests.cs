using System.Text.Json;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.TerminalUi;

/// <summary>
/// The installer is one file that bundles Terminal.Gui and the packages it brings in (PR-1371-R1 F7, R2 F9).
/// <c>THIRD-PARTY-NOTICES.md</c>, which every release ships as its own asset, names each package the installer carries
/// and the terms their licences require. What the installer carries is read from its own dependency manifest, so a
/// package Terminal.Gui starts to bring in fails here until the notices name it; and Terminal.Gui's syntax highlighting,
/// which the installer leaves out (Directory.Build.targets), must stay out of both.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ThirdPartyNoticesTests
{
    private static readonly string Notices = File.ReadAllText(Path.Combine(RepositoryRoot(), "THIRD-PARTY-NOTICES.md")).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static readonly string[] SyntaxHighlighting = ["TextMateSharp", "TextMateSharp.Grammars", "Onigwrap"];

    // What a manifest entry ships: assemblies, native libraries, and either for a given runtime.
    private static readonly string[] FileKinds = ["runtime", "native", "runtimeTargets"];

    [TestMethod]
    public void EveryPackageTheInstallerCarriesForTerminalGui_IsNamed()
    {
        using var installer = InstallerManifest();
        var carried = Closure(installer.RootElement, "Terminal.Gui").Where(package => Carries(installer.RootElement, package)).ToArray();

        Assert.IsGreaterThan(10, carried.Length, "the dependency manifest was read");
        CollectionAssert.Contains(carried, "Terminal.Gui");
        CollectionAssert.Contains(carried, "Wcwidth");
        var missing = carried.Where(package => !Notices.Contains($"`{package}`", StringComparison.Ordinal)).ToArray();
        Assert.IsEmpty(missing, $"THIRD-PARTY-NOTICES.md does not name: {string.Join(", ", missing)}");
    }

    [TestMethod]
    public void TerminalGuisSyntaxHighlighting_IsNeitherCarriedNorNamed()
    {
        // Left out, they are not in the installer's manifest at all, as Terminal.Gui's dependencies or otherwise.
        using var installer = InstallerManifest();

        foreach (var package in SyntaxHighlighting)
        {
            Assert.IsFalse(Carries(installer.RootElement, package), $"the installer carries {package}'s files");
            Assert.DoesNotContain($"`{package}`", Notices, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [DataRow("Copyright 2007-2011 Novell Inc")]
    [DataRow("Copyright 2017 Microsoft Corp")]
    [DataRow("Copyright (c) 2016-2026, Alexandre Mutel")]
    [DataRow("2. Redistributions in binary form must reproduce the above copyright notice,")]
    [DataRow("Copyright Patrik Svensson. Phil Scott")]
    [DataRow("Copyright (c) .NET Foundation and Contributors")]
    public void TheTermsTheLicencesRequire_AreThere(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringAssert.Contains(Notices, text, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("K.Kosako")]
    [DataRow("Fluent Solutions")]
    [DataRow("Version 2.0, January 2004")]
    [DataRow("cgmanifest.json")]
    public void TheNoticesOfWhatIsLeftOut_AreNotThere(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Assert.DoesNotContain(text, Notices, StringComparison.Ordinal);
    }

    // The installer's own dependency manifest, which the build copies beside this test.
    private static JsonDocument InstallerManifest()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "HVO.SkyMonitor.Deployment.Cli.deps.json")));

    // Whether the manifest gives the package a file to ship: an assembly or a native library, for any runtime.
    private static bool Carries(JsonElement manifest, string package)
        => Entry(manifest, package) is { } entry &&
           FileKinds.Any(kind => entry.TryGetProperty(kind, out var files) && files.EnumerateObject().Any());

    // The package's entry in the target the installer runs as, which the manifest names as its runtime target.
    private static JsonElement? Entry(JsonElement manifest, string package)
        => manifest.GetProperty("targets").GetProperty(manifest.GetProperty("runtimeTarget").GetProperty("name").GetString()!).EnumerateObject()
            .Where(entry => entry.Name.Split('/')[0] == package)
            .Select(static entry => (JsonElement?)entry.Value)
            .FirstOrDefault();

    // The packages <paramref name="root"/> depends on, directly or not, and itself, by name, as the manifest records them.
    private static SortedSet<string> Closure(JsonElement manifest, string root)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var name))
        {
            if (!found.Add(name) || Entry(manifest, name) is not { } entry)
            {
                continue;
            }

            if (entry.TryGetProperty("dependencies", out var dependencies))
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
