using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The machine install record (install contract section 5): what this installer installed under the production product
/// root, one entry per instance, at <c>/etc/hvo-sky/install.json</c>. It holds no secret. Each instance's own records
/// stay authoritative: the record is rebuilt from them whenever an operation completes, and nothing in the installer
/// decides anything from it.
/// </summary>
internal sealed record InstallRecord(
    int SchemaVersion,
    string Product,
    string ProductRoot,
    string InstallerVersion,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<InstallRecordInstance> Instances)
{
    public const int CurrentSchemaVersion = 1;

    public const string ProductName = "HVO.SkyMonitor";

    public const string FileName = "install.json";

    /// <summary>Whether the two say the same, whenever each was written.</summary>
    public bool SameAs(InstallRecord? other)
        => other is not null &&
           SchemaVersion == other.SchemaVersion && Product == other.Product && ProductRoot == other.ProductRoot &&
           InstallerVersion == other.InstallerVersion && Instances.Count == other.Instances.Count &&
           Instances.Zip(other.Instances).All(static pair => pair.First.SameAs(pair.Second));
}

/// <summary>
/// One instance in the install record. Every version in it is read from the instance's own release records
/// (<see cref="ReleaseRecords.VersionOfAsync"/>), which own the installed version; the entry only says which of them it
/// is. <see cref="Version"/> is the release the instance runs; <see cref="PreviousVersion"/> is the release of the image
/// it ran before its last upgrade, which a rollback returns to, and is <see langword="null"/> after a rollback;
/// <see cref="RolledBackFrom"/> is the release a rollback left, <see langword="null"/> after an upgrade. Each is
/// <see langword="null"/> when no release names the image (an image given by <c>--image-ref</c>).
/// <see cref="LastImageChange"/> says whether the image last changed by upgrade or rollback, which decides those two.
/// </summary>
internal sealed record InstallRecordInstance(
    string Component,
    Guid InstanceId,
    string FriendlyName,
    InstanceLifecycleCondition Condition,
    string? Version,
    string? PreviousVersion,
    string? RolledBackFrom,
    string ImageId,
    string? PreviousImageId,
    Uri Url,
    ContainerResourceLimits? Resources,
    DateTimeOffset UpdatedUtc,
    InstallRecordImageChange? LastImageChange = null)
{
    public const string CameraAgent = "CameraAgent";

    /// <summary>Whether the two say the same, whenever each was written.</summary>
    public bool SameAs(InstallRecordInstance? other) => other is not null &&
        this with { UpdatedUtc = other.UpdatedUtc } == other;
}

/// <summary>How an instance's image last changed.</summary>
internal enum InstallRecordImageChange
{
    Upgrade,
    Rollback
}

/// <summary>Which operation completed, which decides how an entry's version history moves.</summary>
internal enum InstallRecordEvent
{
    /// <summary>An install, a re-run of one, or a change of its limits: the history stays as it was.</summary>
    Install,

    /// <summary>An upgrade: the version before it becomes the previous version.</summary>
    Upgrade,

    /// <summary>A rollback: the version it left is recorded as rolled back from, and there is no previous version.</summary>
    Rollback,

    /// <summary>A reinstall, uninstall, catalog change or recovery: only the condition can change.</summary>
    Settle
}

/// <summary>One change to the record: an instance's entry set, or an instance removed.</summary>
internal sealed record InstallRecordChange(InstallRecordInstance? Set, Guid? Remove, InstallProgramInstance? Programs = null)
{
    public static InstallRecordChange Of(InstallRecordInstance entry) => new(entry, null);

    public static InstallRecordChange Removing(Guid instanceId) => new(null, instanceId);
}

/// <summary>
/// Where one product root's install record is kept. The production product root's is the machine record beside the host
/// policy, checked as the policy is (<see cref="Trusted"/>). An admitted test root keeps its own, owner-only, in its
/// operations directory, so a test never writes the machine record.
/// </summary>
internal sealed record InstallRecordLocation(string Path, string ProductRoot, HostPolicyLocation? Trusted)
{
    public bool IsMachineRecord => Trusted is not null;

