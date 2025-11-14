namespace HVO.SkyMonitor.Common.Infrastructure.Diagnostics;

public interface ICorrelationIdAccessor
{
    string? GetCorrelationId();
}
