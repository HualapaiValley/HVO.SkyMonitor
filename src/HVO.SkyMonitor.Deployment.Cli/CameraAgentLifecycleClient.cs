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

    // Lifecycle commands target a durable capture-control state rather than a
    // capture-control version: the operation journal is the transactional
    // authority, the CameraAgent completes a command that already matches its
    // durable state as a no-op, and the drained-boundary poll verifies the
    // outcome. Each command carries a fresh command id so a retried or
    // lost-acknowledgement command never collides with an earlier idempotency
    // record, and names its lifecycle operation in the recorded reason.
    public async Task<LifecycleContinuity> PauseAndDrainAsync(
        Guid operationId,
        string verificationToken,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        await PostCommandAsync(client, "pause", operationId, cancellationToken).ConfigureAwait(false);
        return await WaitForDrainedBoundaryAsync(client, cancellationToken).ConfigureAwait(false);
    }

    // A post-mutation boundary reuses the durable pause that the operation
    // recorded before mutation; it only confirms that the restarted CameraAgent
    // honours that boundary instead of issuing another pause command.
    public async Task<LifecycleContinuity> ConfirmDrainedAsync(string verificationToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        return await WaitForDrainedBoundaryAsync(client, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        await PostCommandAsync(client, "resume", operationId, cancellationToken).ConfigureAwait(false);
    }

    // The CameraAgent holds a command until any in-flight drain completes, so a
    // command shares the drain deadline instead of the read timeout.
    private static async Task PostCommandAsync(
        HttpClient client,
        string action,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DrainDeadline);
        try
        {
            using var response = await client.PostAsJsonAsync(
                new Uri($"/api/internal/deployment/lifecycle/{action}", UriKind.Relative),
                new
                {
                    operationId = Guid.NewGuid(),
                    reason = $"transactional lifecycle operation {operationId:D} {action}"
                },
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new InstallerException($"CameraAgent rejected the lifecycle {action} command.");
            }
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InstallerException($"CameraAgent did not acknowledge the lifecycle {action} before the drain deadline.");
        }
    }

    // A restarted CameraAgent reports "Initializing" until it loads the durable
    // capture-control snapshot; only "Running" or "Unavailable" proves the pause
    // is not in effect.
    private static async Task<LifecycleContinuity> WaitForDrainedBoundaryAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + DrainDeadline;
        while (true)
        {
            var state = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
            if (state.CaptureState is "Running" or "Unavailable")
            {
                throw new InstallerException(
                    $"CameraAgent reported capture control '{state.CaptureState}' instead of the durable lifecycle pause.");
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
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned HttpClient owns the handler and the caller disposes the client.")]
    private HttpClient CreateClient(string verificationToken)
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
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.Add("X-HVO-Installation-Token", verificationToken);
        return client;
    }

    internal static async Task<LifecycleContinuity> ReadAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            using var response = await client.GetAsync(
                new Uri("/api/internal/deployment/lifecycle/state", UriKind.Relative), timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false));
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InstallerException("CameraAgent did not report its lifecycle state before the read timeout.");
        }
    }

    private static JsonElement Value(JsonElement root, string name) => root.GetProperty(name).GetProperty("value");
}
