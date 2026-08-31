using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using Asp.Versioning;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Models.Diagnostics;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Controllers;

/// <summary>
/// Diagnostics endpoints that exercise Redis, object storage, and SMTP infrastructure.
/// </summary>
[SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Controllers must remain public for routing.")]
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/diagnostics")]
[Authorize(Policy = AuthorizationPolicyNames.BearerAdmin)]
public sealed class DiagnosticsController : ControllerBase
{
    private readonly IDistributedCache _cache;
    private readonly CentralObjectStorageOptions _storageOptions;
    private readonly SmtpOptions _smtpOptions;
    private readonly IServiceProvider _serviceProvider;

    public DiagnosticsController(
        IDistributedCache cache,
        IOptions<CentralObjectStorageOptions> storageOptions,
        IOptions<SmtpOptions> smtpOptions,
        IServiceProvider serviceProvider,
        ILogger<DiagnosticsController> logger)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);
        ArgumentNullException.ThrowIfNull(smtpOptions);
        _cache = cache;
        _storageOptions = storageOptions.Value;
        _smtpOptions = smtpOptions.Value;
        _serviceProvider = serviceProvider;
    }

    [HttpPost("cache")]
    public async Task<ActionResult<CacheDiagnosticsResponse>> CacheRoundTripAsync(
        [FromBody] CacheDiagnosticsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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

    [HttpPost("object-storage")]
    public async Task<IActionResult> ObjectStorageRoundTripAsync(
        [FromBody] StorageDiagnosticsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var objectStore = _serviceProvider.GetRequiredService<IObjectStore>();
        var bucket = _storageOptions.DiagnosticsBucket;
        if (!string.IsNullOrWhiteSpace(request.Bucket) && !string.Equals(request.Bucket, bucket, StringComparison.Ordinal))
        {
            return Problem(
                detail: $"Only the configured SkyMonitor diagnostics bucket '{bucket}' may be used.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var objectName = string.IsNullOrWhiteSpace(request.ObjectName)
            ? $"diagnostics/{Guid.NewGuid():N}.txt"
            : request.ObjectName!;

        if (!await objectStore.BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                Problem("The configured diagnostics bucket is unavailable."));
        }

        var payload = Encoding.UTF8.GetBytes(request.Content);
        using (var writeStream = new MemoryStream(payload, writable: false))
        {
            await objectStore.PutAsync(
                bucket, objectName, writeStream, writeStream.Length, "text/plain", cancellationToken)
                .ConfigureAwait(false);
        }

        var metadata = await objectStore.StatAsync(bucket, objectName, cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        await objectStore.ReadAsync(
            bucket,
            objectName,
            metadata.Generation,
            (stream, token) => stream.CopyToAsync(buffer, token),
            cancellationToken).ConfigureAwait(false);

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
        ArgumentNullException.ThrowIfNull(request);

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

}
