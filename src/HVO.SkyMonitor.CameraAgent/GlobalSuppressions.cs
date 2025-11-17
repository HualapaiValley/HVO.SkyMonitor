using System.Diagnostics.CodeAnalysis;

// Sample and demo applications intentionally use simpler patterns for demonstration purposes
// ConfigureAwait(false) adds noise in demo code that doesn't benefit from it
[assembly: SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Sample/demo code runs in ASP.NET Core context where ConfigureAwait provides no benefit.", Scope = "module")]

// Demo applications log infrequently and prioritize code clarity over marginal performance gains
[assembly: SuppressMessage("Performance", "CA1848:For improved performance, use the LoggerMessage delegates", Justification = "Sample/demo logs infrequently; readability and simplicity prioritized.", Scope = "module")]

// This project intentionally makes some types public for demonstration and testing purposes
[assembly: SuppressMessage("Design", "CA1515:Make types internal", Justification = "Some types are intentionally public for demonstration and external testing scenarios.", Scope = "module")]

// CA1716: Error page component name intentionally uses reserved keyword for Blazor routing conventions
[assembly: SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Error page follows Blazor/ASP.NET Core naming conventions.", Scope = "type", Target = "~T:HVO.SkyMonitor.CameraAgent.Components.Pages.Error")]

// Broad exception catching in demo clock loops and error boundaries for resilience
[assembly: SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Demo error boundaries and clock loops intentionally catch all exceptions for app resilience.", Scope = "module")]

// CA1822: Navigation link and toolbar action properties could be static but are instance members for consistency with Blazor patterns
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Blazor component instance properties for consistency even when data is static.", Scope = "module")]

// CA1307: Explicit StringComparison added where needed; remaining cases use default ordinal comparison intentionally
[assembly: SuppressMessage("Globalization", "CA1307:Specify StringComparison for clarity", Justification = "Remaining path comparisons intentionally use default ordinal comparison.", Scope = "module")]

// CA1052: Program class with Main method cannot be static in .NET applications
[assembly: SuppressMessage("Design", "CA1052:Static holder types should be Static or NotInheritable", Justification = "Program class with Main method cannot be static per .NET requirements.", Scope = "type", Target = "~T:HVO.SkyMonitor.CameraAgent.Program")]

