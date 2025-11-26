using System.Diagnostics.CodeAnalysis;

// Blazor Server components must resume on the captured synchronization context to update the SignalR circuit, so ConfigureAwait(false) is intentionally avoided.
[assembly: SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Blazor Server components rely on the current synchronization context to dispatch UI updates.", Scope = "module")]

// Logging volume in this project is low and favors readability over LoggerMessage delegate plumbing.
[assembly: SuppressMessage("Performance", "CA1848:For improved performance, use the LoggerMessage delegates", Justification = "CameraAgent logs are low-frequency operator diagnostics where readability is preferred.", Scope = "module")]

// Controllers and Razor components must remain public for ASP.NET Core discovery and routing.
[assembly: SuppressMessage("Design", "CA1515:Make types internal", Justification = "Public visibility is required so MVC/Web API and Razor components can be activated by the framework.", Scope = "module")]

// Error page component name intentionally matches routing conventions even though it overlaps with a C# keyword.
[assembly: SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Error page follows ASP.NET Core naming conventions for routing.", Scope = "type", Target = "~T:HVO.SkyMonitor.CameraAgent.Components.Pages.Error")]

// Some Razor component helpers intentionally remain instance members for binding consistency even when they could be static.
[assembly: SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Component members participate in binding/state and follow Razor conventions.", Scope = "module")]

// Remaining path comparisons intentionally rely on the framework defaults (ordinal) for readability.
[assembly: SuppressMessage("Globalization", "CA1307:Specify StringComparison for clarity", Justification = "Path comparisons already occur on normalized strings; explicit StringComparison would add noise.", Scope = "module")]

// Program must remain non-static because it hosts the application entry point.
[assembly: SuppressMessage("Design", "CA1052:Static holder types should be Static or NotInheritable", Justification = "Program class provides the Main entry point and cannot be static.", Scope = "type", Target = "~T:HVO.SkyMonitor.CameraAgent.Program")]

