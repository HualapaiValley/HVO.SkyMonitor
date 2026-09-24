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
    Task<LifecycleContinuity> ReadContinuityAsync(string verificationToken, CancellationToken cancellationToken);
    Task ResumeRecoveryAsync(Guid operationId, Guid commandId, long expectedVersion, string verificationToken, CancellationToken cancellationToken);
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

// Time budgets for one lifecycle exchange with a CameraAgent. A command shares
// the drain deadline with the boundary poll that confirms it, because the
// CameraAgent holds a pause until in-flight captures drain and holds a resume
// behind its startup initialization and any draining pause.
internal sealed record LifecycleBudgets(TimeSpan ReadTimeout, TimeSpan DrainDeadline, TimeSpan DrainPollInterval)
{
    public static LifecycleBudgets Default { get; } = new(
        ReadTimeout: TimeSpan.FromSeconds(15),
        DrainDeadline: TimeSpan.FromMinutes(2),
        DrainPollInterval: TimeSpan.FromSeconds(2));
}

internal sealed class CameraAgentLifecycleClient(
    Uri baseAddress,
    HttpMessageHandler? handler = null,
    LifecycleBudgets? budgets = null) : ICameraAgentLifecycleClient
{
    private readonly LifecycleBudgets _budgets = budgets ?? LifecycleBudgets.Default;

    // Lifecycle commands target a durable capture-control state rather than a
    // capture-control version: the operation journal is the transactional
    // authority, the CameraAgent completes a command that already matches its
    // durable state as a no-op, and the drained-boundary poll verifies the
    // outcome. Each lifecycle operation carries a fresh command id that is reused
    // across its transient-response retries, so a lost acknowledgement cannot
    // create multiple durable command records. The reason names the surrounding
    // lifecycle operation.
    public async Task<LifecycleContinuity> PauseAndDrainAsync(
        Guid operationId,
        string verificationToken,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        // The CameraAgent holds a pause until in-flight captures drain, so the
        // command and the boundary poll share one drain budget.
        var deadline = DateTimeOffset.UtcNow + _budgets.DrainDeadline;
        await PostCommandAsync(client, "pause", operationId, deadline, cancellationToken).ConfigureAwait(false);
        // An acknowledged pause is itself evidence that the CameraAgent drained,
        // so the confirming poll keeps at least one read budget even when the
        // command consumed the shared drain budget.
        var floor = DateTimeOffset.UtcNow + _budgets.ReadTimeout;
        return await WaitForDrainedBoundaryAsync(client, deadline > floor ? deadline : floor, cancellationToken)
            .ConfigureAwait(false);
    }

    // A post-mutation boundary reuses the durable pause that the operation
    // recorded before mutation; it only confirms that the restarted CameraAgent
    // honours that boundary instead of issuing another pause command.
    public async Task<LifecycleContinuity> ConfirmDrainedAsync(string verificationToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        return await WaitForDrainedBoundaryAsync(client, DateTimeOffset.UtcNow + _budgets.DrainDeadline, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        // The CameraAgent acknowledges a resume only after its startup
        // initialization completes and any draining pause releases the command
        // gate, so a resume after a restart shares the drain-sized budget.
        await PostCommandAsync(
            client,
            "resume",
            operationId,
            DateTimeOffset.UtcNow + _budgets.DrainDeadline,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<LifecycleContinuity> ReadContinuityAsync(string verificationToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        try
        {
            return await ReadStateAsync(client, _budgets.ReadTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is BudgetExceededException or TransientReadException)
        {
            throw new InstallerException("CameraAgent did not provide an authenticated recovery boundary within its budget.", exception);
        }
    }

    public async Task ResumeRecoveryAsync(
        Guid operationId, Guid commandId, long expectedVersion, string verificationToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(verificationToken);
        await PostCommandAsync(client, "resume", operationId, DateTimeOffset.UtcNow + _budgets.DrainDeadline,
            cancellationToken, commandId, expectedVersion).ConfigureAwait(false);
    }

    private async Task PostCommandAsync(
        HttpClient client,
        string action,
        Guid operationId,
        DateTimeOffset deadline,
        CancellationToken cancellationToken,
        Guid? retainedCommandId = null,
        long? expectedVersion = null)
    {
        var commandId = retainedCommandId ?? Guid.NewGuid();
        var payload = new Dictionary<string, object>
        {
            ["operationId"] = commandId,
            ["reason"] = $"transactional lifecycle operation {operationId:D} {action}"
        };
        if (expectedVersion is not null) payload["expectedVersion"] = expectedVersion.Value;
        try
        {
            while (true)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new BudgetExceededException();
                }
                using var response = await WithBudgetAsync(
                    remaining,
                    token => client.PostAsJsonAsync(
                        new Uri($"/api/internal/deployment/lifecycle/{action}", UriKind.Relative),
                        payload,
                        token),
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
                if (!IsTransientCommandStatus(response.StatusCode))
                {
                    throw new InstallerException(
                        $"CameraAgent rejected the lifecycle {action} command with status {(int)response.StatusCode}.");
                }
                var retryDelay = RetryDelay(response, deadline);
                if (retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(WholeMilliseconds(retryDelay), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (BudgetExceededException)
        {
            throw new InstallerException($"CameraAgent did not acknowledge the lifecycle {action} command within its budget.");
        }
        catch (HttpRequestException exception)
        {
            throw new InstallerException(
                $"CameraAgent did not accept the lifecycle {action} command: {Redaction.SafeDiagnostic(exception.Message)}", exception);
        }
    }

    private TimeSpan RetryDelay(HttpResponseMessage response, DateTimeOffset deadline)
    {
        var delay = response.Headers.RetryAfter?.Delta;
        if (!delay.HasValue && response.Headers.RetryAfter?.Date is { } retryAt)
        {
            delay = retryAt - DateTimeOffset.UtcNow;
        }
        var remaining = deadline - DateTimeOffset.UtcNow;
        var selected = delay ?? _budgets.DrainPollInterval;
        if (selected <= TimeSpan.Zero || remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        return selected < remaining ? selected : remaining;
    }

    // A restarted CameraAgent reports "Initializing", or "Unavailable" before its
    // coordinator initializes, until it loads the durable capture-control snapshot;
    // only "Running" or an initialized "Unavailable" proves the pause is not in
    // effect. Transient read failures are retried until the deadline because the
    // boundary follows a container restart; a terminal rejection fails at once.
    private async Task<LifecycleContinuity> WaitForDrainedBoundaryAsync(
        HttpClient client,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        LifecycleContinuity? lastState = null;
        string? lastReadFailure = null;
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new InstallerException(DeadlineMessage(lastState, lastReadFailure));
            }
            var budget = remaining < _budgets.ReadTimeout ? remaining : _budgets.ReadTimeout;
            LifecycleContinuity? state = null;
            try
            {
                state = await ReadStateAsync(client, budget, cancellationToken).ConfigureAwait(false);
                lastState = state;
                lastReadFailure = null;
            }
            catch (BudgetExceededException)
            {
                // A read truncated by the approaching deadline says nothing new
                // once something has been observed; a read that exhausted a full
                // read budget, or the only read the boundary ever got, does.
                if (budget >= _budgets.ReadTimeout || (lastState is null && lastReadFailure is null))
                {
                    lastReadFailure = "CameraAgent did not report its lifecycle state within its budget.";
                }
            }
            catch (TransientReadException exception)
            {
                lastReadFailure = exception.Message;
            }
            catch (InstallerException exception) when (lastState is not null)
            {
                // A terminal rejection keeps the drain progress observed before it.
                throw new InstallerException($"{exception.Message.TrimEnd('.')}; {Describe(lastState)}.", exception);
            }
            if (state is not null)
            {
                if (state.CaptureState == "Running" || (state.CaptureState == "Unavailable" && state.CaptureInitialized))
                {
                    throw new InstallerException(
                        $"CameraAgent reported capture control '{state.CaptureState}' instead of the durable lifecycle pause.");
                }
                if (state.CaptureState == "Paused" && state.CaptureInitialized && state.RawLeased == 0 && state.LaneLeased == 0 &&
                    state.ProcessingLeased == 0 && state.OutboxLeased == 0)
                {
                    return state;
                }
            }
            remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(WholeMilliseconds(remaining < _budgets.DrainPollInterval ? remaining : _budgets.DrainPollInterval), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static string DeadlineMessage(LifecycleContinuity? lastState, string? lastReadFailure)
    {
        var message = "CameraAgent did not reach a durable drained boundary before the lifecycle deadline";
        if (lastState is not null)
        {
            message += $"; {Describe(lastState)}";
        }
        if (lastReadFailure is not null)
        {
            message += $"; the last state read failed: {lastReadFailure}";
        }
        return message + ".";
    }

    private static string Describe(LifecycleContinuity state)
        => $"last observed capture control '{state.CaptureState}' (initialized: {state.CaptureInitialized}, " +
            $"leased raw/lane/processing/outbox {state.RawLeased}/{state.LaneLeased}/{state.ProcessingLeased}/{state.OutboxLeased})";

    // Timers resolve to whole milliseconds and truncate, so a budget or delay
    // that ends a fraction of a millisecond before the deadline would wake the
    // loop early and issue a read with no budget left; rounding up crosses the
    // deadline instead.
    private static TimeSpan WholeMilliseconds(TimeSpan value)
        => TimeSpan.FromMilliseconds(Math.Ceiling(value.TotalMilliseconds));

    private static async Task<T> WithBudgetAsync<T>(
        TimeSpan budget,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget > TimeSpan.Zero ? WholeMilliseconds(budget) : TimeSpan.Zero);
        try
        {
            return await work(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BudgetExceededException();
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
            MaxResponseContentBufferSize = 1024 * 1024,
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.Add("X-HVO-Installation-Token", verificationToken);
        return client;
    }

    // A transport fault or a server-side status that a restart explains (5xx,
    // 408, 429) is transient; any other rejection, and a payload this client
    // cannot parse, cannot become success by waiting. An exhausted read budget
    // propagates so the boundary loop can weigh it against the deadline.
    private static async Task<LifecycleContinuity> ReadStateAsync(HttpClient client, TimeSpan budget, CancellationToken cancellationToken)
    {
        try
        {
            return await WithBudgetAsync(
                budget,
                async token =>
                {
                    using var response = await client.GetAsync(
                        new Uri("/api/internal/deployment/lifecycle/state", UriKind.Relative), token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var message = $"CameraAgent rejected the lifecycle state read with status {(int)response.StatusCode}.";
                        throw IsTransient(response.StatusCode)
                            ? new TransientReadException(message)
                            : new InstallerException(message);
                    }
                    using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                    return Parse(document.RootElement);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new TransientReadException(
                $"CameraAgent lifecycle state could not be read: {Redaction.SafeDiagnostic(exception.Message)}", exception);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InstallerException(
                $"CameraAgent lifecycle state could not be parsed: {Redaction.SafeDiagnostic(exception.Message)}", exception);
        }
    }

    private static bool IsTransient(HttpStatusCode status)
        => (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private static bool IsTransientCommandStatus(HttpStatusCode status)
        => status != HttpStatusCode.InternalServerError && IsTransient(status);

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
            // A CameraAgent image that predates the operations summary omits the
            // flag; treating it as initialized deliberately keeps that image's
            // fast-fail contract on "Unavailable".
            !control.TryGetProperty("isInitialized", out var initialized) || initialized.GetBoolean());
    }

    private static JsonElement Value(JsonElement root, string name) => root.GetProperty(name).GetProperty("value");

    private sealed class BudgetExceededException : Exception
    {
        public BudgetExceededException()
        {
        }

        public BudgetExceededException(string message) : base(message)
        {
        }

        public BudgetExceededException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    private sealed class TransientReadException : Exception
    {
        public TransientReadException()
        {
        }

        public TransientReadException(string message) : base(message)
        {
        }

        public TransientReadException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
