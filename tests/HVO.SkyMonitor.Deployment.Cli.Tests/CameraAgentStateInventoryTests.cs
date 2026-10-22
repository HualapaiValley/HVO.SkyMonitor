using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using Microsoft.Data.Sqlite;
using ContractReplayProfile = HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The installer's snapshot inventory against the previous-release store fixture (#1314 R8). The CameraAgent side
/// (<c>PreviousReleaseStoreReadabilityTests</c>) proves the runtime keeps its SQLite stores at exactly the fixture's
/// locations, plus the ones that release declares it adds, for the fixture's storage roots; this side proves the
/// installer finds every one of those fixture locations from configuration alone, so a store the runtime adds without
/// declaring it, or moves, fails one side or the other. The orbital elements, space-weather and calibration-family stores
/// are locations the snapshot deliberately leaves out (<see cref="CameraAgentStateLayout.SqliteStores"/>), so they are
/// never expected here.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentStateInventoryTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Stores_ForThePreviousReleaseStorageRoots_AreExactlyThePreviousReleaseStoreLocations()
    {
        var (storageRoots, locations) = LoadPreviousRelease();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-inventory-{Guid.NewGuid():N}");
        try
        {
            var paths = InstallationPaths.Create(root, Guid.NewGuid(), "catalog");
            Directory.CreateDirectory(paths.ConfigRoot);
            // The generated deployment configuration names some storage roots; a capture-schedule revision names the rest.
            var generated = CameraConfiguration.Generate(Request(), Guid.Parse("65c0dd43-6490-4f10-b972-0f68c79e45a8"));
            File.WriteAllText(Path.Combine(paths.ConfigRoot, "camera-module.json"), generated.Json);
            var generatedRoots = StorageRootsOf(generated.Json);
            CollectionAssert.IsSubsetOf(generatedRoots, storageRoots.ToArray(),
                "The previous-release fixture does not cover the storage roots the installer generates.");
            WriteRevision(paths, storageRoots.Except(generatedRoots, StringComparer.Ordinal).ToArray());

            var stores = CameraAgentStateInventory.Stores(paths, ContractReplayProfile.InProcess);

            CollectionAssert.AreEqual(
                locations.Where(static location => location != CameraAgentStateLayout.OrbitalElementsStore &&
                                                   location != CameraAgentStateLayout.SpaceWeatherStore &&
                                                   location != CameraAgentStateLayout.CalibrationFamiliesStore).ToArray(),
                stores.Order(StringComparer.Ordinal).ToArray(),
                "The installer's snapshot inventory does not name exactly the store locations the CameraAgent keeps.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static (IReadOnlyList<string> StorageRoots, string[] Locations) LoadPreviousRelease()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "Fixtures", "previous-release", "manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var fixture = document.RootElement;
        Assert.AreEqual(1, fixture.GetProperty("schemaVersion").GetInt32());
        return (
            [.. fixture.GetProperty("storageRoots").EnumerateArray().Select(static item => item.GetString()!)],
            [.. fixture.GetProperty("stores").EnumerateArray().Select(static store => store.GetProperty("location").GetString()!).Order(StringComparer.Ordinal)]);
    }

    private static string[] StorageRootsOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        var roots = new List<string>();
        Collect(document.RootElement);
        return [.. roots.Distinct(StringComparer.Ordinal)];

        void Collect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "storageRoot" && property.Value.ValueKind == JsonValueKind.String) roots.Add(property.Value.GetString()!);
                    else Collect(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Collect(item);
            }
        }
    }

    private static void WriteRevision(InstallationPaths paths, string[] storageRoots)
    {
        var journal = CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        var profile = JsonSerializer.Serialize(new
        {
            processingSteps = storageRoots.Select(static storageRoot => new { type = "FileStorage", options = new { storageRoot } }).ToArray()
        });
        using var connection = new SqliteConnection($"Data Source={journal};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE capture_schedule_revisions(revision_id TEXT PRIMARY KEY, revision_number INTEGER NOT NULL UNIQUE, profile_json BLOB NOT NULL);" +
            "INSERT INTO capture_schedule_revisions VALUES('r1', 1, CAST($profile AS BLOB));";
        command.Parameters.AddWithValue("$profile", profile);
        command.ExecuteNonQuery();
    }

    private static InstallRequest Request() => new()
    {
        FriendlyName = "Camera",
        OwnerEmail = "admin@example.test",
        CatalogBundle = "/srv/catalog.bundle",
        ImageReference = $"sha256:{new string('a', 64)}",
        LatitudeDegrees = 35.2,
        LongitudeDegrees = -114.1,
        ElevationMeters = 800,
        TimeZoneId = "America/Phoenix"
    };
}
