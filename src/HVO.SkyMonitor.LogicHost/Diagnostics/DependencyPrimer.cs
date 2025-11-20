using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.Common.Observability;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using StackExchange.Redis;

namespace HVO.SkyMonitor.LogicHost.Diagnostics;

/// <summary>
/// Generates lightweight Redis and MinIO traffic so Application Insights can observe real dependencies during development.
/// </summary>
internal sealed class DependencyPrimer
{
    private readonly IDistributedCache _cache;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DependencyPrimer> _logger;
    private readonly MinioOptions _minioOptions;
    private readonly RedisOptions _redisOptions;
    private readonly SmtpOptions _smtpOptions;

    public DependencyPrimer(
        IDistributedCache cache,
        IServiceProvider serviceProvider,
        IOptions<MinioOptions> minioOptions,
        IOptions<RedisOptions> redisOptions,
        IOptions<SmtpOptions> smtpOptions,
        ILogger<DependencyPrimer> logger)
    {
        _cache = cache;
        _serviceProvider = serviceProvider;
        _minioOptions = minioOptions.Value;
        _redisOptions = redisOptions.Value;
        _smtpOptions = smtpOptions.Value;
        _logger = logger;
    }

    public async Task PrimeAsync(CancellationToken cancellationToken)
    {
        await PrimeRedisAsync(cancellationToken).ConfigureAwait(false);
        await PrimeMinioAsync(cancellationToken).ConfigureAwait(false);
        await PrimeSmtpAsync(cancellationToken).ConfigureAwait(false);
    }

#pragma warning disable CA1031 // Best-effort priming should never throw.
    private async Task PrimeRedisAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_redisOptions.Configuration))
        {
            _logger.LogDebug("Skipping Redis priming because no configuration is set.");
            return;
        }

        var key = $"diagnostics:prime:{Guid.NewGuid():N}";
        var endpoint = NormalizeRedisEndpoint(_redisOptions.Configuration);
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
        };

        using var activity = DependencyTelemetry.StartRedisActivity("Redis dependency prime", endpoint, key);

        try
        {
            await _cache.SetStringAsync(key, "primed", options, cancellationToken).ConfigureAwait(false);
            await _cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Primed Redis cache dependency via key {Key}", key);
        }
        catch (Exception ex)
        {
            DependencyTelemetry.RecordException(activity, ex);
            _logger.LogWarning(ex, "Redis dependency priming failed.");
        }
    }

    private async Task PrimeMinioAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_minioOptions.Endpoint))
        {
            _logger.LogDebug("Skipping MinIO priming because endpoint is not configured.");
            return;
        }

        var client = _serviceProvider.GetService<IMinioClient>();
        if (client is null)
        {
            _logger.LogDebug("Skipping MinIO priming because no client is registered.");
            return;
        }

        var bucket = _minioOptions.DefaultBucket;
        var objectName = $"diagnostics/prime-{Guid.NewGuid():N}.txt";
        var endpoint = ResolveMinioEndpoint();
        var payload = Encoding.UTF8.GetBytes("prime");

        await EnsureBucketExistsAsync(client, bucket, cancellationToken).ConfigureAwait(false);

        using (var stream = new MemoryStream(payload, writable: false))
        {
            var putArgs = new PutObjectArgs()
                .WithBucket(bucket)
                .WithObject(objectName)
                .WithContentType("text/plain")
                .WithStreamData(stream)
                .WithObjectSize(stream.Length);

            using var putActivity = DependencyTelemetry.StartMinioActivity("MinIO dependency prime (put)", endpoint, bucket, objectName);
            try
            {
                await client.PutObjectAsync(putArgs, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DependencyTelemetry.RecordException(putActivity, ex);
                _logger.LogWarning(ex, "MinIO dependency priming (put) failed.");
                return;
            }
        }

        var buffer = new MemoryStream();
        var getArgs = new GetObjectArgs()
            .WithBucket(bucket)
            .WithObject(objectName)
            .WithCallbackStream(stream => stream.CopyTo(buffer));

        using (var getActivity = DependencyTelemetry.StartMinioActivity("MinIO dependency prime (get)", endpoint, bucket, objectName))
        {
            try
            {
                await client.GetObjectAsync(getArgs, cancellationToken).ConfigureAwait(false);
                buffer.Position = 0;
                _logger.LogInformation("Primed MinIO dependency by storing and retrieving object {ObjectName} in {Bucket}", objectName, bucket);
            }
            catch (Exception ex)
            {
                DependencyTelemetry.RecordException(getActivity, ex);
                _logger.LogWarning(ex, "MinIO dependency priming (get) failed.");
            }
        }
    }

    private async Task PrimeSmtpAsync(CancellationToken cancellationToken)
    {
        var emailService = _serviceProvider.GetService<IEmailNotificationService>();
        if (emailService is null)
        {
            _logger.LogDebug("Skipping SMTP priming because email service is not registered.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_smtpOptions.Host))
        {
            _logger.LogDebug("Skipping SMTP priming because host is not configured.");
            return;
        }

        var recipient = _smtpOptions.From;
        var subject = "SkyMonitor SMTP dependency prime";
        var body = $"SMTP dependency primed at {DateTimeOffset.UtcNow:O}";

        using var activity = DependencyTelemetry.StartSmtpActivity(_smtpOptions.Host, _smtpOptions.Port, recipient);
        try
        {
            await emailService.SendAsync(recipient, subject, body, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Primed SMTP dependency by sending email to {Recipient}", recipient);
        }
        catch (Exception ex)
        {
            DependencyTelemetry.RecordException(activity, ex);
            _logger.LogWarning(ex, "SMTP dependency priming failed.");
        }
    }
#pragma warning restore CA1031

    private static string? NormalizeRedisEndpoint(string? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return null;
        }

        var options = ConfigurationOptions.Parse(configuration);
        var endpoint = options.EndPoints.FirstOrDefault();
        return endpoint?.ToString();
    }

    private async Task EnsureBucketExistsAsync(IMinioClient client, string bucket, CancellationToken cancellationToken)
    {
        var exists = await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            _logger.LogInformation("Creating MinIO bucket {Bucket} for diagnostics priming", bucket);
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private string ResolveMinioEndpoint()
    {
        var scheme = _minioOptions.UseSsl ? "https" : "http";
        return $"{scheme}://{_minioOptions.Endpoint}:{_minioOptions.Port}";
    }
}
