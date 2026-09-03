# HVO.SkyMonitor.LogicHost.Tests

Fast LogicHost unit tests for authentication, authorization, account types, API keys, OAuth claim mapping, and signed URLs. This project has no external-service dependency.

## Run

```bash
dotnet test tests/HVO.SkyMonitor.LogicHost.Tests/HVO.SkyMonitor.LogicHost.Tests.csproj
```

The project is included in `HVO.SkyMonitor.v9.slnx` and runs in the solution CI command. Testcontainers coverage lives in `HVO.SkyMonitor.LogicHost.IntegrationTests`; cross-host composition lives in `HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests`.
