# CI Pipeline Runbook

This document describes the build and validation stages executed in GitHub Actions (and how to reproduce them locally).

## Overview

| Stage | Purpose |
| --- | --- |
| **Restore & Build** | `dotnet restore` + `dotnet build HVO.SkyMonitor.v9.slnx` in Release. |
| **Unit Tests** | `dotnet test` across the solution, excluding `Integration` and `Manual` categories. |
| **Integration Tests** | Available for local and dedicated runs; not part of the current CI workflow. |
| **Artifacts** | Publish coverage and build logs for download. |

## Workflow Configuration

- Located at `.github/workflows/ci.yml`.
- Runs on GitHub-hosted `ubuntu-latest` with the .NET 10 SDK installed by `actions/setup-dotnet`.
- The workflow does not start the local infrastructure stack or inject application-service credentials. Coverage badge publication uses the configured gist secrets.

## Reproducing Locally

1. **Baseline build**
   ```bash
   dotnet build HVO.SkyMonitor.v9.slnx -c Release
   ```
2. **Unit tests only**
   ```bash
   dotnet test HVO.SkyMonitor.v9.slnx -c Release --filter "TestCategory!=Integration&TestCategory!=Hardware"
   ```
3. **Integration tests**
   ```bash
   # Host integration suite
   dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj

   # Camera agent test suite
   dotnet test tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/HVO.SkyMonitor.CameraAgent.IntegrationTests.csproj
   ```
4. **Hardware tests (opt-in)**
   ```bash
   dotnet test HVO.SkyMonitor.v9.slnx --filter TestCategory=Hardware
   ```

## Handling Failures

- **Restore errors**: usually missing feeds—confirm `NuGet.config` matches CI and private feeds are reachable.
- **Testcontainers failures**: ensure Docker is running and plenty of disk space exists for volumes.
- **Database schema drift**: regenerate migrations, then rerun integration tests.

## Pull Request Expectations

1. Run `dotnet test HVO.SkyMonitor.v9.slnx` locally before pushing.
2. Attach logs from failing CI runs to PR discussion when requesting help.
3. Never disable tests in CI without filing an issue and referencing it in the PR.

## Future Enhancements

- Hardware test opt-in flag (CI will honor `RUN_HARDWARE_TESTS=true`).
- Automatic coverage upload to Codecov when credentials become available.
