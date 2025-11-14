namespace HVO.SkyMonitor.Common.Infrastructure.Diagnostics;

/// <summary>
/// Provides access to the current request's correlation ID.
/// </summary>
public interface ICorrelationIdAccessor
{
    /// <summary>
    /// Gets the correlation ID for the current request.
    /// </summary>
    string? CorrelationId { get; }
}
