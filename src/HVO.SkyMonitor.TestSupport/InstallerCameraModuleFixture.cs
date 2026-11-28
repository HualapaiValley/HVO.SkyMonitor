using System.Text.Json.Nodes;

namespace HVO.SkyMonitor.TestSupport;

public static class InstallerCameraModuleFixture
{
    /// <summary>Prepares a runtime test copy of an installer-generated camera module.</summary>
    public static void ConfigureSiloStorage(JsonNode cameraModule)
    {
        ArgumentNullException.ThrowIfNull(cameraModule);
        var steps = cameraModule["pipeline"]!["steps"]!.AsArray();
        JsonNode Storage(string id) => steps.Single(step => step!["id"]!.GetValue<string>() == id)!;

        Storage("LocalStorage")["options"] = new JsonObject { ["updateLatestFrame"] = true };
        Storage("ArchiveStorage")["options"] = new JsonObject
        {
            ["updateLatestFrame"] = false,
            ["publishArchive"] = true
        };
    }
}
