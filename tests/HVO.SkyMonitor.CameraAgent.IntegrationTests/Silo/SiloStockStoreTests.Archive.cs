using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Silo;

public sealed partial class SiloStockStoreTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task ArchiveVerifiesExactClosureThroughQuotaOutageAndSourceLoss()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        using var root = new ArchiveTestDirectory();
        var sourceKeys = ArchiveKeys();
        var archiveKeys = ArchiveKeys();
        var replicaKeys = ArchiveKeys();
        var readerKeys = ArchiveKeys();
        var instance = Guid.NewGuid();
        var binding = new SiloStorageBinding(instance.ToString("D"), CameraAgentSiloBinding.StoreIdFor(instance),
            CameraAgentSiloBinding.BucketFor(instance));
        var archiveId = $"silo-archive-{Guid.NewGuid():N}";
        var archiveBucket = $"archive-{instance:N}";

        var network = new NetworkBuilder().WithName($"hvo-silo-archive-{Guid.NewGuid():N}").Build();
        await using var networkLease = network.ConfigureAwait(false);
        await network.CreateAsync(token).ConfigureAwait(false);
        var targetCertificates = Directory.CreateDirectory(Path.Combine(root.Path, "archive-tls")).FullName;
        Directory.CreateDirectory(Path.Combine(targetCertificates, "CAs"));
        var sourceCertificates = Directory.CreateDirectory(Path.Combine(root.Path, "source-tls", "CAs"));
        var target = BuildStockContainer(archiveKeys.Access, archiveKeys.Secret, "on", network, targetCertificates);
        await using var targetLease = target.ConfigureAwait(false);
        await target.StartAsync(token).ConfigureAwait(false);
        var archiveNetworkOrigin = new UriBuilder("https", target.IpAddress, Port).Uri;
        var caFile = Path.Combine(sourceCertificates.FullName, "archive.pem");
        var authority = await CreateArchiveCertificatesAsync(archiveNetworkOrigin, targetCertificates, caFile, token).ConfigureAwait(false);
        await target.StopAsync(token).ConfigureAwait(false);
        await target.StartAsync(token).ConfigureAwait(false);

        var primary = BuildStockContainer(sourceKeys.Access, sourceKeys.Secret, "on", network, sourceCertificates.Parent!.FullName);
        await using var primaryLease = primary.ConfigureAwait(false);
        await primary.StartAsync(token).ConfigureAwait(false);
        using var sourceAdmin = SiloS3ObjectStore.CreateClient(Endpoint(primary), sourceKeys.Access, sourceKeys.Secret);
        using var transport = new SiloArchiveTransport(authority);
        using var archiveAdmin = SiloS3ObjectStore.CreateClient(archiveNetworkOrigin, archiveKeys.Access, archiveKeys.Secret, transport);
        await CreateVersionedBucketAsync(sourceAdmin, binding.Bucket, token).ConfigureAwait(false);
        await CreateArchiveBucketAsync(archiveAdmin, archiveBucket, token).ConfigureAwait(false);
        var writer = await CreateWriterAsync(primary, binding, token).ConfigureAwait(false);
        await InstallBindingAsync(sourceAdmin, binding, writer.Access, token).ConfigureAwait(false);

        var lab = await ArchiveMaintenance.CreateAsync(primary, root.Path, binding, archiveBucket, archiveId, caFile,
            Origin(Endpoint(primary), sourceKeys), Origin(archiveNetworkOrigin, archiveKeys),
            Origin(archiveNetworkOrigin, replicaKeys), Origin(archiveNetworkOrigin, readerKeys), token).ConfigureAwait(false);
        await lab.McAsync(token, "admin", "user", "add", "archive", replicaKeys.Access, replicaKeys.Secret).ConfigureAwait(false);
        await lab.McAsync(token, "admin", "user", "add", "archive", readerKeys.Access, readerKeys.Secret).ConfigureAwait(false);
        await lab.SetupAsync("enable", token).ConfigureAwait(false);
        var rule = await lab.McAsync(token, "replicate", "export", $"source/{binding.Bucket}").ConfigureAwait(false);
        await lab.SetupAsync("enable", token).ConfigureAwait(false);
        await lab.SetupAsync("verify-enabled", token).ConfigureAwait(false);
        Assert.AreEqual(rule, await lab.McAsync(token, "replicate", "export", $"source/{binding.Bucket}").ConfigureAwait(false));

        var settings = InstalledSettings(binding, Endpoint(primary), writer, Guid.NewGuid(), RandomHex(24));
        settings["CameraAgent:SiloArchive:Endpoint"] = archiveNetworkOrigin.AbsoluteUri;
        settings["CameraAgent:SiloArchive:AuthorityPem"] = authority;
        settings["CameraAgent:SiloArchive:ReadScratchRoot"] = root.Path;
        settings["CameraAgent:SiloArchive:StoreId"] = archiveId;
        settings["CameraAgent:SiloArchive:Bucket"] = archiveBucket;
        settings["CameraAgent:SiloArchive:SourceBindingId"] = binding.BindingId;
        settings["CameraAgent:SiloArchive:AccessKey"] = readerKeys.Access;
        settings["CameraAgent:SiloArchive:SecretKey"] = readerKeys.Secret;
        settings["CameraAgent:SiloArchive:ReplicationEnabled"] = "true";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var sourceReadFault = new ExactReadFault();
        using var source = new SiloInstalledStorage(SiloInstalledSettings.Read(configuration.GetSection("CameraAgent:Silo")),
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = root.Path }), TimeProvider.System,
            decorateReadStore: store => new FaultingReadStore(store, sourceReadFault));
        await source.VerifyReadinessAsync(token).ConfigureAwait(false);
        using var services = new ServiceCollection().AddSingleton(source).BuildServiceProvider();
        var clock = new ArchiveTestClock();
        using var archiveWorker = new SiloArchiveWorker(configuration, services, clock, NullLogger<SiloArchiveWorker>.Instance);
        using var replica = SiloS3ObjectStore.CreateClient(archiveNetworkOrigin, replicaKeys.Access, replicaKeys.Secret, transport);
        using var reader = SiloS3ObjectStore.CreateClient(archiveNetworkOrigin, readerKeys.Access, readerKeys.Secret, transport);

        await lab.McAsync(token, "quota", "set", $"archive/{archiveBucket}", "--size", "64KiB").ConfigureAwait(false);
        var rawBytes = RandomNumberGenerator.GetBytes(256 * 1024);
        var raw = await PublishArchiveFixtureAsync(source, "raw", rawBytes, [], token).ConfigureAwait(false);
        var display = await PublishArchiveFixtureAsync(source, "display", "selected display"u8.ToArray(),
            [new(raw.Member.Identity.Id, raw.Member.Identity.Revision, raw.Member.Identity.Sha256, SiloDependencyKind.Reopen, true)],
            token).ConfigureAwait(false);

        var archiveDescriptor = new SiloStoreDescriptor(archiveId, archiveBucket, SiloStoreClass.Archive, SiloRetrieval.Online, true);
        var archiveObjects = new SiloS3ObjectStore(archiveAdmin, archiveDescriptor, TimeSpan.FromSeconds(3));
        await WaitUntilAsync(async () =>
        {
            using var manifest = await archiveObjects.OpenVersionAsync(display.Manifest.Key, display.Manifest.VersionId, token).ConfigureAwait(false);
            return manifest.Status == SiloOpenStatus.Found;
        }, token).ConfigureAwait(false);
        using (var absent = await archiveObjects.OpenVersionAsync(raw.Member.Locator.Key, raw.Member.Locator.VersionId, token).ConfigureAwait(false))
            Assert.AreNotEqual(SiloOpenStatus.Found, absent.Status, "The archive cap must refuse the exact dependency while allowing the manifest.");
        var pending = await archiveWorker.RunCycleAsync(token).ConfigureAwait(false);
        TestContext.WriteLine($"Capped archive status {{0}} ({{1}}), {{2}} pending.", pending.State, pending.Reason, pending.BacklogCount);
        Assert.AreEqual("pending", pending.State, pending.Reason);
        Assert.AreEqual(2L, pending.BacklogCount);
        Assert.IsGreaterThan(rawBytes.LongLength, pending.BacklogBytes);
        Assert.IsNull(pending.VerifiedCutoff);
        Assert.IsNull((await archiveWorker.FindAsync(display.GroupId, "1", token).ConfigureAwait(false))?.Receipt);
        Assert.IsTrue((await source.Catalog.ReopenAsync(display.GroupId, "1", token).ConfigureAwait(false)).Reopenable);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            clock.AdvanceRetry();
            await archiveWorker.RunCycleAsync(token).ConfigureAwait(false);
        }
        Assert.AreEqual("blocked", archiveWorker.Status.State);
        Assert.IsNull(archiveWorker.Status.LatestVerifiedReceipt);
        configuration["CameraAgent:SiloArchive:SecretKey"] = RandomHex(24);
        Assert.AreEqual("archive-credentials-refused", (await archiveWorker.RunCycleAsync(token).ConfigureAwait(false)).Reason);
        configuration["CameraAgent:SiloArchive:SecretKey"] = readerKeys.Secret;

        await target.StopAsync(token).ConfigureAwait(false);
        var isolated = await PublishArchiveFixtureAsync(source, "during-outage", "local view during archive outage"u8.ToArray(), [], token)
            .ConfigureAwait(false);
        var unavailable = await archiveWorker.RunCycleAsync(token).ConfigureAwait(false);
        Assert.AreEqual("blocked", unavailable.State, unavailable.Reason);
        Assert.IsTrue((await source.Catalog.ReopenAsync(isolated.GroupId, "1", token).ConfigureAwait(false)).Reopenable);
        await target.StartAsync(token).ConfigureAwait(false);
        Assert.AreEqual(archiveNetworkOrigin.Host, target.IpAddress);

        using var resumed = new SiloArchiveWorker(configuration, services, clock, NullLogger<SiloArchiveWorker>.Instance);
        await WaitUntilAsync(async () =>
        {
            clock.AdvanceRetry();
            await resumed.RunCycleAsync(token).ConfigureAwait(false);
            return (await resumed.FindAsync(isolated.GroupId, "1", token).ConfigureAwait(false))?.State == "reachable";
        }, token).ConfigureAwait(false);
        Assert.IsNull(resumed.Status.VerifiedCutoff, "A later receipt must not advance the verified cutoff across the capped dependency.");

        await lab.McAsync(token, "quota", "clear", $"archive/{archiveBucket}").ConfigureAwait(false);
        await lab.ResyncAsync(token).ConfigureAwait(false);
        await WaitUntilAsync(async () =>
        {
            using var arrived = await archiveObjects.OpenVersionAsync(raw.Member.Locator.Key, raw.Member.Locator.VersionId, token).ConfigureAwait(false);
            return arrived.Status == SiloOpenStatus.Found;
        }, token).ConfigureAwait(false);
        await SetArchiveHoldAsync(archiveAdmin, archiveBucket, raw.Member.Locator, ObjectLockLegalHoldStatus.On, token).ConfigureAwait(false);

        // Recreate the worker to prove the queued gaps and bounded retry survive its process state.
        using var caughtUp = new SiloArchiveWorker(configuration, services, clock, NullLogger<SiloArchiveWorker>.Instance);
        await WaitUntilAsync(async () =>
        {
            clock.AdvanceRetry();
            var status = await caughtUp.RunCycleAsync(token).ConfigureAwait(false);
            if (status.State is "failed") Assert.Fail(status.Reason ?? "Archive verification failed.");
            return status.State == "reachable" && status.BacklogCount == 0 && status.VerifiedCutoff?.GroupId == isolated.GroupId;
        }, token).ConfigureAwait(false);
        var verified = (await caughtUp.FindAsync(display.GroupId, "1", token).ConfigureAwait(false))?.Receipt;
        Assert.IsNotNull(verified);
        using (var archive = SiloArchiveStore.Create(configuration.GetSection("CameraAgent:SiloArchive"), clock))
        {
            var segment = await archive.ReadSegmentAsync(new(new(verified.ReceiptGeneration, verified.ReceiptSequence),
                verified.ReceiptSha256, verified.ReceiptVersionId), token).ConfigureAwait(false);
            var recorded = segment.Records.OfType<SiloCopyRecord>().Single(copy => copy.Identity == raw.Member.Identity);
            Assert.AreEqual(SiloArchiveStore.Custody, recorded.Custody);
            Assert.AreEqual(SiloArchiveStore.RetentionPolicy, recorded.RetentionPolicy);
            Assert.IsTrue(recorded.ArchiveRetention?.LegalHold);
        }
        await source.Catalog.CatchUpAsync(await source.State.ReadWriterEvidenceAsync(token).ConfigureAwait(false), null, token).ConfigureAwait(false);
        var indexed = await source.Index.ReadRoleAsync(raw.Member.Identity.Role, token).ConfigureAwait(false);
        Assert.IsNotNull(indexed);
        Assert.IsTrue(indexed.Locators.Any(locator => locator.Source == "copy" && locator.Locator.StoreId == archiveId &&
            locator.Locator.VersionId == raw.Member.Locator.VersionId && locator.Eligible));

        var isolatedReceipt = (await caughtUp.FindAsync(isolated.GroupId, "1", token).ConfigureAwait(false))!.Receipt!;
        await lab.SetupAsync("expire", token, "--key", isolated.Member.Locator.Key, "--version-id", isolated.Member.Locator.VersionId)
            .ConfigureAwait(false);
        await lab.ResyncAsync(token).ConfigureAwait(false);
        await WaitUntilAsync(async () =>
        {
            using var arrived = await archiveObjects.OpenVersionAsync(isolated.Member.Locator.Key, isolated.Member.Locator.VersionId, token)
                .ConfigureAwait(false);
            return arrived.Status == SiloOpenStatus.Found;
        }, token).ConfigureAwait(false);
        using (var late = await caughtUp.ReadAsync(new(isolatedReceipt, isolated.Member.Identity.Id, isolated.Member.Identity.Revision), token)
            .ConfigureAwait(false))
            Assert.AreEqual("policy-expired", late.State, "The same version arriving again must respect its durable expiration.");

        var later = await PublishArchiveFixtureAsync(source, "after-expiry", "reachable after policy expiry"u8.ToArray(), [], token)
            .ConfigureAwait(false);
        clock.Advance(TimeSpan.FromDays(1));
        await WaitUntilAsync(async () =>
        {
            clock.AdvanceRetry();
            var status = await caughtUp.RunCycleAsync(token).ConfigureAwait(false);
            if (status.State == "failed") Assert.Fail(status.Reason ?? "Archive verification failed.");
            return (await caughtUp.FindAsync(isolated.GroupId, "1", token).ConfigureAwait(false))?.State == "policy-expired" &&
                status is { State: "reachable", BacklogCount: 0 } && status.VerifiedCutoff?.GroupId == later.GroupId;
        }, token).ConfigureAwait(false);

        var admission = new SqliteRawCaptureJournal(Path.Combine(root.Path, "journal", "raw-ingress.db"), 5, usesSilo: true);
        await admission.InitializeAsync(token).ConfigureAwait(false);
        await source.Catalog.CatchUpAsync(await source.State.ReadWriterEvidenceAsync(token).ConfigureAwait(false), null, token).ConfigureAwait(false);

        var lifecycle = WebApplication.CreateBuilder();
        lifecycle.WebHost.UseTestServer();
        lifecycle.Configuration["LifecycleControl:Token"] = settings["LifecycleControl:Token"];
        lifecycle.Services.AddSingleton<ICameraAgentSiloVerification>(source);
        lifecycle.Services.AddSingleton<CaptureAdmissionCoordinator>(_ => null!);
        lifecycle.Services.AddSingleton<CameraAgentOperationsSummaryProvider>(_ => null!);
        lifecycle.Services.AddSingleton<DeploymentContinuityReader>(_ => null!);
        lifecycle.Services.AddSingleton(Options.Create(new CameraAgentHostOptions()));
        var lifecycleApp = lifecycle.Build();
        await using var lifecycleLease = lifecycleApp.ConfigureAwait(false);
        lifecycleApp.MapCameraAgentLifecycleEndpoints();
        await lifecycleApp.StartAsync(token).ConfigureAwait(false);
        using var verifier = lifecycleApp.GetTestClient();
        verifier.DefaultRequestHeaders.Add("X-HVO-Installation-Token", settings["LifecycleControl:Token"]);
        var artifacts = new SiloArtifactStorage(source, TimeProvider.System);
        var member = raw.Member.Identity;

        sourceReadFault.Set(raw.Member.Locator, ReadFault.Damaged);
        await AssertLocalHistoryAsync(verifier, HttpStatusCode.Conflict, token).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            artifacts.OpenMemberAsync(Guid.Parse(member.Id), member.Revision, member.Length, member.Sha256, token).AsTask()).ConfigureAwait(false);
        sourceReadFault.Clear();

        await AssertReplicaPermissionsAsync(replica, reader, archiveBucket, raw.Member.Locator, token).ConfigureAwait(false);
        await AssertReplicationDeleteIsolatedAsync(sourceAdmin, archiveAdmin, binding.Bucket, archiveBucket, raw.Member.Locator, token)
            .ConfigureAwait(false);
        await AssertLocalHistoryAsync(verifier, HttpStatusCode.Gone, token).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            artifacts.OpenMemberAsync(Guid.Parse(member.Id), member.Revision, member.Length, member.Sha256, token).AsTask()).ConfigureAwait(false);
        await lab.SetupAsync("disable", token).ConfigureAwait(false);
        await lab.SetupAsync("disable", token).ConfigureAwait(false);
        await lab.SetupAsync("verify-disabled", token).ConfigureAwait(false);
        configuration["CameraAgent:SiloArchive:ReplicationEnabled"] = "false";
        Assert.AreEqual("disabled", (await caughtUp.RunCycleAsync(token).ConfigureAwait(false)).State);
        await primary.DisposeAsync().ConfigureAwait(false);

        using var replacement = new ServiceCollection().BuildServiceProvider();
        using var restore = new SiloArchiveWorker(configuration, replacement, clock, NullLogger<SiloArchiveWorker>.Instance);
        var request = new CameraAgentSiloArchiveRead(verified, raw.Member.Identity.Id, raw.Member.Identity.Revision);
        using (var restored = await restore.ReadAsync(request, token).ConfigureAwait(false))
        {
            Assert.AreEqual("reachable", restored.State);
            Assert.AreEqual(raw.Member.Identity.Sha256, restored.Sha256);
            using var stream = new MemoryStream();
            await restored.Content!.CopyToAsync(stream, token).ConfigureAwait(false);
            CollectionAssert.AreEqual(rawBytes, stream.ToArray());
        }

        var corrupt = request with { Receipt = request.Receipt with { ReceiptSha256 = new string('a', 64) } };
        using (var refused = await restore.ReadAsync(corrupt, token).ConfigureAwait(false))
            Assert.AreEqual("failed", refused.State);
        await lab.SetupAsync("expire", token, "--key", raw.Member.Locator.Key, "--version-id", raw.Member.Locator.VersionId).ConfigureAwait(false);
        using var expired = await restore.ReadAsync(request, token).ConfigureAwait(false);
        Assert.AreEqual("policy-expired", expired.State);
        Assert.IsNull(expired.Content);
    }

    private static (string Access, string Secret) ArchiveKeys() => (RandomHex(12), RandomHex(24));

    private static string Origin(Uri endpoint, (string Access, string Secret) keys) =>
        new UriBuilder(endpoint) { UserName = keys.Access, Password = keys.Secret }.Uri.AbsoluteUri;

    private static async Task<string> CreateArchiveCertificatesAsync(Uri endpoint, string certificates, string caFile, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Disposable Silo Archive CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(now.AddHours(-1), now.AddDays(1));
        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=Disposable Silo Archive", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Parse(endpoint.Host));
        names.AddIpAddress(IPAddress.Loopback);
        serverRequest.CertificateExtensions.Add(names.Build());
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var cert = serverRequest.Create(ca, now.AddMinutes(-15), now.AddHours(8), RandomNumberGenerator.GetBytes(16));
        await File.WriteAllTextAsync(Path.Combine(certificates, "public.crt"), cert.ExportCertificatePem(), token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(certificates, "private.key"), serverKey.ExportPkcs8PrivateKeyPem(), token).ConfigureAwait(false);
        var authority = ca.ExportCertificatePem();
        await File.WriteAllTextAsync(caFile, authority, token).ConfigureAwait(false);
        return authority;
    }

    private static async Task CreateArchiveBucketAsync(AmazonS3Client client, string bucket, CancellationToken token)
    {
        await WaitUntilAsync(async () =>
        {
            try
            {
                await client.ListBucketsAsync(new ListBucketsRequest(), token).ConfigureAwait(false);
                return true;
            }
            catch (AmazonS3Exception exception) when (exception.ErrorCode == "XMinioServerNotInitialized")
            {
                return false;
            }
        }, token).ConfigureAwait(false);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket, ObjectLockEnabledForBucket = true }, token).ConfigureAwait(false);
    }

    private static async Task<(string GroupId, SiloExactLocator Manifest, (SiloObjectIdentity Identity, SiloExactLocator Locator) Member)>
        PublishArchiveFixtureAsync(SiloInstalledStorage source, string name, byte[] bytes, IReadOnlyList<SiloManifestDependency> dependencies,
            CancellationToken token)
    {
        var sha = SiloHash.Sha256(bytes);
        var id = Guid.NewGuid().ToString("D");
        var group = "archive-fixture-" + name;
        var now = DateTimeOffset.UtcNow;
        var member = new SiloDraftMember(id, "1", "fixture", SiloRetentionClass.Full, "application/octet-stream",
            bytes.LongLength, sha, true, SiloFilePayloadSpool.TokenOf(sha));
        var draft = new SiloGroupDraft("archive-fixture", group, "1", now, now, now.AddTicks(1), [member], dependencies, new([]));
        await new SiloArtifactStorage(source, TimeProvider.System).PublishAsync(draft, (_, cancellation) =>
            SiloArtifactStorage.OpenMemoryAsync(bytes, cancellation), token).ConfigureAwait(false);
        var indexed = await source.Index.ReadRoleAsync(new(SiloIdentityKind.Member, id, "1"), token).ConfigureAwait(false);
        var manifest = await source.Index.ReadRoleAsync(new(SiloIdentityKind.Manifest, group, "1"), token).ConfigureAwait(false);
        Assert.IsNotNull(indexed?.AvailableAt);
        Assert.IsNotNull(manifest?.AvailableAt);
        return (group, manifest.AvailableAt, (new(SiloIdentityKind.Member, id, "1", sha, bytes.LongLength), indexed.AvailableAt));
    }

    private static async Task AssertLocalHistoryAsync(HttpClient verifier, HttpStatusCode expected, CancellationToken token)
    {
        using var response = await verifier.PostAsync(VerifyHistoryRoute, null, token).ConfigureAwait(false);
        Assert.AreEqual(expected, response.StatusCode);
        var counts = await response.Content.ReadFromJsonAsync<CameraAgentSiloVerificationResult>(token).ConfigureAwait(false);
        Assert.IsNotNull(counts);
        Assert.AreEqual(0L, counts.UnavailableRoles);
        Assert.IsGreaterThan(0L, expected == HttpStatusCode.Conflict ? counts.DamagedRoles : counts.MissingRoles);
    }

    private static async Task AssertReplicaPermissionsAsync(AmazonS3Client replica, AmazonS3Client reader,
        string bucket, SiloExactLocator version, CancellationToken token)
    {
        foreach (var id in new[] { null, version.VersionId })
        {
            foreach (var client in new[] { replica, reader })
                await DeniedAsync(() => client.DeleteObjectAsync(new DeleteObjectRequest
                { BucketName = bucket, Key = version.Key, VersionId = id }, token)).ConfigureAwait(false);
        }

        foreach (var key in new[] { SiloArchiveStore.MarkerKey, "meta/archive-expiry/forbidden.json" })
            await DeniedAsync(() => replica.PutObjectAsync(new PutObjectRequest
            { BucketName = bucket, Key = key, ContentBody = "forbidden" }, token)).ConfigureAwait(false);
        await DeniedAsync(() => reader.PutObjectAsync(new PutObjectRequest
        { BucketName = bucket, Key = version.Key, ContentBody = "forbidden" }, token)).ConfigureAwait(false);
        await DeniedAsync(() => SetArchiveHoldAsync(replica, bucket, version, ObjectLockLegalHoldStatus.Off, token)).ConfigureAwait(false);
        await DeniedAsync(() => replica.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = version.Key,
            VersionId = version.VersionId,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = DateTime.UtcNow.AddHours(1) }
        }, token)).ConfigureAwait(false);
        await DeniedAsync(() => replica.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules = [new LifecycleRule
                {
                    Id = "forbidden", Status = LifecycleRuleStatus.Enabled,
                    Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = "p/" } },
                    Expiration = new LifecycleRuleExpiration { Days = 1 }
                }]
            }
        }, token)).ConfigureAwait(false);

        static async Task DeniedAsync(Func<Task> attempt)
        {
            var rejected = await Assert.ThrowsExactlyAsync<AmazonS3Exception>(attempt).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Forbidden, rejected.StatusCode);
        }
    }

    private static async Task AssertReplicationDeleteIsolatedAsync(AmazonS3Client source, AmazonS3Client archive,
        string sourceBucket, string archiveBucket, SiloExactLocator version, CancellationToken token)
    {
        var held = await Assert.ThrowsExactlyAsync<AmazonS3Exception>(async () =>
            await archive.DeleteObjectAsync(new DeleteObjectRequest { BucketName = archiveBucket, Key = version.Key, VersionId = version.VersionId },
                token).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, held.StatusCode);
        Assert.AreEqual("InvalidRequest", held.ErrorCode);
        await SetArchiveHoldAsync(archive, archiveBucket, version, ObjectLockLegalHoldStatus.Off, token).ConfigureAwait(false);

        var marker = await source.DeleteObjectAsync(new DeleteObjectRequest { BucketName = sourceBucket, Key = version.Key }, token)
            .ConfigureAwait(false);
        Assert.AreEqual("true", marker.DeleteMarker);
        await source.DeleteObjectAsync(new DeleteObjectRequest { BucketName = sourceBucket, Key = version.Key, VersionId = version.VersionId }, token)
            .ConfigureAwait(false);
        var canaryKey = $"p/delete-isolation-{Guid.NewGuid():N}";
        const string canaryBody = "published after source deletes";
        var canary = await source.PutObjectAsync(new PutObjectRequest
        { BucketName = sourceBucket, Key = canaryKey, ContentBody = canaryBody }, token).ConfigureAwait(false);
        await WaitUntilAsync(async () =>
        {
            try
            {
                using var arrived = await archive.GetObjectAsync(new GetObjectRequest
                { BucketName = archiveBucket, Key = canaryKey, VersionId = canary.VersionId }, token).ConfigureAwait(false);
                using var body = new StreamReader(arrived.ResponseStream);
                Assert.AreEqual(canaryBody, await body.ReadToEndAsync(token).ConfigureAwait(false));
                return true;
            }
            catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }, token).ConfigureAwait(false);
        using var surviving = await archive.GetObjectAsync(new GetObjectRequest
        {
            BucketName = archiveBucket,
            Key = version.Key,
            VersionId = version.VersionId
        }, token).ConfigureAwait(false);
        Assert.AreEqual(version.VersionId, surviving.VersionId);
        using var current = await archive.GetObjectAsync(new GetObjectRequest { BucketName = archiveBucket, Key = version.Key }, token).ConfigureAwait(false);
        Assert.AreEqual(version.VersionId, current.VersionId);
    }

    private static Task<PutObjectLegalHoldResponse> SetArchiveHoldAsync(
        AmazonS3Client client, string bucket, SiloExactLocator locator, ObjectLockLegalHoldStatus status,
        CancellationToken token) =>
        client.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = bucket,
            Key = locator.Key,
            VersionId = locator.VersionId,
            LegalHold = new ObjectLockLegalHold { Status = status }
        }, token);

    private static async Task WaitUntilAsync(Func<Task<bool>> ready, CancellationToken token)
    {
        while (!await ready().ConfigureAwait(false))
            await Task.Delay(TimeSpan.FromMilliseconds(400), token).ConfigureAwait(false);
    }

    private sealed class ArchiveTestClock : TimeProvider
    {
        private TimeSpan _offset;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;

        internal void AdvanceRetry() => Advance(TimeSpan.FromMinutes(5));

        internal void Advance(TimeSpan duration) => _offset += duration;
    }

    private sealed class ArchiveTestDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("silo-archive-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class ArchiveMaintenance(string script, string binaryDirectory, string sourceBucket, string archiveBucket,
        string storeId, string bindingId, Dictionary<string, string> environment)
    {
        internal static async Task<ArchiveMaintenance> CreateAsync(IContainer container, string root, SiloStorageBinding binding, string bucket,
            string storeId, string caFile, string source, string archive, string replica, string reader, CancellationToken token)
        {
            var mc = Path.Combine(root, "mc");
            await File.WriteAllBytesAsync(mc, await container.ReadFileAsync("/usr/bin/mcli", token).ConfigureAwait(false), token).ConfigureAwait(false);
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            File.SetUnixFileMode(mc, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(directory.FullName, "scripts", "silo:archive")))
                directory = directory.Parent ?? throw new InvalidOperationException("The stock Silo archive maintenance script is missing.");
            return new(Path.Combine(directory.FullName, "scripts", "silo:archive"), root, binding.Bucket, bucket, storeId, binding.BindingId,
                new(StringComparer.Ordinal)
                {
                    ["MC_HOST_source"] = source,
                    ["MC_HOST_archive"] = archive,
                    ["MC_HOST_replica"] = replica,
                    ["MC_HOST_reader"] = reader,
                    ["MC_CONFIG_DIR"] = root,
                    ["SSL_CERT_FILE"] = caFile
                });
        }

        internal Task<string> SetupAsync(string operation, CancellationToken token, params string[] extra) =>
            RunAsync("python3", [script, operation, "--source-bucket", sourceBucket, "--archive-bucket", archiveBucket,
                "--store-id", storeId, "--binding", bindingId, .. extra], operation, token);

        internal Task<string> McAsync(CancellationToken token, params string[] args) =>
            RunAsync(Path.Combine(binaryDirectory, "mc"), ["--json", .. args], args[0], token);

        internal async Task ResyncAsync(CancellationToken token)
        {
            var config = await McAsync(token, "replicate", "export", $"source/{sourceBucket}").ConfigureAwait(false);
            using var json = JsonDocument.Parse(config);
            var arn = json.RootElement.GetProperty("config").GetProperty("Rules")[0].GetProperty("Destination").GetProperty("Bucket").GetString()!;
            await McAsync(token, "replicate", "resync", "start", $"source/{sourceBucket}", "--remote-bucket", arn).ConfigureAwait(false);
        }

        private async Task<string> RunAsync(string command, string[] arguments, string operation, CancellationToken token)
        {
            var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var (key, value) in environment) start.Environment[key] = value;
            start.Environment["PATH"] = binaryDirectory + Path.PathSeparator + start.Environment["PATH"];
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The stock Silo maintenance command could not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            Assert.AreEqual(0, process.ExitCode, command == "python3" ? await stderr.ConfigureAwait(false) : $"Stock Silo mc {operation} failed.");
            return await stdout.ConfigureAwait(false);
        }
    }
}
