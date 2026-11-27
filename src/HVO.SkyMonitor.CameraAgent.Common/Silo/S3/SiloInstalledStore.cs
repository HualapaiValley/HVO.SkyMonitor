using System.Text.Json;
using Amazon.S3;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo.S3;

/// <summary>A writer client owned by one CameraAgent process. The installer verifies the pinned sync-on store profile.</summary>
internal sealed class SiloInstalledStore : IDisposable
{
    private const int MaximumBindingBytes = 4096;
    private readonly AmazonS3Client? _client;
    private readonly Func<CancellationToken, ValueTask<bool>> _hasVersioning;
    private readonly Func<SiloExactLocator, long?>? _exactAllocation;
    private readonly SiloInstalledSettings _settings;

    public SiloInstalledStore(SiloInstalledSettings settings)
    {
        _settings = settings;
        var descriptor = new SiloStoreDescriptor(settings.Binding.StoreId, settings.Binding.Bucket,
            SiloStoreClass.Local, SiloRetrieval.Online, PersistenceQualified: true);
        var client = SiloS3ObjectStore.CreateClient(settings.Endpoint, settings.AccessKey, settings.SecretKey);
        _client = client;
        var store = new SiloS3ObjectStore(client, descriptor, TimeSpan.FromSeconds(30));
        Store = store;
        _hasVersioning = store.HasVersioningAsync;
    }

    internal SiloInstalledStore(SiloInstalledSettings settings, ISiloObjectStore store,
        Func<CancellationToken, ValueTask<bool>> hasVersioning, Func<SiloExactLocator, long?>? exactAllocation = null)
    {
        _settings = settings;
        Store = store;
        _hasVersioning = hasVersioning;
        _exactAllocation = exactAllocation;
    }

    public ISiloObjectStore Store { get; }

    public async ValueTask VerifyAsync(CancellationToken cancellationToken)
    {
        if (!await _hasVersioning(cancellationToken).ConfigureAwait(false))
            throw BindingRefused("The installed Silo bucket is not versioned.");

        using var marker = await ReadBindingDocumentAsync(CameraAgentSiloBinding.MarkerKey, cancellationToken).ConfigureAwait(false);
        using var fence = await ReadBindingDocumentAsync(CameraAgentSiloBinding.FenceKey, cancellationToken).ConfigureAwait(false);
        var binding = _settings.Binding;
        if (!HasBinding(marker.RootElement, CameraAgentSiloBinding.MarkerFormat, binding) ||
            !Matches(marker.RootElement, "stateContract", CameraAgentSiloBinding.StateContract) ||
            !Matches(marker.RootElement, "storeId", binding.StoreId) || !Matches(marker.RootElement, "bucket", binding.Bucket))
            throw BindingRefused("The Silo marker does not identify this installation.");

        if (!HasBinding(fence.RootElement, CameraAgentSiloBinding.FenceFormat, binding) ||
            !Matches(fence.RootElement, "writerAccessKeyId", _settings.AccessKey) ||
            !fence.RootElement.TryGetProperty("writerGeneration", out var generation) ||
            generation.ValueKind != JsonValueKind.Number || !generation.TryGetInt64(out var value) || value != 1)
            throw BindingRefused("The currently installed Silo writer does not own the store fence.");
    }

    private async ValueTask<JsonDocument> ReadBindingDocumentAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var current = await SiloVerifier.ReadCurrentAsync(Store, key, MaximumBindingBytes, cancellationToken).ConfigureAwait(false);
            if (current is not { } found)
                throw BindingRefused("An installed Silo binding document is missing.");
            return JsonDocument.Parse(found.Bytes);
        }
        catch (Exception exception) when (exception is JsonException or SiloInvalidObjectException)
        {
            throw BindingRefused("An installed Silo binding document is invalid.");
        }
    }

    private static bool HasBinding(JsonElement root, string format, SiloStorageBinding binding) =>
        Matches(root, "format", format) && Matches(root, "instanceId", binding.InstanceId) &&
        Matches(root, "storageBindingId", binding.BindingId);

    private static bool Matches(JsonElement root, string key, string expected) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String && property.ValueEquals(expected);

    private static SiloConflictException BindingRefused(string message) => new("silo.installed-binding-refused", message);

    internal long? ReadExactAllocationBytes(SiloExactLocator locator) => _exactAllocation?.Invoke(locator);

    public void Dispose() => _client?.Dispose();
}
