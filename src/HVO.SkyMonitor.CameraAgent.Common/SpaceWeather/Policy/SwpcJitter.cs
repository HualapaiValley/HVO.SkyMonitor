using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;

/// <summary>P2: provides one bounded fractional base-interval adjustment per admitted attempt.</summary>
public interface ISwpcJitter
{
    /// <summary>Returns a finite fraction in [-0.2, +0.2] for the product.</summary>
    double NextFraction(SwpcProduct product);
}

/// <summary>Zero jitter for conformance, or uniform jitter from an injected random source.</summary>
public static class SwpcJitter
{
    /// <summary>Proposed v1 uniform jitter bound, applied only to base-interval schedules (P2).</summary>
    public const double JitterFraction = 0.2;

    /// <summary>A stateless source that always returns zero.</summary>
    public static ISwpcJitter None { get; } = new ZeroJitter();

    /// <summary>Throws before a store write if the fraction is non-finite or outside the proposed bounds.</summary>
    public static void Validate(double fraction)
    {
        if (!double.IsFinite(fraction) || fraction is < -JitterFraction or > JitterFraction)
            throw new ArgumentOutOfRangeException(nameof(fraction));
    }

    private sealed class ZeroJitter : ISwpcJitter
    {
        public double NextFraction(SwpcProduct product) => 0;
    }
}

/// <summary>P2: uniform scheduling jitter over the proposed [-0.2, +0.2] range.</summary>
public sealed class SwpcUniformJitter : ISwpcJitter
{
    private readonly Random random;

    /// <summary>Uses the caller's random source; the runner serializes its samples.</summary>
    public SwpcUniformJitter(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        this.random = random;
    }

    /// <inheritdoc />
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness",
        Justification = "Cadence jitter does not make security decisions.")]
    public double NextFraction(SwpcProduct product) => (random.NextDouble() * 2 - 1) * SwpcJitter.JitterFraction;
}
