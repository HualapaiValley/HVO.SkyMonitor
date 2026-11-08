using System.Net.Http.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal sealed class InstallerControlHttpClient : IInstallerControlClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly CameraAgentLifecycleClient _lifecycle;
    private readonly Guid _workerId;
    private readonly string _token;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The owned HttpClient disposes its production handler; injected test handlers remain caller-owned.")]
    internal InstallerControlHttpClient(Uri address, string token, Guid workerId, HttpMessageHandler? handler = null)
    {
        _token = token;
        _workerId = workerId;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false,
            CheckCertificateRevocationList = true }, disposeHandler: handler is null)
        { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1024 * 1024 };
        _http.DefaultRequestHeaders.Add("X-HVO-Installation-Token", token);
        _lifecycle = new CameraAgentLifecycleClient(address, handler);
    }

    public async Task<CameraAgentControlSnapshot> ExchangeAsync(CameraAgentControlReceipt? receipt, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("/api/internal/deployment/lifecycle/control/exchange",
            new CameraAgentControlExchange(_workerId, receipt), CameraAgentControlJsonContext.Default.CameraAgentControlExchange,
            cancellationToken).ConfigureAwait(false);
        Check(response);
        return await response.Content.ReadFromJsonAsync(CameraAgentControlJsonContext.Default.CameraAgentControlSnapshot,
            cancellationToken).ConfigureAwait(false) ?? throw new InstallerException("The lifecycle exchange was empty.");
    }

    public Task<LifecycleContinuity> DrainAsync(CameraAgentControlRequest request, CancellationToken cancellationToken) =>
        _lifecycle.PauseControlAndDrainAsync(request.OperationId, request.ExpectedCaptureVersion, _token, cancellationToken);

    public async Task RestartAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("/api/internal/deployment/lifecycle/control/restart",
            new CameraAgentControlRestart(_workerId, receipt.Request.OperationId, receipt.PausedVersion!.Value),
            CameraAgentControlJsonContext.Default.CameraAgentControlRestart, cancellationToken).ConfigureAwait(false);
        Check(response);
    }

    public Task<LifecycleContinuity> ConfirmDrainedAsync(CancellationToken cancellationToken) =>
        _lifecycle.ConfirmDrainedAsync(_token, cancellationToken);

    public async Task ResumeAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken) =>
        _ = await _lifecycle.ResumeRecoveryAsync(receipt.Request.OperationId, receipt.Request.OperationId,
            receipt.PausedVersion!.Value, _token, cancellationToken).ConfigureAwait(false);

    private static void Check(HttpResponseMessage response)
    {
        if ((int)response.StatusCode >= 500) throw new HttpRequestException("CameraAgent lifecycle transport is unavailable.");
        if (!response.IsSuccessStatusCode) throw new InstallerException($"CameraAgent refused the lifecycle exchange ({(int)response.StatusCode}).");
    }

    public void Dispose() => _http.Dispose();
}
