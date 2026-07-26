using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed class EnvironmentalSourceFactory(
    IServiceProvider serviceProvider,
    IEnumerable<EnvironmentalSourceRegistration> registrations,
    IConfiguration? hostConfiguration = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly Dictionary<string, EnvironmentalSourceRegistration> _registrations = registrations
        .ToDictionary(static registration => registration.Alias, StringComparer.Ordinal);

    public IReadOnlyList<IEnvironmentalSource> Create(EnvironmentalAcquisitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sources = new List<IEnvironmentalSource>(options.Sources.Count);
        foreach (var configuration in options.Sources)
        {
            if (!_registrations.TryGetValue(configuration.Type, out var registration))
            {
                throw new InvalidOperationException(
                    $"Environmental source type '{configuration.Type}' is not registered.");
            }
            var sourceOptions = ResolveOptions(configuration, registration);
            var canonicalOptions = JsonSerializer.SerializeToElement(
                sourceOptions, registration.OptionsType, SerializerOptions);
            var source = ActivatorUtilities.CreateInstance(
                serviceProvider,
                registration.SourceType,
                configuration.ToDescriptor(canonicalOptions),
                sourceOptions) as IEnvironmentalSource ?? throw new InvalidOperationException(
                    $"Environmental source type '{configuration.Type}' does not implement {nameof(IEnvironmentalSource)}.");
            sources.Add(source);
        }
        return sources;
    }

    private object ResolveOptions(
        EnvironmentalSourceConfiguration source,
        EnvironmentalSourceRegistration registration)
    {
        if (source.Options.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize(source.Options, registration.OptionsType, SerializerOptions)
                ?? throw new InvalidOperationException(
                    $"Environmental source '{source.Id}' options could not be parsed.");
        }
        var sourceSection = hostConfiguration?
            .GetSection("CameraAgent:EnvironmentalAcquisition:Sources")
            .GetChildren()
            .SingleOrDefault(section => string.Equals(section["Id"], source.Id, StringComparison.Ordinal));
        var optionsSection = sourceSection?.GetSection("Options");
        if (optionsSection is null || !optionsSection.Exists())
        {
            throw new InvalidOperationException(
                $"Environmental source '{source.Id}' requires an options object.");
        }
        return optionsSection.Get(registration.OptionsType, binder => binder.ErrorOnUnknownConfiguration = true)
            ?? throw new InvalidOperationException(
                $"Environmental source '{source.Id}' options could not be bound.");
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
