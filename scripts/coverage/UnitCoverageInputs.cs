using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
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
        var inventories = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
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
            var expected = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
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

                var pdb = Path.ChangeExtension(assembly, ".pdb");
                var inventoryIdentity = Hash(pdb);
                if (!inventories.TryGetValue(inventoryIdentity, out var sources))
                {
                    sources = ReadExecutableSources(pdb, repositoryRoot);
                    inventories.Add(inventoryIdentity, sources);
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

    private static Dictionary<string, HashSet<string>> ReadExecutableSources(string pdb, string repositoryRoot)
    {
        using var stream = File.OpenRead(pdb);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        var sources = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using var assemblyStream = File.OpenRead(Path.ChangeExtension(pdb, ".dll"));
        using var assembly = new PEReader(assemblyStream);
        var metadata = assembly.GetMetadataReader();
        bool HasAttribute(CustomAttributeHandleCollection attributes, string requiredName)
        {
            foreach (var attribute in attributes)
            {
                var constructor = metadata.GetCustomAttribute(attribute).Constructor;
                var type = constructor.Kind == HandleKind.MemberReference
                    ? metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent
                    : metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType();
                var name = type.Kind switch
                {
                    HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)type).Name),
                    HandleKind.TypeDefinition => metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)type).Name),
                    _ => string.Empty,
                };
                if (name == requiredName)
                {
                    return true;
                }
            }

            return false;
        }

        bool Excluded(CustomAttributeHandleCollection attributes) => HasAttribute(attributes, "ExcludeFromCodeCoverageAttribute");

        string TypeName(TypeDefinitionHandle handle)
        {
            var type = metadata.GetTypeDefinition(handle);
            var parent = type.GetDeclaringType();
            var prefix = parent.IsNil ? metadata.GetString(type.Namespace) : TypeName(parent);
            return string.Concat(prefix, parent.IsNil ? (prefix.Length == 0 ? "" : ".") : "/", metadata.GetString(type.Name));
        }

        var excludedAccessors = new HashSet<MethodDefinitionHandle>();
        foreach (var handle in metadata.PropertyDefinitions)
        {
            var property = metadata.GetPropertyDefinition(handle);
            if (Excluded(property.GetCustomAttributes()))
            {
                var accessors = property.GetAccessors();
                excludedAccessors.Add(accessors.Getter);
                excludedAccessors.Add(accessors.Setter);
                excludedAccessors.UnionWith(accessors.Others);
            }
        }

        bool ExcludedMethod(MethodDefinitionHandle handle)
        {
            var method = metadata.GetMethodDefinition(handle);
            if (excludedAccessors.Contains(handle) || Excluded(method.GetCustomAttributes()))
            {
                return true;
            }

            var type = method.GetDeclaringType();
            while (!type.IsNil)
            {
                var definition = metadata.GetTypeDefinition(type);
                if (Excluded(definition.GetCustomAttributes()))
                {
                    return true;
                }

                type = definition.GetDeclaringType();
            }

            return false;
        }

        if (Excluded(metadata.GetAssemblyDefinition().GetCustomAttributes()))
        {
            return sources;
        }

        foreach (var handle in reader.MethodDebugInformation)
        {
            var method = reader.GetMethodDebugInformation(handle);
            var definition = MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(handle));
            var kickoff = method.GetStateMachineKickoffMethod();
            if (ExcludedMethod(definition) || (!kickoff.IsNil && ExcludedMethod(kickoff)))
            {
                continue;
            }

            var declaringType = metadata.GetMethodDefinition(definition).GetDeclaringType();
            var typeDefinition = metadata.GetTypeDefinition(declaringType);
            var typeName = TypeName(declaringType);
            var generated = typeName.Contains('<', StringComparison.Ordinal) ||
                HasAttribute(typeDefinition.GetCustomAttributes(), "CompilerGeneratedAttribute");
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

                    // Coverlet may omit individual local-function points. Use
                    // PDBs to identify sources and ordinary compiled classes,
                    // rather than inventing a one-to-one line requirement.
                    if (!generated)
                    {
                        lines.Add(typeName);
                    }
                }
            }
        }

        return sources;
    }

    private static void ValidateReportSources(string report, Dictionary<string, Dictionary<string, HashSet<string>>> expected, string repositoryRoot)
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
            var observed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
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

                    lines.Add((string)entry.Attribute("name")!);
                }
            }

            var missing = expected[name].Keys.Except(observed.Keys).Order(StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidDataException($"Report '{report}' is materially incomplete for '{name}': missing {missing.Length} executable source files from its Release PDB, including {string.Join(", ", missing.Take(8))}.");
            }

            foreach (var (source, requiredClasses) in expected[name])
            {
                var missingClasses = requiredClasses.Except(observed[source]).Order(StringComparer.Ordinal).ToArray();
                if (missingClasses.Length > 0)
                {
                    throw new InvalidDataException($"Report '{report}' is materially incomplete for '{source}': missing compiled classes from its Release DLL/PDB, including {string.Join(", ", missingClasses.Take(8))}.");
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
