using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace HVO.SkyMonitor.Common.Configuration;

/// <summary>
/// The operator's writable <c>appsettings.local.json</c>, loaded over the image's own settings files. It is the one
/// persisted place an operator changes a deployed host's settings, by hand or through the operator UI, and it
/// survives image upgrades because it lives in preserved instance storage rather than in the image.
/// </summary>
/// <remarks>
/// The file sits directly above <c>appsettings.{Environment}.json</c>, so installer key-per-file settings,
/// environment variables, and the command line still take precedence; <see cref="FindOverriddenKeys"/> names the
/// keys one of them supplies, because editing those in the file would have no effect.
/// </remarks>
public sealed class DeploymentLocalSettings
{
    /// <summary>Names the settings file explicitly, for a development host or a test.</summary>
    public const string PathVariable = "HVO_SETTINGS_FILE";

    public const string FileName = "appsettings.local.json";

    private readonly IConfigurationRoot _configuration;
    private readonly JsonConfigurationSource _source;
    private volatile bool _initialLoadComplete;

    private DeploymentLocalSettings(string filePath, IConfigurationRoot configuration)
    {
        FilePath = filePath;
        _configuration = configuration;
        _source = new JsonConfigurationSource
        {
            Path = filePath,
            Optional = true,
            ReloadOnChange = true,
            OnLoadException = HandleLoadException
        };
        _source.ResolveFileProvider();
    }

    /// <summary>The absolute path of the settings file, whether or not it exists yet.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Adds the settings file when this host has one: the path <see cref="PathVariable"/> names, else the installer
    /// deployment's <c>App_Data</c> mount. A host with neither, such as a plain <c>dotnet run</c> or a test host,
    /// loads no file, so a stray one under the content root never changes its settings.
    /// </summary>
    public static DeploymentLocalSettings? AddConfiguredFile(ConfigurationManager configuration, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var path = ResolvePath(configuration, contentRootPath);
        if (path is null)
        {
            return null;
        }
        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            throw new InvalidOperationException("The local settings file must be in an existing directory.");
        }

        var settings = new DeploymentLocalSettings(path, configuration);
        var sources = configuration.Sources;
        var index = sources.Count;
        for (var position = sources.Count - 1; position >= 0; position--)
        {
            if (sources[position] is JsonConfigurationSource { Path: { } sourcePath } &&
                Path.GetFileName(sourcePath).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            {
                index = position + 1;
                break;
            }
        }

        // A malformed file stops the host at startup, where the operator sees it. Afterwards a bad hand edit leaves
        // the file's keys unset until it is fixed, as for any reloadable settings file, rather than stopping a
        // running camera over a typo; the operator UI reports the file as unreadable.
        sources.Insert(index, settings._source);
        settings._initialLoadComplete = true;
        return settings;
    }

    /// <summary>The configured settings path, or null when this host loads none.</summary>
    public static string? ResolvePath(IConfiguration configuration, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration[PathVariable];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
            {
                throw new InvalidOperationException($"{PathVariable} must be an absolute path.");
            }
            return Path.GetFullPath(configured);
        }

        // The installer's Compose model predates this file and is pinned for every existing instance, so its
        // key-per-file directory marks the deployment and App_Data, already a preserved writable mount, holds it.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable)))
        {
            return null;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        return Path.Combine(Path.GetFullPath(contentRootPath), "App_Data", FileName);
    }

    /// <summary>Reads the file again now, so a value just written applies without waiting for the file watcher.</summary>
    public void Reload()
    {
        foreach (var provider in _configuration.Providers)
        {
            if (provider is JsonConfigurationProvider jsonProvider && ReferenceEquals(jsonProvider.Source, _source))
            {
                jsonProvider.Load();
                return;
            }
        }
    }

    /// <summary>The keys a source with higher precedence than this file supplies.</summary>
    public IReadOnlyList<string> FindOverriddenKeys(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var providers = _configuration.Providers.ToList();
        var own = providers.FindIndex(provider =>
            provider is JsonConfigurationProvider jsonProvider && ReferenceEquals(jsonProvider.Source, _source));
        if (own < 0)
        {
            return [];
        }
        var later = providers.Skip(own + 1).ToList();
        return [.. keys.Where(key => later.Any(provider => provider.TryGet(key, out _)))];
    }

    private void HandleLoadException(FileLoadExceptionContext context)
    {
        if (!_initialLoadComplete)
        {
            return;
        }
        context.Ignore = true;
    }
}
