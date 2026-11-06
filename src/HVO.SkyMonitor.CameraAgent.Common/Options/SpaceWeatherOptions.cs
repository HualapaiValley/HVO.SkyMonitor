using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Common.Options;

/// <summary>Optional local SWPC acquisition. Provider policy and retention are fixed in code.</summary>
public sealed class SpaceWeatherOptions : IValidatableObject
{
    /// <summary>The configuration section for this feature.</summary>
    public const string SectionName = "CameraAgent:SpaceWeather";

    /// <summary>Permits requests; disabled by default while durable facts remain readable.</summary>
    public bool Enabled { get; init; }

    /// <summary>The distinct approved core products; OVATION is not representable.</summary>
    public IReadOnlyList<SwpcProduct> Products { get => products; init => products = value; }

    private IReadOnlyList<SwpcProduct> products = Enum.GetValues<SwpcProduct>();

    /// <summary>
    /// Replaces the default product set when configuration explicitly supplies Products. The ordinary
    /// collection binder appends defaults and can silently ignore invalid enum tokens. A fresh strict
    /// bind of this field preserves exact configured membership and rejects unknown names such as Ovation.
    /// This runs before nested option validation and changes no other option's binding behavior.
    /// </summary>
    internal void BindProducts(IConfiguration configuration)
    {
        var configured = configuration.GetSection(SectionName).GetChildren()
            .FirstOrDefault(static section => string.Equals(section.Key, nameof(Products), StringComparison.OrdinalIgnoreCase));
        if (configured is not null)
            products = configured.Get<SwpcProduct[]>(static binder => binder.ErrorOnUnknownConfiguration = true) ?? [];
    }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Products is null || Products.Count == 0 || Products.Distinct().Count() != Products.Count ||
            Products.Any(static product => !Enum.IsDefined(product)))
            yield return new ValidationResult("SpaceWeather products must be nonempty, distinct approved products.", [nameof(Products)]);
    }
}