    /// <summary>The record of <paramref name="productRoot"/>, given the production product root this invocation resolved.</summary>
    public static InstallRecordLocation For(string productRoot, string productionProductRoot)
        => string.Equals(productRoot, productionProductRoot, StringComparison.Ordinal)
            ? Machine(HostPolicyLocation.Production, productRoot)
            : new InstallRecordLocation(
                System.IO.Path.Combine(productRoot, "operations", InstallRecord.FileName), productRoot, null);

    /// <summary>The machine record beside the policy <paramref name="policy"/> names.</summary>
    public static InstallRecordLocation Machine(HostPolicyLocation policy, string productRoot)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new InstallRecordLocation(
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(policy.PolicyPath)!, InstallRecord.FileName),
            productRoot,
            policy);
    }
}

/// <summary>
/// Reads, rebuilds and writes the install record. Each operation that completes on an instance settles that instance's
/// entry before it records its completion, so an interrupted settlement is finished by re-running the command (with
/// <c>--resume</c> where it says so). A write happens only when the entry changes, so a re-run that changes nothing
/// writes nothing. The machine record is root-owned, so the runtime user changes it through one narrow <c>sudo</c>
/// helper that accepts only the product root and the one entry, re-reads the host policy itself, and holds its lock.
/// </summary>
internal static partial class InstallRecordStore
{
    public const string CommandName = "internal-record-instance";

    internal const int MaximumRecordBytes = 1024 * 1024;

    internal const int MaximumEntryBytes = 16 * 1024;

    /// <summary>Runs the privileged helper; a test substitutes it to observe the hand-off without root.</summary>
    internal static IProcessRunner PrivilegedRunner { get; set; } = new ProcessRunner();

    private static readonly JsonSerializerOptions CompactOptions = new(InstallRecordJsonContext.Default.Options)
    {
        WriteIndented = false
    };

    /// <summary>
    /// The record as it is written, for people to read (#1384): without the default HTML-safe escaping, which writes the
    /// <c>+</c> of every installer version as <c>\u002B</c>. It is the same JSON, read the same way by every installer;
    /// only control characters, quotes and backslashes are still escaped.
    /// </summary>
    private static readonly JsonTypeInfo<InstallRecord> ReadableRecord = (JsonTypeInfo<InstallRecord>)new JsonSerializerOptions(
        InstallRecordJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    }.GetTypeInfo(typeof(InstallRecord));

