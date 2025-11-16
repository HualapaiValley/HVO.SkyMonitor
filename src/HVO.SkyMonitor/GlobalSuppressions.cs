using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "ASP.NET Core does not capture a synchronization context; ConfigureAwait adds noise without benefit.", Scope = "module")]
[assembly: SuppressMessage("Performance", "CA1848:For improved performance, use the LoggerMessage delegates", Justification = "Identity scaffolding and startup routines log infrequently; readability takes precedence.", Scope = "module")]
[assembly: SuppressMessage("Performance", "CA1863:Cache a 'CompositeFormat'", Justification = "Identity scaffolding uses string.Format only on demand and is generated code.", Scope = "module")]
[assembly: SuppressMessage("Performance", "CA1308:Normalize strings to uppercase", Justification = "Identity scaffolding relies on lowercase secret formatting for compatibility.", Scope = "module")]
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Identity partial classes rely on instance members for dependency injection even when methods have no current instance usage.", Scope = "module")]
[assembly: SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Identity support types are activated via dependency injection and therefore instantiated at runtime.", Scope = "module")]
[assembly: SuppressMessage("Performance", "CA1861:Prefer 'static readonly' fields over constant array arguments", Justification = "EF Core migrations are generated code and remain unchanged for traceability.", Scope = "module")]
