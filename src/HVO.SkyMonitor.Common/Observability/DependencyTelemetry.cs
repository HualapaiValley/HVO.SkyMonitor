using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HVO.SkyMonitor.Common.Observability;

/// <summary>
/// Centralized helpers for emitting dependency Activities so Azure Monitor can build Application Map edges.
/// </summary>
public static class DependencyTelemetry
{
    public const string ActivitySourceName = "HVO.SkyMonitor.Dependencies";

    private static readonly ActivitySource DependencySource = new(ActivitySourceName);

    public static Activity? StartPostgreSqlActivity(string operation, string? dataSource, string? database, string? statement)
    {
        return StartDatabaseActivity(
            operation,
            dbSystem: "postgresql",
            dataSource,
            database,
            statement);
    }

    public static Activity? StartRedisActivity(string operation, string? endpoint, string? key)
    {
        var tags = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(key))
        {
            tags["db.redis.key"] = key;
        }

        return StartDatabaseActivity(
            operation,
            dbSystem: "redis",
            dataSource: endpoint,
            database: null,
            statement: null,
            extraTags: tags);
    }

    public static Activity? StartMinioActivity(string operation, string endpoint, string bucket, string? objectName)
    {
        var activity = DependencySource.StartActivity(operation, ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("peer.service", "minio");
        activity.SetTag("net.peer.name", endpoint);
        activity.SetTag("storage.bucket", bucket);
        if (!string.IsNullOrWhiteSpace(objectName))
        {
            activity.SetTag("storage.object", objectName);
        }

        return activity;
    }

    public static Activity? StartSmtpActivity(string host, int port, string recipient)
    {
        var activity = DependencySource.StartActivity("SMTP send", ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("peer.service", "smtp");
        activity.SetTag("net.peer.name", host);
        activity.SetTag("net.peer.port", port);
        activity.SetTag("messaging.system", "smtp");
        activity.SetTag("messaging.destination", recipient);
        return activity;
    }

    public static void RecordException(Activity? activity, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.SetTag("otel.status_code", "ERROR");
        activity.SetTag("otel.status_description", exception.Message);
    }

    private static Activity? StartDatabaseActivity(
        string operation,
        string dbSystem,
        string? dataSource,
        string? database,
        string? statement,
        IReadOnlyDictionary<string, object?>? extraTags = null)
    {
        var activity = DependencySource.StartActivity(operation, ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("peer.service", dbSystem);
        activity.SetTag("db.system", dbSystem);
        if (!string.IsNullOrWhiteSpace(dataSource))
        {
            activity.SetTag("net.peer.name", dataSource);
        }

        if (!string.IsNullOrWhiteSpace(database))
        {
            activity.SetTag("db.name", database);
        }

        if (!string.IsNullOrWhiteSpace(statement))
        {
            activity.SetTag("db.statement", statement);
        }

        if (extraTags is not null)
        {
            foreach (var (key, value) in extraTags)
            {
                activity.SetTag(key, value);
            }
        }

        return activity;
    }
}
