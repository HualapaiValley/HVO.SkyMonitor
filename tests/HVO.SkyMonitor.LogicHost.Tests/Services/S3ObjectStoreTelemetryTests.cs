using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class S3ObjectStoreTelemetryTests
{
    [TestMethod]
    public void MetricsAndSpans_UseOnlyBoundedNonSecretDimensions()
    {
        const string artifactBucket = "private-artifact-bucket-504";
        const string diagnosticsBucket = "private-diagnostics-bucket-504";
        var telemetry = new ObjectStoreTelemetry(Options.Create(new CentralObjectStorageOptions
        {
            ServiceEndpoint = "secret-endpoint.internal:9000",
            ArtifactBucket = artifactBucket,
            DiagnosticsBucket = diagnosticsBucket,
            AccessKey = "secret-access-key",
            SecretKey = "secret-value"
        }));
        var measurements = new List<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ObjectStoreTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray())));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray())));
        meterListener.Start();

        var stopped = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ObjectStoreTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(activityListener);

        using (telemetry.StartOperation("get", artifactBucket))
        {
            telemetry.RecordOperation("get", "success", artifactBucket, TimeSpan.FromMilliseconds(3));
            telemetry.RecordBytes("get", "read", artifactBucket, 1024);
            telemetry.RecordRetry("get", "caller-retry", artifactBucket);
            telemetry.ChangeActiveStreams("read", 1);
            telemetry.ChangeActiveStreams("read", -1);
        }

        var allowedMetricKeys = new HashSet<string>(
            ["operation", "outcome", "bucket_role", "direction", "reason"],
            StringComparer.Ordinal);
        Assert.IsTrue(measurements.Count >= 6);
        Assert.IsTrue(measurements.SelectMany(item => item.Tags).All(tag => allowedMetricKeys.Contains(tag.Key)));
        Assert.IsFalse(ContainsForbiddenValue(measurements.SelectMany(item => item.Tags).Select(tag => tag.Value)));
        Assert.HasCount(1, stopped);
        var activity = stopped[0];
        CollectionAssert.AreEquivalent(
            new[]
            {
                "object_store.operation",
                "object_store.outcome",
                "object_store.bucket_role",
                "object_store.provider",
                "object_store.addressing_style"
            },
            activity.TagObjects.Select(tag => tag.Key).ToArray());
        Assert.IsFalse(ContainsForbiddenValue(activity.TagObjects.Select(tag => tag.Value)));
        // The provider tag is bounded to the enum's names; it can never carry an endpoint or a path.
        Assert.AreEqual("s3", activity.GetTagItem("object_store.provider"));

        bool ContainsForbiddenValue(IEnumerable<object?> values)
            => values.Select(value => value?.ToString()).Any(value => value is not null
                && (value.Contains(artifactBucket, StringComparison.Ordinal)
                    || value.Contains(diagnosticsBucket, StringComparison.Ordinal)
                    || value.Contains("secret", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task FailureLogs_DoNotExposeProviderResponseOrResourceIdentifiers()
    {
        const string endpoint = "secret-endpoint.internal:9000";
        const string bucket = "private-artifact-bucket-504";
        const string key = "private/path/object.bin";
        const string providerMessage = "provider-secret-response";
        var options = Options.Create(new CentralObjectStorageOptions
        {
            ServiceEndpoint = endpoint,
            UseTls = false,
            AddressingStyle = ObjectStorageAddressingStyle.Path,
            CredentialMode = ObjectStorageCredentialMode.Static,
            AccessKey = "secret-access-key",
            SecretKey = "secret-value",
            ArtifactBucket = bucket
        });
        using var handler = new DeniedHandler(providerMessage);
        using var httpClient = new HttpClient(handler);
        using var client = S3ObjectStoreClientFactory.Create(
            options.Value,
            new SharedHttpClientFactory(httpClient));
        var logger = new RecordingLogger<S3ObjectStore>();
        var store = new S3ObjectStore(
            client,
            new ObjectStoreTelemetry(options),
            TimeProvider.System,
            logger);

        var failure = await Assert.ThrowsExactlyAsync<ObjectStoreException>(
            () => store.StatAsync(bucket, key, CancellationToken.None)).ConfigureAwait(false);

        Assert.AreEqual(ObjectStoreFailureKind.Authorization, failure.Kind);
        Assert.IsNull(failure.InnerException);
        Assert.ContainsSingle(logger.Entries);
        Assert.AreEqual(2182, logger.Entries[0].EventId.Id);
        foreach (var forbidden in new[] { endpoint, bucket, key, providerMessage, "secret-access-key", "secret-value" })
        {
            Assert.DoesNotContain(forbidden, logger.Entries[0].Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.IsNull(logger.Entries[0].Exception);
    }

    [TestMethod]
    public async Task ReaderCallbackFailure_DoesNotEmitStorageFailureLog()
    {
        var options = Options.Create(new CentralObjectStorageOptions
        {
            ServiceEndpoint = "object-store.internal:9000",
            UseTls = false,
            AddressingStyle = ObjectStorageAddressingStyle.Path,
            CredentialMode = ObjectStorageCredentialMode.Static,
            AccessKey = "access",
            SecretKey = "secret"
        });
        using var handler = new SuccessfulGetHandler();
        using var httpClient = new HttpClient(handler);
        using var client = S3ObjectStoreClientFactory.Create(
            options.Value,
            new SharedHttpClientFactory(httpClient));
        var logger = new RecordingLogger<S3ObjectStore>();
        var store = new S3ObjectStore(
            client,
            new ObjectStoreTelemetry(options),
            TimeProvider.System,
            logger);
        var callbackFailure = new InvalidOperationException("caller failure");

        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.ReadAsync(
            CentralObjectStorageOptions.DefaultArtifactBucket,
            "object.bin",
            null,
            (_, _) => Task.FromException(callbackFailure),
            CancellationToken.None)).ConfigureAwait(false);

        Assert.AreSame(callbackFailure, observed);
        Assert.IsEmpty(logger.Entries);
    }

    private sealed class DeniedHandler(string providerMessage) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                RequestMessage = request,
                Content = new StringContent(
                    $"<Error><Code>AccessDenied</Code><Message>{providerMessage}</Message></Error>",
                    Encoding.UTF8,
                    "application/xml")
            });
    }

    private sealed class SuccessfulGetHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([1, 2, 3])
            });
    }

    private sealed class SharedHttpClientFactory(HttpClient client) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => client;
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(EventId EventId, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((eventId, formatter(state, exception), exception));
    }
}
