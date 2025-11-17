using System.Diagnostics.CodeAnalysis;

// ASP.NET Core and Blazor Server do not capture synchronization context; ConfigureAwait(false) adds noise without benefit
// Applied broadly except where we explicitly add ConfigureAwait(false) in library code for correctness
[assembly: SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "ASP.NET Core does not capture a synchronization context; ConfigureAwait adds noise without benefit.", Scope = "module")]

// Identity scaffolding and many UI components log infrequently; LoggerMessage delegates add complexity without meaningful performance gain
// For high-frequency logging in services, we do use LoggerMessage, but UI components prioritize readability
[assembly: SuppressMessage("Performance", "CA1848:For improved performance, use the LoggerMessage delegates", Justification = "Identity scaffolding and UI components log infrequently; readability takes precedence over marginal performance gains.", Scope = "module")]

// Identity scaffolding and EF Core migrations are generated code
[assembly: SuppressMessage("Performance", "CA1863:Cache a 'CompositeFormat'", Justification = "Identity scaffolding uses string.Format only on demand and is generated code.", Scope = "module")]
[assembly: SuppressMessage("Performance", "CA1308:Normalize strings to uppercase", Justification = "Identity scaffolding relies on lowercase secret formatting for compatibility.", Scope = "module")]
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Identity partial classes rely on instance members for dependency injection even when methods have no current instance usage.", Scope = "module")]
[assembly: SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Identity support types are activated via dependency injection and therefore instantiated at runtime.", Scope = "module")]
[assembly: SuppressMessage("Performance", "CA1861:Prefer 'static readonly' fields over constant array arguments", Justification = "EF Core migrations are generated code and remain unchanged for traceability.", Scope = "module")]

// CancellationToken forwarding - SmtpClient.SendMailAsync doesn't have an overload that accepts CancellationToken directly
// We use WaitAsync(cancellationToken) as a workaround which is the recommended pattern
[assembly: SuppressMessage("Reliability", "CA2016:Forward the CancellationToken parameter", Justification = "SmtpClient.SendMailAsync has no CancellationToken overload; we use WaitAsync as recommended pattern.", Scope = "member", Target = "~M:HVO.SkyMonitor.LogicHost.Services.SmtpEmailNotificationService.SendAsync(System.String,System.String,System.String,System.Threading.CancellationToken)~System.Threading.Tasks.Task")]

// Blazor components must remain public for routing and markup discovery
[assembly: SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Blazor components must remain public for routing and Razor usage.", Scope = "namespaceanddescendants", Target = "HVO.SkyMonitor.LogicHost.Components")]

// Diagnostics API contracts are part of the public HTTP surface
[assembly: SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Diagnostics request/response types are serialized over the public API.", Scope = "namespaceanddescendants", Target = "HVO.SkyMonitor.LogicHost.Models.Diagnostics")]

// Configuration option types participate in DI and controller constructors
[assembly: SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Options are injected into public controllers via IOptions<T> and must remain public.", Scope = "type", Target = "HVO.SkyMonitor.LogicHost.Configuration.MinioOptions")]
[assembly: SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Options are injected into public controllers via IOptions<T> and must remain public.", Scope = "type", Target = "HVO.SkyMonitor.LogicHost.Configuration.RedisOptions")]
[assembly: SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Options are injected into public controllers via IOptions<T> and must remain public.", Scope = "type", Target = "HVO.SkyMonitor.LogicHost.Configuration.SmtpOptions")]

// Identity data types are exposed through ASP.NET Core Identity services and public controllers
[assembly: SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Identity types participate in public controller signatures and DI.", Scope = "type", Target = "HVO.SkyMonitor.LogicHost.Data.ApplicationUser")]
[assembly: SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Identity types participate in public controller signatures and DI.", Scope = "type", Target = "HVO.SkyMonitor.LogicHost.Data.ApplicationDbContext")]
[assembly: SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Identity types participate in public controller signatures and DI.", Scope = "type", Target = "HVO.SkyMonitor.LogicHost.Data.AccountType")]
[assembly: SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Migrations are generated public types for EF Core scaffolding.", Scope = "namespaceanddescendants", Target = "HVO.SkyMonitor.LogicHost.Data.Migrations")]

// Authentication services surface in controller constructors for observability
[assembly: SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Authentication logging abstractions are injected into public controllers.", Scope = "type", Target = "HVO.SkyMonitor.LogicHost.Services.AuthenticationMetrics")]
[assembly: SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Authentication logging abstractions are injected into public controllers.", Scope = "type", Target = "HVO.SkyMonitor.LogicHost.Services.AuthenticationEventLogger")]
[assembly: SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Authentication logging abstractions are injected into public controllers.", Scope = "type", Target = "HVO.SkyMonitor.LogicHost.Services.IAuthenticationEventLogger")]

// Host builder needs Program to remain public for logging and testing hooks
[assembly: SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Program is instantiated by hosting and referenced by logging infrastructure.", Scope = "type", Target = "HVO.SkyMonitor.LogicHost.Program")]
