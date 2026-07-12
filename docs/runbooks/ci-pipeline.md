# CI Pipeline Runbook

This document describes the build and validation stages executed in GitHub Actions (and how to reproduce them locally).

## Overview

| Stage | Purpose |
| --- | --- |
| **Restore & Build** | `dotnet restore` + `dotnet build HVO.SkyMonitor.v9.slnx` in Release. |
| **Tests** | `dotnet test` across the solution with the `Integration` and `Manual` category filter. |
| **Artifacts** | Publish coverage and build logs for download. |

## Workflow Configuration

- Located at `.github/workflows/ci.yml`.
- Runs on GitHub-hosted `ubuntu-latest` with the .NET 10 SDK installed by `actions/setup-dotnet`.
- Testcontainers starts disposable SQL Server, Redis, MinIO, and Mailpit containers during the test run. Coverage badge publication uses the configured gist secrets.

## Reproducing Locally

1. **Restore and build**
   ```bash
   dotnet restore
   dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release
   ```
2. **CI-equivalent tests**
   ```bash
   dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter "TestCategory!=Integration&TestCategory!=Manual" --settings tests/coverage.runsettings --collect:"XPlat Code Coverage"
   ```

## Handling Failures

- **Restore errors**: usually missing feeds—confirm `NuGet.config` matches CI and private feeds are reachable.
- **Testcontainers failures**: ensure Docker is running and plenty of disk space exists for volumes.
- **Database schema drift**: regenerate SQL Server migrations, then rerun integration tests.

## Pull Request Expectations

1. Run the CI-equivalent restore, build, and test commands locally before pushing.
2. Attach logs from failing CI runs to PR discussion when requesting help.
3. Never disable tests in CI without filing an issue and referencing it in the PR.
