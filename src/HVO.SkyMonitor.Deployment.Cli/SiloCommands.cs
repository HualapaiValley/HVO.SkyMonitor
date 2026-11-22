using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>The installer-owned first-boot pause as CameraAgent reports it (#1966).</summary>
internal sealed record FirstBootPauseObservation(Guid Id, bool Seeded, bool InForce, bool Resumed, string State, long Version);

/// <summary>
/// The lifecycle-token routes of a Silo-layout instance's first-boot pause: the read-only status and the atomic first
/// resume. Both authenticate with the instance's lifecycle control token, which never leaves its owner-only file.
/// </summary>
internal sealed class SiloFirstBootClient(Uri baseAddress, HttpMessageHandler? handler = null)
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    public async Task<FirstBootPauseObservation> ReadAsync(string lifecycleToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(lifecycleToken);
        var deadline = DateTimeOffset.UtcNow + Budget;
        while (true)
        {
            using var response = await client.GetAsync(new Uri("/api/internal/deployment/lifecycle/first-boot", UriKind.Relative),
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw InstallerException.Refused("CameraAgent reports no installer first-boot pause for this instance.");
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InstallerException($"CameraAgent did not report its first-boot pause (HTTP {(int)response.StatusCode}).");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            return new FirstBootPauseObservation(
                root.GetProperty("id").GetGuid(),
                root.GetProperty("seeded").GetBoolean(),
                root.GetProperty("inForce").GetBoolean(),
                root.GetProperty("resumed").GetBoolean(),
                root.GetProperty("state").GetString() ?? string.Empty,
                root.GetProperty("version").GetInt64());
        }
    }

    /// <summary>Sends the atomic first resume; a refusal reports CameraAgent's fixed reason and changes nothing.</summary>
    public async Task<(string State, long Version)> FirstResumeAsync(
        string lifecycleToken, Guid firstBootId, long expectedVersion, CancellationToken cancellationToken)
    {
        using var client = CreateClient(lifecycleToken);
        using var response = await client.PostAsJsonAsync(
                new Uri("/api/internal/deployment/lifecycle/first-resume", UriKind.Relative),
                new { firstBootId, expectedVersion },
                cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            string? reason = null;
            try
            {
                using var refusal = JsonDocument.Parse(body);
                reason = refusal.RootElement.TryGetProperty("reason", out var value) ? value.GetString() : null;
            }
            catch (JsonException)
            {
            }
            throw InstallerException.Refused(
                "The first resume was refused and capture stays paused: " +
                (reason is { Length: <= 200 } ? reason : "a later accepted capture command holds the pause."));
        }
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InstallerException($"CameraAgent did not accept the first resume (HTTP {(int)response.StatusCode}).");
        using var document = JsonDocument.Parse(body);
        var state = document.RootElement.GetProperty("state");
        return (state.ValueKind == JsonValueKind.String ? state.GetString() ?? string.Empty : state.GetRawText(),
            document.RootElement.GetProperty("version").GetInt64());
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The client owns and disposes the handler it creates.")]
    private HttpClient CreateClient(string token)
    {
        var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }, handler is null)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 64 * 1024
        };
        client.DefaultRequestHeaders.Add("X-HVO-Installation-Token", token);
        return client;
    }
}

/// <summary>
/// <c>cameraagent first-resume</c> and <c>cameraagent silo-verify</c> (#1966). Both read only the instance's own records
/// and verify the Silo runtime before reporting; only the first resume changes anything, and only through CameraAgent's
/// atomic first-resume check.
/// </summary>
internal static class SiloCommands
{
    internal static Func<Uri, SiloFirstBootClient> ClientFactory { get; set; } = static uri => new SiloFirstBootClient(uri);

    internal static Func<DockerClient> DockerFactory { get; set; } = static () => new DockerClient(new ProcessRunner());

    public static async Task<JsonObject> ExecuteAsync(SiloCommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request.Validate();
        var instanceId = request.InstanceId!.Value;
        var paths = InstallationPaths.Create(request.ProductRoot, instanceId, ProductionCatalog.DefaultCatalogId);
        if (!File.Exists(paths.ManifestPath))
            throw InstallerException.Refused("The selected instance has no retained manifest.");
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, cancellationToken, allowSiloLayout: true)
            .ConfigureAwait(false);
        if (manifest.Silo is not { } silo)
            throw InstallerException.Refused("The selected instance does not use the Silo storage layout.");
        if (manifest.LifecycleCondition != InstanceLifecycleCondition.Installed)
            throw InstallerException.Refused("The selected Silo-layout instance is not installed.");
        paths = paths.WithCatalog(manifest.Catalog.CatalogId);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(paths.ResultPath, cancellationToken, allowSiloLayout: true)
            .ConfigureAwait(false);
        // The result names the endpoint the lifecycle token is sent to, so it must be this instance's own, correlated with
        // its manifest, before the token is read or Docker or CameraAgent is called.
        try
        {
            CameraAgentLifecycleManager.EnsureCorrelated(paths, instanceId, manifest, result, allowTransactionalDrift: false);
        }
        catch (InstallerException exception) when (!exception.IsRefusal)
        {
            throw InstallerException.Refused(
                "The instance's retained records do not correlate, so nothing was read or sent. Nothing was changed.", exception);
        }
        var token = (await File.ReadAllTextAsync(Path.Combine(paths.ConfigRoot, "lifecycle-control", "token"), cancellationToken)
            .ConfigureAwait(false)).Trim();
        CameraAgentLifecycleManager.ValidateLifecycleControlToken(manifest, token);

