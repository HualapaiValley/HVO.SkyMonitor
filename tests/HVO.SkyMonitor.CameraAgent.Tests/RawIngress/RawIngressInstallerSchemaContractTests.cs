using HVO.SkyMonitor.Deployment;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "RawIngress")]
public sealed class RawIngressInstallerSchemaContractTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BaselineMatchesInstallerFingerprintAndRejectsAlteredAstrometryIndex(bool wrongProcessingIndex)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-baseline-schema-{Guid.NewGuid():N}");
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
            var options = Microsoft.Extensions.Options.Options.Create(
                new HVO.SkyMonitor.CameraAgent.Common.Options.CameraAgentHostOptions { RawIngressRoot = root });
            using var processing = new HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.SqliteCaptureProcessingStore(options);
            await processing.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            if (wrongProcessingIndex)
            {
                using var malformed = new SqliteConnection($"Data Source={path}");
                await malformed.OpenAsync().ConfigureAwait(false);
                using var change = malformed.CreateCommand();
                change.CommandText = """
                    DROP INDEX ix_processing_outputs_astrometry_lookup;
                    CREATE INDEX ix_processing_outputs_astrometry_lookup ON processing_outputs(capture_id);
                    """;
                await change.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var restartedJournal = constructor.Invoke(arguments);
            await ((Task)initialize.Invoke(restartedJournal, [CancellationToken.None])!).ConfigureAwait(false);
            using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync().ConfigureAwait(false);
            Assert.AreEqual(!wrongProcessingIndex, RawIngressBaselineSchema.Fingerprint == RawIngressBaselineSchema.ComputeFingerprint(connection));
            Assert.AreEqual(!wrongProcessingIndex, RawIngressBaselineSchema.IsCanonical(connection));
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.AreEqual(1L, (long)(await version.ExecuteScalarAsync().ConfigureAwait(false))!);
            using var restartedProcessing = new HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.SqliteCaptureProcessingStore(options);
            if (wrongProcessingIndex)
            {
                var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await restartedProcessing.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                StringAssert.Contains(failure.Message, "not the canonical schema 1 definition", StringComparison.Ordinal);
            }
            else
            {
                await restartedProcessing.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