    /// <summary>
    /// Rebuilds <paramref name="manifest"/>'s entry after <paramref name="kind"/> completed and writes it when it
    /// changed. Returns what happened to the entry: created, changed or unchanged.
    /// </summary>
    public static async Task<DeploymentPlanAction> SettleInstanceAsync(
        InstallRecordLocation location,
        InstallationPaths paths,
        InstanceManifest manifest,
        InstallRecordEvent kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(manifest);
        var record = await ReadAsync(location, cancellationToken).ConfigureAwait(false);
        var existing = record?.Instances.FirstOrDefault(instance => instance.InstanceId == manifest.InstanceId);
        var entry = await ProjectAsync(paths, manifest, kind, existing, cancellationToken).ConfigureAwait(false);
        var programAction = await SettleProgramsAsync(location, new(manifest.InstanceId,
            await PathPrograms.ProjectAsync(paths, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        return !await ApplyAsync(location, record, InstallRecordChange.Of(entry), cancellationToken).ConfigureAwait(false)
            ? programAction
            : existing is null ? DeploymentPlanAction.Create : DeploymentPlanAction.Change;
    }

    /// <summary>
    /// Removes <paramref name="instanceId"/>'s entry when the record has one. Returns <see cref="DeploymentPlanAction.Remove"/>
    /// when it did and <see cref="DeploymentPlanAction.Unchanged"/> when there was none.
    /// </summary>
    public static async Task<DeploymentPlanAction> RemoveInstanceAsync(
        InstallRecordLocation location,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        var record = await ReadAsync(location, cancellationToken).ConfigureAwait(false);
        await SettleProgramsAsync(location, new(instanceId, null), cancellationToken).ConfigureAwait(false);
        return await ApplyAsync(location, record, InstallRecordChange.Removing(instanceId), cancellationToken).ConfigureAwait(false)
            ? DeploymentPlanAction.Remove
            : DeploymentPlanAction.Unchanged;
    }

    /// <summary>
    /// What the entry <paramref name="manifest"/> describes after <paramref name="kind"/> completed, given the entry the
    /// record held before. Every version comes from the instance's release records, never from the entry before: the
    /// entry before decides only whether the image last changed by upgrade or rollback. Running it again after the same
    /// operation gives the same entry, so a resumed settlement is idempotent.
    /// </summary>
    internal static async Task<InstallRecordInstance> ProjectAsync(
        InstallationPaths paths,
        InstanceManifest manifest,
        InstallRecordEvent kind,
        InstallRecordInstance? existing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifest);
        var imageId = manifest.Image.ImageId;
        var previousImageId = manifest.PreviousImage?.ImageId;
        var version = await ReleaseRecords.VersionOfAsync(paths, imageId, cancellationToken).ConfigureAwait(false);
        var previousImageVersion = previousImageId is null
            ? null
            : await ReleaseRecords.VersionOfAsync(paths, previousImageId, cancellationToken).ConfigureAwait(false);
        // Only an upgrade or a rollback that moved the image changes how it last changed; anything else (a re-run, a
        // resumed settlement, an uninstall) keeps what the entry said.
        var lastImageChange = existing?.ImageId == imageId
            ? existing.LastImageChange
            : kind switch
            {
                InstallRecordEvent.Upgrade => InstallRecordImageChange.Upgrade,
                InstallRecordEvent.Rollback => InstallRecordImageChange.Rollback,
                _ => existing?.LastImageChange
            };
        var rolledBack = lastImageChange == InstallRecordImageChange.Rollback;
        var previousVersion = rolledBack ? null : previousImageVersion;
        var rolledBackFrom = rolledBack ? previousImageVersion : null;
        return new InstallRecordInstance(
            InstallRecordInstance.CameraAgent,
            manifest.InstanceId,
            manifest.FriendlyName,
            manifest.LifecycleCondition,
            version,
            previousVersion,
            rolledBackFrom,
            imageId,
            previousImageId,
            PublicUri(manifest.BindAddress, manifest.Port),
            manifest.Resources,
            DateTimeOffset.UtcNow,
            lastImageChange);
    }

    /// <summary>
    /// The record at <paramref name="location"/>, or <see langword="null"/> when there is none. One this installer cannot
    /// read (a newer schema, a damaged file, a file the policy checks refuse) fails rather than being replaced, so a
    /// record nobody can explain is never silently overwritten; removing it lets the next operation rebuild it.
    /// </summary>
    public static async Task<InstallRecord?> ReadAsync(InstallRecordLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        byte[]? content;
        if (location.Trusted is { } trusted)
        {
            content = HostProductRootPolicy.TryReadTrustedFile(trusted, location.Path, MaximumRecordBytes, "Install record");
        }
        else if (!File.Exists(location.Path))
        {
            content = null;
        }
        else
        {
            await using var stream = SafeFileSystem.OpenOwnerFileRead(location.Path);
            if (stream.Length > MaximumRecordBytes)
            {
                throw new InstallerException($"Install record '{location.Path}' exceeds {MaximumRecordBytes} bytes.");
            }
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            content = buffer.ToArray();
        }
        return content is null ? null : Parse(content, location.Path);
    }

    /// <summary>Parses and checks a record; anything this installer would not have written is refused.</summary>
    internal static InstallRecord Parse(ReadOnlySpan<byte> content, string path)
    {
        InstallRecord record;
        try
        {
            using (var document = JsonDocument.Parse(content.ToArray()))
            {
                // The schema first: a newer installer's record may hold what this one cannot keep.
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("schemaVersion", out var schema) &&
                    schema.TryGetInt32(out var number) && number > InstallRecord.CurrentSchemaVersion)
                {
                    throw new InstallerException(
                        $"Install record '{path}' was written by a newer installer (schema {number}); this one reads schema {InstallRecord.CurrentSchemaVersion}.");
                }
            }
            record = JsonSerializer.Deserialize(content, InstallRecordJsonContext.Default.InstallRecord)
                     ?? throw new InstallerException($"Install record '{path}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InstallerException(
                $"Install record '{path}' is not valid JSON this installer wrote; remove it and re-run to rebuild it.", exception);
        }
        if (Problem(record) is { } problem)
        {
            throw new InstallerException($"Install record '{path}' is invalid: {problem}; remove it and re-run to rebuild it.");
        }
        return record;
    }

    /// <summary>What is wrong with a record, or <see langword="null"/> when this installer could have written it.</summary>
    internal static string? Problem(InstallRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.SchemaVersion != InstallRecord.CurrentSchemaVersion)
        {
            return $"schemaVersion must be {InstallRecord.CurrentSchemaVersion}";
        }
        if (record.Product != InstallRecord.ProductName)
        {
            return $"product must be {InstallRecord.ProductName}";
        }
        if (HostProductRootPolicy.ProductRootTextError(record.ProductRoot) is not null &&
            ProductRootPathError(record.ProductRoot) is not null)
        {
            return "productRoot must be an absolute normalized path";
        }
        if (string.IsNullOrEmpty(record.InstallerVersion) || record.InstallerVersion.Length > 160 ||
            record.InstallerVersion.Any(char.IsControl))
        {
            return "installerVersion is invalid";
        }
        if (record.UpdatedUtc.Offset != TimeSpan.Zero || record.Instances is null)
        {
            return "updatedUtc must be UTC and instances must be present";
        }
        if (record.Instances.Select(static instance => instance?.InstanceId).Distinct().Count() != record.Instances.Count)
        {
            return "an instance is listed twice";
        }
        return record.Instances.Select(EntryProblem).FirstOrDefault(static problem => problem is not null);
    }

