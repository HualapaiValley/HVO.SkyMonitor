# HVO.SkyMonitor.TestSupport

Shared test infrastructure library for HVO.SkyMonitor integration and unit tests.

## Overview

This library provides reusable test constants, utilities, and helpers to reduce code duplication across test projects and ensure consistency in test data.

## Contents

### Test Constants

#### `TestHosts`
Shared test host URLs and domains:
- `HttpDevelopmentUrl`: Base HTTP URL for development testing
- `LogicalDomain`: Logical domain name for test scenarios
- `SimulatorAgentUrl`, `ZwoAgentUrl`: Camera agent URLs

#### `TestEmail`
Email addresses and configuration for tests:
- `FromAddress`, `FromDisplayName`: Sender information
- `AdminRecipient`, `OperatorRecipient`, etc.: Test recipients

#### `TestUsers`
Pre-defined test user identities with credentials and roles:
- `Admin`: Full system access
- `Operator`: Operational capabilities
- `Viewer`: Read-only access
- `Regular`: Standard user with no special roles

Each user includes:
- Email, Username, Password
- Full Name
- Assigned Roles array

#### `TestClients`
OAuth/OIDC client configurations:
- `SystemCameraAgent`: Machine-to-machine authentication for camera agents
- `SystemInternal`: Internal service authentication
- `WebUI`: Browser-based authentication (PKCE)
- `MobileApp`: Mobile device authentication

Each client includes:
- Client ID and Secret (for confidential clients)
- Display Name
- Scopes array

#### `TestApiKeys`
Pre-defined API keys for system clients:
- `CameraAgent`: API key for camera agent access
- `InternalService`: Internal service API key
- `Webhook`: Webhook callback authentication
- `ReadOnly`: Read-only monitoring key

Each API key includes:
- Key value (must be unique)
- Name and Description
- Scopes array

#### `TestHmacSecrets`
HMAC secrets for signed URLs and webhooks:
- `SignedUrls`: Secrets for signed URL generation/validation
- `Webhooks`: Webhook signature verification
- `PresignedRequests`: Presigned request authentication

**WARNING**: These are test-only values. Never use in production.

### Test Utilities

#### `HttpHelpers`
HTTP client utilities for authentication:
- `GetClientCredentialsTokenAsync()`: Obtain bearer token via client credentials
- `GetPasswordTokenAsync()`: Obtain bearer token via password grant
- `WithBearerToken()`: Add bearer token to HttpClient
- `WithApiKey()`: Add API key header to HttpClient
- `DefaultJsonOptions`: Consistent JSON serialization settings

#### `TestExtensions`
Extension methods for common test operations:
- `ToJson<T>()`: Serialize object to JSON for debugging
- `FromJson<T>()`: Deserialize JSON to object
- `AssertNotNullOrEmpty()`: Validate string is not empty
- `AssertNotNull<T>()`: Validate object is not null
- `DeepCopy<T>()`: Create deep copy via JSON serialization
- `WaitForConditionAsync()`: Poll for async conditions with timeout

## Usage

### In Test Projects

Reference this library in your test project:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\HVO.SkyMonitor.TestSupport\HVO.SkyMonitor.TestSupport.csproj" />
</ItemGroup>
```

### Example: Integration Test with Bearer Token

```csharp
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;

[TestClass]
public class ApiAuthenticationTests
{
    private WebApplicationFactory<Program> _factory;
    private HttpClient _client;

    [TestInitialize]
    public void Setup()
    {
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    [TestMethod]
    public async Task CanAuthenticateWithClientCredentials()
    {
        // Arrange
        var tokenResponse = await HttpHelpers.GetClientCredentialsTokenAsync(
            _client,
            "/connect/token",
            TestClients.SystemCameraAgent.ClientId,
            TestClients.SystemCameraAgent.ClientSecret,
            "api.camera api.frames"
        );

        var authenticatedClient = HttpHelpers.WithBearerToken(_client, tokenResponse.AccessToken);

        // Act
        var response = await authenticatedClient.GetAsync("/api/v1.0/camera/status");

        // Assert
        Assert.IsTrue(response.IsSuccessStatusCode);
    }
}
```

### Example: Using Test Users

```csharp
using HVO.SkyMonitor.TestSupport;

[TestMethod]
public async Task AdminCanAccessProtectedEndpoint()
{
    // Arrange - Seed test user in database
    await SeedTestUser(
        TestUsers.Admin.Email,
        TestUsers.Admin.Username,
        TestUsers.Admin.Password,
        TestUsers.Admin.Roles
    );

    var tokenResponse = await HttpHelpers.GetPasswordTokenAsync(
        _client,
        "/connect/token",
        TestUsers.Admin.Username,
        TestUsers.Admin.Password,
        TestClients.WebUI.ClientId,
        "openid profile api.admin"
    );

    // Act
    var authenticatedClient = HttpHelpers.WithBearerToken(_client, tokenResponse.AccessToken);
    var response = await authenticatedClient.GetAsync("/api/v1.0/admin/users");

    // Assert
    Assert.IsTrue(response.IsSuccessStatusCode);
}
```

### Example: API Key Authentication

```csharp
using HVO.SkyMonitor.TestSupport;

[TestMethod]
public async Task CanAuthenticateWithApiKey()
{
    // Arrange - Seed API key in database
    await SeedTestApiKey(
        TestApiKeys.CameraAgent.Key,
        TestApiKeys.CameraAgent.Name,
        TestApiKeys.CameraAgent.Scopes
    );

    var authenticatedClient = HttpHelpers.WithApiKey(_client, TestApiKeys.CameraAgent.Key);

    // Act
    var response = await authenticatedClient.PostAsync("/api/v1.0/frames", jsonContent);

    // Assert
    Assert.IsTrue(response.IsSuccessStatusCode);
}
```

## Design Principles

1. **Consistency**: All tests use the same test data, reducing surprises
2. **Discoverability**: Static classes with const values are easy to find via IntelliSense
3. **Safety**: Clear warnings that test secrets must never be used in production
4. **Maintainability**: Centralized test data means changes propagate automatically
5. **Readability**: Well-documented constants improve test clarity

## Security Notes

- All test secrets, passwords, and keys in this library are **FOR TESTING ONLY**
- These values are intentionally weak and predictable
- Production systems must use environment-specific, strong secrets
- Never commit real secrets to this library

## Future Enhancements

Potential additions for future phases:
- Test data builders for complex entities
- Fixture helpers for Testcontainers setup
- Mock service implementations
- Performance testing utilities
- Load test data generators
