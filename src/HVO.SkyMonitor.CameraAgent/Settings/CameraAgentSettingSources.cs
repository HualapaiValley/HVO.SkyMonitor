using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.KeyPerFile;

namespace HVO.SkyMonitor.CameraAgent.Settings;

/// <summary>Where a configuration key's value in effect comes from, and whether it outranks the settings file.</summary>
/// <param name="Overridden">
/// True when a source with higher precedence than the operator settings file supplies the key, so a value saved in the
/// file has no effect until that source no longer sets it.
/// </param>
internal sealed record CameraAgentConfigurationSource(CameraAgentSettingSource Source, bool Overridden);

/// <summary>Finds which configuration layer supplies a key, reading the live providers in precedence order.</summary>
internal sealed class CameraAgentSettingSources
{
    private readonly IConfigurationRoot? _configuration;
    private readonly Func<IConfigurationProvider, bool> _isSettingsFile;

    /// <param name="configuration">The host configuration; a source is reported only when it is a configuration root.</param>
    /// <param name="isSettingsFile">Recognises the provider that loads the operator settings file.</param>
    public CameraAgentSettingSources(IConfiguration configuration, Func<IConfigurationProvider, bool> isSettingsFile)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration as IConfigurationRoot;
        _isSettingsFile = isSettingsFile ?? throw new ArgumentNullException(nameof(isSettingsFile));
    }

    public CameraAgentConfigurationSource Find(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_configuration is null)
        {
            return new(CameraAgentSettingSource.Unavailable, false);
        }

        var providers = _configuration.Providers.ToList();
        var settingsFile = providers.FindIndex(provider => _isSettingsFile(provider));
        for (var index = providers.Count - 1; index >= 0; index--)
        {
            if (providers[index].TryGet(key, out _))
            {
                return new(Classify(providers[index]), settingsFile >= 0 && index > settingsFile);
            }
        }

        return new(CameraAgentSettingSource.Default, false);
    }

    private CameraAgentSettingSource Classify(IConfigurationProvider provider) => provider switch
    {
        _ when _isSettingsFile(provider) => CameraAgentSettingSource.SettingsFile,
        EnvironmentVariablesConfigurationProvider => CameraAgentSettingSource.Environment,
        KeyPerFileConfigurationProvider => CameraAgentSettingSource.SecretsDirectory,
        CommandLineConfigurationProvider => CameraAgentSettingSource.CommandLine,
        JsonConfigurationProvider => CameraAgentSettingSource.AppSettings,
        _ => CameraAgentSettingSource.Host
    };
}
