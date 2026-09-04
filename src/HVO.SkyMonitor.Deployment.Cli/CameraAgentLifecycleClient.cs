using System.Diagnostics.CodeAnalysis;
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
    long OutboxLeased,
    bool CaptureInitialized = true);

internal sealed class CameraAgentLifecycleClient(Uri baseAddress, HttpMessageHandler? handler = null) : ICameraAgentLifecycleClient
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResumeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DrainDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromSeconds(2);

    // Lifecycle commands target a durable capture-control state rather than a
    // capture-control version: the operation journal is the transactional
    // authority, the CameraAgent completes a command that already matches its
    // durable state as a no-op, and the drained-boundary poll verifies the
    // outcome. Each command carries a fresh command id so a retried or
    // lost-acknowledgement command never replays an earlier idempotency record,
    // and names its lifecycle operation in the recorded reason.
    public async Task<LifecycleContinuity> PauseAndDrainAsync(
        Guid operationId,
        string verificationToken,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        // The CameraAgent holds a pause until in-flight captures drain, so the
        // command and the boundary poll share one drain budget.
        var deadline = DateTimeOffset.UtcNow + DrainDeadline;
        await PostCommandAsync(client, "pause", operationId, deadline - DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        return await WaitForDrainedBoundaryAsync(client, deadline, cancellationToken).ConfigureAwait(false);
    }

    // A post-mutation boundary reuses the durable pause that the operation
    // recorded before mutation; it only confirms that the restarted CameraAgent
    // honours that boundary instead of issuing another pause command.
    public async Task<LifecycleContinuity> ConfirmDrainedAsync(string verificationToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        return await WaitForDrainedBoundaryAsync(client, DateTimeOffset.UtcNow + DrainDeadline, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        // A resume completes synchronously unless it queues behind a draining pause.
        await PostCommandAsync(client, "resume", operationId, ResumeTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PostCommandAsync(
        HttpClient client,
        string action,
        Guid operationId,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await WithBudgetAsync(
                budget,
                $"CameraAgent did not acknowledge the lifecycle {action} command within its budget.",
                token => client.PostAsJsonAsync(
                    new Uri($"/api/internal/deployment/lifecycle/{action}", UriKind.Relative),
                    new
                    {
                        operationId = Guid.NewGuid(),
                        reason = $"transactional lifecycle operation {operationId:D} {action}"
                    },
                    token),
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InstallerException(
                    $"CameraAgent rejected the lifecycle {action} command with status {(int)response.StatusCode}.");
            }
        }
        catch (HttpRequestException exception)
        {
            throw new InstallerException(
                $"CameraAgent did not accept the lifecycle {action} command: {Redaction.SafeDiagnostic(exception.Message)}", exception);
        }
    }

    // A restarted CameraAgent reports "Initializing", or "Unavailable" before its
    // coordinator initializes, until it loads the durable capture-control snapshot;
    // only "Running" or an initialized "Unavailable" proves the pause is not in
    // effect. Read failures are retried until the deadline because the boundary
    // follows a container restart.
    private static async Task<LifecycleContinuity> WaitForDrainedBoundaryAsync(
        HttpClient client,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        string? lastReadFailure = null;
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new InstallerException(lastReadFailure is null
                    ? "CameraAgent did not reach a durable drained boundary before the lifecycle deadline."
                    : $"CameraAgent did not reach a durable drained boundary before the lifecycle deadline; the last state read failed: {lastReadFailure}");
            }
            LifecycleContinuity? state = null;
            try
            {
                state = await ReadAsync(client, remaining < ReadTimeout ? remaining : ReadTimeout, cancellationToken)
                    .ConfigureAwait(false);
                lastReadFailure = null;
            }
            catch (InstallerException exception)
            {
                lastReadFailure = exception.Message;
            }
            if (state is not null)
            {
                if (state.CaptureState == "Running" || (state.CaptureState == "Unavailable" && state.CaptureInitialized))
                {
                    throw new InstallerException(
                        $"CameraAgent reported capture control '{state.CaptureState}' instead of the durable lifecycle pause.");
                }
                if (state.CaptureState == "Paused" && state.RawLeased == 0 && state.LaneLeased == 0 &&
                    state.ProcessingLeased == 0 && state.OutboxLeased == 0)
                {
                    return state;
                }
            }
            remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining < DrainPollInterval ? remaining : DrainPollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<T> WithBudgetAsync<T>(
        TimeSpan budget,
        string timeoutMessage,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget > TimeSpan.Zero ? budget : TimeSpan.Zero);
        try
        {
            return await work(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InstallerException(timeoutMessage);
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

    internal static async Task<LifecycleContinuity> ReadAsync(HttpClient client, TimeSpan budget, CancellationToken cancellationToken)
    {
        try
        {
            return await WithBudgetAsync(
                budget,
                "CameraAgent did not report its lifecycle state within its budget.",
                async token =>
                {
                    using var response = await client.GetAsync(
                        new Uri("/api/internal/deployment/lifecycle/state", UriKind.Relative), token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InstallerException(
                            $"CameraAgent rejected the lifecycle state read with status {(int)response.StatusCode}.");
                    }
                    using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                    return Parse(document.RootElement);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InstallerException(
                $"CameraAgent lifecycle state could not be read: {Redaction.SafeDiagnostic(exception.Message)}", exception);
        }
    }

    private static LifecycleContinuity Parse(JsonElement root)
    {
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
            outbox.GetProperty("leasedCount").GetInt64(),
            !control.TryGetProperty("isInitialized", out var initialized) || initialized.GetBoolean());
    }

    private static JsonElement Value(JsonElement root, string name) => root.GetProperty(name).GetProperty("value");
}
