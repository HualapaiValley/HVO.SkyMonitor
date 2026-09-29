using HVO.SkyMonitor.Deployment;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Unit")]
public sealed class RawIngressInstallerSchemaContractTests
{
    [TestMethod]
    public async Task CandidateV14SchemaMatchesFrozenInstallerFingerprint()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-v14-schema-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "journal", "raw-ingress.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var journalType = typeof(HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawIngressState).Assembly
                .GetType("HVO.SkyMonitor.CameraAgent.Common.RawIngress.SqliteRawCaptureJournal", throwOnError: true)!;
            var constructor = journalType.GetConstructors(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).Single();
            var arguments = constructor.GetParameters().Select(parameter => parameter.HasDefaultValue
                ? parameter.DefaultValue : parameter.Name == "databasePath" ? path : (object)5).ToArray();
            var journal = constructor.Invoke(arguments);
            var initialize = journalType.GetMethod("InitializeAsync", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic, binder: null, types: [typeof(CancellationToken)], modifiers: null)!;
            await ((Task)initialize.Invoke(journal, [CancellationToken.None])!).ConfigureAwait(false);
            using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync().ConfigureAwait(false);
            Assert.AreEqual(RawIngressV13Schema.V14Fingerprint, RawIngressV13Schema.ComputeFingerprint(connection));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FrozenV13SchemaMatchesInstallerFingerprintAndDetectsDrift()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-v13-schema-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "journal", "raw-ingress.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync().ConfigureAwait(false);
            using (var create = connection.CreateCommand())
            {
#pragma warning disable CA2100 // Frozen local v13 DDL, never user input.
                create.CommandText = FrozenV13Sql;
#pragma warning restore CA2100
                await create.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            Assert.AreEqual(RawIngressV13Schema.Fingerprint, RawIngressV13Schema.ComputeFingerprint(connection));
            using (var drift = connection.CreateCommand())
            {
                drift.CommandText = "CREATE TABLE installer_drift (id INTEGER);";
                await drift.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            Assert.AreNotEqual(RawIngressV13Schema.Fingerprint, RawIngressV13Schema.ComputeFingerprint(connection));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Extracted from the v13 journal on main (bff5bd6777b137b04d66aae281f066e30358bafb),
    // not from the candidate runtime. The decoded SQL is SQLite's stored schema DDL in type/name order.
    private static string FrozenV13Sql
    {
        get
        {
            using var compressed = new MemoryStream(Convert.FromBase64String(FrozenV13SchemaGzip));
            using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            return reader.ReadToEnd();
        }
    }

    private const string FrozenV13SchemaGzip =
        "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";
}
