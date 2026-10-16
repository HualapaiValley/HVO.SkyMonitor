using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ExecutionEvidenceIntegrationTests
{
    private static readonly string[] BindingViewFields = ["Id", "ObservatoryId", "RegistrationId", "LogicalCameraInstallationId", "InstallationPublicId", "OriginInstallationId", "AgentInstanceId", "CreatedAtUtc"];
    [TestMethod]
    public async Task InstallationBindingsPreserveHistoricalViewsAndReturnExplicitEmptyWithoutWrites()
    {
        var old = await SeedAsync().ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var camera = await BindingCameraAsync(old).ConfigureAwait(false);
        var registration = new DeviceRegistration
        {
            ObservatoryId = old.ObservatoryId,
            DeviceId = $"replacement-{Guid.NewGuid():N}",
            DevicePublicId = Guid.NewGuid(),
            DeviceKeyHash = new string('C', 64),
            Status = DeviceRegistrationStatus.Active,
            OwnerUserId = old.OwnerId,
            OwnerDisplayName = "Test owner",
            FriendlyName = "Replacement",
            ObservatoryName = "Evidence",
            VerificationCodeHash = new string('D', 64),
            IssuedAtUtc = Epoch,
            ActivatedAtUtc = Epoch,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1)
        };
        db.Add(registration);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var replacement = await scope.ServiceProvider.GetRequiredService<ILogicalCameraService>().AssignInstallationAsync(
            camera, registration.Id, old.OwnerId, "binding-read-replacement").ConfigureAwait(false);
        Assert.AreEqual(LogicalCameraMutationOutcome.Applied, replacement.Outcome);
        (await db.DeviceRegistrations.SingleAsync(r => r.Id == old.RegistrationId).ConfigureAwait(false)).Status = DeviceRegistrationStatus.Revoked;
        await db.SaveChangesAsync().ConfigureAwait(false);
        var service = scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>();
        var expected = await service.GetBindingAsync(old.BindingId, old.OwnerId, old.ObservatoryId).ConfigureAwait(false);
        using var client = await BindingKeyClientAsync(old).ConfigureAwait(false);
        using var probe = new BindingReadProbe();
        using var response = await client.GetAsync(BindingPath(old.ObservatoryId, camera, old.InstallationId)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ExecutionEvidenceInstallationBindingsView>().ConfigureAwait(false);
        Assert.IsNotNull(result);
        Assert.IsFalse(result.Truncated);
        Assert.AreEqual(expected, result.Bindings.Single());
        using var emptyResponse = await client.GetAsync(BindingPath(old.ObservatoryId, camera, replacement.InstallationId!.Value)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, emptyResponse.StatusCode);
        var empty = await emptyResponse.Content.ReadFromJsonAsync<ExecutionEvidenceInstallationBindingsView>().ConfigureAwait(false);
        Assert.IsNotNull(empty);
        Assert.IsEmpty(empty.Bindings);
        Assert.IsFalse(empty.Truncated);
        Assert.HasCount(4, probe.Commands);
        Assert.IsTrue(probe.Commands.All(c => c.Sql.TrimStart().StartsWith("SELECT", StringComparison.Ordinal)));
        db.ChangeTracker.Clear();
        Assert.AreEqual(expected, await service.GetBindingAsync(old.BindingId, old.OwnerId, old.ObservatoryId).ConfigureAwait(false));
        Assert.AreEqual(DeviceRegistrationStatus.Revoked, (await db.DeviceRegistrations.AsNoTracking().SingleAsync(r => r.Id == old.RegistrationId).ConfigureAwait(false)).Status);
        Assert.AreEqual(1, await db.Set<ExecutionEvidenceBinding>().CountAsync(b => b.RegistrationId == old.RegistrationId).ConfigureAwait(false));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        CollectionAssert.AreEquivalent(BindingViewFields,
            json.RootElement.GetProperty("Bindings")[0].EnumerateObject().Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public async Task InstallationBindingsEnforceRolesCredentialScopeEntireRouteAndRemovedMembership()
    {
        var a = await SeedAsync().ConfigureAwait(false);
        var b = await SeedAsync().ConfigureAwait(false); // Same owner is a member of A and B.
        var aCamera = await BindingCameraAsync(a).ConfigureAwait(false);
        var bCamera = await BindingCameraAsync(b).ConfigureAwait(false);
        using var client = await BindingKeyClientAsync(a).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var membership = await db.ObservatoryMemberships.SingleAsync(m => m.ObservatoryId == a.ObservatoryId && m.UserId == a.OwnerId).ConfigureAwait(false);
        foreach (var role in new[] { ObservatoryMembershipRole.Viewer, ObservatoryMembershipRole.Manager, ObservatoryMembershipRole.Owner })
        {
            membership.Role = role;
            await db.SaveChangesAsync().ConfigureAwait(false);
            using var allowed = await client.GetAsync(BindingPath(a.ObservatoryId, aCamera, a.InstallationId)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode, role.ToString());
        }
        using var probe = new BindingReadProbe();
        foreach (var path in new[]
        {
            BindingPath(b.ObservatoryId, bCamera, b.InstallationId),
            BindingPath(a.ObservatoryId, bCamera, b.InstallationId),
            BindingPath(a.ObservatoryId, aCamera, b.InstallationId),
            BindingPath(a.ObservatoryId, bCamera, a.InstallationId),
            BindingPath(b.ObservatoryId, aCamera, a.InstallationId),
            BindingPath(a.ObservatoryId, aCamera, a.DevicePublicId), // Never fall back to public identity.
            BindingPath(a.ObservatoryId, aCamera, Guid.NewGuid())
        })
        {
            using var denied = await client.GetAsync(path).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.IsFalse((await denied.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains(b.BindingId.ToString(), StringComparison.OrdinalIgnoreCase));
        }
        Assert.IsFalse(probe.Commands.Any(c => c.Sql.Contains("FROM [ExecutionEvidenceBindings]", StringComparison.Ordinal)));
        db.ObservatoryMemberships.Remove(membership);
        await db.SaveChangesAsync().ConfigureAwait(false);
        using var removed = await client.GetAsync(BindingPath(a.ObservatoryId, aCamera, a.InstallationId)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.IsNull(await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>().ListInstallationBindingsAsync(
            a.ObservatoryId, aCamera, a.InstallationId, a.OwnerId, a.ObservatoryId).ConfigureAwait(false));
        var outsider = await db.Users.Where(u => u.Email == TestUsers.Viewer.Email).Select(u => u.Id).SingleAsync().ConfigureAwait(false);
        Assert.IsNull(await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>().ListInstallationBindingsAsync(
            b.ObservatoryId, bCamera, b.InstallationId, outsider, null).ConfigureAwait(false));
        db.Add(new ObservatoryMembership
        {
            ObservatoryId = b.ObservatoryId,
            UserId = outsider,
            Role = ObservatoryMembershipRole.Viewer,
            AddedAtUtc = Epoch
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        using var tokenClient = AssemblyHooks.Fixture.Factory.CreateClient();
        var token = await HttpHelpers.GetPasswordTokenAsync(tokenClient, "/connect/token", TestUsers.Viewer.Username,
            TestUsers.Viewer.Password, TestClients.WebUI.ClientId, "api.viewer").ConfigureAwait(false);
        using var onlyB = HttpHelpers.WithBearerToken(tokenClient, token.AccessToken);
        using var deniedA = await onlyB.GetAsync(BindingPath(a.ObservatoryId, aCamera, a.InstallationId)).ConfigureAwait(false);
        using var allowedB = await onlyB.GetAsync(BindingPath(b.ObservatoryId, bCamera, b.InstallationId)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, deniedA.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, allowedB.StatusCode);
    }

    [TestMethod]
    public async Task InstallationBindingsRejectInvalidCredentialsMalformedMissingAndPropagateCancellation()
    {
        var device = await SeedAsync().ConfigureAwait(false);
        var camera = await BindingCameraAsync(device).ConfigureAwait(false);
        var path = BindingPath(device.ObservatoryId, camera, device.InstallationId);
        using var anonymous = AssemblyHooks.Fixture.Factory.CreateClient();
        using var invalid = AssemblyHooks.Fixture.Factory.CreateClient();
        invalid.DefaultRequestHeaders.Add("X-API-Key", "invalid-installation-binding-key");
        foreach (var caller in new[] { anonymous, invalid })
        {
            using var response = await caller.GetAsync(path).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var client = await BindingKeyClientAsync(device).ConfigureAwait(false);
        foreach (var segment in new[] { device.ObservatoryId.ToString(), camera.ToString(), device.InstallationId.ToString() })
        {
            foreach (var replacement in new[] { "not-a-guid", Guid.Empty.ToString(), Guid.NewGuid().ToString() })
            {
                using var response = await client.GetAsync(new Uri(path.OriginalString.Replace(segment, replacement, StringComparison.Ordinal), UriKind.Relative)).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var probe = new BindingReadProbe { OnBindingQuery = () => cancellation.Cancel() };
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            using var response = await client.GetAsync(path, cancellation.Token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        Assert.IsTrue(probe.Commands.Any(c => c.Sql.Contains("FROM [ExecutionEvidenceBindings]", StringComparison.Ordinal)), "Cancel after real SQL authority resolution, at binding query execution.");
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>()
            .ListInstallationBindingsAsync(device.ObservatoryId, camera, device.InstallationId, device.OwnerId, device.ObservatoryId, cancellation.Token)).ConfigureAwait(false);
    }

    private static Uri BindingPath(Guid observatory, Guid camera, Guid installation)
        => new($"/api/internal/observatories/{observatory:D}/cameras/{camera:D}/installations/{installation:D}/execution-evidence/bindings", UriKind.Relative);

    private static async Task<Guid> BindingCameraAsync(Device device)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().LogicalCameraInstallations
            .Where(i => i.Id == device.InstallationId).Select(i => i.LogicalCameraId).SingleAsync().ConfigureAwait(false);
    }

    private static async Task<HttpClient> BindingKeyClientAsync(Device device)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var key = await scope.ServiceProvider.GetRequiredService<IApiKeyLifecycleService>().CreateAsync(
            device.OwnerId, TestUsers.Operator.Email, $"binding-read-{Guid.NewGuid():N}", ApiKeyAccessLevel.Read,
            device.ObservatoryId, DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None).ConfigureAwait(false);
        var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key.PlaintextKey);
        return client;
    }

    private sealed record BindingCommand(string Sql, (string Name, object Value)[] Parameters);

    // The evidence service owns its separate SQL context/pool. Observe actual EF diagnostics rather
    // than replacing that context or changing production registration to install a test interceptor.
    private sealed class BindingReadProbe : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable _all;
        private readonly List<IDisposable> _listeners = [];
        public ConcurrentQueue<BindingCommand> Commands { get; } = new();
        public Action? OnBindingQuery { get; init; }
        public BindingReadProbe() => _all = DiagnosticListener.AllListeners.Subscribe(this);
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == "Microsoft.EntityFrameworkCore") _listeners.Add(value.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key != "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuting" || value.Value is not CommandEventData data ||
                data.Command.Connection is not SqlConnection connection || new SqlConnectionStringBuilder(connection.ConnectionString).ApplicationName != "HVO.ExecutionEvidence") return;
            Commands.Enqueue(new(data.Command.CommandText, data.Command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value!)).ToArray()));
            if (data.Command.CommandText.Contains("FROM [ExecutionEvidenceBindings]", StringComparison.Ordinal)) OnBindingQuery?.Invoke();
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose()
        {
            _all.Dispose();
            foreach (var listener in _listeners) listener.Dispose();
        }
    }
}
