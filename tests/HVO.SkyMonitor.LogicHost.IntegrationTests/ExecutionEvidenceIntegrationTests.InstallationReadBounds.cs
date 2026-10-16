using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ExecutionEvidenceIntegrationTests
{
    [TestMethod]
    public async Task InstallationBindingsUseBoundedIndexedSentinelAcrossUnrelatedGlobalHistory()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        var camera = await BindingCameraAsync(device).ConfigureAwait(false);
        using var client = await BindingKeyClientAsync(device).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // Faithful retained history: 256 different registrations/installations, four bindings each.
        // Each registration is within the actual default quota; all belong to other installations.
        for (var n = 0; n < 256; n++)
        {
            var registration = new DeviceRegistration
            {
                ObservatoryId = device.ObservatoryId,
                DeviceId = $"binding-history-{Guid.NewGuid():N}",
                DevicePublicId = Guid.NewGuid(),
                DeviceKeyHash = new string('A', 64),
                Status = DeviceRegistrationStatus.Revoked,
                OwnerUserId = device.OwnerId,
                OwnerDisplayName = "Test owner",
                FriendlyName = "Retained history",
                ObservatoryName = "Binding history",
                VerificationCodeHash = new string('B', 64),
                IssuedAtUtc = Epoch,
                ActivatedAtUtc = Epoch,
                ExpiresAtUtc = Epoch.AddDays(1)
            };
            var installation = new LogicalCameraInstallation
            {
                LogicalCameraId = camera,
                RegistrationId = registration.Id,
                InstallationPublicId = registration.DevicePublicId.Value,
                AssignedAtUtc = Epoch.AddDays(-3).AddMinutes(n * 2),
                RetiredAtUtc = Epoch.AddDays(-3).AddMinutes(n * 2 + 1),
                AssignedByUserId = device.OwnerId,
                RetiredByUserId = device.OwnerId,
                AssignmentReasonCode = "history",
                RetirementReasonCode = "replaced"
            };
            db.Add(registration);
            db.Add(installation);
            for (var b = 0; b < 4; b++) AddReadBinding(db, device, registration.Id, installation.Id, installation.InstallationPublicId);
        }
        for (var n = 1; n < 16; n++) AddReadBinding(db, device, device.RegistrationId, device.InstallationId, device.DevicePublicId);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var expected = await db.Set<ExecutionEvidenceBinding>().Where(b => b.RegistrationId == device.RegistrationId)
            .OrderBy(b => b.Id).Select(b => b.Id).ToArrayAsync().ConfigureAwait(false);
        var samples = new List<object>();
        foreach (var overflow in new[] { false, true })
        {
            if (overflow)
            {
                // Defensive sentinel fixture exceeds the enforced creation maximum deliberately;
                // it is not a supported registration operation or a changed quota.
                AddReadBinding(db, device, device.RegistrationId, device.InstallationId, device.DevicePublicId);
                await db.SaveChangesAsync().ConfigureAwait(false);
                expected = await db.Set<ExecutionEvidenceBinding>().Where(b => b.RegistrationId == device.RegistrationId)
                    .OrderBy(b => b.Id).Select(b => b.Id).Take(16).ToArrayAsync().ConfigureAwait(false);
            }
            using var probe = new BindingReadProbe();
            var timer = Stopwatch.StartNew();
            using var response = await client.GetAsync(BindingPath(device.ObservatoryId, camera, device.InstallationId)).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            timer.Stop();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<ExecutionEvidenceInstallationBindingsView>().ConfigureAwait(false);
            Assert.IsNotNull(result);
            Assert.HasCount(16, result.Bindings);
            Assert.AreEqual(overflow, result.Truncated);
            CollectionAssert.AreEqual(expected, result.Bindings.Select(b => b.Id).ToArray());
            Assert.IsTrue(result.Bindings.All(b => b.LogicalCameraInstallationId == device.InstallationId));
            Assert.HasCount(2, probe.Commands, "One authorized relationship query, then one bounded binding query.");
            Assert.IsTrue(probe.Commands.All(c => c.Sql.TrimStart().StartsWith("SELECT", StringComparison.Ordinal)));
            var query = probe.Commands.Last();
            Assert.IsTrue(query.Sql.Contains("TOP(@", StringComparison.Ordinal));
            Assert.IsFalse(query.Sql.Contains("OFFSET", StringComparison.Ordinal));
            Assert.IsTrue(query.Parameters.Any(p => p.Value is int take && take == 17));
            var plan = await BindingPlanAsync(query, overflow ? 17 : 16).ConfigureAwait(false);
            Assert.IsTrue(bytes.Length < 16 * 1024);
            samples.Add(new
            {
                overflow,
                ServiceQueryCount = probe.Commands.Count,
                MaterializedBindingViews = overflow ? 17 : 16,
                ReturnedBindings = result.Bindings.Count,
                ResponseBytes = bytes.Length,
                HttpMilliseconds = timer.Elapsed.TotalMilliseconds,
                Plan = plan
            });
        }
        Assert.AreEqual(17, await db.Set<ExecutionEvidenceBinding>().CountAsync(b => b.RegistrationId == device.RegistrationId).ConfigureAwait(false));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.IsNotNull(root);
        if (Environment.GetEnvironmentVariable("HVO_ISSUE1266_EVIDENCE") == "1")
        {
            var markers = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("HVO.SkyMonitor.", StringComparison.Ordinal) == true)
                .Select(a => a.DefinedTypes.First().AsType()).ToArray();
            var source = await EvidenceSourceIdentity.CaptureAsync(root.FullName, markers).ConfigureAwait(false);
            var directory = Path.Combine(root.FullName, "TestResults", "issue-1266", source.OutputDirectoryName, source.RunId);
            Directory.CreateDirectory(directory);
            await EvidenceSourceIdentity.WriteJsonAsync(Path.Combine(directory, "binding-read.json"), new
            {
                Source = source,
                UnrelatedBindings = 1024,
                UnrelatedRegistrations = 256,
                SelectedBindings = 17,
                Method = "Two single HTTP reads at concurrency 1 after fixture seeding; absolute candidate observation, no percentile or before/improvement claim. Physical SQL plan replay uses the exact captured command and parameters. Materialized views equal the SELECT result rows (no client filtering before the fixed sentinel).",
                Samples = samples
            }, new JsonSerializerOptions { WriteIndented = true }).ConfigureAwait(false);
        }
        TestContext.WriteLine(JsonSerializer.Serialize(samples));
    }

    private static void AddReadBinding(ApplicationDbContext db, Device owner, Guid registration, Guid installation, Guid publicId)
    {
        var binding = new ExecutionEvidenceBinding
        {
            ObservatoryId = owner.ObservatoryId,
            RegistrationId = registration,
            LogicalCameraInstallationId = installation,
            InstallationPublicId = publicId,
            OriginInstallationId = Guid.NewGuid(),
            AgentInstanceId = Guid.NewGuid(),
            CreatedAtUtc = Epoch,
            CreatedByUserId = owner.OwnerId
        };
        db.Add(binding);
        db.Add(new ExecutionEvidenceBindingUsage { BindingId = binding.Id });
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Replays only the internally captured EF-generated SELECT with its original parameter collection; no request text is used as SQL.")]
    private static async Task<object> BindingPlanAsync(BindingCommand captured, int expectedRows)
    {
        await using var connection = new SqlConnection(AssemblyHooks.Fixture.SqlServerConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET STATISTICS XML ON; {captured.Sql}; SET STATISTICS XML OFF;";
        foreach (var parameter in captured.Parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var rows = 0;
        while (await reader.ReadAsync().ConfigureAwait(false)) rows++;
        Assert.AreEqual(expectedRows, rows);
        Assert.IsTrue(await reader.NextResultAsync().ConfigureAwait(false));
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        var plan = XDocument.Parse(reader.GetString(0));
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var operators = plan.Descendants(ns + "RelOp").Where(op => op.Elements().Any(e =>
            e.Elements(ns + "Object").Any(o => (string?)o.Attribute("Table") == "[ExecutionEvidenceBindings]")))
            .Select(op => new
            {
                PhysicalOp = (string?)op.Attribute("PhysicalOp"),
                Index = op.Descendants(ns + "Object").First().Attribute("Index")?.Value,
                ActualRowsRead = op.Elements(ns + "RunTimeInformation").Elements(ns + "RunTimeCountersPerThread")
                    .Sum(c => double.Parse(c.Attribute("ActualRowsRead")?.Value ?? "0", CultureInfo.InvariantCulture))
            }).ToArray();
        Assert.IsTrue(operators.Any(op => op.PhysicalOp == "Index Seek" && op.Index == "[IX_ExecutionEvidenceBindings_RegistrationId_Id]"));
        Assert.IsTrue(operators.All(op => op.ActualRowsRead <= 17), JsonSerializer.Serialize(operators));
        return new { ReturnedRows = rows, BindingOperators = operators, PhysicalBindingRowsRead = operators.Sum(op => op.ActualRowsRead) };
    }
}