    /// <summary>What is wrong with one entry, or <see langword="null"/> when this installer could have written it.</summary>
    internal static string? EntryProblem(InstallRecordInstance? entry)
    {
        if (entry is null)
        {
            return "an instance entry is empty";
        }
        if (entry.Component != InstallRecordInstance.CameraAgent || entry.InstanceId == Guid.Empty)
        {
            return "an instance must be a CameraAgent with an instanceId";
        }
        if (string.IsNullOrWhiteSpace(entry.FriendlyName) || entry.FriendlyName.Length > InstallRequest.MaximumFriendlyNameLength ||
            entry.FriendlyName.Any(char.IsControl))
        {
            return $"instance {entry.InstanceId:D} has an invalid friendlyName";
        }
        if (!Enum.IsDefined(entry.Condition) || entry.LastImageChange is { } change && !Enum.IsDefined(change))
        {
            return $"instance {entry.InstanceId:D} has an invalid condition or image change";
        }
        foreach (var version in new[] { entry.Version, entry.PreviousVersion, entry.RolledBackFrom })
        {
            if (version is not null && !ProductVersion.IsValid(version))
            {
                return $"instance {entry.InstanceId:D} names '{version}', which is not a release version";
            }
        }
        if (!ImageIdPattern().IsMatch(entry.ImageId ?? string.Empty) ||
            entry.PreviousImageId is not null && !ImageIdPattern().IsMatch(entry.PreviousImageId))
        {
            return $"instance {entry.InstanceId:D} has an invalid image ID";
        }
        if (entry.Url is not { IsAbsoluteUri: true } url || url.Scheme != Uri.UriSchemeHttp || url.UserInfo.Length != 0 ||
            url.Query.Length != 0 || url.Fragment.Length != 0)
        {
            return $"instance {entry.InstanceId:D} has an invalid url";
        }
        if (entry.Resources is not null && !CameraAgentContainerResources.IsValid(entry.Resources))
        {
            return $"instance {entry.InstanceId:D} has invalid resources";
        }
        return entry.UpdatedUtc.Offset == TimeSpan.Zero ? null : $"instance {entry.InstanceId:D} has a non-UTC updatedUtc";
    }

