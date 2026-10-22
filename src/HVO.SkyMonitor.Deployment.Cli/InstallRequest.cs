using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal enum CameraAgentReplayProfile
{
    InProcess,
    LocalRunner
}

internal sealed record InstallRequest
{
    public const string DefaultProductRoot = "/var/lib/hvo-sky";

    /// <summary>The longest camera name CameraAgent accepts as its operator-facing display name.</summary>
    public const int MaximumFriendlyNameLength = 80;

    public Guid? InstanceId { get; init; }
    public required string FriendlyName { get; init; }
    /// <summary>
    /// The owner's durable email key. Optional: an omitted value, including one missing from an installer
    /// configuration file, is the non-routable <see cref="OwnerCredentialDefaults.Email"/>.
    /// </summary>
    public string OwnerEmail
    {
        get => _ownerEmail;
        init => _ownerEmail = value ?? OwnerCredentialDefaults.Email;
    }
    public string BindAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5130;
    public string ProductRoot { get; init; } = DefaultProductRoot;
    public string? CatalogBundle { get; init; }
    public string ImageReference { get; init; } = string.Empty;
    public string? ImageArchive { get; init; }
    public string? ImageArchiveSha256 { get; init; }

    /// <summary>
    /// The folder holding a release's files (<c>--release</c>): its release.json and SHA256SUMS, and its offline image
    /// archive for an archive install. The release selects the CameraAgent image, so it excludes <see cref="ImageReference"/>.
    /// </summary>
    public string? Release { get; init; }

    /// <summary>
    /// How the release's image reaches Docker (<c>--image-source</c>), or <see langword="null"/> for the default, its
    /// offline archive.
    /// </summary>
    public ReleaseImageSource? ImageSource { get; init; }
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
    public bool NoDownload { get; init; }
    public CameraAgentReplayProfile ReplayProfile { get; init; } = CameraAgentReplayProfile.InProcess;

    /// <summary>
    /// The CameraAgent container's CPU limit the operator set with <c>--cpus</c>, or <see langword="null"/> to leave it
    /// to the installer default.
    /// </summary>
    public decimal? Cpus { get; init; }

    /// <summary>
    /// The CameraAgent container's memory limit the operator set with <c>--memory</c>, such as <c>6g</c>, or
    /// <see langword="null"/> to leave it to the installer default.
    /// </summary>
    public string? Memory { get; init; }

    /// <summary>
    /// Whether this invocation may install below a product root other than <see cref="ProductionProductRoot"/>.
    /// Deny by default, decided once where the process boundary is crossed, and carried on the request rather
    /// than reread from process state, so a request validated as admitted stays admitted and one validated as
    /// refused cannot become admitted by anything that runs afterwards. It is deliberately not serialized: an
    /// installer configuration file must never be able to grant itself a non-production root.
    /// </summary>
    [JsonIgnore]
    public bool AllowTestProductRoot { get; init; }

    /// <summary>
    /// The production product root this invocation resolved at the command-line boundary: the root a valid host policy
    /// names, or <see cref="DefaultProductRoot"/> without one. Like <see cref="AllowTestProductRoot"/> it is decided
    /// once, carried on the request, and never serialized, so an installer configuration file cannot redirect it.
    /// </summary>
    [JsonIgnore]
    public string ProductionProductRoot { get; init; } = DefaultProductRoot;

    /// <summary>Whether this request installs into the production product root rather than an admitted test root.</summary>
    [JsonIgnore]
    public bool IsProductionRoot => string.Equals(ProductRoot, ProductionProductRoot, StringComparison.Ordinal);

    private readonly string _ownerEmail = OwnerCredentialDefaults.Email;

    public void Validate()
    {
        if (!Enum.IsDefined(ReplayProfile))
        {
            throw new InstallUsageException("--replay-profile must be in-process or local-runner.");
        }
        if (string.IsNullOrWhiteSpace(FriendlyName))
        {
            throw new InstallUsageException("--friendly-name is required.");
        }
        if (FriendlyName.Trim().Length > MaximumFriendlyNameLength)
        {
            throw new InstallUsageException(
                $"--friendly-name must be at most {MaximumFriendlyNameLength} characters.");
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
        ProductRootAdmission.Validate(ProductRoot, ProductionProductRoot, AllowTestProductRoot);
        if (CatalogBundle is null)
        {
            throw new InstallUsageException("--catalog-bundle is required.");
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
        ValidateImageSelection();

        if (PasswordFile is not null)
        {
            ValidateAbsolutePath(PasswordFile, "--password-file");
        }
        if (PasswordFile is not null && GeneratePassword)
        {
            throw new InstallUsageException("--password-file cannot be combined with --generate-password.");
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

        _ = ContainerResources();

        foreach (var value in new[] { FriendlyName, OwnerEmail, ProductRoot, CatalogBundle, ImageReference, ImageArchive, Release, PasswordFile, TimeZoneId, Memory })
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

    /// <summary>The CameraAgent container limits this installation asks for, each one the operator set or the default.</summary>
    internal ContainerResourceLimits ContainerResources() => CameraAgentContainerResources.Resolve(Cpus, Memory);

    /// <summary>
    /// Validates only how the image was selected. A release and an operator-supplied image are mutually exclusive: the
    /// installation either takes the immutable image from the release's verified release.json or is told exactly which
    /// image to use, never both.
    /// </summary>
    internal void ValidateImageSelection()
    {
        if (ImageSource is { } source && !Enum.IsDefined(source))
        {
            throw new InstallUsageException("--image-source must be archive or registry.");
        }
        if (Release is null)
        {
            if (ImageSource is not null)
            {
                throw new InstallUsageException("--image-source requires --release.");
            }
            if (!Regex.IsMatch(ImageReference, "^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant) &&
                !Regex.IsMatch(ImageReference, "^[^@\\s]+@sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            {
                throw new InstallUsageException("--image-ref must be an immutable digest or image ID.");
            }
            return;
        }
        ValidateAbsolutePath(Release, "--release");
        if (ImageReference.Length != 0)
        {
            throw new InstallUsageException("--image-ref cannot be combined with --release.");
        }
        if (ImageArchive is not null || ImageArchiveSha256 is not null)
        {
            throw new InstallUsageException("--image-archive and --image-archive-sha256 cannot be combined with --release.");
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

/// <summary>
/// The one rule every command applies to its product root: an absolute normalized path other than <c>/</c> that is the
/// production product root resolved at the command-line boundary, unless the invocation was admitted for a test root.
/// </summary>
internal static class ProductRootAdmission
{
    public static void Validate(string productRoot, string productionProductRoot, bool allowTestProductRoot)
    {
        ArgumentNullException.ThrowIfNull(productRoot);
        if (!Path.IsPathFullyQualified(productRoot) || productRoot.Contains("//", StringComparison.Ordinal) ||
            Path.GetFullPath(productRoot) != productRoot.TrimEnd('/') || productRoot == "/")
        {
            throw new InstallUsageException("--product-root must be an absolute normalized path other than root.");
        }
        if (!string.Equals(productRoot, productionProductRoot, StringComparison.Ordinal) && !allowTestProductRoot)
        {
            throw new InstallUsageException(
                $"--product-root must be the production product root '{productionProductRoot}' outside isolated tests.");
        }
    }
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

    /// <summary>
    /// The usage the command line attached to a mistake made in it, printed after the reason. An exception raised
    /// by an operation already under way has none.
    /// </summary>
    public string? Usage { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(InstallRequest))]
internal sealed partial class InstallRequestJsonContext : JsonSerializerContext;
