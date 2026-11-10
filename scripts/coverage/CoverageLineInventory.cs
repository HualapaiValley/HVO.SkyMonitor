using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

/// <summary>A reviewed denominator, independent of the reports being gated.</summary>
internal sealed class CoverageLineInventory
{
    private readonly Dictionary<string, HashSet<int>> files = new(StringComparer.Ordinal);
    private const string Remedy = "Restore intact Coverlet inputs. After a legitimate source/toolchain change, regenerate scripts/coverage/development-v1-lines.json from a fresh complete Unit collection with scripts/coverage:line-inventory and review the diff; do not change coverage floors.";

    internal static CoverageLineInventory Load(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, "scripts", "coverage", "development-v1-lines.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var manifest = document.RootElement;
            using var sdk = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "global.json")));
            var packages = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Packages.props"));
            var collector = (string?)packages.Descendants("PackageVersion")
                .Single(entry => (string?)entry.Attribute("Include") == "coverlet.collector").Attribute("Version");
            if (manifest.GetProperty("schemaVersion").GetInt32() != 1 ||
                manifest.GetProperty("sdkVersion").GetString() != sdk.RootElement.GetProperty("sdk").GetProperty("version").GetString() ||
                manifest.GetProperty("collectorVersion").GetString() != collector ||
                !Hash(Path.Combine(repositoryRoot, "tests", "coverage.runsettings")).Equals(manifest.GetProperty("settingsSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Pinned coverage line inventory '{path}' does not match the collector settings/SDK/version. {Remedy}");
            }

            using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "coverage", "baseline.json")));
            using var policy = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "coverage", "policy.json")));
            var retired = policy.RootElement.GetProperty("retiredSourcePaths").EnumerateArray().Select(static entry => entry.GetString()).ToHashSet(StringComparer.Ordinal);
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var floor in baseline.RootElement.GetProperty("files").EnumerateObject())
            {
                var source = Path.Combine(repositoryRoot, floor.Name.StartsWith("tools/", StringComparison.Ordinal) ? floor.Name : "src/" + floor.Name);
                if (!File.Exists(source))
                {
                    if (!retired.Contains(floor.Name))
                    {
                        throw new InvalidDataException($"Required source '{floor.Name}' is absent and is not retired.");
                    }

                    continue;
                }

                if (!floor.Value.TryGetProperty("developmentV1LocalOnlyReason", out _))
                {
                    active.Add(floor.Name);
                }
            }

            var inventory = new CoverageLineInventory();
            foreach (var file in manifest.GetProperty("files").EnumerateObject())
            {
                if (!active.Contains(file.Name))
                {
                    throw new InvalidDataException($"Pinned line inventory contains non-active floor '{file.Name}'. {Remedy}");
                }

                if (!Hash(Path.Combine(repositoryRoot, "src", file.Name)).Equals(file.Value.GetProperty("sourceSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Pinned coverage line inventory is stale for '{file.Name}': source SHA-256 changed. {Remedy}");
                }

                var lines = file.Value.GetProperty("lines").EnumerateArray().Select(static line => line.GetInt32()).ToArray();
                if (lines.Length == 0 || lines.Any(static line => line <= 0) || !lines.SequenceEqual(lines.Distinct().Order()) ||
                    !inventory.files.TryAdd(file.Name, lines.ToHashSet()))
                {
                    throw new InvalidDataException($"Invalid pinned line set for '{file.Name}'. {Remedy}");
                }
            }

            if (!active.SetEquals(inventory.files.Keys))
            {
                throw new InvalidDataException($"Pinned line inventory does not cover every active canonical floor; missing [{string.Join(", ", active.Except(inventory.files.Keys))}]. {Remedy}");
            }

            Console.WriteLine($"Verified pinned emitted-line inventory for {inventory.files.Count} canonical files; SHA-256 {Hash(path)}.");
            return inventory;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"Invalid pinned coverage line inventory '{path}': {exception.Message} {Remedy}", exception);
        }
    }

    internal IEnumerable<string> PathsForOwner(string owner)
        => files.Keys.Where(path => path.StartsWith(owner + "/", StringComparison.Ordinal));

    internal void Validate(string report, string source, HashSet<int> classLines, HashSet<int> methodLines)
    {
        if (!files.TryGetValue(source, out var required))
        {
            return;
        }

        foreach (var (level, observed) in new[] { ("class-level", classLines), ("method-level", methodLines) })
        {
            if (!required.SetEquals(observed))
            {
                throw new InvalidDataException($"Report '{report}' has incomplete or unexpected {level} coverage for '{source}' against the pinned emitted-line inventory: missing [{string.Join(", ", required.Except(observed).Order().Take(12))}], unexpected [{string.Join(", ", observed.Except(required).Order().Take(12))}]. {Remedy}");
            }
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
