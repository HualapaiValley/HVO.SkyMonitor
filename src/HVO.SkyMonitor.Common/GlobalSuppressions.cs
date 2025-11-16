using System.Diagnostics.CodeAnalysis;

// HvoServiceExceptionHandler logs exceptions infrequently during unhandled exception scenarios
// LoggerMessage delegates would add complexity for minimal gain in error handling paths
[assembly: SuppressMessage("Performance", "CA1848:For improved performance, use the LoggerMessage delegates", Justification = "Exception handler logs infrequently during error scenarios; readability prioritized.", Scope = "type", Target = "~T:HVO.SkyMonitor.Common.Infrastructure.Diagnostics.HvoServiceExceptionHandler")]
