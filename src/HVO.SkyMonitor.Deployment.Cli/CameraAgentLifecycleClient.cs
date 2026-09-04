using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HVO.SkyMonitor.Deployment;

internal interface ICameraAgentLifecycleClient
{
    Task<LifecycleContinuity> PauseAndDrainAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken);
    Task<LifecycleContinuity> ConfirmDrainedAsync(string verificationToken, CancellationToken cancellationToken);
    Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken);
}

internal sealed record LifecycleContinuity(
    string CaptureState,
    long CaptureVersion,
    long CaptureSequence,
    long RawPending,
    long RawLeased,
    long LanePending,
    long LaneLeased,
    long ProcessingPending,
    long ProcessingLeased,
    long OutboxPending,
    long OutboxLeased);

internal sealed class CameraAgentLifecycleClient(Uri baseAddress, HttpMessageHandler? handler = null) : ICameraAgentLifecycleClient
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DrainDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromSeconds(2);

    // The CameraAgent keys every lifecycle command on the request's operation id and
    // rejects a replay whose payload differs, while a command that already matches
    // the durable state completes as a no-op. Each command therefore carries a fresh
    // command id and records the lifecycle operation in its reason, so a retried or
    // lost-acknowledgement command converges on the durable state and 409 only ever
    // means a concurrent capture-control change.
    public async Task<LifecycleContinuity> PauseAndDrainAsync(
        Guid operationId,
        string verificationToken,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken, ReadTimeout);
        var before = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
        // The pause response is held until in-flight captures drain, so it may exceed
        // one exposure; the durable boundary is confirmed by polling afterwards.
        using var pauseClient = CreateClient(verificationToken, DrainDeadline);
        using var response = await pauseClient.PostAsJsonAsync(
            new Uri("/api/internal/deployment/lifecycle/pause", UriKind.Relative),
            new
            {
                operationId = Guid.NewGuid(),
                expectedVersion = before.CaptureVersion,
                reason = $"transactional lifecycle operation {operationId:D}"
            },
            cancellationToken).ConfigureAwait(false);
        EnsureAccepted(response, "pause");
        return await WaitForDrainedBoundaryAsync(client, null, cancellationToken).ConfigureAwait(false);
    }

    // A post-mutation boundary reuses the durable pause that the same operation
    // recorded before mutation; it only confirms that the restarted CameraAgent
    // honours that boundary instead of issuing another pause command.
    public async Task<LifecycleContinuity> ConfirmDrainedAsync(string verificationToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken, ReadTimeout);
        return await WaitForDrainedBoundaryAsync(client, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken, ReadTimeout);
        var state = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/internal/deployment/lifecycle/resume", UriKind.Relative),
            new
            {
                operationId = Guid.NewGuid(),
                expectedVersion = state.CaptureVersion,
                reason = $"transactional lifecycle operation {operationId:D} completed"
            },
            cancellationToken).ConfigureAwait(false);
        EnsureAccepted(response, "resume");
    }

    private static void EnsureAccepted(HttpResponseMessage response, string action)
    {
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InstallerException(
                $"CameraAgent rejected the lifecycle {action} because its capture control state changed concurrently.");
        }
        response.EnsureSuccessStatusCode();
    }

    // A restarted CameraAgent reports "Initializing" until it loads the durable
    // capture-control snapshot, so only an observed "Running" proves the pause was lost.
    private static async Task<LifecycleContinuity> WaitForDrainedBoundaryAsync(
        HttpClient client,
        LifecycleContinuity? initial,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + DrainDeadline;
        var state = initial ?? await ReadAsync(client, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            if (state.CaptureState == "Running")
            {
                throw new InstallerException("CameraAgent did not preserve the durable lifecycle pause across the mutation boundary.");
            }
            if (state.CaptureState == "Paused" && state.RawLeased == 0 && state.LaneLeased == 0 &&
                state.ProcessingLeased == 0 && state.OutboxLeased == 0)
            {
                return state;
            }
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new InstallerException("CameraAgent did not reach a durable drained boundary before the lifecycle deadline.");
            }
            await Task.Delay(remaining < DrainPollInterval ? remaining : DrainPollInterval, cancellationToken).ConfigureAwait(false);
            state = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned HttpClient owns the handler and the caller disposes the client.")]
    private HttpClient CreateClient(string verificationToken, TimeSpan timeout)
    {
        var client = new HttpClient(
            handler ?? new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                CheckCertificateRevocationList = true
            },
            disposeHandler: handler is null)
        {
            BaseAddress = baseAddress,
            Timeout = timeout
        };
        client.DefaultRequestHeaders.Add("X-HVO-Installation-Token", verificationToken);
        return client;
    }

    internal static async Task<LifecycleContinuity> ReadAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            new Uri("/api/internal/deployment/lifecycle/state", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var control = Value(root, "captureControl");
        var raw = Value(root, "rawIngress");
        var lanes = Value(root, "captureLanes");
        var processing = Value(root, "captureProcessing");
        var outbox = Value(root, "artifactOutbox");
        return new LifecycleContinuity(
            control.GetProperty("state").GetString() ?? string.Empty,
            control.GetProperty("version").GetInt64(),
            root.GetProperty("captureSequence").GetInt64(),
            raw.GetProperty("pendingCount").GetInt64(),
            raw.GetProperty("leasedCount").GetInt64(),
            lanes.GetProperty("pendingCount").GetInt64(),
            lanes.GetProperty("leasedCount").GetInt64(),
            processing.GetProperty("pendingCount").GetInt64(),
            processing.GetProperty("leasedCount").GetInt64(),
            outbox.GetProperty("pendingCount").GetInt64(),
            outbox.GetProperty("leasedCount").GetInt64());
    }

    private static JsonElement Value(JsonElement root, string name) => root.GetProperty(name).GetProperty("value");
}
