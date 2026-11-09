using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

/// <summary>Checks input completeness against build metadata rather than other coverage inputs.</summary>
internal static class UnitCoverageInputs
{
    internal static void Validate(string repositoryRoot, string resultsDirectory, string[] reports, IEnumerable<string> floorPaths)
    {
        var owners = floorPaths.Select(static path => path.Split('/')[0]).ToHashSet(StringComparer.Ordinal);
        var reportsByHash = reports.GroupBy(Hash).ToDictionary(static group => group.Key, static group => new Queue<string>(group));
        var inventories = new Dictionary<string, Dictionary<string, HashSet<int>>>(StringComparer.Ordinal);
        var seenProjects = new HashSet<string>(StringComparer.Ordinal);
        XNamespace trxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        foreach (var trx in Directory.GetFiles(resultsDirectory, "*.trx"))
        {
            var document = XDocument.Load(trx);
            var codeBases = document.Descendants(trxNamespace + "TestMethod")
                .Select(static method => (string?)method.Attribute("codeBase") ?? string.Empty)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (codeBases.Length != 1 || !File.Exists(codeBases[0]))
            {
                throw new InvalidDataException($"TRX '{trx}' must identify one existing test assembly from this build.");
            }

            var project = Path.GetFileNameWithoutExtension(codeBases[0]);
            var expectedTestAssembly = Path.Combine(repositoryRoot, "tests", project, "bin", "Release", "net10.0", project + ".dll");
            if (!Path.GetFullPath(codeBases[0]).Equals(expectedTestAssembly, StringComparison.Ordinal) || !seenProjects.Add(project))
            {
                throw new InvalidDataException($"Unexpected or repeated Unit project '{project}' in '{trx}'.");
            }

            var deployment = (string?)document.Descendants(trxNamespace + "Deployment").SingleOrDefault()?.Attribute("runDeploymentRoot");
            var attachments = document.Descendants(trxNamespace + "Collector")
                .Where(static collector => (string?)collector.Attribute("collectorDisplayName") == "XPlat code coverage")
                .Descendants(trxNamespace + "A").Select(static entry => (string?)entry.Attribute("href")).ToArray();
            if (deployment is null || attachments.Length != 1 || attachments[0] is null)
            {
                throw new InvalidDataException($"TRX '{trx}' must retain exactly one XPlat coverage attachment.");
            }

            var attachment = Path.GetFullPath(Path.Combine(resultsDirectory, deployment, "In", attachments[0]!));
            if (!attachment.StartsWith(Path.GetFullPath(resultsDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Coverage attachment escapes the result root in '{trx}'.");
            }

            if (!reportsByHash.TryGetValue(Hash(attachment), out var matches) || !matches.TryDequeue(out var report))
            {
                throw new InvalidDataException($"Unit project '{project}' has no matching original coverage report; a report was replaced or is incomplete.");
            }

            var assemblyDirectory = Path.GetDirectoryName(expectedTestAssembly)!;
            var expected = new Dictionary<string, Dictionary<string, HashSet<int>>>(StringComparer.Ordinal);
            using var dependencies = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(expectedTestAssembly, ".deps.json")));
            var runtimeTarget = dependencies.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
            var runtimeAssemblies = dependencies.RootElement.GetProperty("targets").GetProperty(runtimeTarget).EnumerateObject()
                .Where(static library => library.Value.TryGetProperty("runtime", out _))
                .SelectMany(static library => library.Value.GetProperty("runtime").EnumerateObject())
                .Select(static asset => Path.GetFileNameWithoutExtension(asset.Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var owner in owners.Intersect(runtimeAssemblies, StringComparer.Ordinal))
            {
                var assembly = Path.Combine(assemblyDirectory, owner + ".dll");
                if (!File.Exists(assembly))
                {
                    throw new InvalidDataException($"Unit project '{project}' is missing dependency assembly '{assembly}'.");
                }

                if (!inventories.TryGetValue(assembly, out var sources))
                {
                    sources = ReadExecutableSources(Path.ChangeExtension(assembly, ".pdb"), repositoryRoot);
                    inventories.Add(assembly, sources);
                }

                if (sources.Count > 0)
                {
                    expected.Add(owner, sources);
                }
            }

            ValidateReportSources(report, expected, repositoryRoot);
        }

        var expectedProjects = Directory.GetDirectories(Path.Combine(repositoryRoot, "tests"))
            .Where(static path => File.Exists(Path.Combine(path, "test-categories.json")))
            .Where(static path =>
            {
                using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "test-categories.json")));
                return inventory.RootElement.GetProperty("Unit").GetInt32() > 0;
            })
            .Select(static path => Path.GetFileName(path)).ToHashSet(StringComparer.Ordinal);
        if (!seenProjects.SetEquals(expectedProjects) || reportsByHash.Values.Any(static queue => queue.Count > 0))
        {
            throw new InvalidDataException($"Unit coverage/TRX inventory differs from the Unit-bearing project set; missing: {string.Join(", ", expectedProjects.Except(seenProjects))}.");
        }

        Console.WriteLine($"Verified {seenProjects.Count} Unit report identities and complete assembly/source inventories against Release portable PDBs.");
    }

    private static Dictionary<string, HashSet<int>> ReadExecutableSources(string pdb, string repositoryRoot)
    {
        using var stream = File.OpenRead(pdb);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        var sources = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var handle in reader.MethodDebugInformation)
        {
            var method = reader.GetMethodDebugInformation(handle);
            foreach (var point in method.GetSequencePoints())
            {
                if (point.IsHidden)
                {
                    continue;
                }

                var document = point.Document.IsNil ? method.Document : point.Document;
                var path = reader.GetString(reader.GetDocument(document).Name).Replace('\\', '/');
                var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
                if (relative.StartsWith("src/", StringComparison.Ordinal) &&
                    !relative.Split('/').Any(static part => part is "obj" or "bin" or "Migrations"))
                {
                    if (!sources.TryGetValue(relative[4..], out var lines))
                    {
                        lines = [];
                        sources.Add(relative[4..], lines);
                    }

                    lines.Add(point.StartLine);
                }
            }
        }

        return sources;
    }

    private static void ValidateReportSources(string report, Dictionary<string, Dictionary<string, HashSet<int>>> expected, string repositoryRoot)
    {
        var document = XDocument.Load(report);
        var packages = document.Descendants("package").ToArray();
        var names = packages.Select(static package => (string?)package.Attribute("name") ?? string.Empty).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || !expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(names))
        {
            throw new InvalidDataException($"Report '{report}' has incomplete assembly inventory; expected [{string.Join(", ", expected.Keys)}], observed [{string.Join(", ", names)}].");
        }

        foreach (var package in packages)
        {
            var name = (string)package.Attribute("name")!;
            var observed = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            foreach (var entry in package.Descendants("class"))
            {
                var filename = (string?)entry.Attribute("filename") ?? string.Empty;
                var absolute = Path.IsPathRooted(filename) ? filename : Path.Combine(repositoryRoot, "src", filename);
                var relative = Path.GetRelativePath(Path.Combine(repositoryRoot, "src"), absolute).Replace('\\', '/');
                if (!relative.StartsWith(name + "/", StringComparison.Ordinal))
                {
                    // A project-root source in Coverlet is sometimes just a basename.
                    relative = name + "/" + filename.Replace('\\', '/');
                }

                foreach (var line in entry.Element("lines")?.Elements("line") ?? [])
                {
                    if (!observed.TryGetValue(relative, out var lines))
                    {
                        lines = [];
                        observed.Add(relative, lines);
                    }

                    lines.Add(int.Parse((string)line.Attribute("number")!, System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            var missing = expected[name].Keys.Except(observed.Keys).Order(StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidDataException($"Report '{report}' is materially incomplete for '{name}': missing {missing.Length} executable source files from its Release PDB, including {string.Join(", ", missing.Take(8))}.");
            }

            foreach (var (source, requiredLines) in expected[name])
            {
                var missingLines = requiredLines.Except(observed[source]).Order().ToArray();
                if (missingLines.Length > 0)
                {
                    throw new InvalidDataException($"Report '{report}' is materially incomplete for '{source}': missing executable sequence-point lines from its Release PDB, including {string.Join(", ", missingLines.Take(8))}.");
                }
            }
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
