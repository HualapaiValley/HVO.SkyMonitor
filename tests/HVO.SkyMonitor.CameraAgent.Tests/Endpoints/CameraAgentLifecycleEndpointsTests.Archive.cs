using System.Net;
using System.Net.Http.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Tests.Silo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

public sealed partial class CameraAgentLifecycleEndpointsTests
{
    private static readonly Uri ArchiveRoute = new("/api/internal/deployment/lifecycle/silo/archive", UriKind.Relative);
    private static readonly Uri ArchiveReadRoute = new(ArchiveRoute + "/read", UriKind.Relative);
    private const string ArchiveToken = "fixture-lifecycle-token";

    [TestMethod]
    public async Task ArchiveRoutesAuthorizeAndValidateBeforeAccessingArchive()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var builder = ArchiveHostBuilder();
        builder.Configuration[SiloArchiveFixture.SettingsPath + ":ReplicationEnabled"] = "false";
        var host = builder.Build();
        await using var hostLease = host.ConfigureAwait(false);
        host.MapCameraAgentLifecycleEndpoints();
        await host.StartAsync(token).ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = new(host.Urls.Single()) };
        var publication = new Uri(ArchiveRoute + "/publication?groupId=group&revisionId=1", UriKind.Relative);

        var receipt = new CameraAgentSiloArchiveReceipt(SiloArchiveFixture.StoreId, SiloArchiveFixture.Bucket, "group", "1",
            1, 2, 0, 1, 3, "receipt-version", new string('a', 64));
        var read = new CameraAgentSiloArchiveRead(receipt, "artifact", "1");
        using var unauthenticated = await client.GetAsync(ArchiveRoute, token).ConfigureAwait(false);
        using var unauthorizedPublication = await client.GetAsync(publication, token).ConfigureAwait(false);
        using var unauthorizedRead = await client.PostAsJsonAsync(ArchiveReadRoute, read, token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorizedPublication.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorizedRead.StatusCode);

        client.DefaultRequestHeaders.Add("X-HVO-Installation-Token", ArchiveToken);
        using var state = await client.GetAsync(ArchiveRoute, token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, state.StatusCode);
        Assert.IsTrue(state.Headers.CacheControl?.NoStore);
        Assert.AreEqual("disabled", (await state.Content.ReadFromJsonAsync<CameraAgentSiloArchiveStatus>(token).ConfigureAwait(false))?.State);
        using var missing = await client.GetAsync(publication, token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.IsTrue(missing.Headers.CacheControl?.NoStore);
        var oversized = new Uri(ArchiveRoute + "/publication?groupId=" + new string('x', 129) + "&revisionId=1", UriKind.Relative);
        using var invalidPublication = await client.GetAsync(oversized, token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidPublication.StatusCode);
        using var invalidRead = await client.PostAsJsonAsync(ArchiveReadRoute, read with
        {
            Receipt = receipt with { ReceiptSequence = receipt.PublicationSequence, ReceiptVersionId = "" }
        }, token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidRead.StatusCode);
        using var disabled = await client.PostAsJsonAsync(ArchiveReadRoute, read, token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, disabled.StatusCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ArchiveReadVerifiesBytesDisposesDownloadAndMapsArchiveOutcomes(bool partialExpiry)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var upstream = await SiloArchiveFixture.StartAsync(silo.Binding.BindingId, token).ConfigureAwait(false);
        await using var upstreamLease = upstream.ConfigureAwait(false);
        var (reference, commit, publish) = await SiloArchiveTests.PublishAndReplicateAsync(
            silo, upstream, SiloDrafts.Capture(silo.Payloads, 1), token).ConfigureAwait(false);
        var expired = publish.Members[0];
        if (partialExpiry)
        {
            upstream.WriteExpiry(expired.Locator, silo.Clock.GetUtcNow());
            upstream.Delete(expired.Locator);
        }
        using var archive = SiloArchiveStore.Create(upstream.Settings, silo.Clock);
        var records = await new SiloArchiveVerification(archive, silo.Clock)
            .VerifyPublicationAsync(reference, commit, silo.Index, prefix: 0, token).ConfigureAwait(false);
        var journal = await silo.AppendAsync([.. records.Records]).ConfigureAwait(false);
        upstream.Replicate(silo.Local, journal);

        var builder = ArchiveHostBuilder();
        builder.Configuration.AddConfiguration(upstream.Configuration);
        var settings = builder.Configuration.GetSection(SiloArchiveFixture.SettingsPath);
        settings["ReadScratchRoot"] = silo.RootPath;
        settings["ReplicationEnabled"] = "false";
        var host = builder.Build();
        await using var hostLease = host.ConfigureAwait(false);
        host.MapCameraAgentLifecycleEndpoints();
        await host.StartAsync(token).ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = new(host.Urls.Single()) };
        client.DefaultRequestHeaders.Add("X-HVO-Installation-Token", ArchiveToken);

        var receipt = new CameraAgentSiloArchiveReceipt(archive.Marker.StoreId, archive.Marker.Bucket, reference.GroupId, reference.RevisionId,
            reference.Position.Generation, reference.Position.Sequence, reference.Index, journal.Position.Generation, journal.Position.Sequence,
            journal.VersionId, journal.Sha256);
        var member = publish.Members[1];
        var read = new CameraAgentSiloArchiveRead(receipt, member.ArtifactId, member.ArtifactRevision);
        var worker = host.Services.GetRequiredService<ICameraAgentSiloArchive>();
        using (var open = await worker.ReadAsync(read, token).ConfigureAwait(false))
        {
            Assert.AreEqual("reachable", open.State);
            await AssertHttpReadAsync(HttpStatusCode.ServiceUnavailable, read).ConfigureAwait(false);
        }

        for (var download = 0; download < 2; download++)
        {
            using var response = await client.PostAsJsonAsync(ArchiveReadRoute, read, token).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.CacheControl?.NoStore);
            Assert.AreEqual(member.Sha256, response.Headers.GetValues("X-HVO-Content-Sha256").Single());
            CollectionAssert.AreEqual(silo.Local.Read(member.Locator.Key, member.Locator.VersionId),
                await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
        }

        await AssertHttpReadAsync(HttpStatusCode.NotFound, read with { ArtifactId = "unknown-artifact" }).ConfigureAwait(false);
        if (partialExpiry)
            await AssertHttpReadAsync(HttpStatusCode.Gone, new(receipt, expired.ArtifactId, expired.ArtifactRevision)).ConfigureAwait(false);
        await AssertHttpReadAsync(HttpStatusCode.BadRequest, read with { Receipt = receipt with { ReceiptVersionId = "\0" } })
            .ConfigureAwait(false);
        await AssertHttpReadAsync(HttpStatusCode.Conflict, read with { Receipt = receipt with { ReceiptSha256 = new string('d', 64) } })
            .ConfigureAwait(false);

        foreach (var maximum in new[] { "not-a-number", "9223372036854775808", "5368709121" })
        {
            settings["MaximumReadBytes"] = maximum;
            await AssertHttpReadAsync(HttpStatusCode.Conflict, read).ConfigureAwait(false);
        }
        settings["MaximumReadBytes"] = "1";
        await AssertHttpReadAsync(HttpStatusCode.ServiceUnavailable, read).ConfigureAwait(false);
        settings["MaximumReadBytes"] = null;
        var endpoint = settings["Endpoint"];
        settings["Endpoint"] = "http://127.0.0.1";
        await AssertHttpReadAsync(HttpStatusCode.Conflict, read).ConfigureAwait(false);
        settings["Endpoint"] = endpoint;

        settings["ReadScratchRoot"] = "relative";
        await AssertHttpReadAsync(HttpStatusCode.ServiceUnavailable, read).ConfigureAwait(false);
        var target = Directory.CreateDirectory(Path.Combine(silo.RootPath, "scratch"));
        target.CreateSubdirectory("subdirectory");
        var link = Path.Combine(silo.RootPath, "symlink");
        Directory.CreateSymbolicLink(link, target.FullName);
        settings["ReadScratchRoot"] = link;
        await AssertHttpReadAsync(HttpStatusCode.ServiceUnavailable, read).ConfigureAwait(false);
        settings["ReadScratchRoot"] = Path.Combine(link, "subdirectory");
        await AssertHttpReadAsync(HttpStatusCode.ServiceUnavailable, read).ConfigureAwait(false);
        settings["ReadScratchRoot"] = silo.RootPath;

        upstream.UnavailableKey = member.Locator.Key;
        await AssertHttpReadAsync(HttpStatusCode.ServiceUnavailable, read).ConfigureAwait(false);
        upstream.UnavailableKey = null;
        upstream.StallKey = member.Locator.Key;
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var pending = worker.ReadAsync(read, cancel.Token).AsTask();
            await upstream.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            await cancel.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending).ConfigureAwait(false);
        }
        upstream.StallKey = null;
        upstream.WriteExpiry(member.Locator, DateTimeOffset.UtcNow.AddMinutes(1));
        await AssertHttpReadAsync(HttpStatusCode.Gone, read).ConfigureAwait(false);

        async Task AssertHttpReadAsync(HttpStatusCode status, CameraAgentSiloArchiveRead request)
        {
            using var response = await client.PostAsJsonAsync(ArchiveReadRoute, request, token).ConfigureAwait(false);
            Assert.AreEqual(status, response.StatusCode);
            Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        }
    }

    private static WebApplicationBuilder ArchiveHostBuilder()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration["LifecycleControl:Token"] = ArchiveToken;
        builder.Services.AddSingleton<CaptureAdmissionCoordinator>(_ => null!);
        builder.Services.AddSingleton<CameraAgentOperationsSummaryProvider>(_ => null!);
        builder.Services.AddSingleton<DeploymentContinuityReader>(_ => null!);
        builder.Services.AddSingleton(Options.Create(new CameraAgentHostOptions()));
        builder.Services.AddSingleton<ICameraAgentSiloArchive>(services =>
            new SiloArchiveWorker(builder.Configuration, services, TimeProvider.System, NullLogger<SiloArchiveWorker>.Instance));
        return builder;
    }
}
