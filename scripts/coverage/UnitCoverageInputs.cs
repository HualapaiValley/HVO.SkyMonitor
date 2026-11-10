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
        ValidateCollectorSettings(repositoryRoot);
        var floors = floorPaths.ToHashSet(StringComparer.Ordinal);
        if (floors.Any(static path => path.StartsWith("tools/", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Development v1 inventory supports active src/ floor owners only; extend the DLL/PDB inventory before enforcing a tools/ floor.");
        }

        var lineInventory = CoverageLineInventory.Load(repositoryRoot);
        var owners = floors.Select(static path => path.Split('/')[0]).ToHashSet(StringComparer.Ordinal);
        var reportsByHash = reports.GroupBy(Hash).ToDictionary(static group => group.Key, static group => new Queue<string>(group));
        var inventories = new Dictionary<string, Dictionary<string, SourceInventory>>(StringComparer.Ordinal);
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
            var expected = new Dictionary<string, Dictionary<string, SourceInventory>>(StringComparer.Ordinal);
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
                var inventoryIdentity = Hash(assembly) + Hash(pdb);
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

            ValidateReportSources(report, expected, repositoryRoot, floors, lineInventory);
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

        foreach (var source in inventories.Values.SelectMany(static sources => sources)
            .Where(entry => floors.Contains(entry.Key) && entry.Value.SharedGeneratedLines.Count > 0)
            .GroupBy(static entry => entry.Key).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            var lines = source.SelectMany(static entry => entry.Value.SharedGeneratedLines).Distinct().Order();
            Console.WriteLine($"PDB inventory bound for '{source.Key}': state-machine lines shared with other generated methods [{string.Join(", ", lines)}] use the pinned class/method line inventory; PDB-only overlapping points can be legitimately absent.");
        }

        Console.WriteLine($"Verified {seenProjects.Count} Unit report identities and complete assembly/source/class inventories and statement lines (ordinary and state-machine methods) of floored files against Release portable PDBs.");
    }

    private static Dictionary<string, SourceInventory> ReadExecutableSources(string pdb, string repositoryRoot)
    {
        using var stream = File.OpenRead(pdb);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        var sources = new Dictionary<string, SourceInventory>(StringComparer.Ordinal);
        using var assemblyStream = File.OpenRead(Path.ChangeExtension(pdb, ".dll"));
        using var assembly = new PEReader(assemblyStream);
        var metadata = assembly.GetMetadataReader();
        var pdbId = new BlobContentId(reader.DebugMetadataHeader!.Id);
        var associated = assembly.ReadDebugDirectory().Any(entry =>
            entry.Type == DebugDirectoryEntryType.CodeView && entry.Stamp == pdbId.Stamp &&
            assembly.ReadCodeViewDebugDirectoryData(entry).Guid == pdbId.Guid &&
            assembly.ReadCodeViewDebugDirectoryData(entry).Age == 1);
        if (!associated)
        {
            throw new InvalidDataException($"Portable PDB '{pdb}' does not match its DLL debug identifier. Restore the matching Release DLL/PDB pair and rerun collection.");
        }

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

        // Iterator finally helpers can carry user source without their own
        // kickoff record. The MoveNext record associates the whole state type
        // with its original method, including those cleanup helpers.
        var stateMachineKickoffs = new Dictionary<TypeDefinitionHandle, MethodDefinitionHandle>();
        foreach (var handle in reader.MethodDebugInformation)
        {
            var kickoff = reader.GetMethodDebugInformation(handle).GetStateMachineKickoffMethod();
            if (!kickoff.IsNil)
            {
                var definition = MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(handle));
                stateMachineKickoffs[metadata.GetMethodDefinition(definition).GetDeclaringType()] = kickoff;
            }
        }

        // Coverlet can omit a source line shared by a state machine and a
        // separate generated lambda/local function (the two PDB records have
        // ambiguous ownership). Use the independent pinned emitted-line set
        // there instead of falsely requiring every generated PDB point. The
        // metadata boundary never exempts measured class/method records.
        var sharedGeneratedLines = new Dictionary<DocumentHandle, HashSet<int>>();
        foreach (var handle in reader.MethodDebugInformation)
        {
            var definition = MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(handle));
            var methodDefinition = metadata.GetMethodDefinition(definition);
            var type = methodDefinition.GetDeclaringType();
            if (stateMachineKickoffs.ContainsKey(type) ||
                (!TypeName(type).Contains('<', StringComparison.Ordinal) &&
                 !metadata.GetString(methodDefinition.Name).Contains('<', StringComparison.Ordinal) &&
                 !HasAttribute(metadata.GetTypeDefinition(type).GetCustomAttributes(), "CompilerGeneratedAttribute") &&
                 !HasAttribute(methodDefinition.GetCustomAttributes(), "CompilerGeneratedAttribute")))
            {
                continue;
            }

            var method = reader.GetMethodDebugInformation(handle);
            foreach (var point in method.GetSequencePoints().Where(static point => !point.IsHidden))
            {
                var document = point.Document.IsNil ? method.Document : point.Document;
                if (!sharedGeneratedLines.TryGetValue(document, out var lines))
                {
                    lines = [];
                    sharedGeneratedLines.Add(document, lines);
                }

                for (var line = point.StartLine; line <= point.EndLine; line++)
                {
                    lines.Add(line);
                }
            }
        }

        var excludedSections = new Dictionary<DocumentHandle, List<(int Start, int End)>>();
        foreach (var handle in reader.MethodDebugInformation)
        {
            var method = reader.GetMethodDebugInformation(handle);
            var definition = MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(handle));
            var kickoff = stateMachineKickoffs.GetValueOrDefault(metadata.GetMethodDefinition(definition).GetDeclaringType());
            if (!ExcludedMethod(definition) && (kickoff.IsNil || !ExcludedMethod(kickoff)))
            {
                continue;
            }

            foreach (var group in method.GetSequencePoints().Where(static point => !point.IsHidden)
                .GroupBy(point => point.Document.IsNil ? method.Document : point.Document))
            {
                if (!excludedSections.TryGetValue(group.Key, out var sections))
                {
                    sections = [];
                    excludedSections.Add(group.Key, sections);
                }

                sections.Add((group.Min(static point => point.StartLine), group.Max(static point => point.EndLine)));
            }
        }

        foreach (var handle in reader.MethodDebugInformation)
        {
            var method = reader.GetMethodDebugInformation(handle);
            var definition = MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(handle));
            var kickoff = stateMachineKickoffs.GetValueOrDefault(metadata.GetMethodDefinition(definition).GetDeclaringType());
            if (ExcludedMethod(definition) || (!kickoff.IsNil && ExcludedMethod(kickoff)))
            {
                continue;
            }

            var declaringType = metadata.GetMethodDefinition(definition).GetDeclaringType();
            var typeDefinition = metadata.GetTypeDefinition(declaringType);
            var typeName = TypeName(declaringType);
            var generated = typeName.Contains('<', StringComparison.Ordinal) ||
                HasAttribute(typeDefinition.GetCustomAttributes(), "CompilerGeneratedAttribute");
            var methodDefinition = metadata.GetMethodDefinition(definition);
            var ordinaryMethod = !generated &&
                !metadata.GetString(methodDefinition.Name).Contains('<', StringComparison.Ordinal) &&
                !HasAttribute(methodDefinition.GetCustomAttributes(), "CompilerGeneratedAttribute");
            foreach (var point in method.GetSequencePoints())
            {
                if (point.IsHidden)
                {
                    continue;
                }

                var document = point.Document.IsNil ? method.Document : point.Document;
                if (excludedSections.TryGetValue(document, out var sections) &&
                    sections.Any(section => point.StartLine >= section.Start && point.StartLine <= section.End))
                {
                    continue;
                }

                var path = reader.GetString(reader.GetDocument(document).Name).Replace('\\', '/');
                var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
                if (relative.StartsWith("src/", StringComparison.Ordinal) &&
                    !relative.Split('/').Any(static part => part is "obj" or "bin" or "Migrations"))
                {
                    if (!sources.TryGetValue(relative[4..], out var source))
                    {
                        source = new SourceInventory();
                        sources.Add(relative[4..], source);
                    }

                    if (!generated)
                    {
                        source.Classes.Add(typeName);
                    }

                    // State-machine MoveNext has a PDB link to its original
                    // async/iterator method. Its user source is measurable too.
                    // Coverlet emits every line of an instrumented point's span.
                    if (ordinaryMethod || !kickoff.IsNil)
                    {
                        for (var line = point.StartLine; line <= point.EndLine; line++)
                        {
                            if (!ordinaryMethod && sharedGeneratedLines.TryGetValue(document, out var shared) && shared.Contains(line))
                            {
                                source.SharedGeneratedLines.Add(line);
                            }
                            else
                            {
                                source.StatementLines.Add(line);
                            }
                        }
                    }
                }
            }
        }

        foreach (var source in sources.Values)
        {
            source.SharedGeneratedLines.ExceptWith(source.StatementLines);
        }

        return sources;
    }

    private static void ValidateReportSources(string report, Dictionary<string, Dictionary<string, SourceInventory>> expected, string repositoryRoot, HashSet<string> floors, CoverageLineInventory lineInventory)
    {
        var document = XDocument.Load(report);
        // Coverlet declares an exact emitted line count even for generated
        // methods whose PDB points are not all coverable. Preserve that input
        // inventory too; use unique source lines as the canonical denominator.
        var emittedLines = document.Descendants("class")
            .SelectMany(entry => (entry.Element("lines")?.Elements("line") ?? [])
                .Select(line => ((string?)entry.Attribute("filename"), (int)line.Attribute("number")!)))
            .Distinct().Count();
        if (!int.TryParse((string?)document.Root?.Attribute("lines-valid"), out var declaredLines) || declaredLines != emittedLines)
        {
            throw new InvalidDataException($"Report '{report}' has inconsistent emitted line inventory: declared lines-valid='{(string?)document.Root?.Attribute("lines-valid")}', observed {emittedLines} unique source lines. Restore the complete original Coverlet report; generated lines must not silently disappear.");
        }

        var packages = document.Descendants("package").ToArray();
        var names = packages.Select(static package => (string?)package.Attribute("name") ?? string.Empty).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || !expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(names))
        {
            throw new InvalidDataException($"Report '{report}' has incomplete assembly inventory; expected [{string.Join(", ", expected.Keys)}], observed [{string.Join(", ", names)}].");
        }

        foreach (var package in packages)
        {
            var name = (string)package.Attribute("name")!;
            var observed = new Dictionary<string, SourceInventory>(StringComparer.Ordinal);
            var observedMethods = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
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

                if (!observedMethods.TryGetValue(relative, out var methodLines))
                {
                    methodLines = [];
                    observedMethods.Add(relative, methodLines);
                }

                foreach (var line in entry.Elements("methods").Elements("method").Elements("lines").Elements("line"))
                {
                    methodLines.Add((int)line.Attribute("number")!);
                }

                foreach (var line in entry.Element("lines")?.Elements("line") ?? [])
                {
                    if (!observed.TryGetValue(relative, out var source))
                    {
                        source = new SourceInventory();
                        observed.Add(relative, source);
                    }

                    source.Classes.Add((string)entry.Attribute("name")!);
                    source.StatementLines.Add((int)line.Attribute("number")!);
                }
            }

            var missing = expected[name].Keys.Except(observed.Keys).Order(StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidDataException($"Report '{report}' is materially incomplete for '{name}': missing {missing.Length} executable source files from its Release PDB, including {string.Join(", ", missing.Take(8))}.");
            }

            foreach (var (source, required) in expected[name])
            {
                var missingClasses = required.Classes.Except(observed[source].Classes).Order(StringComparer.Ordinal).ToArray();
                if (missingClasses.Length > 0)
                {
                    throw new InvalidDataException($"Report '{report}' is materially incomplete for '{source}': missing compiled classes from its Release DLL/PDB, including {string.Join(", ", missingClasses.Take(8))}.");
                }

                if (floors.Contains(source))
                {
                    var missingLines = required.StatementLines.Except(observed[source].StatementLines).Order().ToArray();
                    if (missingLines.Length > 0)
                    {
                        throw new InvalidDataException($"Report '{report}' is materially incomplete for floored file '{source}': missing {missingLines.Length} ordinary/state-machine statement lines from its Release DLL/PDB, including {string.Join(", ", missingLines.Take(12))}. Restore complete XPlat collection; omitted lines cannot be counted as covered.");
                    }
                }
            }

            // Pin every canonical floor owned by this included package even if
            // the PDB source inventory failed to discover that particular file.
            foreach (var source in lineInventory.PathsForOwner(name))
            {
                lineInventory.Validate(report, source, observed.GetValueOrDefault(source)?.StatementLines ?? [], observedMethods.GetValueOrDefault(source) ?? []);
            }
        }
    }

    // Fail explicitly when collector exclusions outgrow the inventory model.
    // This guard runs in the coverage stage, never adds Preflight work, and
    // prevents a future setting from silently disagreeing with the PDB scan.
    private static void ValidateCollectorSettings(string repositoryRoot)
    {
        var document = XDocument.Load(Path.Combine(repositoryRoot, "tests", "coverage.runsettings"));
        var configurations = document.Descendants("DataCollector")
            .Where(static collector => (string?)collector.Attribute("friendlyName") == "XPlat Code Coverage")
            .Elements("Configuration").ToArray();
        if (configurations.Length != 1)
        {
            throw new InvalidDataException("Expected one canonical XPlat coverage configuration for the DLL/PDB inventory.");
        }

        var supported = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Format"] = "cobertura",
            ["Exclude"] = "[*.Tests]*,[*.IntegrationTests]*,[HVO.SkyMonitor.TestSupport]*,[HVO.SkyMonitor.LogicHost.TestInfrastructure]*",
            ["ExcludeByFile"] = "**/obj/**,**/bin/**,**/Migrations/**",
            ["ExcludeByAttribute"] = "ExcludeFromCodeCoverage",
            ["IncludeTestAssembly"] = "false",
        };
        var settings = configurations[0].Elements().Where(static setting => setting.Name.LocalName is not ("Include" or "SingleHit")).ToArray();
        if (settings.Length != supported.Count || settings.Select(static setting => setting.Name.LocalName).Distinct(StringComparer.Ordinal).Count() != settings.Length ||
            settings.Any(setting => !supported.TryGetValue(setting.Name.LocalName, out var value) || setting.Value.Trim() != value))
        {
            throw new InvalidDataException("Unsupported canonical coverage settings for the DLL/PDB inventory (exclusions/attributes/options changed). Update and verify UnitCoverageInputs against genuine Coverlet output before changing collector scope.");
        }
    }

    private sealed class SourceInventory
    {
        internal HashSet<string> Classes { get; } = new(StringComparer.Ordinal);
        internal HashSet<int> StatementLines { get; } = [];
        internal HashSet<int> SharedGeneratedLines { get; } = [];
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
