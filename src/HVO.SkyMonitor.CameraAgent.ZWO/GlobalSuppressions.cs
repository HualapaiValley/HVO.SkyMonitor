using System.Diagnostics.CodeAnalysis;

// ZWO Camera Agent is similar to Simulator - demo/testing application with simpler patterns
// ConfigureAwait(false) adds noise in demo code that doesn't benefit from it
[assembly: SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Demo/ZWO agent runs in ASP.NET Core context where ConfigureAwait provides no benefit.", Scope = "module")]

// Demo applications log infrequently and prioritize code clarity over marginal performance gains
[assembly: SuppressMessage("Performance", "CA1848:For improved performance, use the LoggerMessage delegates", Justification = "Demo/ZWO agent logs infrequently; readability and simplicity prioritized.", Scope = "module")]

// ZWO agent intentionally makes some types public for demonstration and testing purposes
[assembly: SuppressMessage("Design", "CA1515:Make types internal", Justification = "ZWO agent types are intentionally public for demonstration and external testing scenarios.", Scope = "module")]

// CA1716: Error page component name intentionally uses reserved keyword for Blazor routing conventions
[assembly: SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Error page follows Blazor/ASP.NET Core naming conventions.", Scope = "type", Target = "~T:HVO.SkyMonitor.CameraAgent.ZWO.Components.Pages.Error")]

// Broad exception catching in demo clock loops and error boundaries for resilience
[assembly: SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Demo error boundaries and clock loops intentionally catch all exceptions for app resilience.", Scope = "module")]

// CA1822: Navigation link and toolbar action properties could be static but are instance members for consistency with Blazor patterns
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Blazor component instance properties for consistency even when data is static.", Scope = "module")]

// CA1307: Explicit StringComparison added where needed; remaining cases use default ordinal comparison intentionally
[assembly: SuppressMessage("Globalization", "CA1307:Specify StringComparison for clarity", Justification = "Remaining path comparisons intentionally use default ordinal comparison.", Scope = "module")]

// CA5394: Random is used for demo weather data, not security
[assembly: SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Random used only for demo weather data generation, not security purposes.", Scope = "module")]

// CA5404: ValidateAudience disabled intentionally for development/testing scenarios in ZWO agent
[assembly: SuppressMessage("Security", "CA5404:Do not disable token validation checks", Justification = "ZWO agent is a development/testing tool where flexible token validation is appropriate.", Scope = "module")]

// CA1052: Program class with Main method cannot be static in .NET applications
[assembly: SuppressMessage("Design", "CA1052:Static holder types should be Static or NotInheritable", Justification = "Program class with Main method cannot be static per .NET requirements.", Scope = "type", Target = "~T:HVO.SkyMonitor.CameraAgent.ZWO.Program")]

// SampleStatusService validates principal properties inline; null validation happens via framework model binding
[assembly: SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "Framework ensures principal is non-null through authentication middleware.", Scope = "member", Target = "~M:HVO.SkyMonitor.CameraAgent.ZWO.Services.SampleStatusService.GetAuthenticatedStatus(System.Security.Claims.ClaimsPrincipal)~HVO.Result{HVO.SkyMonitor.CameraAgent.ZWO.Models.v1.SampleAuthenticatedResponse}")]
