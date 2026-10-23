using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Containers;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.MsSql;

namespace HVO.SkyMonitor.IntegrationTests;

// Test evidence only. This is neither a supported recovery-set format nor a runtime admission API.
internal sealed partial class JoinedRecoveryFixture
{
    internal static readonly string[] Buckets = ["skymonitor-artifacts", "skymonitor-diagnostics"];
    private static readonly DateTimeOffset Cutoff = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private const string UserId = "joined-recovery-fixture";
    private readonly string runId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string password = "Hvo1630_aA1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly Dictionary<string, byte[]> expectedHost = new(StringComparer.Ordinal);
    private readonly List<ObjectFact> expectedObjects = [];
    private readonly Dictionary<string, string> expectedClaims = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> timings = new(StringComparer.Ordinal);
    private MsSqlContainer container = null!;
    private string connectionString = string.Empty;
    private string root = string.Empty;
    private string sourceDatabase = string.Empty;
    private string targetDatabase = string.Empty;
    private byte[] sqlBackup = [];
    private string sqlFacts = string.Empty;
    private string phase = "setup";

    internal string SourceObjects => Path.Combine(root, "source-objects");
    internal string ObjectBackup => Path.Combine(root, "object-backup");
    private string SourceHost => Path.Combine(root, "source-host");
    private string BackupPath => "/var/opt/mssql/data/hvo1630_" + runId + ".bak";

    internal async Task RunAsync()
    {
        root = Directory.CreateTempSubdirectory("hvo-1630-").FullName;
        sourceDatabase = "Hvo1630_" + runId;
        targetDatabase = "HvoVerify_" + runId;
        container = new MsSqlBuilder(IntegrationTestFixture.SqlServerImage).WithPassword(password)
            .WithLabel("hvo.issue", "1630").WithLabel("hvo.fixture-run", runId)
            .WithCleanUp(false).Build();
        await MeasureAsync("container-start", async () =>
        {
            await container.StartAsync();
            connectionString = new SqlConnectionStringBuilder(container.GetConnectionString()) { Pooling = false }.ConnectionString;
        });
        await MeasureAsync("canonical-seed", SeedAsync);
        await MeasureAsync("sql-backup-restore-checkdb", BackupSqlAsync);
        await MeasureAsync("object-backup", async () =>
        {
            await FilesystemObjectBackup.BackupAsync(SourceObjects, Buckets, ObjectBackup, TimeProvider.System, CancellationToken.None);
        });
        RestoredFiles positive = null!;
        await MeasureAsync("object-host-restore", async () => positive = await RestoreFilesAsync("positive"));
        Require(await FilesystemObjectBackup.VerifyAsync(ObjectBackup, positive.Objects, CancellationToken.None) == 0, "offline-verify");
        await MeasureAsync("joined-admission", async () => Require(await AdmitAsync(positive) == "admitted", "positive-admission"));
        await MeasureAsync("refusals", async () => await ExerciseRefusalsAsync(positive));
        await EmitEvidenceAsync();
        phase = "cleanup";
        // Only this disposable container and this exact run directory are owned by this fixture.
        await container.DisposeAsync();
        Require(container.State == TestcontainersStates.Undefined, "container-cleanup");
        Directory.Delete(root, recursive: true);
        Require(!Directory.Exists(root), "filesystem-cleanup");
        Console.WriteLine("issue1630 cleanup: one owned SQL container (source/target databases and native backup), one owned filesystem tree removed; no shared services used");
    }

    // Called at the outer test boundary; raw exceptions, endpoints, paths and key material never reach MSTest.
    internal string SafeFailure(Exception exception) => JsonSerializer.Serialize(new
    {
        Phase = phase,
        Category = exception switch
        {
            SqlException => "sql",
            SanitizedScriptException => "sql-script",
            IOException => "io",
            ObjectStoreException storage => ObjectStoreException.GetOutcome(storage.Kind),
            FixtureFailure failure => failure.Code,
            _ => "fixture"
        },
        SqlNumber = exception is SqlException sql ? sql.Number
            : exception is SanitizedScriptException script ? script.SqlErrorNumber : 0,
        ResourcesRetained = true
    });

