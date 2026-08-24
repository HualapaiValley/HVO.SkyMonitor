using System.Globalization;
using System.Text.Json;

namespace HVO.SkyMonitor.Deployment;

internal static class CommandLine
{
    public static InstallRequest Parse(string[] args)
    {
        if (args.Length < 2 || args[0] != "cameraagent" || args[1] != "install")
        {
            throw new InstallUsageException("Usage: hvo-skymonitor cameraagent install [options]");
        }

        string? configPath = null;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InstallUsageException($"Unexpected argument '{option}'.");
            }

            if (option is "--dry-run" or "--resume" or "--json" or "--generate-password" or "--acknowledge-plaintext-http" or "--no-download")
            {
                if (!flags.Add(option))
                {
                    throw new InstallUsageException($"Duplicate option '{option}'.");
                }
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InstallUsageException($"Option '{option}' requires a value.");
            }

            if (!values.TryAdd(option, args[++index]))
            {
                throw new InstallUsageException($"Duplicate option '{option}'.");
            }
        }

        if (values.Remove("--config", out configPath) && values.Count > 0)
        {
            throw new InstallUsageException("--config cannot be combined with value options.");
        }

        InstallRequest request;
        if (configPath is not null)
        {
            using var stream = SafeFileSystem.OpenRegularFileRead(configPath);
            request = JsonSerializer.Deserialize(stream, InstallRequestJsonContext.Default.InstallRequest)
                ?? throw new InstallUsageException("The installer configuration is empty.");
        }
        else
        {
            RejectUnknown(values.Keys);
            request = new InstallRequest
            {
                InstanceId = ParseGuid(Get(values, "--instance-id"), "--instance-id"),
                FriendlyName = RequireOrPrompt(values, "--friendly-name", "Friendly name: "),
                OwnerEmail = RequireOrPrompt(values, "--owner-email", "Owner email: "),
                BindAddress = Get(values, "--bind-address") ?? "127.0.0.1",
                Port = ParseInt(Get(values, "--port"), 5130, "--port"),
                ProductRoot = Get(values, "--product-root") ?? InstallRequest.DefaultProductRoot,
                CatalogBundle = Get(values, "--catalog-bundle"),
                CatalogManifest = Get(values, "--catalog-manifest"),
                CatalogIndex = Get(values, "--catalog-index"),
                CatalogVersion = Get(values, "--catalog-version"),
                AssetBaseUrl = Get(values, "--asset-base-url"),
                Channel = ParseChannel(Get(values, "--channel")),
                ImageReference = RequireOrPrompt(values, "--image-ref", "Immutable CameraAgent image digest or ID: "),
                ImageArchive = Get(values, "--image-archive"),
                ImageArchiveSha256 = Get(values, "--image-archive-sha256"),
                PasswordFile = Get(values, "--password-file"),
                LatitudeDegrees = ParseDouble(Get(values, "--latitude"), 0, "--latitude"),
                LongitudeDegrees = ParseDouble(Get(values, "--longitude"), 0, "--longitude"),
                ElevationMeters = ParseDouble(Get(values, "--elevation"), 0, "--elevation"),
                TimeZoneId = Get(values, "--time-zone") ?? "UTC"
            };
        }

        request = request with
        {
            DryRun = flags.Contains("--dry-run") || request.DryRun,
            Resume = flags.Contains("--resume") || request.Resume,
            Json = flags.Contains("--json") || request.Json,
            AcknowledgePlaintextHttp = flags.Contains("--acknowledge-plaintext-http") || request.AcknowledgePlaintextHttp,
            GeneratePassword = flags.Contains("--generate-password") || request.GeneratePassword,
            NoDownload = flags.Contains("--no-download") || request.NoDownload
        };
        request.Validate();
        return request;
    }

    private static void RejectUnknown(IEnumerable<string> options)
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "--instance-id", "--friendly-name", "--owner-email", "--bind-address", "--port",
            "--product-root", "--catalog-bundle", "--catalog-manifest", "--catalog-index", "--catalog-version", "--asset-base-url", "--channel", "--image-ref", "--image-archive",
            "--image-archive-sha256", "--password-file", "--latitude", "--longitude",
            "--elevation", "--time-zone"
        };
        var unknown = options.FirstOrDefault(option => !known.Contains(option));
        if (unknown is not null)
        {
            throw new InstallUsageException($"Unknown option '{unknown}'.");
        }
    }

    private static string RequireOrPrompt(
        IReadOnlyDictionary<string, string?> values,
        string option,
        string prompt)
    {
        var value = Get(values, option);
        if (value is not null)
        {
            return value;
        }
        if (Console.IsInputRedirected)
        {
            throw new InstallUsageException($"{option} is required.");
        }
        Console.Error.Write(prompt);
        return Console.ReadLine() is { Length: > 0 } entered
            ? entered
            : throw new InstallUsageException($"{option} is required.");
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string option)
        => values.TryGetValue(option, out var value) ? value : null;

    private static Guid? ParseGuid(string? value, string option)
        => value is null ? null : Guid.TryParseExact(value, "D", out var parsed)
            ? parsed
            : throw new InstallUsageException($"{option} must be a canonical UUID.");

    private static int ParseInt(string? value, int fallback, string option)
        => value is null ? fallback : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InstallUsageException($"{option} must be an integer.");

    private static double ParseDouble(string? value, double fallback, string option)
        => value is null ? fallback : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InstallUsageException($"{option} must be a number.");

    private static DistributionChannel ParseChannel(string? value)
        => value switch
        {
            null or "local" => DistributionChannel.Local,
            "stable" => DistributionChannel.Stable,
            "nightly" => DistributionChannel.Nightly,
            "prerelease" => DistributionChannel.Prerelease,
            _ => throw new InstallUsageException("--channel must be stable, nightly, prerelease, or local.")
        };
}