    /// <summary>
    /// The record <paramref name="change"/> leaves: the entry set or removed, instances in instance order, and the
    /// record unchanged (the same object) when the change makes no difference.
    /// </summary>
    internal static InstallRecord Apply(
        InstallRecord? record,
        InstallRecordChange change,
        string productRoot,
        string installerVersion,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(change);
        var instances = (record?.Instances ?? []).ToList();
        if (change.Set is { } entry)
        {
            var index = instances.FindIndex(instance => instance.InstanceId == entry.InstanceId);
            if (index >= 0 && instances[index].SameAs(entry) && record!.ProductRoot == productRoot)
            {
                return record;
            }
            if (index >= 0)
            {
                instances[index] = entry;
            }
            else
            {
                instances.Add(entry);
            }
        }
        else if (change.Remove is { } removed)
        {
            if (record is null || instances.RemoveAll(instance => instance.InstanceId == removed) == 0)
            {
                return record ?? new InstallRecord(
                    InstallRecord.CurrentSchemaVersion, InstallRecord.ProductName, productRoot, installerVersion, now, []);
            }
        }
        return new InstallRecord(
            InstallRecord.CurrentSchemaVersion,
            InstallRecord.ProductName,
            productRoot,
            installerVersion,
            now,
            [.. instances.OrderBy(static instance => instance.InstanceId)]);
    }