        var docker = DockerFactory();
        CameraAgentLifecycleManager.EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        var verification = await SiloInstallation.VerifyAsync(docker, paths, instanceId, silo, manifest.Image, cancellationToken).ConfigureAwait(false);
        var client = ClientFactory(result.Url);
        var observed = await client.ReadAsync(token, cancellationToken).ConfigureAwait(false);
        if (observed.Id != silo.FirstBootPauseId || !observed.Seeded)
            throw InstallerException.Refused("CameraAgent's first-boot pause is not the one this instance was installed with.");

        var output = new JsonObject
        {
            ["instanceId"] = instanceId.ToString("D"),
            ["storeId"] = silo.StoreId,
            ["bucket"] = silo.Bucket,
            ["storageBindingId"] = silo.StorageBindingId,
            ["siloImage"] = silo.ImageReference,
            ["siloImageId"] = silo.ImageId,
            ["siloPlatformDigest"] = silo.ImagePlatformDigest,
            ["dataRoot"] = silo.DataRoot,
            ["dataRootFileSystem"] = silo.DataRootFileSystem,
            ["writerGeneration"] = silo.WriterGeneration,
            ["writerAccessKeyId"] = verification.WriterAccessKeyId,
            ["writerRotated"] = verification.Rotated,
            ["fenceVersionId"] = verification.FenceVersionId,
            ["binding"] = "verified",
            ["headroom"] = new JsonObject
            {
                ["availableBytes"] = verification.Headroom.AvailableBytes,
                ["totalBytes"] = verification.Headroom.TotalBytes,
                ["requiredBytes"] = verification.Headroom.RequiredBytes,
                ["state"] = verification.Headroom.Ready ? "ready" : "insufficient"
            },
            ["archive"] = verification.Archive is { } archive && silo.Archive is { } configured
                ? new JsonObject
                {
                    ["endpoint"] = configured.Endpoint,
                    ["bucket"] = configured.Bucket,
                    ["state"] = archive.State switch
                    {
                        SiloArchiveState.Verified => "verified",
                        SiloArchiveState.TemporarilyUnavailable => "temporarily-unavailable",
                        _ => "refused"
                    },
                    ["reason"] = archive.Reason,
                    ["certificateMatchesInstall"] = archive.ServerCertificateSha256 is null
                        ? null
                        : archive.ServerCertificateSha256 == configured.ServerCertificateSha256
                }
                : null,
            ["readiness"] = verification.Readiness,
            ["firstBootPauseInForce"] = observed.InForce,
            ["firstResumeAccepted"] = observed.Resumed,
            ["captureState"] = observed.State,
            ["captureVersion"] = observed.Version
        };
        if (request.Kind == SiloCommandKind.Verify) return output;

        if (observed.Resumed)
        {
            output["outcome"] = "already-resumed";
            return output;
        }
        if (verification.Readiness == "not-ready")
            throw InstallerException.Refused(
                "The first resume was refused and capture stays paused: the Silo runtime is not ready " +
                "(data-root headroom below the reserve, or the archive store failed verification). Run silo-verify for details.");
        if (!observed.InForce || observed.State != "paused")
            throw InstallerException.Refused(
                "The first resume was refused and capture stays paused: a later accepted capture command holds the pause, " +
                "so the operator who placed it decides when to resume.");
        _ = await client.FirstResumeAsync(token, silo.FirstBootPauseId, observed.Version, cancellationToken).ConfigureAwait(false);
        // Report the durable state CameraAgent now holds, in the same words the status route uses.
        var after = await client.ReadAsync(token, cancellationToken).ConfigureAwait(false);
        if (!after.Resumed)
            throw new InstallerException("CameraAgent did not record the first resume.");
        output["outcome"] = "resumed";
        output["captureState"] = after.State;
        output["captureVersion"] = after.Version;
        output["firstResumeAccepted"] = true;
        output["firstBootPauseInForce"] = false;
        output["resumedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return output;
    }
}
