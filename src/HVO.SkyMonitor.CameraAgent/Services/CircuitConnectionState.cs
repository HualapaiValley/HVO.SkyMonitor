using Microsoft.AspNetCore.Components.Server.Circuits;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// Whether the current Blazor circuit has a connected browser. The server keeps a disconnected circuit, and every
/// component timer in it, alive for the disconnected-circuit retention period, so a component that heartbeats from a
/// server timer must check this before claiming that someone is still watching.
/// </summary>
internal sealed class CircuitConnectionState : CircuitHandler
{
    private volatile bool _connected = true;

    public bool IsConnected => _connected;

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _connected = false;
        return Task.CompletedTask;
    }
}
