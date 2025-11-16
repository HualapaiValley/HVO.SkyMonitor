using System.IO;
using System.Text;
using Asp.Versioning;
using HVO.SkyMonitor.Configuration;
using HVO.SkyMonitor.Models.Diagnostics;
using HVO.SkyMonitor.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.Controllers;

/// <summary>
/// Diagnostics endpoints that exercise Redis, MinIO, and SMTP infrastructure.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/diagnostics")]
[Authorize(AuthenticationSchemes = "Bearer")]
public sealed class DiagnosticsController : ControllerBase
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<DiagnosticsController> _logger;
    private readonly MinioOptions _minioOptions;
    private readonly SmtpOptions _smtpOptions;
    private readonly IServiceProvider _serviceProvider;

    public DiagnosticsController(
        IDistributedCache cache,
        IOptions<MinioOptions> minioOptions,
        IOptions<SmtpOptions> smtpOptions,
        IServiceProvider serviceProvider,
        ILogger<DiagnosticsController> logger)
    {
        _cache = cache;
        _logger = logger;
        _minioOptions = minioOptions.Value;
        _smtpOptions = smtpOptions.Value;
        _serviceProvider = serviceProvider;
    }

    [HttpPost("cache")]
    public async Task<ActionResult<CacheDiagnosticsResponse>> CacheRoundTripAsync(
        [FromBody] CacheDiagnosticsRequest request,
        CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(request.Key) ? $"diagnostics:{Guid.NewGuid():N}" : request.Key;
        var options = new DistributedCacheEntryOptions();
        if (request.ExpirationSeconds is { } ttl && ttl > 0)
        {
            options.SetAbsoluteExpiration(TimeSpan.FromSeconds(ttl));
        }

        await _cache.SetStringAsync(key, request.Value, options, cancellationToken);
        var retrieved = await _cache.GetStringAsync(key, cancellationToken);

        return Ok(new CacheDiagnosticsResponse
        {
            Key = key,
            WrittenValue = request.Value,
            RetrievedValue = retrieved,
            CacheHit = retrieved is not null
        });
    }

    [HttpPost("minio")]
    public async Task<IActionResult> MinioRoundTripAsync(
        [FromBody] StorageDiagnosticsRequest request,
        CancellationToken cancellationToken = default)
    {
        var minioClient = ResolveMinioClient();
        if (minioClient is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Problem("MinIO is not configured."));
        }

        var bucket = string.IsNullOrWhiteSpace(request.Bucket) ? _minioOptions.DefaultBucket : request.Bucket!;
        var objectName = string.IsNullOrWhiteSpace(request.ObjectName)
            ? $"diagnostics/{Guid.NewGuid():N}.txt"
            : request.ObjectName!;

        await EnsureBucketExistsAsync(minioClient, bucket, cancellationToken);

        var payload = Encoding.UTF8.GetBytes(request.Content);
        using (var writeStream = new MemoryStream(payload, writable: false))
        {
            var putArgs = new PutObjectArgs()
                .WithBucket(bucket)
                .WithObject(objectName)
                .WithContentType("text/plain")
                .WithStreamData(writeStream)
                .WithObjectSize(writeStream.Length);

            await minioClient.PutObjectAsync(putArgs, cancellationToken).ConfigureAwait(false);
        }

        var buffer = new MemoryStream();
        var getArgs = new GetObjectArgs()
            .WithBucket(bucket)
            .WithObject(objectName)
            .WithCallbackStream(stream => stream.CopyTo(buffer));

        await minioClient.GetObjectAsync(getArgs, cancellationToken).ConfigureAwait(false);

        buffer.Position = 0;
        var storedContent = Encoding.UTF8.GetString(buffer.ToArray());

        return Ok(new StorageDiagnosticsResponse
        {
            Bucket = bucket,
            ObjectName = objectName,
            Content = storedContent
        });
    }

    [HttpPost("email")]
    public async Task<IActionResult> SendDiagnosticsEmailAsync(
        [FromBody] EmailDiagnosticsRequest request,
        CancellationToken cancellationToken = default)
    {
        var emailService = _serviceProvider.GetService<IEmailNotificationService>();
        if (emailService is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, Problem("SMTP is not configured."));
        }

        await emailService.SendAsync(request.Recipient, request.Subject, request.Body, cancellationToken);

        return Ok(new EmailDiagnosticsResponse
        {
            Recipient = request.Recipient,
            Subject = request.Subject,
            Sent = true
        });
    }

    private IMinioClient? ResolveMinioClient()
    {
        var client = _serviceProvider.GetService<IMinioClient>();
        if (client is not null)
        {
            return client;
        }

        if (string.IsNullOrWhiteSpace(_minioOptions.Endpoint)
            || string.IsNullOrWhiteSpace(_minioOptions.AccessKey)
            || string.IsNullOrWhiteSpace(_minioOptions.SecretKey))
        {
            _logger.LogWarning("MinIO diagnostics requested but configuration is incomplete.");
            return null;
        }

        var builder = new MinioClient()
            .WithEndpoint(_minioOptions.Endpoint, _minioOptions.Port)
            .WithCredentials(_minioOptions.AccessKey, _minioOptions.SecretKey);

        if (_minioOptions.UseSsl)
        {
            builder = builder.WithSSL();
        }

        if (!string.IsNullOrWhiteSpace(_minioOptions.Region))
        {
            builder = builder.WithRegion(_minioOptions.Region);
        }

        return builder.Build();
    }

    private async Task EnsureBucketExistsAsync(IMinioClient client, string bucket, CancellationToken cancellationToken)
    {
        var exists = await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            _logger.LogInformation("Creating MinIO bucket {Bucket}", bucket);
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