    internal void RetainPrivateAttribution()
    {
        // No password/key bytes. This local receipt is private and is never attached to test output.
        File.WriteAllText(Path.Combine(root, "private-attribution.json"), JsonSerializer.Serialize(new
        {
            Run = runId, ContainerId = container.Id, SourceDatabase = sourceDatabase,
            TargetDatabase = targetDatabase, Backup = BackupPath, Root = root, Phase = phase
        }));
    }

    private async Task SeedAsync()
    {
        phase = "canonical-seed";
        CreateObjectRoot(SourceObjects);
        Directory.CreateDirectory(SourceHost);
        using (var keys = new ServiceCollection().AddDataProtection()
                   .SetApplicationName("hvo-1630-fixture")
                   .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(SourceHost, "keys")))
                   .Services.BuildServiceProvider())
        {
            var key = keys.GetRequiredService<IKeyManager>().CreateNewKey(Cutoff, Cutoff.AddYears(10));
            expectedClaims.Add("key", key.KeyId.ToString("D", CultureInfo.InvariantCulture));
        }
        var instance = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(SourceHost, "instance.json"), JsonSerializer.Serialize(new { Instance = instance }));
        // Public configuration carries a reference only; the ephemeral SQL password is never serialized.
        File.WriteAllText(Path.Combine(SourceHost, "config.json"), JsonSerializer.Serialize(new
        {
            Instance = instance, Cutoff, Provider = "Filesystem", Buckets,
            CredentialReference = "ephemeral-test-credential", KeyReference = expectedClaims["key"],
            CatalogId = "hyg-v42-fixture", PackageVersion = "hyg-v42-fixture-1"
        }));
        using (var catalog = CatalogFixtureInstallation.Create(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v42-bright-stars.sqlite")))
        {
            CopyTree(Path.Combine(catalog.Root, "versions"), Path.Combine(SourceHost, "catalog"));
        }
        foreach (var file in Directory.EnumerateFiles(SourceHost, "*", SearchOption.AllDirectories))
            expectedHost.Add(Path.GetRelativePath(SourceHost, file), await File.ReadAllBytesAsync(file));
        expectedClaims.Add("instance", instance);
        expectedClaims.Add("cutoff", Cutoff.ToString("O", CultureInfo.InvariantCulture));
        expectedClaims.Add("config", Hash(expectedHost["config.json"]));
        expectedClaims.Add("catalog", Hash(expectedHost[Path.Combine("catalog", "hyg-v42-fixture-1", "manifest.json")]));
        expectedClaims.Add("catalog-data", Hash(expectedHost[Path.Combine("catalog", "hyg-v42-fixture-1", "hyg_v42.sqlite")]));

        using (var store = OpenStore(SourceObjects))
        {
            for (var i = 0; i < 3; i++)
            {
                var bucket = Buckets[i == 2 ? 1 : 0];
                var key = i == 2 ? "fixture/status.json" : $"fixture/artifact-{i}.bin";
                var bytes = Encoding.UTF8.GetBytes(i == 2 ? "{\"status\":\"fixture\"}" : $"hvo-1630-artifact-{i}\n");
                var type = i == 2 ? "application/json" : "application/octet-stream";
                await store.PutAsync(bucket, key, new MemoryStream(bytes), bytes.Length, type, CancellationToken.None);
                var metadata = await store.StatAsync(bucket, key, CancellationToken.None);
                expectedObjects.Add(new(bucket, key, metadata.Generation, type, bytes.Length, Hash(bytes), metadata.LastModifiedUtc));
            }
        }
        await using var db = OpenDb(sourceDatabase);
        await db.Database.MigrateAsync();
        _ = await DatabaseSeeder.ConvergeCanonicalCentralGraphChainAsync(db,
            new CentralProcessingGraphNodeRegistry(new CentralDerivativeRecipeCatalog()),
            CanonicalCentralGraphSeedChain.Revisions, DatabaseSeeder.CreateBasicCentralProcessingGraph(), Cutoff, NullLogger.Instance);
        var user = new ApplicationUser { Id = UserId, UserName = "fixture", SecurityStamp = Guid.NewGuid().ToString("N") };
        user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, password);
        db.Users.Add(user);
        foreach (var claim in expectedClaims)
            db.UserClaims.Add(new IdentityUserClaim<string> { UserId = UserId, ClaimType = claim.Key, ClaimValue = claim.Value });
        var frame = new CentralFrame
        {
            Id = Guid.Parse("16300000-0000-0000-0000-000000000001"),
            RegistrationId = Guid.Parse("16300000-0000-0000-0000-000000000002"),
            DevicePublicId = Guid.Parse("16300000-0000-0000-0000-000000000003"),
            ObservatoryId = Guid.Parse("16300000-0000-0000-0000-000000000004"),
            FrameId = Guid.Parse("16300000-0000-0000-0000-000000000005"),
            AgentId = "joined-recovery-fixture", CapturedAtUtc = Cutoff, FirstReceivedAtUtc = Cutoff
        };
        for (var i = 0; i < 2; i++)
        {
            var fact = expectedObjects[i];
            frame.Artifacts.Add(new CentralArtifact
            {
                Id = Guid.Parse($"16300000-0000-0000-0001-{i + 1:D12}"),
                ArtifactId = Guid.Parse($"16300000-0000-0000-0002-{i + 1:D12}"),
                DevicePublicId = frame.DevicePublicId, Frame = frame, CentralFrameId = frame.Id,
                Role = i == 0 ? FrameArtifactRole.Raw : FrameArtifactRole.Preview,
                RecipeVersion = "fixture-1", ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
                MediaType = fact.ContentType, ByteLength = fact.Length, ChecksumSha256 = fact.Sha256,
                StorageReference = $"object://{fact.Bucket}/{fact.Key}", ReceivedAtUtc = Cutoff,
                IdempotencyKey = Hash(Encoding.UTF8.GetBytes("artifact-" + i.ToString(CultureInfo.InvariantCulture)))
            });
        }
        db.CentralFrames.Add(frame);
        await db.SaveChangesAsync();
        sqlFacts = await ReadSqlFactsAsync(sourceDatabase);
    }

    private async Task BackupSqlAsync()
    {
        phase = "sql-backup-restore-checkdb";
        await using var master = new SqlConnection(connectionString);
        await master.OpenAsync();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DatabaseName"] = sourceDatabase, ["RunId"] = runId,
            ["ApprovedBackupDirectory"] = "/var/opt/mssql/data", ["BackupFilePrefix"] = "hvo1630_",
            ["BackupFilePath"] = BackupPath, ["InvariantSchema"] = "dbo", ["InvariantTable"] = "AspNetUserClaims",
            ["InvariantKeyColumn"] = "Id", ["InvariantTextColumn"] = "ClaimValue", ["InvariantNumberColumn"] = "Id",
            ["MetadataRetentionDeclaredDays"] = "1", ["MetadataRetentionControlReference"] = "issue1630-disposable-owner-cleanup"
        };
        var result = await SqlOperationsIssue255Tests.ExecuteScriptAsync(master, "backup-restore-checkdb.sql", variables, RetainTarget);
        var row = result.ResultSets.SelectMany(set => set.Rows).Single(item => item.ContainsKey("copy_only"));
        Require(Convert.ToInt32(row["copy_only"], CultureInfo.InvariantCulture) == 1 && Equals(row["state"], "passed"), "sql-native-result");
        Require(Equals(row["source_canonical_sha256"], row["restored_canonical_sha256"]), "sql-invariant");
        Console.WriteLine("issue1630 native toolkit: " + JsonSerializer.Serialize(row));
        sqlBackup = await container.ReadFileAsync(BackupPath);
        Require(await ReadSqlFactsAsync(targetDatabase) == sqlFacts, "restored-sql-facts");
    }

    private static string RetainTarget(string script)
    {
        // The shipped primitive drops its verification database after CHECKDB/invariants. Replace only
        // that normal-path drop, leaving backup, FILELISTONLY/MOVE, CHECKDB and failure handling intact.
        const string drop = "    EXEC sys.sp_executesql @dropSql;";
        Require(script.Split(drop, StringSplitOptions.None).Length == 2, "toolkit-retain-anchor");
        return script.Replace(drop, "    PRINT N'Test-only joined validation retains its disposable target.';", StringComparison.Ordinal);
    }

    internal async Task<RestoredFiles> RestoreFilesAsync(string label)
    {
        var target = Path.Combine(root, label);
        var objects = Path.Combine(target, "objects");
        CreateObjectRoot(objects);
        await FilesystemObjectBackup.RestoreAsync(ObjectBackup, objects, Buckets, CancellationToken.None);
        CopyTree(SourceHost, Path.Combine(target, "host"));
        await File.WriteAllBytesAsync(Path.Combine(target, "sql.bak"), sqlBackup);
        return new(target, objects, Path.Combine(target, "host"), Path.Combine(target, "sql.bak"));
    }

    internal async Task<string> AdmitAsync(RestoredFiles files)
    {
        // Read-only. Expected facts were retained before backup; candidate inventory/config never
        // supplies the expected values. A matching self-checksum alone cannot admit a mixed cutoff.
        if (!File.Exists(files.SqlBackup)) return "sql-component-missing";
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(await File.ReadAllBytesAsync(files.SqlBackup)), SHA256.HashData(sqlBackup)))
            return "sql-component-altered";
        try { if (await ReadSqlFactsAsync(targetDatabase) != sqlFacts) return "sql-facts"; }
        catch (FixtureFailure) { return "sql-facts"; }
        foreach (var expected in expectedHost)
        {
            var path = Path.Combine(files.Host, expected.Key);
            if (!File.Exists(path)) return "host-component-missing";
            if (!CryptographicOperations.FixedTimeEquals(await File.ReadAllBytesAsync(path), expected.Value))
                return expected.Key.StartsWith("keys" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "key-identity"
                    : expected.Key.StartsWith("catalog" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "catalog-identity"
                    : expected.Key == "instance.json" ? "instance-identity" : "config-identity";
        }
        if (Directory.EnumerateFiles(files.Host, "*", SearchOption.AllDirectories).Count() != expectedHost.Count) return "host-inventory";
        // Offline verifier is deliberately not used during admission: it takes an exclusive maintenance
        // lock. Inspect committed bytes without opening/reconciling/mutating the provider under test.
        foreach (var fact in expectedObjects)
        {
            var descriptorPath = Path.Combine(files.Objects, FilesystemObjectLayout.DescriptorRelativePath(fact.Bucket, FilesystemObjectLayout.KeyHash(fact.Key)));
            if (!File.Exists(descriptorPath)) return "object-component-missing";
            FilesystemObjectDescriptor? descriptor;
            try { descriptor = JsonSerializer.Deserialize<FilesystemObjectDescriptor>(await File.ReadAllTextAsync(descriptorPath), FilesystemObjectLayout.DescriptorJson); }
            catch (JsonException) { return "object-descriptor"; }
            if (descriptor is null || descriptor.Key != fact.Key || descriptor.Generation != fact.Generation
                || descriptor.ContentType != fact.ContentType || descriptor.Length != fact.Length
                || descriptor.Sha256 != fact.Sha256 || !descriptor.ModifiedUtc.EqualsExact(fact.ModifiedUtc)
                || !descriptor.IsWellFormed(fact.Key)) return "object-identity";
            var data = Path.Combine(files.Objects, FilesystemObjectLayout.DataRelativePath(fact.Bucket, FilesystemObjectLayout.KeyHash(fact.Key), fact.Generation));
            if (!File.Exists(data)) return "referenced-generation-missing";
            var bytes = await File.ReadAllBytesAsync(data);
            if (bytes.LongLength != fact.Length || Hash(bytes) != fact.Sha256) return "referenced-generation-altered";
        }
        var descriptors = Buckets.Sum(bucket => Directory.EnumerateFiles(Path.Combine(files.Objects, bucket), "*" + FilesystemObjectLayout.DescriptorSuffix, SearchOption.AllDirectories).Count());
        return descriptors == expectedObjects.Count ? "admitted" : "object-inventory";
    }

    private async Task<string> ReadSqlFactsAsync(string database, bool validate = true)
    {
        await using var db = OpenDb(database);
        var claims = await db.UserClaims.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.UserId, item.ClaimType, item.ClaimValue }).ToArrayAsync();
        var artifacts = await db.CentralArtifacts.AsNoTracking().OrderBy(item => item.Id).Select(item => new
        {
            item.Id, item.ArtifactId, item.CentralFrameId, item.DevicePublicId, item.Role, item.RecipeVersion,
            item.MediaType, item.ByteLength, item.ChecksumSha256, item.StorageReference, item.ReceivedAtUtc,
            item.ObjectState, item.ReconstructionState, item.IdempotencyKey
        }).ToArrayAsync();
        var frames = await db.CentralFrames.AsNoTracking().OrderBy(item => item.Id).Select(item => new
        {
            item.Id, item.FrameId, item.DevicePublicId, item.RegistrationId, item.ObservatoryId, item.AgentId, item.CapturedAtUtc
        }).ToArrayAsync();
        var seed = await db.CentralProcessingGraphRevisions.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.DefinitionIdentitySha256, item.PortablePlanIdentitySha256, item.CentralPlanIdentitySha256 }).ToArrayAsync();
        var assignments = await db.CentralProcessingGraphAssignments.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.RevisionId, item.EffectiveFromUtc }).ToArrayAsync();
        var users = await db.Users.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.UserName, item.SecurityStamp, item.PasswordHash }).ToArrayAsync();
        var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        if (validate)
        {
        Require(artifacts.Length == 2 && frames.Length == 1 && seed.Length > 0 && migrations.Length == 1, "canonical-shape");
        foreach (var pair in expectedClaims)
            Require(claims.Count(claim => claim.UserId == UserId && claim.ClaimType == pair.Key && claim.ClaimValue == pair.Value) == 1, "sql-host-pair");
        foreach (var artifact in artifacts)
            Require(expectedObjects.Any(fact => artifact.StorageReference == $"object://{fact.Bucket}/{fact.Key}"
                && artifact.MediaType == fact.ContentType && artifact.ByteLength == fact.Length && artifact.ChecksumSha256 == fact.Sha256), "sql-object-reference");
        }
        return JsonSerializer.Serialize(new { claims, artifacts, frames, seed, assignments, users, migrations });
    }

    private ApplicationDbContext OpenDb(string database) => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseSqlServer(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString).Options);

    internal static FilesystemObjectStore OpenStore(string path)
    {
        var options = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.Filesystem };
        options.Filesystem.Root = path;
        var wrapped = Options.Create(options);
        return new(wrapped, new ObjectStoreTelemetry(wrapped), TimeProvider.System, NullLogger<FilesystemObjectStore>.Instance);
    }

    private async Task MeasureAsync(string name, Func<Task> action)
    {
        phase = name;
        var stopwatch = Stopwatch.StartNew();
        await action();
        timings.Add(name, stopwatch.ElapsedMilliseconds);
    }

    internal static void CreateObjectRoot(string path)
    {
        foreach (var bucket in Buckets)
        {
            var directory = Path.Combine(path, bucket);
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
    }

    internal static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void Require(bool condition, string code) { if (!condition) throw new FixtureFailure(code); }
    internal sealed record RestoredFiles(string Root, string Objects, string Host, string SqlBackup);
    private sealed record ObjectFact(string Bucket, string Key, string Generation, string ContentType, long Length, string Sha256, DateTimeOffset ModifiedUtc);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Test failure carries only a closed safe code, never raw infrastructure details.")]
    private sealed class FixtureFailure(string code) : Exception("Joined recovery fixture failed.") { internal string Code { get; } = code; }
}
