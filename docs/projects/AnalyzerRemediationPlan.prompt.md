## Analyzer Remediation Plan

Ship a warning-free `dotnet build` by fixing root causes where practical—tightening access modifiers on Identity/UI code, adding null guards, standardizing logging/async patterns, and cleaning disposal/random usage—while reserving scoped suppressions for intentional design or generated artifacts.

### Steps
1. Harden shared libraries: align with the latest `HVO.Core` primitives (Result/Option analyzers) and continue updating `Security/SignedTicketService.cs` plus `Observability/SkyMonitorObservabilityExtensions.cs` with `ThrowIfNull`, `LoggerMessage`, and static helpers.
2. Normalize access modifiers and data contracts in Identity/UI (`src/HVO.SkyMonitor.LogicHost/Components/**`, `Configuration/*.cs`, `Models/Diagnostics/*.cs`, `Data/ApplicationUser.cs`) by making classes internal, sealing namespaces, and converting mutable collections; suppress CA1716 if renaming breaks routing.
3. Modernize services/controllers: adjust `SmtpEmailNotificationService`, `DiagnosticsController`, `DefaultApiKeyAuthenticationHandler`, `AuthenticationMetrics`, and `DatabaseSeeder` for null guards, `ConfigureAwait(false)`, `using var`, and `MinioClient` disposal.
4. Refactor camera agents (`src/HVO.SkyMonitor.CameraAgent*/`) to add LoggerMessage partials, `ConfigureAwait(false)`, URI overloads, disposal/IDisposable implementations, and update option DTOs (Uri types, readonly arrays); decide on CA1515 scope (internal or suppress).
5. Clean demo components (`CameraAgent`, UI `Weather.razor`) by replacing `Random` with `RandomNumberGenerator` where security applies (or suppress for demos), tightening exception handling, and reducing async warnings (`ConfigureAwait`, `LoggerMessage`).
6. Extend `.editorconfig` and `GlobalSuppressions.cs` only for unavoidable scenarios (e.g., public APIs mandated by ASP.NET Identity, intentional sample randomness), documenting justification in `docs/projects/plan-infraAndTestingVNext.prompt.md`.

### Open Questions
1. Identity scaffolding options: internalize all components vs. directory-wide CA1515 suppression?
2. Sample/demo code: convert to production-grade patterns or isolate under test-only projects to suppress analyzers?
3. Central auth/token handling: refactor into reusable helper to simplify CA2000/CA1001 fixes, or acceptable to suppress with comments?
