using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.IntegrationTests;

internal sealed partial class JoinedRecoveryFixture
{
    private readonly List<string> refused = [];

    private async Task ExerciseRefusalsAsync(RestoredFiles positive)
    {
        var first = expectedObjects[0];
        var descriptor = FilesystemObjectLayout.DescriptorRelativePath(first.Bucket, FilesystemObjectLayout.KeyHash(first.Key));
        var generation = FilesystemObjectLayout.DataRelativePath(first.Bucket, FilesystemObjectLayout.KeyHash(first.Key), first.Generation);
        var keyFile = expectedHost.Keys.Single(key => key.StartsWith("keys" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        var catalogManifest = Path.Combine("catalog", "hyg-v42-fixture-1", "manifest.json");
        var catalogData = Path.Combine("catalog", "hyg-v42-fixture-1", "hyg_v42.sqlite");
        var cases = new (string Label, string Expected, Action<RestoredFiles> Mutate)[]
        {
            ("missing-sql", "sql-component-missing", files => File.Delete(files.SqlBackup)),
            ("truncated-sql", "sql-component-altered", files => File.WriteAllBytes(files.SqlBackup, sqlBackup[..128])),
            ("missing-instance", "host-component-missing", files => File.Delete(Path.Combine(files.Host, "instance.json"))),
            ("wrong-instance", "instance-identity", files => File.WriteAllText(Path.Combine(files.Host, "instance.json"), "{\"Instance\":\"other-fixture\"}")),
            ("missing-config", "host-component-missing", files => File.Delete(Path.Combine(files.Host, "config.json"))),
            ("wrong-config", "config-identity", files => File.WriteAllText(Path.Combine(files.Host, "config.json"), "{}")),
            ("missing-key", "host-component-missing", files => File.Delete(Path.Combine(files.Host, keyFile))),
            ("wrong-key", "key-identity", files => File.WriteAllText(Path.Combine(files.Host, keyFile), "<key id=\"other-fixture\"/>")),
            ("missing-catalog", "host-component-missing", files => File.Delete(Path.Combine(files.Host, catalogData))),
            ("wrong-catalog-identity", "catalog-identity", files => File.WriteAllText(Path.Combine(files.Host, catalogManifest), "{}")),
            ("truncated-catalog", "catalog-identity", files => File.WriteAllBytes(Path.Combine(files.Host, catalogData), [0])),
            ("missing-objects", "object-component-missing", files => Directory.Delete(Path.Combine(files.Objects, first.Bucket), recursive: true)),
            ("missing-descriptor", "object-component-missing", files => File.Delete(Path.Combine(files.Objects, descriptor))),
            ("truncated-descriptor", "object-descriptor", files => File.WriteAllText(Path.Combine(files.Objects, descriptor), "{")),
            ("absent-generation", "referenced-generation-missing", files => File.Delete(Path.Combine(files.Objects, generation))),
            ("altered-generation", "referenced-generation-altered", files => File.WriteAllBytes(Path.Combine(files.Objects, generation), Encoding.UTF8.GetBytes(new string('x', (int)first.Length)))),
            ("truncated-generation", "referenced-generation-altered", files => File.WriteAllBytes(Path.Combine(files.Objects, generation), [0])),
            ("wrong-generation", "object-identity", files => ChangeDescriptor(files, descriptor, value => value with { Generation = new string('a', 32) })),
            ("wrong-type", "object-identity", files => ChangeDescriptor(files, descriptor, value => value with { ContentType = "text/plain" })),
            ("wrong-length", "object-identity", files => ChangeDescriptor(files, descriptor, value => value with { Length = value.Length + 1 })),
            ("wrong-sha", "object-identity", files => ChangeDescriptor(files, descriptor, value => value with { Sha256 = new string('0', 64) })),
            ("wrong-key-name", "object-identity", files => ChangeDescriptor(files, descriptor, value => value with { Key = "other-key" })),
            ("wrong-time", "object-identity", files => ChangeDescriptor(files, descriptor, value => value with { ModifiedUtc = value.ModifiedUtc.AddSeconds(1) })),
            ("extra-host-component", "host-inventory", files => File.WriteAllText(Path.Combine(files.Host, "extra.json"), "{}"))
        };
        foreach (var item in cases)
        {
            var target = await RestoreFilesAsync(item.Label);
            item.Mutate(target);
            await AssertRefusalPreservesAsync(item.Label, item.Expected, target);
        }

        // A genuine second provider cutoff with a valid self-checksummed inventory and identical
        // content. Only the opaque generation/time changes; self-consistency cannot prove pairing.
        var laterSource = Path.Combine(root, "later-source");
        CopyTree(SourceObjects, laterSource);
        using (var store = OpenStore(laterSource))
        {
            var bytes = Encoding.UTF8.GetBytes("hvo-1630-artifact-0\n");
            await store.PutAsync(first.Bucket, first.Key, new MemoryStream(bytes), bytes.Length, first.ContentType, CancellationToken.None);
        }
        var laterBackup = Path.Combine(root, "later-backup");
        await FilesystemObjectBackup.BackupAsync(laterSource, Buckets, laterBackup, TimeProvider.System, CancellationToken.None);
        var mixed = await RestoreFilesAsync("mixed-cutoff");
        await FilesystemObjectBackup.RestoreAsync(laterBackup, mixed.Objects, Buckets, CancellationToken.None);
        Require(await FilesystemObjectBackup.VerifyAsync(laterBackup, mixed.Objects, CancellationToken.None) == 0, "later-self-verification");
        await AssertRefusalPreservesAsync("mixed-cutoff", "object-identity", mixed);

        await using (var db = OpenDb(targetDatabase))
        {
            await db.CentralArtifacts.Where(item => item.Id == Guid.Parse("16300000-0000-0000-0001-000000000001"))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.StorageReference, "object://skymonitor-artifacts/fixture/wrong-reference"));
        }
        await AssertRefusalPreservesAsync("wrong-sql-reference", "sql-facts", positive);
        await using (var db = OpenDb(targetDatabase))
        {
            await db.CentralArtifacts.Where(item => item.Id == Guid.Parse("16300000-0000-0000-0001-000000000001"))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.StorageReference, $"object://{first.Bucket}/{first.Key}"));
        }
        foreach (var claim in expectedClaims)
        {
            await using (var db = OpenDb(targetDatabase))
                await db.UserClaims.Where(item => item.ClaimType == claim.Key)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ClaimValue, "different-fixture"));
            await AssertRefusalPreservesAsync("sql-" + claim.Key + "-identity", "sql-facts", positive);
            await using (var db = OpenDb(targetDatabase))
                await db.UserClaims.Where(item => item.ClaimType == claim.Key)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ClaimValue, claim.Value));
        }
        Require(await AdmitAsync(positive) == "admitted", "post-refusal-positive");
        Require(Hash(await container.ReadFileAsync(BackupPath)) == Hash(sqlBackup), "native-backup-preserved");
    }

    private async Task AssertRefusalPreservesAsync(string label, string expected, RestoredFiles target)
    {
        var sourceBefore = Fingerprint(SourceObjects) + Fingerprint(SourceHost) + Fingerprint(ObjectBackup);
        var targetBefore = Fingerprint(target.Root);
        var sqlBefore = await ReadSqlFactsAsync(targetDatabase, validate: false);
        var result = await AdmitAsync(target);
        Require(result == expected, "refusal-" + label);
        Require(sourceBefore == Fingerprint(SourceObjects) + Fingerprint(SourceHost) + Fingerprint(ObjectBackup), "source-preservation");
        Require(targetBefore == Fingerprint(target.Root), "target-preservation");
        Require(sqlBefore == await ReadSqlFactsAsync(targetDatabase, validate: false), "target-sql-preservation");
        Require(sqlFacts == await ReadSqlFactsAsync(sourceDatabase), "source-sql-preservation");
        refused.Add(label + ":" + result);
    }

    private static string Fingerprint(string path)
    {
        // Includes private key bytes for in-process preservation checks only. Never emitted.
        var facts = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(path, file) + ":" + Hash(File.ReadAllBytes(file)));
        return Hash(Encoding.UTF8.GetBytes(string.Join('\n', facts)));
    }

    private static void ChangeDescriptor(RestoredFiles files, string relative, Func<FilesystemObjectDescriptor, FilesystemObjectDescriptor> change)
    {
        var path = Path.Combine(files.Objects, relative);
        var descriptor = JsonSerializer.Deserialize<FilesystemObjectDescriptor>(File.ReadAllText(path), FilesystemObjectLayout.DescriptorJson)
            ?? throw new InvalidOperationException("Missing test descriptor.");
        File.WriteAllText(path, JsonSerializer.Serialize(change(descriptor), FilesystemObjectLayout.DescriptorJson));
    }

    private async Task EmitEvidenceAsync()
    {
        await using var master = new SqlConnection(connectionString);
        await master.OpenAsync();
        await using var command = master.CreateCommand();
        command.CommandText = "SELECT CONVERT(nvarchar(40), SERVERPROPERTY('ProductVersion'));";
        var engine = Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        var script = Path.Combine(SqlOperationsIssue255Tests.FindRepositoryRoot(), "deploy", "sql", "backup-restore-checkdb.sql");
        var evidence = JsonSerializer.Serialize(new
        {
            Issue = 1630,
            Source = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            SqlImage = IntegrationTestFixture.SqlServerImage,
            SqlEngine = engine,
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Topology = "one disposable SQL container; separate process-local filesystem source, backup and restored trees; no application writers",
            SqlScriptSha256 = Hash(await File.ReadAllBytesAsync(script)),
            SqlBackupSha256 = Hash(sqlBackup),
            SqlBackupBytes = sqlBackup.LongLength,
            SqlFactsSha256 = Hash(Encoding.UTF8.GetBytes(sqlFacts)),
            ObjectInventorySha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(ObjectBackup, FilesystemObjectBackup.InventoryFileName))),
            PublicConfigSha256 = expectedClaims["config"],
            CatalogManifestSha256 = expectedClaims["catalog"],
            CatalogDataSha256 = expectedClaims["catalog-data"],
            Objects = expectedObjects,
            Refusals = refused,
            PhaseMilliseconds = timings,
            Limitations = "fixture admission and identity comparison only; no authentication restart, writer fencing, encryption/off-host, interrupted integrated restore, production RPO/RTO, native ext4 qualification or ARM64 SQL claim"
        });
        Require(!evidence.Contains(password, StringComparison.Ordinal) && !evidence.Contains(root, StringComparison.Ordinal)
            && !evidence.Contains(connectionString, StringComparison.Ordinal) && !evidence.Contains(BackupPath, StringComparison.Ordinal), "evidence-redaction");
        Console.WriteLine("issue1630 evidence: " + evidence);
    }
}
