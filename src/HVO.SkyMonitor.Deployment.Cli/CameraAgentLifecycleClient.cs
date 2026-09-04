using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HVO.SkyMonitor.Deployment;

internal interface ICameraAgentLifecycleClient
{
    Task<LifecycleContinuity> PauseAndDrainAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken);
    Task<LifecycleContinuity> ConfirmDrainedAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken);
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
    private static readonly TimeSpan DrainDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromSeconds(2);

    // The CameraAgent records every lifecycle command under the idempotency key
    // "lifecycle:{operationId}:{action}" and rejects a replay whose payload differs
    // with 409. A retried pause or resume therefore conflicts whenever the first
    // attempt already advanced the durable capture-control version, so a conflict is
    // accepted only when the durable state already matches the requested target.
    public async Task<LifecycleContinuity> PauseAndDrainAsync(
        Guid operationId,
        string verificationToken,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        var before = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/internal/deployment/lifecycle/pause", UriKind.Relative),
            new { operationId, expectedVersion = before.CaptureVersion, reason = "transactional lifecycle operation" },
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var current = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
            if (!IsPausedOrPausing(current))
            {
                throw new InstallerException("CameraAgent rejected the lifecycle pause because its capture control state changed.");
            }
        }
        else
        {
            response.EnsureSuccessStatusCode();
        }
        return await WaitForDrainedBoundaryAsync(client, cancellationToken).ConfigureAwait(false);
    }

    // A post-mutation boundary reuses the durable pause that the same operation
    // already recorded before mutation; it only confirms that the restarted
    // CameraAgent honours that boundary instead of issuing a second pause command.
    public async Task<LifecycleContinuity> ConfirmDrainedAsync(
        Guid operationId,
        string verificationToken,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        var current = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
        if (!IsPausedOrPausing(current))
        {
            throw new InstallerException("CameraAgent did not preserve the durable lifecycle pause across the mutation boundary.");
        }
        return await WaitForDrainedBoundaryAsync(client, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        var state = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/internal/deployment/lifecycle/resume", UriKind.Relative),
            new { operationId, expectedVersion = state.CaptureVersion, reason = "transactional lifecycle operation completed" },
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var current = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
            if (current.CaptureState != "Running")
            {
                throw new InstallerException("CameraAgent rejected the lifecycle resume because its capture control state changed.");
            }
            return;
        }
        response.EnsureSuccessStatusCode();
    }

    private static async Task<LifecycleContinuity> WaitForDrainedBoundaryAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + DrainDeadline;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
            if (state.CaptureState == "Paused" && state.RawLeased == 0 && state.LaneLeased == 0 &&
                state.ProcessingLeased == 0 && state.OutboxLeased == 0)
            {
                return state;
            }
            await Task.Delay(DrainPollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new InstallerException("CameraAgent did not reach a durable drained boundary before the lifecycle deadline.");
    }

    private static bool IsPausedOrPausing(LifecycleContinuity state)
        => state.CaptureState is "Paused" or "PauseRequested";

    private HttpClient CreateClient(string verificationToken)
    {
        var client = handler is null
            ? new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) }
            : new HttpClient(handler, disposeHandler: false) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };
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
