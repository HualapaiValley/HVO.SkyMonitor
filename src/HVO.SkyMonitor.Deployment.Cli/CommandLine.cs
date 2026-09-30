using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal static class CommandLine
{
    /// <summary>
    /// The one place a non-production product root can be admitted. The variable is read here, where the
    /// process boundary is crossed, and never again: every request carries the answer it was parsed with, so
    /// validation cannot be changed by anything that mutates process state after a request exists. Only the
    /// ordinal value <c>1</c> admits, and an installer configuration file cannot reach this decision at all.
    /// </summary>
    private static bool TestProductRootAdmittedByEnvironment()
        => string.Equals(
            Environment.GetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT"),
            "1",
            StringComparison.Ordinal);

    public static DeploymentCommand ParseCommand(string[] args)
        => ParseCommand(args, TestProductRootAdmittedByEnvironment());

    /// <summary>Parses with admission stated explicitly, so a caller never has to mutate process state.</summary>
    internal static DeploymentCommand ParseCommand(string[] args, bool allowTestProductRoot)
    {
        if (args.Length >= 2 && args[0] == "cameraagent" && args[1] == "install")
        {
            return new InstallDeploymentCommand(Parse(args, allowTestProductRoot))
            {
                AllowTestProductRoot = allowTestProductRoot
            };
        }
        if (args.Length >= 2 && args[0] == "cameraagent" && args[1] == "recover-owner")
        {
            return ParseOwnerRecovery(args, allowTestProductRoot);
        }
        if (args.Length >= 2 && args[0] == "cameraagent" && args[1] == "preflight")
        {
            return ParseStatePreflight(args, allowTestProductRoot);
        }
        if (args.Length >= 2 && args[0] == "cameraagent" && args[1] == "reset-state")
        {
            return ParseStateReset(args, allowTestProductRoot);
        }
        return ParseLifecycle(args, allowTestProductRoot);
    }

    public static InstallRequest Parse(string[] args)
        => Parse(args, TestProductRootAdmittedByEnvironment());

    /// <summary>Parses with admission stated explicitly, so a caller never has to mutate process state.</summary>
    internal static InstallRequest Parse(string[] args, bool allowTestProductRoot)
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
                throw new InstallUsageException("Unexpected positional argument.");
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
                ImageReference = Get(values, "--image-manifest") is null && Get(values, "--image-index") is null
                    ? RequireOrPrompt(values, "--image-ref", "Immutable CameraAgent image digest or ID: ")
                    : Get(values, "--image-ref") ?? string.Empty,
                ImageArchive = Get(values, "--image-archive"),
                ImageArchiveSha256 = Get(values, "--image-archive-sha256"),
                ImageManifest = Get(values, "--image-manifest"),
                ImageIndex = Get(values, "--image-index"),
                ImageVersion = Get(values, "--image-version"),
                PasswordFile = Get(values, "--password-file"),
                LatitudeDegrees = ParseDouble(Get(values, "--latitude"), 0, "--latitude"),
                LongitudeDegrees = ParseDouble(Get(values, "--longitude"), 0, "--longitude"),
                ElevationMeters = ParseDouble(Get(values, "--elevation"), 0, "--elevation"),
                TimeZoneId = Get(values, "--time-zone") ?? "UTC",
                ReplayProfile = ParseReplayProfile(Get(values, "--replay-profile"))
            };
        }

        request = request with
        {
            AllowTestProductRoot = allowTestProductRoot,
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
            "--image-archive-sha256", "--image-manifest", "--image-index", "--image-version",
            "--password-file", "--latitude", "--longitude",
            "--elevation", "--time-zone", "--replay-profile"
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

    private static CameraAgentReplayProfile ParseReplayProfile(string? value)
        => value switch
        {
            null or "in-process" => CameraAgentReplayProfile.InProcess,
            "local-runner" => CameraAgentReplayProfile.LocalRunner,
            _ => throw new InstallUsageException("--replay-profile must be in-process or local-runner.")
        };

    private static LifecycleRequest ParseLifecycle(string[] args, bool allowTestProductRoot)
    {
        var (operation, optionOffset) = args switch
        {
            ["status", ..] => ((LifecycleOperationKind?)null, 1),
            ["cameraagent", "upgrade", ..] => (LifecycleOperationKind.Upgrade, 2),
            ["cameraagent", "rollback", ..] => (LifecycleOperationKind.Rollback, 2),
            ["cameraagent", "reinstall", ..] => (LifecycleOperationKind.Reinstall, 2),
            ["cameraagent", "uninstall", ..] => (LifecycleOperationKind.Uninstall, 2),
            ["cameraagent", "purge", ..] => (LifecycleOperationKind.Purge, 2),
            ["catalog", "install", ..] => (LifecycleOperationKind.CatalogInstall, 2),
            ["catalog", "select", ..] => (LifecycleOperationKind.CatalogSelect, 2),
            ["catalog", "rollback", ..] => (LifecycleOperationKind.CatalogRollback, 2),
            ["catalog", "gc", ..] => (LifecycleOperationKind.CatalogGarbageCollect, 2),
            _ => throw new InstallUsageException(
                "Usage: hvo-skymonitor status|cameraagent <install|recover-owner|preflight|reset-state|upgrade|rollback|reinstall|uninstall|purge>|catalog <install|select|rollback|gc> [options]")
        };
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = optionOffset; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InstallUsageException("Unexpected positional argument.");
            }
            if (option is "--dry-run" or "--resume" or "--restore-only" or "--json" or "--no-download" or "--migration-backward-compatible")
            {
                if (!flags.Add(option)) throw new InstallUsageException($"Duplicate option '{option}'.");
                continue;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(option, args[++index]))
            {
                throw new InstallUsageException($"Option '{option}' requires one non-duplicate value.");
            }
        }
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "--instance-id", "--confirm-instance-id", "--operation-id", "--product-root", "--image-ref", "--image-archive",
            "--image-archive-sha256", "--image-manifest", "--image-index", "--image-version",
            "--catalog-bundle", "--catalog-manifest", "--catalog-index",
            "--catalog-version", "--asset-base-url", "--channel"
        };
        var unknown = values.Keys.FirstOrDefault(option => !known.Contains(option));
        if (unknown is not null) throw new InstallUsageException($"Unknown option '{unknown}'.");
        var request = new LifecycleRequest(
            operation,
            ParseGuid(Get(values, "--instance-id"), "--instance-id"),
            Get(values, "--product-root") ?? InstallRequest.DefaultProductRoot,
            flags.Contains("--dry-run"),
            flags.Contains("--resume"),
            flags.Contains("--json"))
        {
            AllowTestProductRoot = allowTestProductRoot,
            RestoreOnly = flags.Contains("--restore-only"),
            RecoveryOperationId = ParseGuid(Get(values, "--operation-id"), "--operation-id"),
            ImageReference = Get(values, "--image-ref"),
            ImageArchive = Get(values, "--image-archive"),
            ImageArchiveSha256 = Get(values, "--image-archive-sha256"),
            ImageManifest = Get(values, "--image-manifest"),
            ImageIndex = Get(values, "--image-index"),
            ImageVersion = Get(values, "--image-version"),
            NoDownload = flags.Contains("--no-download"),
            MigrationBackwardCompatible = flags.Contains("--migration-backward-compatible"),
            ConfirmationInstanceId = ParseGuid(Get(values, "--confirm-instance-id"), "--confirm-instance-id"),
            CatalogBundle = Get(values, "--catalog-bundle"),
            CatalogManifest = Get(values, "--catalog-manifest"),
            CatalogIndex = Get(values, "--catalog-index"),
            CatalogVersion = Get(values, "--catalog-version"),
            AssetBaseUrl = Get(values, "--asset-base-url"),
            Channel = ParseChannel(Get(values, "--channel"))
        };
        if (operation == LifecycleOperationKind.CatalogSelect && request.CatalogVersion is null)
        {
            throw new InstallUsageException("catalog select requires --catalog-version.");
        }
        request.Validate();
        return request;
    }

    private static CameraAgentStatePreflightRequest ParseStatePreflight(string[] args, bool allowTestProductRoot)
    {
        var (values, flags) = ParseOptions(args, 2, ["--json", "--no-download"]);
        RejectUnknown(values, [
            "--instance-id", "--product-root", "--image-ref",
            "--image-manifest", "--image-index", "--image-version", "--asset-base-url", "--channel"
        ]);
        var request = new CameraAgentStatePreflightRequest(
            ParseGuid(Get(values, "--instance-id"), "--instance-id"),
            Get(values, "--product-root") ?? InstallRequest.DefaultProductRoot,
            Get(values, "--image-ref"),
            flags.Contains("--json"))
        {
            AllowTestProductRoot = allowTestProductRoot,
            ImageManifest = Get(values, "--image-manifest"),
            ImageIndex = Get(values, "--image-index"),
            ImageVersion = Get(values, "--image-version"),
            AssetBaseUrl = Get(values, "--asset-base-url"),
            // Presence, not the resolved value: an explicit --channel local must be rejected without a signed
            // release just as --channel stable is.
            Channel = Get(values, "--channel") is { } channel ? ParseChannel(channel) : null,
            NoDownload = flags.Contains("--no-download")
        };
        request.Validate();
        return request;
    }

    private static CameraAgentStateResetRequest ParseStateReset(string[] args, bool allowTestProductRoot)
    {
        var (values, flags) = ParseOptions(args, 2, ["--json", "--dry-run"]);
        RejectUnknown(values, ["--instance-id", "--confirm-instance-id", "--product-root"]);
        var request = new CameraAgentStateResetRequest(
            ParseGuid(Get(values, "--instance-id"), "--instance-id"),
            ParseGuid(Get(values, "--confirm-instance-id"), "--confirm-instance-id"),
            Get(values, "--product-root") ?? InstallRequest.DefaultProductRoot,
            flags.Contains("--dry-run"),
            flags.Contains("--json"))
        {
            AllowTestProductRoot = allowTestProductRoot
        };
        request.Validate();
        return request;
    }

    private static (Dictionary<string, string?> Values, HashSet<string> Flags) ParseOptions(
        string[] args,
        int optionOffset,
        IReadOnlyCollection<string> supportedFlags)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = optionOffset; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InstallUsageException("Unexpected positional argument.");
            }
            if (supportedFlags.Contains(option))
            {
                if (!flags.Add(option)) throw new InstallUsageException($"Duplicate option '{option}'.");
                continue;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(option, args[++index]))
            {
                throw new InstallUsageException($"Option '{option}' requires one non-duplicate value.");
            }
        }
        return (values, flags);
    }

    private static void RejectUnknown(Dictionary<string, string?> values, IReadOnlyCollection<string> known)
    {
        var unknown = values.Keys.FirstOrDefault(option => !known.Contains(option));
        if (unknown is not null) throw new InstallUsageException($"Unknown option '{unknown}'.");
    }

    private static OwnerRecoveryRequest ParseOwnerRecovery(string[] args, bool allowTestProductRoot)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InstallUsageException("Unexpected positional argument.");
            }

            if (option is "--generate-password" or "--resume" or "--json")
            {
                if (!flags.Add(option))
                {
                    throw new InstallUsageException($"Duplicate option '{option}'.");
                }
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(option, args[++index]))
            {
                throw new InstallUsageException($"Option '{option}' requires one non-duplicate value.");
            }
        }

        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "--instance-id", "--product-root", "--password-file"
        };
        var unknown = values.Keys.FirstOrDefault(option => !known.Contains(option));
        if (unknown is not null)
        {
            throw new InstallUsageException($"Unknown option '{unknown}'.");
        }

        var request = new OwnerRecoveryRequest(
            ParseGuid(Get(values, "--instance-id"), "--instance-id"),
            Get(values, "--product-root") ?? InstallRequest.DefaultProductRoot,
            Get(values, "--password-file"),
            flags.Contains("--generate-password"),
            flags.Contains("--resume"),
            flags.Contains("--json"))
        {
            AllowTestProductRoot = allowTestProductRoot
        };
        request.Validate();
        return request;
    }
}
