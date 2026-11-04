using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment;

internal sealed record PreflightRequest(string Inventory, string Mode, string RunId, string StateRoot, string EvidenceRoot, bool Json = false) : DeploymentCommand(Json)
{
    public void Validate()
    {
        if (Mode is not ("isolated" or "persistent") || !Path.IsPathFullyQualified(Inventory))
            throw new InstallUsageException("Preflight requires an inventory, isolated or persistent mode, safe run ID and absolute state/evidence roots.");
    }
    public static PreflightRequest Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal); var json = false;
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--json" && !json) { json = true; continue; }
            if (key is not ("--inventory" or "--mode" or "--run-id" or "--state-root" or "--evidence-root")) throw new InstallUsageException($"Unknown option '{key}'.");
            if (i + 1 >= args.Length || !options.TryAdd(key, args[++i])) throw new InstallUsageException($"Missing value or repeated option '{key}'.");
        }
        string Need(string key) => options.GetValueOrDefault(key) ?? throw new InstallUsageException(key + " is required.");
        var run = options.GetValueOrDefault("--run-id") ?? "preflight-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd't'HHmmss'z'", System.Globalization.CultureInfo.InvariantCulture);
        var output = Path.GetFullPath("TestResults/deploy/" + run);
        var result = new PreflightRequest(Path.GetFullPath(Need("--inventory")), Need("--mode"), run, options.GetValueOrDefault("--state-root") ?? output + "/state", options.GetValueOrDefault("--evidence-root") ?? output + "/evidence", json);
        result.Validate(); return result;
    }
}
