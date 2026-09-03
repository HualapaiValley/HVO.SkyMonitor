# HVO.SkyMonitor.LogicHost.IntegrationTests

LogicHost integration tests using Testcontainers for disposable SQL Server, Redis, MinIO, and Mailpit services.

`IntegrationTestFixture` creates the service containers once per test assembly and hosts LogicHost through `WebApplicationFactory`. The suite covers authentication, device registration, rig profiles, artifact ingest, history, diagnostics, cache, storage, and SMTP behavior.

## Run

Docker must be available.

```bash
dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj
```

Run a focused test with:

```bash
dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj --filter "FullyQualifiedName~HVO.SkyMonitor.IntegrationTests.HealthCheckTests.HealthCheckReturnsHealthyAsync"
```

Use `AssemblyHooks.Fixture` for the shared fixture in new tests. Create test-specific records through its `Factory` or a scoped `ApplicationDbContext`; never use production shared-service credentials.
