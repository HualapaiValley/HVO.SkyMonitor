using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// An answers file for <c>cameraagent install --answers FILE</c> (install contract section 5): everything an install
/// asks, as strict JSON with a schema version. A member it does not know is a usage error, so a misspelt answer is never
/// silently ignored. The members are those <c>--config</c> reads, under the same names, without the run flags
/// (<c>dryRun</c>, <c>resume</c>, <c>json</c>), which stay on the command line, and without the key-signed release
/// selectors, which are retired (#1309). It never holds a secret: a password is given in its own file
/// (<see cref="PasswordFile"/>) or generated. It must name its instance, so running it again addresses the same instance
/// and, with nothing different, changes nothing.
/// </summary>
internal sealed record InstallAnswers
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>The largest answers file read; a real one is well under a kilobyte.</summary>
    public const int MaximumBytes = 64 * 1024;

    public int? SchemaVersion { get; init; }
    public Guid? InstanceId { get; init; }
    public string? FriendlyName { get; init; }
    public string? OwnerEmail { get; init; }
    public string? BindAddress { get; init; }
    public int? Port { get; init; }
    public string? ProductRoot { get; init; }
    public string? CatalogBundle { get; init; }
    public string? ImageReference { get; init; }
    public string? ImageArchive { get; init; }
    public string? ImageArchiveSha256 { get; init; }
    public string? PasswordFile { get; init; }
    public double? LatitudeDegrees { get; init; }
    public double? LongitudeDegrees { get; init; }
    public double? ElevationMeters { get; init; }
    public string? TimeZoneId { get; init; }
    public bool? AcknowledgePlaintextHttp { get; init; }
    public bool? GeneratePassword { get; init; }
    public bool? NoDownload { get; init; }
    public CameraAgentReplayProfile? ReplayProfile { get; init; }
    public decimal? Cpus { get; init; }
    public string? Memory { get; init; }

    /// <summary>
    /// Reads and checks an answers file. Every problem is a usage error (exit 2) naming the file: one that cannot be
    /// read, is not JSON, has a member this installer does not know or one twice, or names no schema version, the wrong
    /// one, or no instance or camera name.
    /// </summary>
    public static InstallAnswers Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] content;
        try
        {
            using var stream = SafeFileSystem.OpenRegularFileRead(path);
            if (stream.Length > MaximumBytes)
            {
                throw new InstallUsageException($"The answers file '{path}' is larger than {MaximumBytes} bytes.");
            }
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            content = buffer.ToArray();
        }
        catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException)
        {
            throw new InstallUsageException($"The answers file '{path}' could not be read: {exception.Message}", exception);
        }
        return Parse(content, path);
    }

    /// <summary>Parses answers already read from <paramref name="path"/>.</summary>
    public static InstallAnswers Parse(ReadOnlySpan<byte> content, string path)
    {
        InstallAnswers answers;
        try
        {
            answers = JsonSerializer.Deserialize(content, InstallAnswersJsonContext.Default.InstallAnswers)
                ?? throw new InstallUsageException($"The answers file '{path}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InstallUsageException($"The answers file '{path}' is not valid: {exception.Message}", exception);
        }
        if (answers.SchemaVersion is null)
        {
            throw new InstallUsageException(
                $"The answers file '{path}' names no schemaVersion; this installer reads schema {CurrentSchemaVersion}.");
        }
        if (answers.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InstallUsageException(
                $"The answers file '{path}' is schema {answers.SchemaVersion}; this installer reads schema {CurrentSchemaVersion}.");
        }
        if (answers.InstanceId is not { } instanceId || instanceId == Guid.Empty)
        {
            throw new InstallUsageException(
                $"The answers file '{path}' must name its instanceId, so running it again addresses the same instance.");
        }
        if (string.IsNullOrWhiteSpace(answers.FriendlyName))
        {
            throw new InstallUsageException($"The answers file '{path}' must name the camera's friendlyName.");
        }
        return answers;
    }

    /// <summary>
    /// The install request these answers make, with the defaults the command line uses for every answer left out. A
    /// file that names no product root installs into the production product root this host's policy resolved, exactly
    /// as the command line does.
    /// </summary>
    public InstallRequest ToRequest(string productionProductRoot) => new()
    {
        InstanceId = InstanceId,
        FriendlyName = FriendlyName!,
        OwnerEmail = OwnerEmail ?? OwnerCredentialDefaults.Email,
        BindAddress = BindAddress ?? "127.0.0.1",
        Port = Port ?? 5130,
        ProductRoot = ProductRoot ?? productionProductRoot,
        CatalogBundle = CatalogBundle,
        ImageReference = ImageReference ?? string.Empty,
        ImageArchive = ImageArchive,
        ImageArchiveSha256 = ImageArchiveSha256,
        PasswordFile = PasswordFile,
        LatitudeDegrees = LatitudeDegrees ?? 0,
        LongitudeDegrees = LongitudeDegrees ?? 0,
        ElevationMeters = ElevationMeters ?? 0,
        TimeZoneId = TimeZoneId ?? "UTC",
        AcknowledgePlaintextHttp = AcknowledgePlaintextHttp ?? false,
        GeneratePassword = GeneratePassword ?? false,
        NoDownload = NoDownload ?? false,
        ReplayProfile = ReplayProfile ?? CameraAgentReplayProfile.InProcess,
        Cpus = Cpus,
        Memory = Memory
    };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(InstallAnswers))]
internal sealed partial class InstallAnswersJsonContext : JsonSerializerContext;
