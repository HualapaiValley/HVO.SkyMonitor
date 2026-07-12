# HVO.SkyMonitor.Tests

Fast LogicHost unit tests for authentication, authorization, account types, API keys, OAuth claim mapping, and signed URLs. This project has no external-service dependency.

## Run

```bash
dotnet test tests/HVO.SkyMonitor.Tests/HVO.SkyMonitor.Tests.csproj
```

The project is included in `HVO.SkyMonitor.v9.slnx` and runs in the solution CI command. Testcontainers coverage lives in `HVO.SkyMonitor.IntegrationTests` and `HVO.SkyMonitor.CameraAgent.IntegrationTests`.
