# HVO.SkyMonitor.IntegrationTests

Integration tests for HVO.SkyMonitor using Testcontainers for infrastructure orchestration.

## Overview

This project contains integration tests that verify the HVO.SkyMonitor application works correctly with real infrastructure services (PostgreSQL, Redis, MinIO) running in Docker containers via Testcontainers.

## Architecture

### IntegrationTestFixture

The `IntegrationTestFixture` class orchestrates:
- **PostgreSQL 17** - Database container
- **Redis 7** - Cache container  
- **MinIO** - Object storage container
- **WebApplicationFactory** - In-process HTTP server hosting the application

All containers are started once per test class and reused across tests for performance.

### Test Organization

Tests are organized by feature area:
- `HealthCheckTests.cs` - Infrastructure health verification
- *Future*: Authentication tests, API authorization tests, storage tests

## Running Tests

### Prerequisites

- Docker must be running (Testcontainers manages container lifecycle)
- .NET 10 SDK installed

### Run All Tests

```bash
dotnet test
```

### Run Specific Test Class

```bash
dotnet test --filter "FullyQualifiedName~HealthCheckTests"
```

### Run Single Test

```bash
dotnet test --filter "FullyQualifiedName~HealthCheckTests.HealthCheck_ReturnsHealthy"
```

## Writing New Tests

### Basic Pattern

```csharp
[TestClass]
public class MyFeatureTests
{
    private static IntegrationTestFixture? _fixture;
    private HttpClient? _client;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.InitializeAsync();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _fixture?.Dispose();
    }

    [TestInitialize]
    public void TestInitialize()
    {
        _client = _fixture!.Factory.CreateClient();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client?.Dispose();
    }

    [TestMethod]
    public async Task MyTest()
    {
        // Arrange
        var request = "/api/v1.0/my-endpoint";

        // Act
        var response = await _client!.GetAsync(request);

        // Assert
        response.EnsureSuccessStatusCode();
    }
}
```

### Using TestSupport Library

The `HVO.SkyMonitor.TestSupport` library provides shared test constants:

```csharp
using HVO.SkyMonitor.TestSupport;

// Use predefined test users
var username = TestUsers.Admin.Username;
var password = TestUsers.Admin.Password;

// Use predefined OAuth clients
var clientId = TestClients.SystemCameraAgent.ClientId;
var clientSecret = TestClients.SystemCameraAgent.ClientSecret;

// Use predefined API keys
var apiKey = TestApiKeys.CameraAgent.Key;
```

### Authentication Examples

#### Bearer Token Authentication

```csharp
var token = await HttpHelpers.GetClientCredentialsTokenAsync(
    _client,
    "/connect/token",
    TestClients.SystemCameraAgent.ClientId,
    TestClients.SystemCameraAgent.ClientSecret,
    "api.camera api.frames"
);

var authenticatedClient = HttpHelpers.WithBearerToken(_client, token.AccessToken);
var response = await authenticatedClient.GetAsync("/api/v1.0/protected-endpoint");
```

#### API Key Authentication

```csharp
var authenticatedClient = HttpHelpers.WithApiKey(_client, TestApiKeys.CameraAgent.Key);
var response = await authenticatedClient.GetAsync("/api/v1.0/protected-endpoint");
```

## Test Data

### Database

The fixture uses an in-memory SQLite database by default. When PostgreSQL migration is complete, tests will use the PostgreSQL container.

### Seeding

Test data seeding is handled by `IntegrationTestFixture.SeedTestDataAsync()`. Add seed data for your tests:

```csharp
private async Task SeedTestDataAsync()
{
    using var scope = Factory.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    
    // Add test users
    // Add test clients
    // Add test API keys
    
    await dbContext.SaveChangesAsync();
}
```

## Troubleshooting

### Tests fail with "Docker not found"

Ensure Docker Desktop is running. Testcontainers requires access to the Docker daemon.

### Tests are slow

The first test run downloads container images which takes time. Subsequent runs reuse cached images and are much faster.

### Containers not cleaning up

Testcontainers should automatically clean up containers. If you see orphaned containers:

```bash
docker ps -a | grep testcontainers
docker rm -f $(docker ps -a -q --filter "label=org.testcontainers")
```

### Port conflicts

Testcontainers uses random ports to avoid conflicts. If you still see port conflicts, check for services already running on your host.

## Best Practices

1. **Reuse fixtures** - Use `[ClassInitialize]` to start containers once per test class
2. **Dispose clients** - Always dispose HttpClient instances in `[TestCleanup]`
3. **Use TestSupport** - Reference shared constants from `HVO.SkyMonitor.TestSupport`
4. **Isolate tests** - Each test should be independent and not rely on test execution order
5. **Clean data** - Reset database state between tests if needed

## Performance

- Container startup: ~5-10 seconds (first time, with image pull: ~30-60 seconds)
- Test execution: Milliseconds to seconds per test
- Container cleanup: ~1-2 seconds

## References

- [Testcontainers for .NET](https://dotnet.testcontainers.org/)
- [WebApplicationFactory](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests)
- [MSTest Documentation](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-mstest)
- [HVO.SkyMonitor.TestSupport](../../src/HVO.SkyMonitor.TestSupport/README.md)
