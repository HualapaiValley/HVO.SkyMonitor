# HVO.SkyMonitor Tests

This project contains comprehensive tests for the HVO.SkyMonitor authentication and authorization system.

## Test Categories

### Unit Tests
Unit tests validate individual components without requiring external dependencies. These tests run quickly and are suitable for continuous integration.

**Test Files:**
- `AccountTypeTests.cs` - Validates AccountType enum and ApplicationUser integration
- `OAuth2ClaimMappingTests.cs` - Tests OAuth2/OIDC claim structure (subject, name, email, account_type, scopes)
- `ApiKeyAuthenticationTests.cs` - Tests API key format and validation result structure
- `SignedUrlTests.cs` - Tests signed URL generation, validation, expiration, and tampering detection
- `AuthorizationPolicyTests.cs` - Tests RequireSystemAccount and RequireUserAccount policies

**Running Unit Tests:**
```bash
dotnet test --filter "TestCategory!=AspireIntegration"
```

### Aspire Integration Tests
Integration tests using `.NET Aspire` distributed application testing framework. These tests start the entire AppHost with all dependencies (Redis, PostgreSQL, MinIO) and validate end-to-end scenarios.

**Requirements:**
- Docker or Podman must be installed and running
- Aspire workload must be installed: `dotnet workload install aspire`

**Test Files:**
- `AspireIntegrationTests.cs` - Tests the complete distributed application startup and OAuth2/API key endpoints

**Running Aspire Integration Tests:**
```bash
# Requires Docker/Podman to be running
dotnet test --filter "TestCategory=AspireIntegration"
```

**Note:** Aspire integration tests may fail in environments without Docker/Podman. They are marked with `[TestCategory("RequiresDocker")]` and should be run in appropriate environments (local development, CI with Docker support).

## Running All Tests

To run all tests (unit + integration):
```bash
dotnet test
```

To run only unit tests (no Docker required):
```bash
dotnet test --filter "TestCategory!=AspireIntegration"
```

To run only integration tests:
```bash
dotnet test --filter "TestCategory=AspireIntegration"
```

## Test Coverage

**Total: 29 unit tests + 4 Aspire integration tests = 33 tests**

### Unit Tests (25 tests - HVO.SkyMonitor.Tests)
- AccountType validation: 3 tests
- OAuth2 claim mapping: 7 tests
- API key authentication: 3 tests
- Signed URL validation: 6 tests
- Authorization policies: 6 tests

### Camera Agent Tests (8 tests - HVO.SkyMonitor.CameraAgent.Tests)
- Central authentication service: 3 tests
- Error handling scenarios: 5 tests

### Aspire Integration Tests (4 tests - HVO.SkyMonitor.Tests)
- Application startup validation
- OAuth2 token endpoint validation
- Protected endpoint authentication
- API key endpoint authentication

## Continuous Integration

For CI environments without Docker, use:
```bash
dotnet test --filter "TestCategory!=AspireIntegration"
```

For CI environments with Docker (e.g., GitHub Actions with Docker service), run all tests:
```bash
dotnet test
```

## Test Infrastructure

### Dependencies
- **MSTest** - Test framework
- **FluentAssertions** - Fluent assertion library
- **Aspire.Hosting.Testing** - Aspire distributed application testing framework
- **Microsoft.AspNetCore.Mvc.Testing** - ASP.NET Core integration testing
- **Moq** - Mocking framework (CameraAgent tests)

### Design Principles
- Tests follow AAA pattern (Arrange, Act, Assert)
- Test naming: `MethodName_Scenario_ExpectedResult`
- Comprehensive XML documentation on test classes
- Tests organized by functionality
- Separation of unit tests and integration tests
