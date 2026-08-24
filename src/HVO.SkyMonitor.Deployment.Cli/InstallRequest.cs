using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Deployment;

internal sealed record InstallRequest
{
    public const string DefaultProductRoot = "/var/lib/hvo/skymonitor";

    public Guid? InstanceId { get; init; }
    public required string FriendlyName { get; init; }
    public required string OwnerEmail { get; init; }
    public string BindAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5130;
    public string ProductRoot { get; init; } = DefaultProductRoot;
    public required string CatalogBundle { get; init; }
    public required string ImageReference { get; init; }
    public string? ImageArchive { get; init; }
    public string? ImageArchiveSha256 { get; init; }
    public string? PasswordFile { get; init; }
    public double LatitudeDegrees { get; init; }
    public double LongitudeDegrees { get; init; }
    public double ElevationMeters { get; init; }
    public string TimeZoneId { get; init; } = "UTC";
    public bool AcknowledgePlaintextHttp { get; init; }
    public bool DryRun { get; init; }
    public bool Resume { get; init; }
    public bool Json { get; init; }
    public bool GeneratePassword { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FriendlyName))
        {
            throw new InstallUsageException("--friendly-name is required.");
        }

        if (!Regex.IsMatch(OwnerEmail, "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", RegexOptions.CultureInvariant))
        {
            throw new InstallUsageException("--owner-email must be a valid email address.");
        }

        if (!IPAddress.TryParse(BindAddress, out var address))
        {
            throw new InstallUsageException("--bind-address must be an IP address.");
        }
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new InstallUsageException("--bind-address currently supports IPv4 addresses only.");
        }

        if (!IPAddress.IsLoopback(address) && !AcknowledgePlaintextHttp)
        {
            throw new InstallUsageException("Non-loopback plaintext HTTP requires --acknowledge-plaintext-http.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new InstallUsageException("--port must be between 1 and 65535.");
        }

        ValidateAbsolutePath(ProductRoot, "--product-root");
        if (!string.Equals(ProductRoot, DefaultProductRoot, StringComparison.Ordinal) &&
            !string.Equals(
                Environment.GetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT"),
                "1",
                StringComparison.Ordinal))
        {
            throw new InstallUsageException("--product-root must be /var/lib/hvo/skymonitor outside isolated tests.");
        }
        ValidateAbsolutePath(CatalogBundle, "--catalog-bundle");
        if (ImageArchive is not null)
        {
            ValidateAbsolutePath(ImageArchive, "--image-archive");
            if (!IsSha256(ImageArchiveSha256))
            {
                throw new InstallUsageException("--image-archive-sha256 is required with --image-archive.");
            }
        }

        if (PasswordFile is not null)
        {
            ValidateAbsolutePath(PasswordFile, "--password-file");
        }
        if (PasswordFile is not null && GeneratePassword)
        {
            throw new InstallUsageException("--generate-password cannot be combined with --password-file.");
        }

        if (!Regex.IsMatch(ImageReference, "^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant) &&
            !Regex.IsMatch(ImageReference, "^[^@\\s]+@sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant))
        {
            throw new InstallUsageException("--image-ref must be an immutable digest or image ID.");
        }

        if (!double.IsFinite(LatitudeDegrees) || !double.IsFinite(LongitudeDegrees) || !double.IsFinite(ElevationMeters) ||
            LatitudeDegrees is < -90 or > 90 || LongitudeDegrees is < -180 or > 180)
        {
            throw new InstallUsageException("Observatory coordinates are outside their valid ranges.");
        }
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new InstallUsageException("--time-zone is not installed on this host.", exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new InstallUsageException("--time-zone is invalid on this host.", exception);
        }

        foreach (var value in new[] { FriendlyName, OwnerEmail, ProductRoot, CatalogBundle, ImageReference, ImageArchive, PasswordFile, TimeZoneId })
        {
            if (value?.Any(char.IsControl) == true)
            {
                throw new InstallUsageException("Installer text and path inputs must not contain control characters.");
            }
        }

        if (Resume && InstanceId is null)
        {
            throw new InstallUsageException("--resume requires --instance-id.");
        }
    }

    private static void ValidateAbsolutePath(string path, string option)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains("//", StringComparison.Ordinal))
        {
            throw new InstallUsageException($"{option} must be an absolute normalized path.");
        }

        var normalized = Path.GetFullPath(path);
        if (!string.Equals(normalized, path.TrimEnd('/'), StringComparison.Ordinal) || normalized == "/")
        {
            throw new InstallUsageException($"{option} must be an absolute normalized path other than root.");
        }
    }

    private static bool IsSha256(string? value)
        => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
}

internal sealed class InstallUsageException : Exception
{
    public InstallUsageException()
    {
    }

    public InstallUsageException(string message)
        : base(message)
    {
    }

    public InstallUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(InstallRequest))]
internal sealed partial class InstallRequestJsonContext : JsonSerializerContext;