    private static async Task<bool> ApplyAsync(
        InstallRecordLocation location,
        InstallRecord? record,
        InstallRecordChange change,
        CancellationToken cancellationToken)
    {
        var next = Apply(record, change, location.ProductRoot, InstallerBuildIdentity.Current, DateTimeOffset.UtcNow);
        if (ReferenceEquals(next, record) || change.Remove is not null && record is null)
        {
            return false;
        }
        if (location.Trusted is not { } trusted)
        {
            await SafeFileSystem.WriteJsonAtomicAsync(location.Path, next, ReadableRecord, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        if (!trusted.IsProduction || NativeLinux.getuid() == 0)
        {
            // A test policy location belongs to the invoking user, and root already holds the policy owner's authority,
            // so neither needs the privileged hand-off; the change is applied under the policy lock directly.
            ApplyTrusted(trusted, location.ProductRoot, change);
            return true;
        }
        var result = await PrivilegedRunner.RunAsync("sudo", HelperArguments(location.ProductRoot, change), cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InstallerException(
                $"The install record '{location.Path}' could not be updated: {Redaction.SafeDiagnostic(result.StandardError)}");
        }
        // The helper re-read the record under the lock, so the entry is read back as this user before it counts.
        var written = await ReadAsync(location, cancellationToken).ConfigureAwait(false);
        var settled = change.Set is { } entry
            ? written?.Instances.FirstOrDefault(instance => instance.InstanceId == entry.InstanceId)?.SameAs(entry) == true
            : written is null || written.Instances.All(instance => instance.InstanceId != change.Remove);
        if (!settled)
        {
            throw new InstallerException($"The install record '{location.Path}' does not hold the change after the privileged update.");
        }
        return true;
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the record beside <paramref name="policy"/> with the policy owner's authority,
    /// under the policy lock, for the product root the policy resolves. Returns <c>updated</c> or <c>unchanged</c>.
    /// </summary>
    internal static string ApplyTrusted(HostPolicyLocation policy, string productRoot, InstallRecordChange change)
    {
        ArgumentNullException.ThrowIfNull(policy);
        using var policyLock = HostProductRootPolicy.AcquireLock(policy);
        if (!string.Equals(HostProductRootPolicy.Resolve(policy).Path, productRoot, StringComparison.Ordinal))
        {
            throw InstallerException.Refused(
                $"The install record names only the production product root; '{productRoot}' is not the root the host policy resolves.");
        }
        var location = InstallRecordLocation.Machine(policy, productRoot);
        if (change.Programs is { } programs)
            return ApplyProgramsTrusted(location, programs);
        var content = HostProductRootPolicy.TryReadTrustedFile(policy, location.Path, MaximumRecordBytes, "Install record");
        var record = content is null ? null : Parse(content, location.Path);
        var next = Apply(record, change, productRoot, InstallerBuildIdentity.Current, DateTimeOffset.UtcNow);
        if (ReferenceEquals(next, record) || change.Remove is not null && record is null)
        {
            return HostPolicyChange.Unchanged;
        }
        HostProductRootPolicy.WriteTrustedFile(
            policy,
            location.Path,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(next, ReadableRecord) + "\n"),
            "The install record");
        return HostPolicyChange.Updated;
    }

    /// <summary>The <c>sudo</c> arguments that hand one change of <paramref name="productRoot"/>'s record to the helper.</summary>
    internal static IReadOnlyList<string> HelperArguments(string productRoot, InstallRecordChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        List<string> arguments = ["--", $"/proc/{Environment.ProcessId}/exe", CommandName, "--product-root", productRoot];
        if (change.Programs is { } programs)
        {
            arguments.Add("--programs");
            arguments.Add(JsonSerializer.Serialize(programs, CompactOptions.GetTypeInfo(typeof(InstallProgramInstance))));
        }
        else if (change.Set is { } entry)
        {
            arguments.Add("--set");
            arguments.Add(JsonSerializer.Serialize(entry, CompactOptions.GetTypeInfo(typeof(InstallRecordInstance))));
        }
        else
        {
            arguments.Add("--remove");
            arguments.Add(change.Remove!.Value.ToString("D"));
        }
        return arguments;
    }

    /// <summary>
    /// Parses exactly <c>internal-record-instance --product-root &lt;root&gt; (--set &lt;entry&gt; | --remove &lt;uuid&gt;)</c>
    /// with an entry this installer could have written.
    /// </summary>
    internal static bool TryParse(string[] args, [NotNullWhen(true)] out string? productRoot, [NotNullWhen(true)] out InstallRecordChange? change)
    {
        ArgumentNullException.ThrowIfNull(args);
        productRoot = null;
        change = null;
        if (args.Length != 5 || args[0] != CommandName || args[1] != "--product-root" ||
            HostProductRootPolicy.ProductRootTextError(args[2]) is not null && ProductRootPathError(args[2]) is not null)
        {
            return false;
        }
        if (args[3] == "--remove" && Guid.TryParseExact(args[4], "D", out var removed) && removed != Guid.Empty)
        {
            (productRoot, change) = (args[2], InstallRecordChange.Removing(removed));
            return true;
        }
        if (args[3] is not ("--set" or "--programs") || Encoding.UTF8.GetByteCount(args[4]) > MaximumEntryBytes)
        {
            return false;
        }
        try
        {
            if (args[3] == "--programs")
            {
                var programs = JsonSerializer.Deserialize(args[4], InstallRecordJsonContext.Default.InstallProgramInstance);
                if (ProgramEntryProblem(programs) is not null) return false;
                (productRoot, change) = (args[2], new(null, null, programs));
                return true;
            }
            var entry = (InstallRecordInstance?)JsonSerializer.Deserialize(
                args[4], CompactOptions.GetTypeInfo(typeof(InstallRecordInstance)));
            if (EntryProblem(entry) is not null)
            {
                return false;
            }
            (productRoot, change) = (args[2], InstallRecordChange.Of(entry!));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The <c>sudo</c>-executed record update. It writes only the machine record beside the production host policy, for
    /// the product root that policy resolves, under its lock; no argument can redirect the record or its owner.
    /// </summary>
    public static int RunInternal(string[] args)
    {
        if (NativeLinux.getuid() != 0 || !TryParse(args, out var productRoot, out var change))
        {
            return 2;
        }
        try
        {
            Console.Out.WriteLine(ApplyTrusted(HostPolicyLocation.Production, productRoot, change));
            return 0;
        }
        catch (InstallerException exception) when (exception.IsRefusal)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    internal static Uri PublicUri(string bindAddress, int port)
        => new UriBuilder("http", bindAddress is "0.0.0.0" or "::" ? "localhost" : bindAddress, port).Uri;

    /// <summary>An admitted test root need not have the policy's portable segments, only an absolute normalized path.</summary>
    private static string? ProductRootPathError(string? value)
        => value is not null && Path.IsPathFullyQualified(value) && value != "/" && !value.EndsWith('/') &&
           !value.Contains("//", StringComparison.Ordinal) && string.Equals(Path.GetFullPath(value), value, StringComparison.Ordinal) &&
           !value.Any(char.IsControl)
            ? null
            : "productRoot must be an absolute normalized path";

    [GeneratedRegex("^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageIdPattern();
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(InstallRecord))]
[JsonSerializable(typeof(InstallRecordInstance))]
[JsonSerializable(typeof(InstallProgramRecord))]
[JsonSerializable(typeof(InstallProgramInstance))]
internal sealed partial class InstallRecordJsonContext : JsonSerializerContext;
