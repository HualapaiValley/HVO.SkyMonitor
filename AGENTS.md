# HVO.SkyMonitor Agent Guide

## Toolchain and Validation

- Use the SDK pinned in `global.json` (`10.0.100`, prerelease permitted) and the solution `HVO.SkyMonitor.v9.slnx`.
- Package versions are centralized in `Directory.Packages.props`; do not put `Version` attributes on individual `PackageReference` items.
- Reproduce CI with:
  ```bash
  dotnet restore
  dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release
  dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter "TestCategory!=Integration&TestCategory!=Manual" --settings tests/coverage.runsettings --collect:"XPlat Code Coverage"
  ```
- There are currently no `TestCategory` attributes in the test source, so CI's filter does not exclude the Testcontainers suites. Docker must be available for the solution test command.
- Run a focused MSTest with `dotnet test <project> --filter "FullyQualifiedName~Namespace.Class.Method"`.
- `tests/coverage.runsettings` excludes test assemblies, `TestSupport`, migrations, and build output; keep coverage configuration aligned when adding projects.

## Architecture Boundaries

- `docs/project-plan.md` is the authoritative roadmap and architecture source. It forbids references between `CameraAgent` and `LogicHost`.
- `AgentCore` contains stable transport-neutral camera, rig, frame, and artifact contracts only; do not add ASP.NET, EF Core, MinIO, SkiaSharp, or camera-SDK dependencies.
- `CameraAgent.Common` owns edge capture orchestration: module registration/factory, ordered processing, storage, retention, telemetry, and configuration loading. The `CameraAgent` host registers this through `AddCameraAgentInfrastructure` and hosts the local UI/API/identity.
- `LogicHost` is the central ASP.NET host; it owns SQL Server/Redis/MinIO-backed central services and runs EF migrations plus seed data at startup.
- New reusable astronomy/projection behavior belongs in `HVO.SkyMonitor.Astronomy`, and reusable image algorithms in `HVO.SkyMonitor.Imaging`; do not create host-specific projection math. Astronomy catalogs are versioned read-only SQLite snapshots deployed locally to LogicHost and every CameraAgent, never part of the shared SQL Server schema.

## Runtime and Infrastructure

- SQL Server, Redis, MinIO, and Mailpit are persistent shared services on `hvo-docker`; configure their endpoints and credentials in the ignored `.env` using `.env.template`.
- Use `./scripts/infra:start [logichost|cameraagent]` rather than raw Compose for application containers. `--reset` only deletes application container state and rebuilding refreshes the image.
- Run hosts directly with `dotnet run --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj` and `dotnet run --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj`. Compose exposes them at ports `5174` and `5130` respectively.
- CameraAgent's local Identity database and data-protection keys are runtime state under `App_Data/` and `DataProtection-Keys/`; do not add them to commits.
- Keep local secrets in user secrets, `.env`, or `.devcontainer/devcontainer.local.env`. These are intentionally ignored; `.env.template` contains the supported local defaults.

## Tests and UI

- LogicHost integration tests start SQL Server, Redis, MinIO, and Mailpit through Testcontainers; CameraAgent integration tests reuse that central-host fixture and wire in-process HTTP handlers.
- CameraAgent module/pipeline configuration comes from `cameraagent.sample.json` and the `CameraAgent` configuration section. Preserve configuration-driven module and processing-step discovery rather than adding host-specific branches.
- For Blazor components with logic, keep markup, code-behind, scoped CSS, and optional scoped JS in sibling `.razor`, `.razor.cs`, `.razor.css`, and `.razor.js` files. Root/layout components must render `data-theme="hvo-dark"` on `<html>`.
