using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace HVO.SkyMonitor.CameraAgent.Tests.Notifications;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", CameraAgentTestAreas.Services)]
public sealed class OperatorNotificationStoreTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ExpectedOpsRecipients = ["ops@example.test"];
    [TestMethod]
    public async Task DefaultOff_CreatesZeroFilesOnDiskAsync()
    {
        using var dir = TempStateDir.Create();
        using var store = CreateStore(dir.Path, isDevelopment: false);

        var snapshot = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(snapshot.Options.Enabled);
        Assert.IsFalse(snapshot.HasSavedOverrides);
        Assert.IsTrue(snapshot.CanPersist);
        Assert.IsFalse(snapshot.StoreCorrupt);
        Assert.IsFalse(File.Exists(store.ConfigFilePath));
    }

    [TestMethod]
    public async Task SaveAndReload_AtomicallyEncryptsSecretsIncludingNtfyTopicEnforcesPermissionsAndSupportsKeepReplaceClearAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        const string smtpUser = "protected-user-1122";
        const string smtpPass = "protected-pass-3344";
        const string webhookUrl = "https://hooks.example.test/secret-path-5566?sig=7788";
        const string webhookToken = "protected-token-9900";
        const string ntfyTopic = "protected-ntfy-topic-4455";

        using (var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider))
        {
            var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var options = new OperatorNotificationsOptions
            {
                Enabled = true,
                Smtp = new SmtpNotificationOptions
                {
                    Enabled = true,
                    Host = "smtp.example.test",
                    Port = 587,
                    SecurityMode = SmtpSecurityMode.StartTls,
                    FromAddress = "camera@example.test",
                    Recipients = ["ops@example.test"]
                },
                Webhook = new WebhookNotificationOptions
                {
                    Enabled = true,
                    Format = WebhookPayloadFormat.Ntfy
                }
            };

            var saved = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                initial.Version,
                options,
                SmtpUsername: smtpUser,
                SmtpPassword: smtpPass,
                WebhookUrl: webhookUrl,
                WebhookAuthToken: webhookToken,
                WebhookNtfyTopic: ntfyTopic), "owner-1", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved.Outcome, saved.Message);
            Assert.IsNotNull(saved.Snapshot);
            Assert.IsTrue(saved.Snapshot.Secrets.HasSmtpUsername);
            Assert.IsTrue(saved.Snapshot.Secrets.HasSmtpPassword);
            Assert.IsTrue(saved.Snapshot.Secrets.HasWebhookUrl);
            Assert.IsTrue(saved.Snapshot.Secrets.HasWebhookNtfyTopic);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, saved.Snapshot.Secrets.WebhookNtfyTopicSource);
            Assert.AreEqual("https://hooks.example.test", saved.Snapshot.Secrets.WebhookEndpointHost);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, saved.Snapshot.Secrets.WebhookUrlSource);

            // Verify ONE atomic envelope file exists, contains zero plaintext secrets, and has 0600 permissions.
            Assert.IsTrue(File.Exists(store.ConfigFilePath));
            var diskContent = await File.ReadAllTextAsync(store.ConfigFilePath).ConfigureAwait(false);
            foreach (var canary in new[] { smtpUser, smtpPass, "secret-path-5566", "7788", webhookToken, ntfyTopic })
            {
                Assert.DoesNotContain(canary, diskContent, StringComparison.Ordinal);
            }

            if (!OperatingSystem.IsWindows())
            {
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.ConfigFilePath));
            }
        }

        // Reload in a fresh store instance sharing the DataProtection key ring.
        using (var reloaded = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider))
        {
            var (opts, secrets) = await reloaded.GetEffectiveForDeliveryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(opts.Enabled);
            Assert.AreEqual(smtpUser, secrets.SmtpUsername);
            Assert.AreEqual(smtpPass, secrets.SmtpPassword);
            Assert.AreEqual(webhookUrl, secrets.WebhookUrl);
            Assert.AreEqual(webhookToken, secrets.WebhookAuthToken);
            Assert.AreEqual(ntfyTopic, secrets.WebhookNtfyTopic);

            // Stale version returns Conflict.
            var conflict = await reloaded.SaveAsync(new OperatorNotificationsUpdateRequest("stale-version", opts), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Conflict, conflict.Outcome);

            // Updating without re-entering secrets keeps existing ones; Clear removes selected secret.
            var snap = await reloaded.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var cleared = await reloaded.SaveAsync(new OperatorNotificationsUpdateRequest(
                snap.Version,
                opts,
                ClearWebhookAuthToken: true,
                ClearWebhookNtfyTopic: true), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, cleared.Outcome);
            var (_, afterClear) = await reloaded.GetEffectiveForDeliveryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(smtpPass, afterClear.SmtpPassword);
            Assert.IsNull(afterClear.WebhookAuthToken);
            Assert.IsNull(afterClear.WebhookNtfyTopic);
        }
    }

    [TestMethod]
    public async Task SaveAsync_PasswordOnlyRotation_AdvancesVersionAndRejectsStaleEditorWithConflictAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider);

        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            }
        };

        var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var saved1 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            initial.Version, options, SmtpUsername: "user1", SmtpPassword: "password-v1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved1.Outcome);
        var version1 = saved1.Snapshot!.Version;

        // Rotate ONLY the password (options and secret presence flags remain identical).
        var saved2 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            version1, options, SmtpPassword: "password-v2"), "owner-2", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved2.Outcome);
        var version2 = saved2.Snapshot!.Version;
        Assert.AreNotEqual(version1, version2, "Secret-only rotation MUST advance the optimistic concurrency version.");

        // Stale editor holding version1 MUST receive Conflict and NOT overwrite password-v2.
        var staleAttempt = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            version1, options, SmtpPassword: "stale-password-v0"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Conflict, staleAttempt.Outcome);

        var (_, effective) = await store.GetEffectiveForDeliveryAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("password-v2", effective.SmtpPassword);
    }

    [TestMethod]
    public async Task SaveAsync_FaultInjectionDuringRelayRotation_NeverMixesNewOptionsWithOldSecretsAsync()
    {
        using var dir = TempStateDir.Create();
        var realProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        var faultProvider = new FaultInjectingDataProtectionProvider(realProvider);

        var relay1Options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-one.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            }
        };
        var relay2Options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-two.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            }
        };

        string versionAfterRelay1;
        using (var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: faultProvider))
        {
            var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var saved1 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                initial.Version, relay1Options, SmtpUsername: "relay1-user", SmtpPassword: "relay1-password"),
                "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved1.Outcome);
            versionAfterRelay1 = saved1.Snapshot!.Version;

            // 1. Inject DataProtection Protect() failure during rotation to relay2 + relay2-password.
            faultProvider.FailProtect = true;
            await Assert.ThrowsExactlyAsync<CryptographicException>(async () =>
                await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                    versionAfterRelay1, relay2Options, SmtpUsername: "relay2-user", SmtpPassword: "relay2-password"),
                    "owner-1", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            faultProvider.FailProtect = false;

            var (afterDpFailOpts, afterDpFailSecrets) = await store.GetEffectiveForDeliveryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-one.example.test", afterDpFailOpts.Smtp.Host);
            Assert.AreEqual("relay1-password", afterDpFailSecrets.SmtpPassword);
        }

        // 2. Inject pre-rename I/O failure during rotation -> disk and restarted store still see relay1 + relay1-password.
        using (var storeWithIoFault = new OperatorNotificationStore(
            realProvider,
            Options.Create(new DeviceProvisioningOptions { StateDirectory = dir.Path }),
            new ConfigurationBuilder().Build(),
            new OperatorNotificationNetworkPolicy(isDevelopment: false),
            NullLogger<OperatorNotificationStore>.Instance)
        {
            BeforePublishTempFile = _ => throw new IOException("Simulated disk fault before atomic rename.")
        })
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await storeWithIoFault.SaveAsync(new OperatorNotificationsUpdateRequest(
                    versionAfterRelay1, relay2Options, SmtpUsername: "relay2-user", SmtpPassword: "relay2-password"),
                    "owner-1", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }

        // 3. Inject cancellation -> existing envelope stays intact.
        using (var storeForCancel = CreateStore(dir.Path, isDevelopment: false, dpProvider: realProvider))
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await storeForCancel.SaveAsync(new OperatorNotificationsUpdateRequest(
                    versionAfterRelay1, relay2Options, SmtpUsername: "relay2-user", SmtpPassword: "relay2-password"),
                    "owner-1", cts.Token).ConfigureAwait(false)).ConfigureAwait(false);
        }

        // 4. Fresh restart verifies consistent atomic pair (relay-one + relay1-password) and no leftover .tmp files.
        using (var restarted = CreateStore(dir.Path, isDevelopment: false, dpProvider: realProvider))
        {
            var (opts, secrets) = await restarted.GetEffectiveForDeliveryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-one.example.test", opts.Smtp.Host);
            Assert.AreEqual("relay1-user", secrets.SmtpUsername);
            Assert.AreEqual("relay1-password", secrets.SmtpPassword);
            var leftovers = Directory.EnumerateFiles(dir.Path, "*.tmp").ToArray();
            Assert.IsEmpty(leftovers);
        }
    }

    [TestMethod]
    public async Task CorruptOrUndecryptableStore_NeverFallsBackToHostCredentialsAndAllowsExplicitRecoveryAsync()
    {
        using var dir = TempStateDir.Create();
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "fallback-host.example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-fallback-user",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-fallback-password",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://host-fallback.example.test/secret"
            })
            .Build();

        // Case A: Corrupt JSON on disk.
        var storePath = Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName);
        const string corruptPayload = "{ this is not valid json !!!";
        await File.WriteAllTextAsync(storePath, corruptPayload).ConfigureAwait(false);

        using (var store = CreateStore(dir.Path, isDevelopment: false, configuration: hostConfig))
        {
            Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, store.ConfigurationErrorReasonCode);

            var snapshot = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(snapshot.StoreCorrupt);
            Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, snapshot.StoreStatusReasonCode);
            Assert.IsFalse(snapshot.Options.Enabled);
            Assert.IsFalse(snapshot.Secrets.HasSmtpPassword, "Corrupt store must NOT silently fall back to host config secrets.");
            Assert.IsFalse(snapshot.Secrets.HasWebhookUrl, "Corrupt store must NOT silently fall back to host config webhook URL.");

            var effective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(effective.StoreCorrupt);
            Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, effective.ConfigurationErrorReasonCode);
            Assert.IsFalse(effective.Options.Enabled);
            Assert.IsTrue(effective.Secrets.IsEmpty);

            // Damaged file MUST remain preserved intact on disk prior to operator recovery.
            Assert.AreEqual(corruptPayload, await File.ReadAllTextAsync(storePath).ConfigureAwait(false));

            // Explicit operator recovery PUT succeeds, quarantines damaged file, and restores normal state.
            var recovered = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snapshot.Version,
                new OperatorNotificationsOptions { Enabled = false }), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, recovered.Outcome, recovered.Message);
            Assert.IsNotNull(recovered.Snapshot);
            Assert.IsFalse(recovered.Snapshot.StoreCorrupt);
            Assert.IsNull(store.ConfigurationErrorReasonCode);
            Assert.IsTrue(File.Exists(store.QuarantineFilePath));
            Assert.AreEqual(corruptPayload, await File.ReadAllTextAsync(store.QuarantineFilePath).ConfigureAwait(false));
        }

        // Case B: Valid JSON envelope whose DataProtection ciphertext cannot be decrypted by a new key ring.
        var keyRingA = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "ring-a")));
        var keyRingB = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "ring-b")));
        using (var writerStore = CreateStore(dir.Path, isDevelopment: false, dpProvider: keyRingA, configuration: hostConfig))
        {
            var snap = await writerStore.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var saved = await writerStore.SaveAsync(new OperatorNotificationsUpdateRequest(
                snap.Version,
                new OperatorNotificationsOptions
                {
                    Enabled = true,
                    Smtp = new SmtpNotificationOptions
                    {
                        Enabled = true,
                        Host = "smtp.example.test",
                        Port = 587,
                        SecurityMode = SmtpSecurityMode.StartTls,
                        FromAddress = "camera@example.test",
                        Recipients = ["ops@example.test"]
                    }
                },
                SmtpUsername: "ring-a-user",
                SmtpPassword: "ring-a-password"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved.Outcome);
        }

        using (var readerStore = CreateStore(dir.Path, isDevelopment: false, dpProvider: keyRingB, configuration: hostConfig))
        {
            var effective = await readerStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(effective.StoreCorrupt);
            Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, effective.ConfigurationErrorReasonCode);
            Assert.IsNull(effective.Secrets.SmtpPassword, "Undecryptable store must NOT fall back to host config password.");
            Assert.IsNull(effective.Secrets.WebhookUrl, "Undecryptable store must NOT fall back to host config webhook URL.");
        }
    }

    [TestMethod]
    public async Task SymlinkTarget_IsRefusedOnReadAndWriteAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = TempStateDir.Create();
        var outsideFile = Path.Combine(dir.Path, "outside-target.txt");
        await File.WriteAllTextAsync(outsideFile, "decoy").ConfigureAwait(false);
        var stateDir = Path.Combine(dir.Path, "state");
        Directory.CreateDirectory(stateDir);
        File.CreateSymbolicLink(Path.Combine(stateDir, OperatorNotificationStore.ConfigFileName), outsideFile);

        using var store = CreateStore(stateDir, isDevelopment: false);
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await store.ReadAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ProductionStore_RejectsMailpitWithoutWritingFilesAsync()
    {
        using var dir = TempStateDir.Create();
        using var store = CreateStore(dir.Path, isDevelopment: false);
        var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);

        var result = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            initial.Version,
            new OperatorNotificationsOptions
            {
                Enabled = true,
                Smtp = new SmtpNotificationOptions
                {
                    Enabled = true,
                    Host = "mailpit",
                    Port = 1025,
                    SecurityMode = SmtpSecurityMode.None,
                    FromAddress = "camera@example.test",
                    Recipients = ["ops@example.test"]
                }
            }), "owner-1", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, result.Outcome);
        Assert.IsNotNull(result.Problems);
        Assert.IsTrue(result.Problems.ContainsKey("smtp.host"));
        Assert.IsFalse(File.Exists(store.ConfigFilePath));
    }

    [TestMethod]
    public async Task DirectoryAtEnvelopePath_NeverFallsBackToHostCredentialsAndReportsStoreUnreadableAsync()
    {
        using var dir = TempStateDir.Create();
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "fallback-host.example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-fallback-user",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-fallback-password",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://host-fallback.example.test/secret"
            })
            .Build();

        // Create a directory where the envelope file belongs.
        var envelopePath = Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName);
        Directory.CreateDirectory(envelopePath);

        using var store = CreateStore(dir.Path, isDevelopment: false, configuration: hostConfig);

        Assert.IsFalse(store.CurrentOptions.Enabled);
        Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, store.ConfigurationErrorReasonCode);

        var snapshot = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(snapshot.StoreCorrupt);
        Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, snapshot.StoreStatusReasonCode);
        Assert.IsFalse(snapshot.Options.Enabled);
        Assert.IsFalse(snapshot.CanPersist);
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.Version));
        Assert.IsFalse(snapshot.Secrets.HasSmtpPassword, "Directory at store path must NEVER fall back to host config secrets.");
        Assert.IsFalse(snapshot.Secrets.HasWebhookUrl, "Directory at store path must NEVER fall back to host config webhook URL.");

        var effective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(effective.StoreCorrupt);
        Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, effective.ConfigurationErrorReasonCode);
        Assert.IsFalse(effective.Options.Enabled);
        Assert.IsTrue(effective.Secrets.IsEmpty);

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await store.ReadAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task UnreadableRegularStoreFile_ReportsDegradedAndSupportsGetAndResetRecoveryAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        RequireModesEnforced();

        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "fallback-host.example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-fallback-user",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-fallback-password"
            })
            .Build();

        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);
        var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var saved = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            initial.Version,
            new OperatorNotificationsOptions
            {
                Enabled = true,
                Smtp = new SmtpNotificationOptions
                {
                    Enabled = true,
                    Host = "smtp.example.test",
                    Port = 587,
                    SecurityMode = SmtpSecurityMode.StartTls,
                    FromAddress = "camera@example.test",
                    Recipients = ["ops@example.test"]
                }
            },
            SmtpUsername: "saved-user",
            SmtpPassword: "saved-password"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved.Outcome);

        try
        {
            // Make the persisted regular file unreadable at runtime (0000).
            File.SetUnixFileMode(store.ConfigFilePath, UnixFileMode.None);

            // Runtime delivery lookup must NOT throw UnauthorizedAccessException or fall back to host config/credentials.
            var effective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(effective.StoreCorrupt);
            Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, effective.ConfigurationErrorReasonCode);
            Assert.IsFalse(effective.Options.Enabled);
            Assert.IsTrue(effective.Secrets.IsEmpty);
            Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, store.ConfigurationErrorReasonCode);

            // GET snapshot must NOT throw UnauthorizedAccessException and must produce a valid concurrency version for reset recovery.
            var snapshot = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(snapshot.StoreCorrupt);
            Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, snapshot.StoreStatusReasonCode);
            Assert.IsTrue(snapshot.CanPersist);
            Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.Version));
            Assert.IsFalse(snapshot.Secrets.HasSmtpPassword);

            // Stale version still returns Conflict.
            var conflict = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                "stale-version",
                new OperatorNotificationsOptions { Enabled = false }), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Conflict, conflict.Outcome);

            // Explicit reset/save recovery with the returned version replaces the 0000 regular file atomically with a 0600 envelope.
            var recovered = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snapshot.Version,
                new OperatorNotificationsOptions { Enabled = false }), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, recovered.Outcome, recovered.Message);
            Assert.IsNotNull(recovered.Snapshot);
            Assert.IsFalse(recovered.Snapshot.StoreCorrupt);
            Assert.IsNull(recovered.Snapshot.StoreStatusReasonCode);
            Assert.IsNull(store.ConfigurationErrorReasonCode);
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.ConfigFilePath));
        }
        finally
        {
            if (File.Exists(store.ConfigFilePath))
            {
                File.SetUnixFileMode(store.ConfigFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [TestMethod]
    public async Task MalformedHostConfiguration_ReportsInvalidConfigurationNotStoreCorruptAndAllowsOverrideSaveAsync()
    {
        using var dir = TempStateDir.Create();
        var malformedConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "false",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "SSL",
                [OperatorNotificationsOptions.SectionKey + ":RequestTimeoutSeconds"] = "ten"
            })
            .Build();

        using var store = CreateStore(dir.Path, isDevelopment: false, configuration: malformedConfig);

        Assert.IsFalse(store.CurrentOptions.Enabled);
        Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, store.ConfigurationErrorReasonCode);

        var snapshot = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(snapshot.StoreCorrupt);
        Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, snapshot.StoreStatusReasonCode);
        Assert.IsTrue(snapshot.CanPersist);
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.Version));

        var saved = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapshot.Version,
            new OperatorNotificationsOptions { Enabled = false }), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved.Outcome, saved.Message);
        Assert.IsNull(store.ConfigurationErrorReasonCode);
        Assert.IsNull(saved.Snapshot!.StoreStatusReasonCode);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task BlockedAncestorOfStateDirectory_NeverFallsBackToHostSettingsOrCredentialsAndReportsStoreUnreadableAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "fallback-host.example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "fallback@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-fallback-user",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-fallback-password",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://host-fallback.example.test/secret"
            })
            .Build();

        // Case 1 & 2: Regular file directly at parent of StateDirectory and two levels above StateDirectory (ENOTDIR).
        var blockerFile = Path.Combine(dir.Path, "not-a-directory");
        await File.WriteAllTextAsync(blockerFile, "regular-file-blocking-ancestor").ConfigureAwait(false);

        // Case 3: Symlink to a regular file at ancestor of StateDirectory.
        var symlinkToFile = Path.Combine(dir.Path, "symlink-to-file");
        File.CreateSymbolicLink(symlinkToFile, blockerFile);

        // Case 4: Dangling symlink at ancestor of StateDirectory.
        var danglingSymlink = Path.Combine(dir.Path, "dangling-symlink");
        File.CreateSymbolicLink(danglingSymlink, Path.Combine(dir.Path, "does-not-exist"));

        var blockedStateDirs = new[]
        {
            Path.Combine(blockerFile, "state"),
            Path.Combine(blockerFile, "nested", "state"),
            Path.Combine(symlinkToFile, "state"),
            Path.Combine(danglingSymlink, "state")
        };

        foreach (var blockedStateDir in blockedStateDirs)
        {
            using var store = CreateStore(blockedStateDir, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);

            Assert.IsFalse(store.CurrentOptions.Enabled, $"Blocked ancestor '{blockedStateDir}' must NOT reactivate host Enabled=true.");
            Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, store.ConfigurationErrorReasonCode,
                $"Blocked ancestor '{blockedStateDir}' must report store-unreadable.");

            var effective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(effective.StoreCorrupt, $"Expected StoreCorrupt for '{blockedStateDir}'.");
            Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, effective.ConfigurationErrorReasonCode);
            Assert.IsFalse(effective.Options.Enabled);
            Assert.IsTrue(effective.Secrets.IsEmpty, $"Blocked ancestor '{blockedStateDir}' must NEVER fall back to host credentials.");

            var snapshot = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(snapshot.StoreCorrupt);
            Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, snapshot.StoreStatusReasonCode);
            Assert.IsFalse(snapshot.CanPersist, $"Blocked ancestor '{blockedStateDir}' must report CanPersist=false.");
            Assert.IsFalse(snapshot.Options.Enabled);
            Assert.IsFalse(snapshot.Secrets.HasSmtpPassword);
            Assert.IsFalse(snapshot.Secrets.HasWebhookUrl);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task RelativeDotDotSymlinkUnderSymlinkedAncestor_AllowsValidDirectoryTargetAndStillRejectsInvalidTargetsAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "fallback-host.example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-fallback-user",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-fallback-password"
            })
            .Build();

        // Kernel symlink layout: a -> real/x/y and a/b -> ../../z (resolves via real/x/y/../../z == real/z).
        var realXY = Path.Combine(dir.Path, "real", "x", "y");
        var realZ = Path.Combine(dir.Path, "real", "z");
        Directory.CreateDirectory(realXY);
        Directory.CreateDirectory(realZ);
        var blockerFile = Path.Combine(dir.Path, "real", "blocker.txt");
        await File.WriteAllTextAsync(blockerFile, "not-a-directory").ConfigureAwait(false);

        var symlinkA = Path.Combine(dir.Path, "a");
        File.CreateSymbolicLink(symlinkA, realXY);
        File.CreateSymbolicLink(Path.Combine(realXY, "b"), Path.Combine("..", "..", "z"));
        File.CreateSymbolicLink(Path.Combine(realXY, "b-file"), Path.Combine("..", "..", "blocker.txt"));
        File.CreateSymbolicLink(Path.Combine(realXY, "b-dangling"), Path.Combine("..", "..", "does-not-exist"));

        // 1. Valid relative '..' symlink target (a/b/state -> real/z/state) is accepted, persisted, and reloaded.
        var validStateDir = Path.Combine(symlinkA, "b", "state");
        var preInspection = NotificationStateFilePublisher.InspectStateFilePath(validStateDir, OperatorNotificationStore.ConfigFileName);
        Assert.AreEqual(NotificationPathStatus.ConfirmedAbsent, preInspection.Status);

        using (var store = CreateStore(validStateDir, isDevelopment: false, dpProvider: dpProvider))
        {
            var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(initial.CanPersist);
            Assert.IsFalse(initial.StoreCorrupt);
            Assert.IsNull(initial.StoreStatusReasonCode);

            var saved = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                initial.Version,
                new OperatorNotificationsOptions
                {
                    Enabled = true,
                    Smtp = new SmtpNotificationOptions
                    {
                        Enabled = true,
                        Host = "smtp.example.test",
                        Port = 587,
                        SecurityMode = SmtpSecurityMode.StartTls,
                        FromAddress = "camera@example.test",
                        Recipients = ["ops@example.test"]
                    }
                },
                SmtpUsername: "symlink-user",
                SmtpPassword: "symlink-password"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved.Outcome, saved.Message);
        }

        var postInspection = NotificationStateFilePublisher.InspectStateFilePath(validStateDir, OperatorNotificationStore.ConfigFileName);
        Assert.AreEqual(NotificationPathStatus.ExistsRegularFile, postInspection.Status);

        using (var reloaded = CreateStore(validStateDir, isDevelopment: false, dpProvider: dpProvider))
        {
            var (opts, secrets) = await reloaded.GetEffectiveForDeliveryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(opts.Enabled);
            Assert.AreEqual("symlink-user", secrets.SmtpUsername);
            Assert.AreEqual("symlink-password", secrets.SmtpPassword);
        }

        // 2. Relative '..' symlink under the same symlinked parent targeting a file or missing path still fails closed.
        foreach (var invalidStateDir in new[]
        {
            Path.Combine(symlinkA, "b-file", "state"),
            Path.Combine(symlinkA, "b-dangling", "state")
        })
        {
            using var blockedStore = CreateStore(invalidStateDir, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);
            Assert.IsFalse(blockedStore.CurrentOptions.Enabled);
            Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, blockedStore.ConfigurationErrorReasonCode);
            var snapshot = await blockedStore.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(snapshot.StoreCorrupt);
            Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, snapshot.StoreStatusReasonCode);
            Assert.IsFalse(snapshot.CanPersist);
            Assert.IsFalse(snapshot.Secrets.HasSmtpPassword);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [DataRow(UnixFileMode.UserRead | UnixFileMode.UserWrite)] // 0600 (readable, not traversable)
    [DataRow(UnixFileMode.None)] // 0000 (unreadable and not traversable)
    public async Task UntraversableAncestorDirectory_FailsClosedAsStoreUnreadableWithCanPersistFalseAsync(UnixFileMode ancestorMode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        RequireModesEnforced();

        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "fallback-host.example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "fallback@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-fallback-user",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-fallback-password",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://host-fallback.example.test/secret"
            })
            .Build();

        var lockedAncestor = Path.Combine(dir.Path, "locked-ancestor");
        Directory.CreateDirectory(lockedAncestor);
        var stateDirs = new[]
        {
            Path.Combine(lockedAncestor, "state"),
            Path.Combine(lockedAncestor, "nested", "state")
        };

        try
        {
            File.SetUnixFileMode(lockedAncestor, ancestorMode);

            foreach (var blockedStateDir in stateDirs)
            {
                using var store = CreateStore(blockedStateDir, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);

                Assert.IsFalse(store.CurrentOptions.Enabled,
                    $"Untraversable ancestor ({ancestorMode}) at '{blockedStateDir}' must NOT reactivate host Enabled=true.");
                Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, store.ConfigurationErrorReasonCode);

                var effective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(effective.StoreCorrupt);
                Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, effective.ConfigurationErrorReasonCode);
                Assert.IsFalse(effective.Options.Enabled);
                Assert.IsTrue(effective.Secrets.IsEmpty,
                    $"Untraversable ancestor ({ancestorMode}) must NEVER fall back to host credentials.");

                var snapshot = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(snapshot.StoreCorrupt);
                Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, snapshot.StoreStatusReasonCode);
                Assert.IsFalse(snapshot.CanPersist,
                    $"Untraversable ancestor ({ancestorMode}) must report CanPersist=false.");
                Assert.IsFalse(snapshot.Options.Enabled);
                Assert.IsFalse(snapshot.Secrets.HasSmtpPassword);
                Assert.IsFalse(snapshot.Secrets.HasWebhookUrl);
            }
        }
        finally
        {
            if (Directory.Exists(lockedAncestor))
            {
                File.SetUnixFileMode(lockedAncestor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [TestMethod]
    public async Task WebhookOriginChange_NeverCarriesOverHostOrPriorOriginTokenAndSendsNoAuthorizationHeaderAsync()
    {
        // 1. Direct full-destination-binding unit checks across scheme, host, effective port, path, and query variations.
        var hostSecrets = new OperatorNotificationSecrets(
            WebhookUrl: "https://hooks.example.test/host-path",
            WebhookAuthToken: "host-token-alpha",
            WebhookUserKey: "host-user-alpha",
            WebhookNtfyTopic: "host-topic-alpha");

        // Same canonical full destination (case-insensitive host, explicit default port 443, SAME path) -> inherits host secrets.
        var sameDestProtected = new OperatorNotificationSecrets(WebhookUrl: "https://HOOKS.EXAMPLE.TEST:443/host-path");
        var sameDestMerged = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(sameDestProtected, hostSecrets);
        var sameDestPresence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(sameDestProtected, hostSecrets);
        Assert.AreEqual("host-token-alpha", sameDestMerged.WebhookAuthToken);
        Assert.AreEqual("host-user-alpha", sameDestMerged.WebhookUserKey);
        Assert.AreEqual("host-topic-alpha", sameDestMerged.WebhookNtfyTopic);
        Assert.IsTrue(sameDestPresence.HasWebhookAuthToken);
        Assert.AreEqual(NotificationSecretSource.Configuration, sameDestPresence.WebhookAuthTokenSource);

        // Different path on SAME origin, different path case, different query, different host, different port, or different scheme
        // -> NEVER inherits host webhook secrets.
        foreach (var differentDestUrl in new[]
        {
            "https://HOOKS.EXAMPLE.TEST:443/other-path",
            "https://hooks.example.test/HOST-PATH",
            "https://hooks.example.test/host-path?tenant=2",
            "https://other.example.test/host-path",
            "https://hooks.example.test:8443/host-path",
            "http://hooks.example.test/host-path"
        })
        {
            var diffProtected = new OperatorNotificationSecrets(WebhookUrl: differentDestUrl);
            var diffMerged = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(diffProtected, hostSecrets);
            var diffPresence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(diffProtected, hostSecrets);
            Assert.IsNull(diffMerged.WebhookAuthToken, $"Different destination URL '{differentDestUrl}' must NOT inherit host WebhookAuthToken.");
            Assert.IsNull(diffMerged.WebhookUserKey, $"Different destination URL '{differentDestUrl}' must NOT inherit host WebhookUserKey.");
            Assert.IsNull(diffMerged.WebhookNtfyTopic, $"Different destination URL '{differentDestUrl}' must NOT inherit host WebhookNtfyTopic.");
            Assert.IsFalse(diffPresence.HasWebhookAuthToken, $"Different destination URL '{differentDestUrl}' must report HasWebhookAuthToken=false.");
            Assert.AreEqual(NotificationSecretSource.None, diffPresence.WebhookAuthTokenSource);
            Assert.IsFalse(diffPresence.HasWebhookUserKey);
            Assert.IsFalse(diffPresence.HasWebhookNtfyTopic);
        }

        // 2. End-to-end store + real WebhookNotificationTransport delivery across same-origin path changes and distinct origins.
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using var cert = WebhookNotificationTransportTests.CreateEphemeralLoopbackCertificate();
        var serverA = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        var serverB = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await using (serverA.ConfigureAwait(false))
        await using (serverB.ConfigureAwait(false))
        {
            var loopbackPolicy = new OperatorNotificationNetworkPolicy(isDevelopment: false)
            {
                AllowLoopbackForTesting = true
            };
            var hostConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                    [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                    [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = $"https://127.0.0.1:{serverA.Port}/host-hook",
                    [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "host-token-server-a",
                    [ConfigurationOperatorNotificationConfigProvider.WebhookUserKeyConfigKey] = "host-user-server-a",
                    [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = "host-topic-server-a"
                })
                .Build();

            using var store = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = dir.Path }),
                hostConfig,
                loopbackPolicy,
                NullLogger<OperatorNotificationStore>.Instance);
            using var transport = new WebhookNotificationTransport(loopbackPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, cert.Thumbprint, StringComparison.Ordinal)
            };

            var webhookOptions = new OperatorNotificationsOptions
            {
                Enabled = true,
                Webhook = new WebhookNotificationOptions
                {
                    Enabled = true,
                    Format = WebhookPayloadFormat.Generic,
                    AuthHeaderName = "Authorization"
                }
            };
            var sampleEvent = new OperatorNotificationEvent(
                OperatorNotificationEventKind.DailyHeartbeat,
                OperatorNotificationSeverity.Info,
                "origin-test",
                "Origin test",
                "Verifying full-destination webhook token inheritance.",
                new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero),
                "TestCam");

            // Step A1: Exact same canonical destination (serverA + /host-hook) inherits host token and sends Authorization header.
            var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var sameDestSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                initial.Version,
                webhookOptions,
                WebhookUrl: $"HTTPS://127.0.0.1:{serverA.Port}/host-hook"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, sameDestSave.Outcome, sameDestSave.Message);
            Assert.IsTrue(sameDestSave.Snapshot!.Secrets.HasWebhookAuthToken);
            Assert.AreEqual(NotificationSecretSource.Configuration, sameDestSave.Snapshot.Secrets.WebhookAuthTokenSource);

            var sameDestEffective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("host-token-server-a", sameDestEffective.Secrets.WebhookAuthToken);
            var sendSameDest = await transport.SendAsync(new WebhookDeliveryRequest(
                sameDestEffective.Secrets.WebhookUrl!,
                sameDestEffective.Options.Webhook.Format,
                sameDestEffective.Options.Webhook.AuthHeaderName,
                sameDestEffective.Secrets.WebhookNtfyTopic,
                sameDestEffective.Options.Webhook.AllowPrivateNetwork,
                sameDestEffective.Secrets.WebhookAuthToken,
                sameDestEffective.Secrets.WebhookUserKey,
                sampleEvent,
                1, 0, true, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(sendSameDest.Succeeded, sendSameDest.ReasonCode);
            Assert.HasCount(1, serverA.Captured);
            Assert.AreEqual("Bearer host-token-server-a", serverA.Captured[0].Header("Authorization"));

            // Step A2: Replacing path on the SAME origin (serverA /host-hook -> /same-origin-new-path) without supplying
            // a new token MUST NOT inherit host token/userKey/topic and MUST send NO Authorization header!
            var sameOriginNewPathSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                sameDestSave.Snapshot.Version,
                webhookOptions,
                WebhookUrl: $"https://127.0.0.1:{serverA.Port}/same-origin-new-path"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, sameOriginNewPathSave.Outcome, sameOriginNewPathSave.Message);
            Assert.IsFalse(sameOriginNewPathSave.Snapshot!.Secrets.HasWebhookAuthToken);
            Assert.AreEqual(NotificationSecretSource.None, sameOriginNewPathSave.Snapshot.Secrets.WebhookAuthTokenSource);
            Assert.IsFalse(sameOriginNewPathSave.Snapshot.Secrets.HasWebhookUserKey);
            Assert.IsFalse(sameOriginNewPathSave.Snapshot.Secrets.HasWebhookNtfyTopic);

            var sameOriginNewPathEffective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(sameOriginNewPathEffective.Secrets.WebhookAuthToken);
            Assert.IsNull(sameOriginNewPathEffective.Secrets.WebhookUserKey);
            Assert.IsNull(sameOriginNewPathEffective.Secrets.WebhookNtfyTopic);

            var sendNewPathA = await transport.SendAsync(new WebhookDeliveryRequest(
                sameOriginNewPathEffective.Secrets.WebhookUrl!,
                sameOriginNewPathEffective.Options.Webhook.Format,
                sameOriginNewPathEffective.Options.Webhook.AuthHeaderName,
                sameOriginNewPathEffective.Secrets.WebhookNtfyTopic,
                sameOriginNewPathEffective.Options.Webhook.AllowPrivateNetwork,
                sameOriginNewPathEffective.Secrets.WebhookAuthToken,
                sameOriginNewPathEffective.Secrets.WebhookUserKey,
                sampleEvent,
                1, 0, true, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(sendNewPathA.Succeeded, sendNewPathA.ReasonCode);
            Assert.HasCount(2, serverA.Captured);
            Assert.IsNull(serverA.Captured[1].Header("Authorization"),
                "Different webhook path on the SAME origin MUST NOT receive the original path's Authorization header.");

            // Step B: Replacing URL with DIFFERENT origin (serverB, different port) and omitting/clearing token MUST NOT inherit
            // host token and MUST send NO Authorization header on delivery!
            var crossOriginSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                sameOriginNewPathSave.Snapshot.Version,
                webhookOptions,
                WebhookUrl: $"https://127.0.0.1:{serverB.Port}/other-origin-hook",
                ClearWebhookAuthToken: true), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, crossOriginSave.Outcome, crossOriginSave.Message);
            Assert.IsFalse(crossOriginSave.Snapshot!.Secrets.HasWebhookAuthToken);
            Assert.AreEqual(NotificationSecretSource.None, crossOriginSave.Snapshot.Secrets.WebhookAuthTokenSource);
            Assert.IsFalse(crossOriginSave.Snapshot.Secrets.HasWebhookUserKey);
            Assert.IsFalse(crossOriginSave.Snapshot.Secrets.HasWebhookNtfyTopic);

            var crossOriginEffective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(crossOriginEffective.Secrets.WebhookAuthToken);
            Assert.IsNull(crossOriginEffective.Secrets.WebhookUserKey);
            Assert.IsNull(crossOriginEffective.Secrets.WebhookNtfyTopic);

            var sendB = await transport.SendAsync(new WebhookDeliveryRequest(
                crossOriginEffective.Secrets.WebhookUrl!,
                crossOriginEffective.Options.Webhook.Format,
                crossOriginEffective.Options.Webhook.AuthHeaderName,
                crossOriginEffective.Secrets.WebhookNtfyTopic,
                crossOriginEffective.Options.Webhook.AllowPrivateNetwork,
                crossOriginEffective.Secrets.WebhookAuthToken,
                crossOriginEffective.Secrets.WebhookUserKey,
                sampleEvent,
                1, 0, true, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(sendB.Succeeded, sendB.ReasonCode);
            Assert.HasCount(1, serverB.Captured);
            Assert.IsNull(serverB.Captured[0].Header("Authorization"),
                "Different webhook origin MUST NOT receive the original host Authorization header.");

            // Step C: Save explicit token for serverB, then replace with a third origin without token -> stored token for serverB
            // MUST NOT carry over to the third origin; clearing stored URL also never leaks serverB's token to serverA.
            var explicitTokenSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                crossOriginSave.Snapshot.Version,
                webhookOptions,
                WebhookAuthToken: "explicit-token-server-b"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, explicitTokenSave.Outcome);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, explicitTokenSave.Snapshot!.Secrets.WebhookAuthTokenSource);

            var thirdOriginSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                explicitTokenSave.Snapshot.Version,
                webhookOptions,
                WebhookUrl: "https://third-origin.example.test/alerts"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, thirdOriginSave.Outcome);
            Assert.IsFalse(thirdOriginSave.Snapshot!.Secrets.HasWebhookAuthToken,
                "Changing stored webhook origin without supplying a new token must drop the previous origin's stored token.");
            var thirdEffective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(thirdEffective.Secrets.WebhookAuthToken);

            // Clearing stored URL after storing a custom origin + token reverts cleanly to host URL + host token without leaking custom token.
            var saveWithCustomToken = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                thirdOriginSave.Snapshot.Version,
                webhookOptions,
                WebhookAuthToken: "third-origin-secret"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saveWithCustomToken.Outcome);

            var clearedUrlSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                saveWithCustomToken.Snapshot!.Version,
                webhookOptions,
                ClearWebhookUrl: true), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, clearedUrlSave.Outcome);
            var afterClearUrl = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual($"https://127.0.0.1:{serverA.Port}/host-hook", afterClearUrl.Secrets.WebhookUrl);
            Assert.AreEqual("host-token-server-a", afterClearUrl.Secrets.WebhookAuthToken,
                "Clearing stored webhook URL must never carry over the cleared origin's stored token onto the host origin.");
        }
    }

    [TestMethod]
    public async Task SmtpEndpointChange_NeverCarriesOverHostOrPriorRelayCredentialsAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-a.example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-relay-a-user",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-relay-a-pass"
            })
            .Build();

        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);

        // 1. Same SMTP host (case-insensitive) + same port inherits host credentials.
        var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(initial.Secrets.HasSmtpUsername);
        Assert.IsTrue(initial.Secrets.HasSmtpPassword);
        Assert.AreEqual(NotificationSecretSource.Configuration, initial.Secrets.SmtpPasswordSource);

        var sameEndpointSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            initial.Version,
            CreateSmtpOptions("RELAY-A.EXAMPLE.TEST", 587, SmtpSecurityMode.StartTls)), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, sameEndpointSave.Outcome, sameEndpointSave.Message);
        Assert.IsTrue(sameEndpointSave.Snapshot!.Secrets.HasSmtpUsername);
        Assert.IsTrue(sameEndpointSave.Snapshot.Secrets.HasSmtpPassword);
        Assert.AreEqual(NotificationSecretSource.Configuration, sameEndpointSave.Snapshot.Secrets.SmtpPasswordSource);
        var sameEffective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("host-relay-a-user", sameEffective.Secrets.SmtpUsername);
        Assert.AreEqual("host-relay-a-pass", sameEffective.Secrets.SmtpPassword);

        // 2. Different SMTP host (same port) MUST NOT inherit host username or password.
        var diffHostSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            sameEndpointSave.Snapshot.Version,
            CreateSmtpOptions("relay-b.example.test", 587, SmtpSecurityMode.StartTls)), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, diffHostSave.Outcome, diffHostSave.Message);
        Assert.IsFalse(diffHostSave.Snapshot!.Secrets.HasSmtpUsername);
        Assert.IsFalse(diffHostSave.Snapshot.Secrets.HasSmtpPassword);
        Assert.AreEqual(NotificationSecretSource.None, diffHostSave.Snapshot.Secrets.SmtpUsernameSource);
        Assert.AreEqual(NotificationSecretSource.None, diffHostSave.Snapshot.Secrets.SmtpPasswordSource);
        var diffHostEffective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(diffHostEffective.Secrets.SmtpUsername);
        Assert.IsNull(diffHostEffective.Secrets.SmtpPassword);

        // 3. Same SMTP host, DIFFERENT port (2525 != 587) MUST NOT inherit host username or password.
        var diffPortSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            diffHostSave.Snapshot.Version,
            CreateSmtpOptions("relay-a.example.test", 2525, SmtpSecurityMode.StartTls)), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, diffPortSave.Outcome, diffPortSave.Message);
        Assert.IsFalse(diffPortSave.Snapshot!.Secrets.HasSmtpUsername);
        Assert.IsFalse(diffPortSave.Snapshot.Secrets.HasSmtpPassword);
        var diffPortEffective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(diffPortEffective.Secrets.SmtpUsername);
        Assert.IsNull(diffPortEffective.Secrets.SmtpPassword);

        // 4. Explicit stored credentials on relay-b:587 are used without mixing host password, and changing away from relay-b:587
        //    (or clearing SMTP host) drops relay-b's stored credentials unless re-entered.
        var explicitRelayBSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            diffPortSave.Snapshot.Version,
            CreateSmtpOptions("relay-b.example.test", 587, SmtpSecurityMode.StartTls),
            SmtpUsername: "relay-b-user",
            SmtpPassword: "relay-b-pass"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, explicitRelayBSave.Outcome);
        var relayBEffective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-b-user", relayBEffective.Secrets.SmtpUsername);
        Assert.AreEqual("relay-b-pass", relayBEffective.Secrets.SmtpPassword);

        var relayCSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            explicitRelayBSave.Snapshot!.Version,
            CreateSmtpOptions("relay-c.example.test", 587, SmtpSecurityMode.StartTls),
            SmtpUsername: "relay-c-only-user"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, relayCSave.Outcome);
        var relayCEffective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-c-only-user", relayCEffective.Secrets.SmtpUsername);
        Assert.IsNull(relayCEffective.Secrets.SmtpPassword,
            "Changing SMTP relay with only a new username must NOT carry over prior stored or host passwords.");

        // 5. Real loopback SmtpNotificationTransport verification: host credentials on port A MUST NOT bleed onto
        //    an unauthenticated local relay saved on a different port B (and must send zero AUTH commands).
        var serverB = await SmtpNotificationTransportTests.InProcessSmtpCaptureServer.StartAsync(
            new SmtpNotificationTransportTests.SmtpServerBehavior { AuthMechanisms = "PLAIN LOGIN" }).ConfigureAwait(false);
        await using (serverB.ConfigureAwait(false))
        {
            using var loopbackDir = TempStateDir.Create();
            var loopbackDp = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(loopbackDir.Path, "dp-keys")));
            var loopbackPolicy = new OperatorNotificationNetworkPolicy(isDevelopment: false) { AllowLoopbackForTesting = true };
            var portA = serverB.Port == 587 ? 2587 : 587;
            var loopbackHostConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "127.0.0.1",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = portA.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                    [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-port-a-user",
                    [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-port-a-pass"
                })
                .Build();

            using var loopbackStore = new OperatorNotificationStore(
                loopbackDp,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = loopbackDir.Path }),
                loopbackHostConfig,
                loopbackPolicy,
                NullLogger<OperatorNotificationStore>.Instance);
            var snap = await loopbackStore.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var switchedPortSave = await loopbackStore.SaveAsync(new OperatorNotificationsUpdateRequest(
                snap.Version,
                CreateSmtpOptions("127.0.0.1", serverB.Port, SmtpSecurityMode.None)), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, switchedPortSave.Outcome, switchedPortSave.Message);

            var (effectiveOpts, effectiveSecrets) = await loopbackStore.GetEffectiveForDeliveryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(effectiveSecrets.SmtpUsername);
            Assert.IsNull(effectiveSecrets.SmtpPassword);

            var smtpTransport = new SmtpNotificationTransport(loopbackPolicy, TimeProvider.System);
            var delivery = await smtpTransport.SendAsync(new SmtpDeliveryRequest(
                effectiveOpts.Smtp.Host,
                effectiveOpts.Smtp.Port,
                effectiveOpts.Smtp.SecurityMode,
                effectiveOpts.Smtp.FromAddress,
                effectiveOpts.Smtp.FromDisplayName,
                effectiveOpts.Smtp.Recipients,
                effectiveSecrets.SmtpUsername,
                effectiveSecrets.SmtpPassword,
                "Relay port change check",
                "No host credentials sent.",
                "test",
                TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(delivery.Succeeded, delivery.ReasonCode);
            Assert.IsFalse(serverB.ReceivedAnyAuthCommand, "Different SMTP port MUST NOT transmit inherited host credentials.");
        }
    }

    [TestMethod]
    public async Task FirstPublicationIntoNestedNonExistentDirectories_CreatesAndSyncsEveryParentAndFailsClosedOnSymlinkOrPermissionErrorsAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        var existingParent = Path.Combine(dir.Path, "existing");
        Directory.CreateDirectory(existingParent);
        var level1 = Path.Combine(existingParent, "level1");
        var level2 = Path.Combine(level1, "level2");
        var nestedStateDir = Path.Combine(level2, "state");

        var syncedDirectories = new List<string>();
        using var store = new OperatorNotificationStore(
            dpProvider,
            Options.Create(new DeviceProvisioningOptions { StateDirectory = nestedStateDir }),
            new ConfigurationBuilder().Build(),
            new OperatorNotificationNetworkPolicy(isDevelopment: false),
            NullLogger<OperatorNotificationStore>.Instance)
        {
            OnDirectorySynced = syncedDirectories.Add
        };

        // 1. First publication creates existing/level1/level2/state and fsyncs target + parent of EVERY created directory.
        var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(Directory.Exists(level1), "Default-off read must still create zero directories on disk.");

        var firstSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            initial.Version,
            new OperatorNotificationsOptions { Enabled = false }), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, firstSave.Outcome, firstSave.Message);
        Assert.IsTrue(Directory.Exists(nestedStateDir));
        Assert.IsTrue(File.Exists(store.ConfigFilePath));

        var expectedFullState = Path.TrimEndingDirectorySeparator(Path.GetFullPath(nestedStateDir));
        var expectedLevel2 = Path.TrimEndingDirectorySeparator(Path.GetFullPath(level2));
        var expectedLevel1 = Path.TrimEndingDirectorySeparator(Path.GetFullPath(level1));
        var expectedExisting = Path.TrimEndingDirectorySeparator(Path.GetFullPath(existingParent));

        Assert.IsTrue(syncedDirectories.Count >= 5,
            "First publication into nested non-existent directories must sync state, every created parent, and post-rename state.");
        CollectionAssert.AreEqual(
            new[] { expectedFullState, expectedLevel2, expectedLevel1, expectedExisting },
            syncedDirectories.Take(4).ToArray(),
            "First publication into nested non-existent directories must fsync the target directory and every newly created directory's parent bottom-up.");
        Assert.AreEqual(expectedFullState, syncedDirectories[^1],
            "Final post-rename fsync must target the leaf state directory.");

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode restrictedDirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.AreEqual(restrictedDirMode, File.GetUnixFileMode(level1),
                "Newly created intermediate ancestor level1 must be 0700 (not group/other writable).");
            Assert.AreEqual(restrictedDirMode, File.GetUnixFileMode(level2),
                "Newly created intermediate ancestor level2 must be 0700 (not group/other writable).");
            Assert.AreEqual(restrictedDirMode, File.GetUnixFileMode(nestedStateDir),
                "Newly created leaf state directory must be 0700.");
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(store.ConfigFilePath));
        }

        // 2. Subsequent publication into the already-existing directory does NOT re-sync ancestors.
        syncedDirectories.Clear();
        var secondSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            firstSave.Snapshot!.Version,
            new OperatorNotificationsOptions { Enabled = false }), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, secondSave.Outcome, secondSave.Message);
        CollectionAssert.AreEqual(
            new[] { expectedFullState, expectedFullState },
            syncedDirectories,
            "Publication into an existing directory should only sync the state directory itself.");

        // 3. Simulated parent-directory fsync failure fails closed before reporting success.
        var faultNestedDir = Path.Combine(existingParent, "fault-lvl1", "state");
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
                faultNestedDir,
                OperatorNotificationStore.ConfigFileName,
                Encoding.UTF8.GetBytes("{}"),
                beforeRename: null,
                onDirectorySynced: synced =>
                {
                    if (string.Equals(synced, expectedExisting, StringComparison.Ordinal))
                    {
                        throw new IOException("Simulated parent directory fsync EIO.");
                    }
                },
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(Path.Combine(faultNestedDir, OperatorNotificationStore.ConfigFileName)),
            "Failed parent directory fsync must abort before publishing the target file.");

        // 4. Fail-closed checks: regular file ancestor, symlink target/ancestor, and untraversable/read-only ancestor.
        var blockerFile = Path.Combine(dir.Path, "regular-blocker");
        await File.WriteAllTextAsync(blockerFile, "blocker").ConfigureAwait(false);
        Assert.ThrowsExactly<IOException>(() =>
            NotificationStateFilePublisher.EnsureRestrictedDirectory(Path.Combine(blockerFile, "nested", "state")));

        if (!OperatingSystem.IsWindows())
        {
            var realTargetDir = Path.Combine(dir.Path, "real-target");
            Directory.CreateDirectory(realTargetDir);
            var symlinkStateDir = Path.Combine(dir.Path, "symlink-state");
            File.CreateSymbolicLink(symlinkStateDir, realTargetDir);
            Assert.ThrowsExactly<IOException>(() =>
                NotificationStateFilePublisher.EnsureRestrictedDirectory(symlinkStateDir));

            var symlinkToFile = Path.Combine(dir.Path, "symlink-to-file");
            File.CreateSymbolicLink(symlinkToFile, blockerFile);
            Assert.ThrowsExactly<IOException>(() =>
                NotificationStateFilePublisher.EnsureRestrictedDirectory(Path.Combine(symlinkToFile, "nested", "state")));

            var danglingSymlink = Path.Combine(dir.Path, "dangling-ancestor");
            File.CreateSymbolicLink(danglingSymlink, Path.Combine(dir.Path, "nonexistent-target"));
            Assert.ThrowsExactly<IOException>(() =>
                NotificationStateFilePublisher.EnsureRestrictedDirectory(Path.Combine(danglingSymlink, "nested", "state")));

            RequireModesEnforced();
            var lockedDir = Path.Combine(dir.Path, "locked-parent");
            Directory.CreateDirectory(lockedDir);
            try
            {
                File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600 (not traversable)
                Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
                    NotificationStateFilePublisher.EnsureRestrictedDirectory(Path.Combine(lockedDir, "nested", "state")));

                File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute); // 0500 (read-only, cannot mkdir)
                Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
                    NotificationStateFilePublisher.EnsureRestrictedDirectory(Path.Combine(lockedDir, "nested", "state")));
            }
            finally
            {
                File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [TestMethod]
    public async Task OversizedConfigurationEnvelope_FailsClosedWithBoundedReadZeroHostFallbackAndNoQuarantineCopyAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "fallback-host.example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "fallback@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-fallback-user",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-fallback-password",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://host-fallback.example.test/secret",
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "host-fallback-token"
            })
            .Build();

        // Write an oversized regular file (> MaximumEnvelopeBytes) at ConfigFilePath.
        var storePath = Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName);
        var oversizedBytes = new byte[OperatorNotificationStore.MaximumEnvelopeBytes + 4096];
        Array.Fill(oversizedBytes, (byte)' ');
        oversizedBytes[0] = (byte)'{';
        oversizedBytes[^1] = (byte)'}';
        await File.WriteAllBytesAsync(storePath, oversizedBytes).ConfigureAwait(false);

        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);

        // Startup must fail closed with StoreCorrupt and zero host fallback.
        Assert.IsFalse(store.CurrentOptions.Enabled);
        Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, store.ConfigurationErrorReasonCode);

        var snapshot = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(snapshot.StoreCorrupt);
        Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, snapshot.StoreStatusReasonCode);
        Assert.IsFalse(snapshot.CanPersist, "Oversized envelope must report CanPersist=false.");
        Assert.IsFalse(snapshot.Options.Enabled);
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.Version));
        Assert.IsFalse(snapshot.Secrets.HasSmtpPassword, "Oversized store must NEVER fall back to host SMTP credentials.");
        Assert.IsFalse(snapshot.Secrets.HasWebhookUrl, "Oversized store must NEVER fall back to host webhook URL.");
        Assert.IsFalse(snapshot.Secrets.HasWebhookAuthToken, "Oversized store must NEVER fall back to host webhook token.");

        var effective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(effective.StoreCorrupt);
        Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, effective.ConfigurationErrorReasonCode);
        Assert.IsFalse(effective.Options.Enabled);
        Assert.IsTrue(effective.Secrets.IsEmpty);

        // Update, Read, Save(secrets), and Clear all fail closed and NEVER copy the oversized file into quarantine.
        var saveResult = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapshot.Version,
            new OperatorNotificationsOptions { Enabled = false }), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Unavailable, saveResult.Outcome);
        Assert.IsFalse(File.Exists(store.QuarantineFilePath),
            "Oversized corrupt configuration envelope must NEVER be copied unboundedly into quarantine.");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.ReadAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await store.SaveAsync(OperatorNotificationSecrets.Empty, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await store.ClearAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        // Verify bounded stream reader strictly caps reads at maxBytes + 1 even for non-seekable multi-megabyte streams.
        const int cap = 4096;
        using (var exactStream = new NonSeekableCountingStream(cap))
        {
            var exact = await NotificationStateFilePublisher.ReadBoundedStreamAsync(exactStream, cap, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(exact.ExceededLimit);
            Assert.AreEqual(cap, exact.Content.Length);
            Assert.AreEqual(cap, exactStream.TotalBytesRead);
        }

        using (var largeAsyncStream = new NonSeekableCountingStream(10 * 1024 * 1024))
        {
            var exceededAsync = await NotificationStateFilePublisher.ReadBoundedStreamAsync(largeAsyncStream, cap, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(exceededAsync.ExceededLimit);
            Assert.IsTrue(exceededAsync.Content.IsEmpty);
            Assert.AreEqual(cap + 1, largeAsyncStream.TotalBytesRead,
                "Async bounded stream reader must stop reading immediately at maxBytes + 1.");
        }

        using (var largeSyncStream = new NonSeekableCountingStream(10 * 1024 * 1024))
        {
            var exceededSync = NotificationStateFilePublisher.ReadBoundedStream(largeSyncStream, cap);
            Assert.IsTrue(exceededSync.ExceededLimit);
            Assert.IsTrue(exceededSync.Content.IsEmpty);
            Assert.AreEqual(cap + 1, largeSyncStream.TotalBytesRead,
                "Sync bounded stream reader must stop reading immediately at maxBytes + 1.");
        }
    }

    [TestMethod]
    public async Task TokenOnlySaveAgainstHostWebhookOrigin_DoesNotLeakWhenHostOriginChangesAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using var cert = WebhookNotificationTransportTests.CreateEphemeralLoopbackCertificate();
        var serverA = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        var serverB = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await using (serverA.ConfigureAwait(false))
        await using (serverB.ConfigureAwait(false))
        {
            var loopbackPolicy = new OperatorNotificationNetworkPolicy(isDevelopment: false)
            {
                AllowLoopbackForTesting = true
            };
            var urlA = $"https://127.0.0.1:{serverA.Port}/host-hook";
            var urlB = $"https://127.0.0.1:{serverB.Port}/rotated-hook";

            var configProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
            });
            var hostConfig = new ConfigurationRoot([configProvider]);

            using var store = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = dir.Path }),
                hostConfig,
                loopbackPolicy,
                NullLogger<OperatorNotificationStore>.Instance);
            using var webhookTransport = new WebhookNotificationTransport(loopbackPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, cert.Thumbprint, StringComparison.Ordinal)
            };
            using var telemetry = new OperatorNotificationTelemetry();
            using var dispatcher = new OperatorNotificationDispatcher(
                store,
                new SmtpNotificationTransport(loopbackPolicy, TimeProvider.System),
                webhookTransport,
                telemetry,
                TimeProvider.System);

            var webhookOptions = new OperatorNotificationsOptions
            {
                Enabled = true,
                TestSendMinimumIntervalSeconds = 0,
                Webhook = new WebhookNotificationOptions
                {
                    Enabled = true,
                    Format = WebhookPayloadFormat.Generic,
                    AuthHeaderName = "Authorization"
                }
            };

            // 1. Save ONLY a protected webhook token while the URL stays inherited from host configuration (origin A).
            var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var tokenOnlySave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                initial.Version,
                webhookOptions,
                WebhookAuthToken: "protected-token-origin-a"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, tokenOnlySave.Outcome, tokenOnlySave.Message);
            Assert.AreEqual(NotificationSecretSource.Configuration, tokenOnlySave.Snapshot!.Secrets.WebhookUrlSource);
            Assert.IsTrue(tokenOnlySave.Snapshot.Secrets.HasWebhookAuthToken);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, tokenOnlySave.Snapshot.Secrets.WebhookAuthTokenSource);

            // Live delivery + test-send to origin A include the protected token bound to origin A.
            var testA = await dispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(testA.RateLimited);
            Assert.IsTrue(testA.Channels[0].Succeeded, testA.Channels[0].ReasonCode);
            Assert.HasCount(1, serverA.Captured);
            Assert.AreEqual("Bearer protected-token-origin-a", serverA.Captured[0].Header("Authorization"));

            // 2. Host configuration rotates Webhook:Url from origin A to origin B (without a host token).
            configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
            });

            var snapshotAfterRotate = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(snapshotAfterRotate.Secrets.HasWebhookAuthToken,
                "Protected webhook token bound to host origin A MUST NOT report present after host rotates to origin B.");
            Assert.AreEqual(NotificationSecretSource.None, snapshotAfterRotate.Secrets.WebhookAuthTokenSource);

            var effectiveAfterRotate = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(urlB, effectiveAfterRotate.Secrets.WebhookUrl);
            Assert.IsNull(effectiveAfterRotate.Secrets.WebhookAuthToken,
                "Protected webhook token bound to host origin A MUST NOT be served for host origin B.");

            // Both test-send and live dispatcher delivery to origin B MUST NOT send origin A's Authorization header!
            var testB = await dispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(testB.RateLimited);
            Assert.IsTrue(testB.Channels[0].Succeeded, testB.Channels[0].ReasonCode);

            var published = dispatcher.TryPublish(new OperatorNotificationEvent(
                OperatorNotificationEventKind.DiskPressure,
                OperatorNotificationSeverity.Warning,
                "disk-origin-b",
                "Disk pressure",
                "Live dispatch after host webhook origin rotation.",
                DateTimeOffset.UtcNow,
                "TestCam"));
            Assert.IsTrue(published);
            var processed = await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, processed);

            Assert.HasCount(2, serverB.Captured);
            foreach (var request in serverB.Captured)
            {
                Assert.IsNull(request.Header("Authorization"),
                    "Host origin B MUST NEVER receive protected-token-origin-a on live delivery or test-send.");
            }

            // 3. When host config supplies its own token for origin B, origin B uses host-token-b (never origin A's protected token).
            configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB,
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "host-token-origin-b"
            });
            var withHostTokenB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("host-token-origin-b", withHostTokenB.Secrets.WebhookAuthToken);
            var snapHostTokenB = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(NotificationSecretSource.Configuration, snapHostTokenB.Secrets.WebhookAuthTokenSource);

            // 4. Subsequent unrelated save while host is on origin B (no host token) MUST NOT re-bind origin A's stale protected token onto origin B.
            configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
            });
            var snapBeforeUnrelatedSave = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var unrelatedSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snapBeforeUnrelatedSave.Version,
                webhookOptions), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, unrelatedSave.Outcome, unrelatedSave.Message);
            Assert.IsFalse(unrelatedSave.Snapshot!.Secrets.HasWebhookAuthToken);
            var afterUnrelatedSave = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(afterUnrelatedSave.Secrets.WebhookAuthToken);

            // 5. Direct IOperatorNotificationSecretStore.SaveAsync token-only save against host origin A also binds to origin A.
            configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
            });
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                new OperatorNotificationSecrets(WebhookAuthToken: "secret-store-token-a"),
                CancellationToken.None).ConfigureAwait(false);
            var directOnA = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("secret-store-token-a", directOnA.Secrets.WebhookAuthToken);

            configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
            });
            var directOnB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(urlB, directOnB.Secrets.WebhookUrl);
            Assert.IsNull(directOnB.Secrets.WebhookAuthToken,
                "IOperatorNotificationSecretStore.SaveAsync token-only save must NOT leak after host origin rotation.");
        }
    }

    [TestMethod]
    public async Task ProtectedSmtpCredentials_AreBoundToEndpointAndModeInEnvelopeAndHandleLegacyEnvelopesSafelyAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://host-origin-b.example.test/hook"
            })
            .Build();

        using (var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig))
        {
            var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var saved = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                initial.Version,
                CreateSmtpOptions("relay-a.example.test", 587, SmtpSecurityMode.StartTls),
                SmtpUsername: "bound-smtp-user",
                SmtpPassword: "bound-smtp-pass"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved.Outcome, saved.Message);
        }

        // Verify unchanged control retains bound SMTP credentials, while mutating plaintext SMTP host, port, OR
        // SecurityMode (using numeric JSON enum values matching SerializerOptions) parses cleanly without StoreCorrupt
        // and drops the encrypted SMTP credentials (bound to relay-a:587:StartTls) instead of leaking to another relay/mode.
        var originalJson = await File.ReadAllTextAsync(Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName)).ConfigureAwait(false);
        {
            var controlRoot = JsonNode.Parse(originalJson)!.AsObject();
            var controlSmtp = controlRoot["options"]!["smtp"]!.AsObject();
            controlSmtp["host"] = "relay-a.example.test";
            controlSmtp["port"] = 587;
            controlSmtp["securityMode"] = (int)SmtpSecurityMode.StartTls;
            await File.WriteAllTextAsync(
                Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName),
                controlRoot.ToJsonString()).ConfigureAwait(false);

            using var controlStore = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);
            var controlSnap = await controlStore.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(controlSnap.StoreCorrupt, "Unchanged numeric-enum envelope must parse cleanly without StoreCorrupt.");
            Assert.IsTrue(controlSnap.Secrets.HasSmtpUsername, "Unchanged control must retain bound SmtpUsername.");
            Assert.IsTrue(controlSnap.Secrets.HasSmtpPassword, "Unchanged control must retain bound SmtpPassword.");
            var controlEff = await controlStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(controlEff.StoreCorrupt);
            Assert.AreEqual("bound-smtp-user", controlEff.Secrets.SmtpUsername);
            Assert.AreEqual("bound-smtp-pass", controlEff.Secrets.SmtpPassword);
        }

        foreach (var (host, port, mode, label) in new[]
        {
            ("relay-b.example.test", 587, SmtpSecurityMode.StartTls, "host-drift"),
            ("relay-a.example.test", 2525, SmtpSecurityMode.StartTls, "port-drift"),
            ("relay-a.example.test", 587, SmtpSecurityMode.ImplicitTls, "mode-implicit-tls-drift"),
            ("relay-a.example.test", 587, SmtpSecurityMode.None, "mode-none-drift")
        })
        {
            var rootNode = JsonNode.Parse(originalJson)!.AsObject();
            var smtpNode = rootNode["options"]!["smtp"]!.AsObject();
            smtpNode["host"] = host;
            smtpNode["port"] = port;
            smtpNode["securityMode"] = (int)mode;
            await File.WriteAllTextAsync(
                Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName),
                rootNode.ToJsonString()).ConfigureAwait(false);

            using var driftedStore = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);
            var snap = await driftedStore.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(snap.StoreCorrupt, $"Drifted envelope ({label}) must parse cleanly without StoreCorrupt.");
            Assert.IsFalse(snap.Secrets.HasSmtpUsername, $"Drifted envelope ({label}) must NOT expose bound SmtpUsername.");
            Assert.IsFalse(snap.Secrets.HasSmtpPassword, $"Drifted envelope ({label}) must NOT expose bound SmtpPassword.");
            var eff = await driftedStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(eff.StoreCorrupt, $"Drifted envelope ({label}) must not classify as StoreCorrupt.");
            Assert.IsNull(eff.Secrets.SmtpUsername, $"Drifted envelope ({label}) must drop bound SmtpUsername.");
            Assert.IsNull(eff.Secrets.SmtpPassword, $"Drifted envelope ({label}) must drop bound SmtpPassword.");
        }

        // Legacy envelope handling (lacking explicit BoundWebhookDestination / BoundSmtpEndpoint fields):
        // (A) Legacy envelopes lacking explicit encrypted BoundSmtpEndpoint OR BoundWebhookDestination MUST BOTH
        //     fail closed (requiring fresh operator authorization) — encrypted WebhookUrl alone is NEVER treated
        //     as destination-binding proof!
        // (B) Legacy envelope with WebhookUrl=null + WebhookAuthToken also fails safe and does NOT apply to host origin B.
        var protector = dpProvider.CreateProtector("CameraAgent", "OperatorNotifications", "v1");
        var legacyExplicitSecretsJson = JsonSerializer.Serialize(new
        {
            smtpUsername = "legacy-smtp-user",
            smtpPassword = "legacy-smtp-pass",
            webhookUrl = "https://legacy-origin.example.test/alerts",
            webhookAuthToken = "legacy-explicit-token",
            webhookUserKey = (string?)null,
            webhookNtfyTopic = (string?)null
        });
        var legacyExplicitEnvelope = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            revision = "legacy-rev-1",
            options = new OperatorNotificationsOptions
            {
                Enabled = true,
                Smtp = new SmtpNotificationOptions
                {
                    Enabled = true,
                    Host = "legacy-relay.example.test",
                    Port = 587,
                    SecurityMode = SmtpSecurityMode.StartTls,
                    FromAddress = "camera@example.test",
                    Recipients = ["ops@example.test"]
                },
                Webhook = new WebhookNotificationOptions { Enabled = true }
            },
            protectedSecrets = protector.Protect(legacyExplicitSecretsJson)
        }, WebJsonOptions);
        await File.WriteAllTextAsync(Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName), legacyExplicitEnvelope).ConfigureAwait(false);

        using (var legacyExplicitStore = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig))
        {
            var snap = await legacyExplicitStore.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(snap.StoreCorrupt);
            Assert.IsFalse(snap.Secrets.HasSmtpUsername,
                "Legacy SMTP envelope lacking encrypted BoundSmtpEndpoint must fail closed.");
            Assert.IsFalse(snap.Secrets.HasSmtpPassword,
                "Legacy SMTP envelope lacking encrypted BoundSmtpEndpoint must fail closed.");
            Assert.IsTrue(snap.Secrets.HasWebhookUrl);
            Assert.IsFalse(snap.Secrets.HasWebhookAuthToken,
                "Legacy webhook envelope lacking encrypted BoundWebhookDestination must fail closed.");

            var eff = await legacyExplicitStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(eff.StoreCorrupt);
            Assert.IsNull(eff.Secrets.SmtpUsername,
                "Legacy SMTP credentials without encrypted BoundSmtpEndpoint must NOT be released.");
            Assert.IsNull(eff.Secrets.SmtpPassword,
                "Legacy SMTP credentials without encrypted BoundSmtpEndpoint must NOT be released.");
            Assert.AreEqual("https://legacy-origin.example.test/alerts", eff.Secrets.WebhookUrl);
            Assert.IsNull(eff.Secrets.WebhookAuthToken,
                "Legacy webhook credentials without encrypted BoundWebhookDestination must NOT be released.");

            // Fresh re-entry writes encrypted BoundSmtpEndpoint + BoundWebhookDestination and restores ProtectedStore delivery.
            var rebound = await legacyExplicitStore.SaveAsync(new OperatorNotificationsUpdateRequest(
                snap.Version,
                snap.Options,
                SmtpUsername: "reentered-smtp-user",
                SmtpPassword: "reentered-smtp-pass",
                WebhookAuthToken: "reentered-webhook-token"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, rebound.Outcome, rebound.Message);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, rebound.Snapshot!.Secrets.SmtpUsernameSource);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, rebound.Snapshot.Secrets.SmtpPasswordSource);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, rebound.Snapshot.Secrets.WebhookAuthTokenSource);
            var afterReentry = await legacyExplicitStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("reentered-smtp-user", afterReentry.Secrets.SmtpUsername);
            Assert.AreEqual("reentered-smtp-pass", afterReentry.Secrets.SmtpPassword);
            Assert.AreEqual("reentered-webhook-token", afterReentry.Secrets.WebhookAuthToken);
        }

        var legacyUnboundTokenJson = JsonSerializer.Serialize(new
        {
            smtpUsername = (string?)null,
            smtpPassword = (string?)null,
            webhookUrl = (string?)null,
            webhookAuthToken = "legacy-unbound-token",
            webhookUserKey = (string?)null,
            webhookNtfyTopic = (string?)null
        });
        var legacyUnboundEnvelope = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            revision = "legacy-rev-2",
            options = new OperatorNotificationsOptions
            {
                Enabled = true,
                Webhook = new WebhookNotificationOptions { Enabled = true }
            },
            protectedSecrets = protector.Protect(legacyUnboundTokenJson)
        }, WebJsonOptions);
        await File.WriteAllTextAsync(Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName), legacyUnboundEnvelope).ConfigureAwait(false);

        using (var legacyUnboundStore = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig))
        {
            var snap = await legacyUnboundStore.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(snap.Secrets.HasWebhookAuthToken,
                "Legacy envelope without WebhookUrl or BoundWebhookOrigin must fail safe and drop unbound token.");
            var eff = await legacyUnboundStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("https://host-origin-b.example.test/hook", eff.Secrets.WebhookUrl);
            Assert.IsNull(eff.Secrets.WebhookAuthToken);
        }
    }

    [TestMethod]
    public async Task HostConfigurationReload_NeverMixesDestinationAndCredentialsAcrossRevisionsAsync()
    {
        static Dictionary<string, string?> RevisionValues(string tag) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = $"relay-{tag}.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = $"user-{tag}",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = $"pass-{tag}",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = $"https://origin-{tag}.example.test/hook",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = $"token-{tag}"
        };

        // 1. Deterministic mid-read reload WITH OnReload() triggered right as secrets are read after options.
        var midReadWithReload = new MidReadRotatingConfigurationProvider(
            RevisionValues("a"),
            RevisionValues("b"),
            triggerKey: ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey,
            fireReloadToken: true);
        var configWithReload = new ConfigurationRoot([midReadWithReload]);
        using var directProvider = new ConfigurationOperatorNotificationConfigProvider(configWithReload);

        var directEffective = await directProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(midReadWithReload.HasRotated);
        Assert.AreEqual("relay-b.example.test", directEffective.Options.Smtp.Host);
        Assert.AreEqual("user-b", directEffective.Secrets.SmtpUsername);
        Assert.AreEqual("pass-b", directEffective.Secrets.SmtpPassword);
        Assert.AreEqual("https://origin-b.example.test/hook", directEffective.Secrets.WebhookUrl);
        Assert.AreEqual("token-b", directEffective.Secrets.WebhookAuthToken);

        // 2. Deterministic mid-read Data swap BEFORE OnReload() fires: invoke ReadHostSnapshot directly BEFORE any
        //    store constructor touches configPreToken so the double-read stability guard is genuinely exercised!
        var midReadPreToken = new MidReadRotatingConfigurationProvider(
            RevisionValues("a"),
            RevisionValues("b"),
            triggerKey: ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey,
            fireReloadToken: false);
        var configPreToken = new ConfigurationRoot([midReadPreToken]);
        var directPreTokenSnapshot = ConfigurationOperatorNotificationConfigProvider.ReadHostSnapshot(configPreToken);
        Assert.IsTrue(midReadPreToken.HasRotated, "Mid-read trigger must have fired during the initial read.");
        Assert.IsNull(directPreTokenSnapshot.BindingErrorReasonCode);
        Assert.AreEqual("relay-b.example.test", directPreTokenSnapshot.Options.Smtp.Host);
        Assert.AreEqual("user-b", directPreTokenSnapshot.Secrets.SmtpUsername);
        Assert.AreEqual("pass-b", directPreTokenSnapshot.Secrets.SmtpPassword);
        Assert.AreEqual("https://origin-b.example.test/hook", directPreTokenSnapshot.Secrets.WebhookUrl);
        Assert.AreEqual("token-b", directPreTokenSnapshot.Secrets.WebhookAuthToken);

        // 3. Concurrent atomic rotation between Revision A and Revision B across both providers -> ZERO mixed reads.
        var concurrentProvider = new RotatingHostConfigurationProvider(RevisionValues("a"));
        var concurrentConfig = new ConfigurationRoot([concurrentProvider]);
        using var configOnlyProvider = new ConfigurationOperatorNotificationConfigProvider(concurrentConfig);
        using var concurrentDir = TempStateDir.Create();
        using var concurrentStore = CreateStore(concurrentDir.Path, isDevelopment: false, configuration: concurrentConfig);

        using var cts = new CancellationTokenSource();
        var revA = RevisionValues("a");
        var revB = RevisionValues("b");
        var rotator = Task.Run(() =>
        {
            var flip = false;
            while (!cts.IsCancellationRequested)
            {
                concurrentProvider.Rotate(flip ? revA : revB);
                flip = !flip;
            }
        });

        try
        {
            for (var i = 0; i < 200; i++)
            {
                var effDirect = await configOnlyProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                AssertConsistentRevision(effDirect, "ConfigurationOperatorNotificationConfigProvider");

                var effStore = await concurrentStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                AssertConsistentRevision(effStore, "OperatorNotificationStore");
            }
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await rotator.ConfigureAwait(false);
        }

        static void AssertConsistentRevision(OperatorNotificationEffectiveConfiguration eff, string source)
        {
            if (eff.ConfigurationErrorReasonCode == NotificationReasonCodes.InvalidConfiguration && !eff.Options.Enabled)
            {
                Assert.IsTrue(eff.Secrets.IsEmpty, $"{source} fail-closed snapshot must have empty secrets.");
                return;
            }

            var isA = eff.Options.Smtp.Host == "relay-a.example.test";
            var isB = eff.Options.Smtp.Host == "relay-b.example.test";
            Assert.IsTrue(isA || isB, $"{source} observed unexpected SMTP host '{eff.Options.Smtp.Host}'.");
            var expectedTag = isA ? "a" : "b";
            Assert.AreEqual($"user-{expectedTag}", eff.Secrets.SmtpUsername, $"{source} mixed SMTP host and username across revisions!");
            Assert.AreEqual($"pass-{expectedTag}", eff.Secrets.SmtpPassword, $"{source} mixed SMTP host and password across revisions!");
            Assert.AreEqual($"https://origin-{expectedTag}.example.test/hook", eff.Secrets.WebhookUrl, $"{source} mixed SMTP host and webhook URL across revisions!");
            Assert.AreEqual($"token-{expectedTag}", eff.Secrets.WebhookAuthToken, $"{source} mixed webhook URL and auth token across revisions!");
        }
    }

    [TestMethod]
    public async Task SmtpSecurityModeChange_OnSameHostAndPort_DropsHostAndStoredCredentialsAndAllowsLocalNoneRelayAsync()
    {
        // 1. Direct helper checks: same host + port with different SecurityMode NEVER matches.
        var startTls = new SmtpNotificationOptions { Host = "127.0.0.1", Port = 587, SecurityMode = SmtpSecurityMode.StartTls };
        var implicitTls = new SmtpNotificationOptions { Host = "127.0.0.1", Port = 587, SecurityMode = SmtpSecurityMode.ImplicitTls };
        var none = new SmtpNotificationOptions { Host = "127.0.0.1", Port = 587, SecurityMode = SmtpSecurityMode.None };
        Assert.IsTrue(ConfigurationOperatorNotificationConfigProvider.HasMatchingSmtpEndpoint(startTls, startTls));
        Assert.IsFalse(ConfigurationOperatorNotificationConfigProvider.HasMatchingSmtpEndpoint(implicitTls, startTls));
        Assert.IsFalse(ConfigurationOperatorNotificationConfigProvider.HasMatchingSmtpEndpoint(none, startTls));
        Assert.IsFalse(ConfigurationOperatorNotificationConfigProvider.HasMatchingSmtpEndpoint(none, implicitTls));

        // 2. End-to-end store + real loopback SMTP server on the EXACT SAME host and port.
        var server = await SmtpNotificationTransportTests.InProcessSmtpCaptureServer.StartAsync(
            new SmtpNotificationTransportTests.SmtpServerBehavior { AuthMechanisms = "PLAIN LOGIN" }).ConfigureAwait(false);
        await using (server.ConfigureAwait(false))
        {
            using var dir = TempStateDir.Create();
            var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
            var loopbackPolicy = new OperatorNotificationNetworkPolicy(isDevelopment: false) { AllowLoopbackForTesting = true };
            var hostConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "127.0.0.1",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                    [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-starttls-user",
                    [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-starttls-pass"
                })
                .Build();

            using var store = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = dir.Path }),
                hostConfig,
                loopbackPolicy,
                NullLogger<OperatorNotificationStore>.Instance);

            var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(NotificationSecretSource.Configuration, initial.Secrets.SmtpPasswordSource);

            // Switch StartTls -> ImplicitTls on the SAME host + port: MUST drop inherited host credentials.
            var implicitSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                initial.Version,
                CreateSmtpOptions("127.0.0.1", server.Port, SmtpSecurityMode.ImplicitTls)), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, implicitSave.Outcome, implicitSave.Message);
            Assert.IsFalse(implicitSave.Snapshot!.Secrets.HasSmtpUsername);
            Assert.IsFalse(implicitSave.Snapshot.Secrets.HasSmtpPassword);
            Assert.AreEqual(NotificationSecretSource.None, implicitSave.Snapshot.Secrets.SmtpPasswordSource);
            var implicitEffective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(implicitEffective.Secrets.SmtpUsername);
            Assert.IsNull(implicitEffective.Secrets.SmtpPassword);

            // Switch to StartTls with explicit protected credentials, then switch StartTls -> ImplicitTls -> drops stored creds.
            var protectedStartTlsSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                implicitSave.Snapshot.Version,
                CreateSmtpOptions("127.0.0.1", server.Port, SmtpSecurityMode.StartTls),
                SmtpUsername: "stored-starttls-user",
                SmtpPassword: "stored-starttls-pass"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, protectedStartTlsSave.Outcome, protectedStartTlsSave.Message);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, protectedStartTlsSave.Snapshot!.Secrets.SmtpPasswordSource);

            var storedToImplicit = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                protectedStartTlsSave.Snapshot.Version,
                CreateSmtpOptions("127.0.0.1", server.Port, SmtpSecurityMode.ImplicitTls)), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, storedToImplicit.Outcome, storedToImplicit.Message);
            Assert.IsFalse(storedToImplicit.Snapshot!.Secrets.HasSmtpUsername);
            Assert.IsFalse(storedToImplicit.Snapshot.Secrets.HasSmtpPassword);

            // Switch StartTls (with BOTH host and stored creds) -> None on the SAME local host + port:
            // MUST drop both host and stored credentials so validation accepts the credential-free local relay and sends zero AUTH!
            var backToStartTls = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                storedToImplicit.Snapshot.Version,
                CreateSmtpOptions("127.0.0.1", server.Port, SmtpSecurityMode.StartTls),
                SmtpUsername: "stored-starttls-user-2",
                SmtpPassword: "stored-starttls-pass-2"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, backToStartTls.Outcome, backToStartTls.Message);

            var switchedToNone = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                backToStartTls.Snapshot!.Version,
                CreateSmtpOptions("127.0.0.1", server.Port, SmtpSecurityMode.None)), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, switchedToNone.Outcome,
                $"Switching StartTls -> None on same local host+port must drop credentials and pass validation: {switchedToNone.Message}");
            Assert.IsFalse(switchedToNone.Snapshot!.Secrets.HasSmtpUsername);
            Assert.IsFalse(switchedToNone.Snapshot.Secrets.HasSmtpPassword);

            var (noneOpts, noneSecrets) = await store.GetEffectiveForDeliveryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(noneSecrets.SmtpUsername);
            Assert.IsNull(noneSecrets.SmtpPassword);

            var smtpTransport = new SmtpNotificationTransport(loopbackPolicy, TimeProvider.System);
            var delivery = await smtpTransport.SendAsync(new SmtpDeliveryRequest(
                noneOpts.Smtp.Host,
                noneOpts.Smtp.Port,
                noneOpts.Smtp.SecurityMode,
                noneOpts.Smtp.FromAddress,
                noneOpts.Smtp.FromDisplayName,
                noneOpts.Smtp.Recipients,
                noneSecrets.SmtpUsername,
                noneSecrets.SmtpPassword,
                "SecurityMode None check",
                "Local unauthenticated relay delivery.",
                "test",
                TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(delivery.Succeeded, delivery.ReasonCode);
            Assert.IsFalse(server.ReceivedAnyAuthCommand,
                "Switching StartTls -> None on the same host+port must NOT transmit inherited or stored credentials.");
        }
    }

    [TestMethod]
    public async Task InvalidIdnWebhookHostInHostConfiguration_AllowsSnapshotReadAndCorrectiveSaveAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        const string invalidIdnUrl = "https://bad\u200Dhost.example.test/alerts";
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = invalidIdnUrl,
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "host-token"
            })
            .Build();

        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);

        Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, store.ConfigurationErrorReasonCode);

        var snapshot = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(snapshot.StoreCorrupt);
        Assert.IsTrue(snapshot.CanPersist);
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.Version));
        Assert.IsTrue(snapshot.Secrets.HasWebhookUrl);
        Assert.IsNull(snapshot.Secrets.WebhookEndpointHost);
        Assert.IsFalse(snapshot.Secrets.HasWebhookAuthToken,
            "Invalid IDN host URL must not match origin for inheriting host auth token.");

        var effective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, effective.ConfigurationErrorReasonCode);

        // Corrective save replacing the invalid IDN URL with a valid HTTPS URL succeeds cleanly.
        var fixedSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapshot.Version,
            new OperatorNotificationsOptions
            {
                Enabled = true,
                Webhook = new WebhookNotificationOptions { Enabled = true }
            },
            WebhookUrl: "https://hooks.example.test/recovered-alerts",
            WebhookAuthToken: "recovered-token"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, fixedSave.Outcome, fixedSave.Message);
        Assert.IsNull(store.ConfigurationErrorReasonCode);
        Assert.AreEqual("https://hooks.example.test", fixedSave.Snapshot!.Secrets.WebhookEndpointHost);
        Assert.AreEqual(NotificationSecretSource.ProtectedStore, fixedSave.Snapshot.Secrets.WebhookAuthTokenSource);
    }

    [TestMethod]
    public async Task InterruptedFirstPublicationRetryAndConcurrentPublisher_StillSyncUnverifiedParentDirectoriesAsync()
    {
        using var dir = TempStateDir.Create();
        var existingParent = Path.Combine(dir.Path, "existing");
        Directory.CreateDirectory(existingParent);
        var expectedExisting = Path.TrimEndingDirectorySeparator(Path.GetFullPath(existingParent));

        // 1. Interrupted right after leaf directory creation (before ANY parent directory is synced) -> retry MUST sync all parents!
        var level1 = Path.Combine(existingParent, "interrupted-lvl1");
        var stateDir = Path.Combine(level1, "state");
        var expectedLevel1 = Path.TrimEndingDirectorySeparator(Path.GetFullPath(level1));
        var expectedState = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDir));

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
                stateDir,
                OperatorNotificationStore.ConfigFileName,
                Encoding.UTF8.GetBytes("{\"v\":1}"),
                beforeRename: null,
                onDirectorySynced: synced =>
                {
                    if (string.Equals(synced, expectedState, StringComparison.Ordinal))
                    {
                        throw new IOException("Simulated interruption immediately after leaf directory creation before parent sync.");
                    }
                },
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsTrue(Directory.Exists(stateDir), "Directories remain on disk after interrupted first publish.");
        Assert.IsFalse(File.Exists(Path.Combine(stateDir, OperatorNotificationStore.ConfigFileName)));

        var retrySynced = new List<string>();
        await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
            stateDir,
            OperatorNotificationStore.ConfigFileName,
            Encoding.UTF8.GetBytes("{\"v\":1}"),
            beforeRename: null,
            onDirectorySynced: retrySynced.Add,
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(retrySynced.Count >= 4);
        CollectionAssert.AreEqual(
            new[] { expectedState, expectedLevel1, expectedExisting },
            retrySynced.Take(3).ToArray(),
            "Retry after interrupted first publish MUST still sync every unverified parent directory before returning success.");
        Assert.AreEqual(expectedState, retrySynced[^1]);

        // Subsequent publication after retry succeeded skips already-verified parent directories.
        var thirdSynced = new List<string>();
        await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
            stateDir,
            OperatorNotificationStore.ConfigFileName,
            Encoding.UTF8.GetBytes("{\"v\":2}"),
            beforeRename: null,
            onDirectorySynced: thirdSynced.Add,
            CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(new[] { expectedState, expectedState }, thirdSynced);

        // 2. Concurrent publisher racing after directory creation while first publisher has not yet finished parent syncs.
        var concurrentLevel1 = Path.Combine(existingParent, "concurrent-lvl1");
        var concurrentStateDir = Path.Combine(concurrentLevel1, "state");
        var expectedConcurrentLevel1 = Path.TrimEndingDirectorySeparator(Path.GetFullPath(concurrentLevel1));
        var expectedConcurrentState = Path.TrimEndingDirectorySeparator(Path.GetFullPath(concurrentStateDir));

        using var createdAndPaused = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);

        var firstTask = Task.Run(async () =>
            await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
                concurrentStateDir,
                OperatorNotificationStore.ConfigFileName,
                Encoding.UTF8.GetBytes("{\"first\":true}"),
                beforeRename: null,
                onDirectorySynced: synced =>
                {
                    if (string.Equals(synced, expectedConcurrentState, StringComparison.Ordinal) && !createdAndPaused.IsSet)
                    {
                        createdAndPaused.Set();
                        releaseFirst.Wait(TimeSpan.FromSeconds(10));
                    }
                },
                CancellationToken.None).ConfigureAwait(false));

        Assert.IsTrue(createdAndPaused.Wait(TimeSpan.FromSeconds(10)), "First publisher did not pause after directory creation.");
        Assert.IsTrue(Directory.Exists(concurrentStateDir));

        var concurrentSynced = new List<string>();
        await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
            concurrentStateDir,
            FileOperatorNotificationHeartbeatStore.HeartbeatFileName,
            Encoding.UTF8.GetBytes("{\"lastSiteDate\":\"2026-11-26\"}"),
            beforeRename: null,
            onDirectorySynced: concurrentSynced.Add,
            CancellationToken.None).ConfigureAwait(false);

        releaseFirst.Set();
        await firstTask.ConfigureAwait(false);

        CollectionAssert.AreEqual(
            new[] { expectedConcurrentState, expectedConcurrentLevel1, expectedExisting, expectedConcurrentState },
            concurrentSynced,
            "Concurrent publisher targeting newly created directories whose parents are not yet verified-synced MUST sync those parents before acknowledging.");
    }

    [TestMethod]
    public async Task OversizedWriteOnlyEnvelopeAndExternalGrowth_FailClosedAsStoreCorruptAndRefuseReplacementAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "fallback-host.example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-user",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-pass"
            })
            .Build();

        // 1. Write-only (0200) 262,145-byte envelope on Unix MUST report store-corrupt, CanPersist=false, and reject Save/Clear.
        if (!OperatingSystem.IsWindows())
        {
            RequireModesEnforced();
            var writeOnlyDir = Path.Combine(dir.Path, "write-only");
            Directory.CreateDirectory(writeOnlyDir);
            var storePath = Path.Combine(writeOnlyDir, OperatorNotificationStore.ConfigFileName);
            var oversizedBytes = new byte[OperatorNotificationStore.MaximumEnvelopeBytes + 1];
            Array.Fill(oversizedBytes, (byte)' ');
            oversizedBytes[0] = (byte)'{';
            oversizedBytes[^1] = (byte)'}';
            await File.WriteAllBytesAsync(storePath, oversizedBytes).ConfigureAwait(false);
            File.SetUnixFileMode(storePath, UnixFileMode.UserWrite); // 0200 (write-only, unreadable)

            try
            {
                using var store = CreateStore(writeOnlyDir, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);

                Assert.IsFalse(store.CurrentOptions.Enabled);
                Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, store.ConfigurationErrorReasonCode);

                var snapshot = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(snapshot.StoreCorrupt);
                Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, snapshot.StoreStatusReasonCode);
                Assert.IsFalse(snapshot.CanPersist, "Write-only oversized envelope MUST report CanPersist=false.");
                Assert.IsFalse(snapshot.Secrets.HasSmtpPassword, "Write-only oversized envelope MUST NOT fall back to host secrets.");

                var effective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(effective.StoreCorrupt);
                Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, effective.ConfigurationErrorReasonCode);
                Assert.IsTrue(effective.Secrets.IsEmpty);

                var saveResult = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                    snapshot.Version,
                    new OperatorNotificationsOptions { Enabled = false }), "owner-1", CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(OperatorNotificationsSaveOutcome.Unavailable, saveResult.Outcome);

                await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    await ((IOperatorNotificationSecretStore)store).SaveAsync(
                        OperatorNotificationSecrets.Empty, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    await ((IOperatorNotificationSecretStore)store).ClearAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

                Assert.AreEqual(OperatorNotificationStore.MaximumEnvelopeBytes + 1, new FileInfo(storePath).Length,
                    "Write-only oversized envelope must remain untouched on disk.");
            }
            finally
            {
                if (File.Exists(storePath))
                {
                    File.SetUnixFileMode(storePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }

        // 2. External file growth between initial size check and atomic rename MUST be blocked immediately before replace.
        var growthDir = Path.Combine(dir.Path, "external-growth");
        Directory.CreateDirectory(growthDir);
        string initialVersion;
        using (var setupStore = CreateStore(growthDir, isDevelopment: false, dpProvider: dpProvider))
        {
            var init = await setupStore.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var firstSave = await setupStore.SaveAsync(new OperatorNotificationsUpdateRequest(
                init.Version,
                new OperatorNotificationsOptions { Enabled = false }), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, firstSave.Outcome);
            initialVersion = firstSave.Snapshot!.Version;
        }

        var growthBytes = new byte[OperatorNotificationStore.MaximumEnvelopeBytes + 1];
        Array.Fill(growthBytes, (byte)'X');
        using (var guardedStore = new OperatorNotificationStore(
            dpProvider,
            Options.Create(new DeviceProvisioningOptions { StateDirectory = growthDir }),
            new ConfigurationBuilder().Build(),
            new OperatorNotificationNetworkPolicy(isDevelopment: false),
            NullLogger<OperatorNotificationStore>.Instance)
        {
            BeforePublishTempFile = _ => File.WriteAllBytes(Path.Combine(growthDir, OperatorNotificationStore.ConfigFileName), growthBytes)
        })
        {
            var growthResult = await guardedStore.SaveAsync(new OperatorNotificationsUpdateRequest(
                initialVersion,
                new OperatorNotificationsOptions { Enabled = false }), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Unavailable, growthResult.Outcome);
            Assert.IsNotNull(growthResult.Snapshot);
            Assert.IsTrue(growthResult.Snapshot.StoreCorrupt);
            Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, growthResult.Snapshot.StoreStatusReasonCode);
            Assert.IsFalse(growthResult.Snapshot.CanPersist);
            Assert.AreEqual(OperatorNotificationStore.MaximumEnvelopeBytes + 1, new FileInfo(guardedStore.ConfigFilePath).Length);
            Assert.IsEmpty(Directory.EnumerateFiles(growthDir, "*.tmp").ToArray());
        }
    }

    [TestMethod]
    public async Task SecretStoreSaveAsync_OversizedPayload_ThrowsWithoutPublishingAndPreservesPriorReadableEnvelopeAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider);

        var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var validOptions = new OperatorNotificationsOptions
        {
            Enabled = true,
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };
        var saved = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            initial.Version,
            validOptions,
            WebhookUrl: "https://hooks.example.test/valid",
            WebhookAuthToken: "prior-valid-token"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved.Outcome, saved.Message);

        var priorBytes = await File.ReadAllBytesAsync(store.ConfigFilePath).ConfigureAwait(false);

        // 200,000-char token expands past MaximumEnvelopeBytes (256 KiB) after DataProtection encryption + JSON framing.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await ((IOperatorNotificationSecretStore)store).SaveAsync(new OperatorNotificationSecrets(
                WebhookUrl: "https://hooks.example.test/valid",
                WebhookAuthToken: new string('A', 200_000)), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        // Plaintext > MaximumEnvelopeBytes also fails fast without touching disk.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await ((IOperatorNotificationSecretStore)store).SaveAsync(new OperatorNotificationSecrets(
                WebhookUrl: "https://hooks.example.test/valid",
                WebhookAuthToken: new string('B', OperatorNotificationStore.MaximumEnvelopeBytes + 1024)), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        // Prior readable envelope MUST remain byte-for-byte intact and healthy with zero temporary leftovers.
        var afterBytes = await File.ReadAllBytesAsync(store.ConfigFilePath).ConfigureAwait(false);
        CollectionAssert.AreEqual(priorBytes, afterBytes,
            "Oversized IOperatorNotificationSecretStore.SaveAsync MUST leave the prior envelope untouched on disk.");
        Assert.IsEmpty(Directory.EnumerateFiles(dir.Path, "*.tmp").ToArray());

        var snap = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(snap.StoreCorrupt);
        Assert.IsTrue(snap.CanPersist);
        var eff = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("prior-valid-token", eff.Secrets.WebhookAuthToken);
    }

    [TestMethod]
    public async Task SecretStoreReadAndSmtpOnlySave_AfterHostWebhookOriginRotation_DoesNotRebindStaleWebhookTokenAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using var cert = WebhookNotificationTransportTests.CreateEphemeralLoopbackCertificate();
        var serverA = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        var serverB = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await using (serverA.ConfigureAwait(false))
        await using (serverB.ConfigureAwait(false))
        {
            var loopbackPolicy = new OperatorNotificationNetworkPolicy(isDevelopment: false)
            {
                AllowLoopbackForTesting = true
            };
            var urlA = $"https://127.0.0.1:{serverA.Port}/host-hook";
            var urlB = $"https://127.0.0.1:{serverB.Port}/rotated-hook";

            var configProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
            });
            var hostConfig = new ConfigurationRoot([configProvider]);

            using var store = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = dir.Path }),
                hostConfig,
                loopbackPolicy,
                NullLogger<OperatorNotificationStore>.Instance);
            using var webhookTransport = new WebhookNotificationTransport(loopbackPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, cert.Thumbprint, StringComparison.Ordinal)
            };
            using var telemetry = new OperatorNotificationTelemetry();
            using var dispatcher = new OperatorNotificationDispatcher(
                store,
                new SmtpNotificationTransport(loopbackPolicy, TimeProvider.System),
                webhookTransport,
                telemetry,
                TimeProvider.System);

            var options = new OperatorNotificationsOptions
            {
                Enabled = true,
                TestSendMinimumIntervalSeconds = 0,
                Smtp = new SmtpNotificationOptions
                {
                    Enabled = true,
                    Host = "relay-a.example.test",
                    Port = 587,
                    SecurityMode = SmtpSecurityMode.StartTls,
                    FromAddress = "camera@example.test",
                    Recipients = ["ops@example.test"]
                },
                Webhook = new WebhookNotificationOptions
                {
                    Enabled = true,
                    Format = WebhookPayloadFormat.Generic,
                    AuthHeaderName = "Authorization"
                }
            };

            // 1. Save protected webhook token bound to host origin A + SMTP credentials.
            var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var saved = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                initial.Version,
                options,
                SmtpUsername: "smtp-user-1",
                SmtpPassword: "smtp-pass-1",
                WebhookAuthToken: "origin-a-secret-token"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved.Outcome, saved.Message);

            var readOnA = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("origin-a-secret-token", readOnA.WebhookAuthToken);
            Assert.AreEqual("smtp-user-1", readOnA.SmtpUsername);

            var testA = await dispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(testA.Channels[0].Succeeded, testA.Channels[0].ReasonCode);
            Assert.HasCount(1, serverA.Captured);
            Assert.AreEqual("Bearer origin-a-secret-token", serverA.Captured[0].Header("Authorization"));

            // 2. Host rotates Webhook:Url from origin A -> origin B.
            configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
            });

            var snapAfterRotate = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(NotificationSecretSource.None, snapAfterRotate.Secrets.WebhookAuthTokenSource);

            // 2a. Post-rotation ReadAsync must NOT echo unbound stale token, and SMTP-only SaveAsync must NOT rebind it to B.
            var readAfterRotate = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(readAfterRotate.WebhookAuthToken,
                "ReadAsync must not return stale webhook credentials unbound for the rotated origin.");
            Assert.AreEqual("smtp-user-1", readAfterRotate.SmtpUsername);

            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                readAfterRotate with { SmtpUsername = "smtp-user-2" },
                CancellationToken.None).ConfigureAwait(false);

            var snapAfterPostRotateSave = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(snapAfterPostRotateSave.Secrets.HasWebhookAuthToken);
            Assert.AreEqual(NotificationSecretSource.None, snapAfterPostRotateSave.Secrets.WebhookAuthTokenSource);
            var effAfterPostRotateSave = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("smtp-user-2", effAfterPostRotateSave.Secrets.SmtpUsername);
            Assert.IsNull(effAfterPostRotateSave.Secrets.WebhookAuthToken);

            // 2b. Even if a caller cached pre-rotation ReadAsync (containing origin-a's token!) and saves an SMTP-only
            //     mutation AFTER host rotated A -> B — AND RETRIES that exact same cached save multiple times —
            //     SaveAsync MUST NEVER rebind the stale token onto origin B on attempt 1, attempt 2, or attempt N!
            configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
            });
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                new OperatorNotificationSecrets(
                    SmtpUsername: "smtp-user-3",
                    SmtpPassword: "smtp-pass-3",
                    WebhookAuthToken: "cached-origin-a-token"),
                CancellationToken.None).ConfigureAwait(false);
            var cachedPreRotateSecrets = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("cached-origin-a-token", cachedPreRotateSecrets.WebhookAuthToken);
            Assert.IsNotNull(cachedPreRotateSecrets.Provenance);

            configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
            });
            var cachedSmtpOnlySave = cachedPreRotateSecrets with { SmtpUsername = "smtp-user-4" };
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                cachedSmtpOnlySave,
                CancellationToken.None).ConfigureAwait(false);

            var snapAfterCachedSave1 = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(snapAfterCachedSave1.Secrets.HasWebhookAuthToken,
                "Attempt 1: SMTP-only SaveAsync carrying a pre-rotation cached webhook token MUST NOT rebind it to rotated origin B.");
            var effAfterCachedSave1 = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("smtp-user-4", effAfterCachedSave1.Secrets.SmtpUsername);
            Assert.AreEqual(urlB, effAfterCachedSave1.Secrets.WebhookUrl);
            Assert.IsNull(effAfterCachedSave1.Secrets.WebhookAuthToken);

            // Retry 2: saving the EXACT SAME cached pre-rotation SMTP-only snapshot again MUST STILL NOT rebind token A!
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                cachedSmtpOnlySave,
                CancellationToken.None).ConfigureAwait(false);

            var snapAfterCachedSave2 = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(snapAfterCachedSave2.Secrets.HasWebhookAuthToken,
                "Attempt 2 (retry): retrying the same cached pre-rotation SMTP-only save MUST NEVER rebind origin A's token to origin B.");
            var effAfterCachedSave2 = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("smtp-user-4", effAfterCachedSave2.Secrets.SmtpUsername);
            Assert.AreEqual(urlB, effAfterCachedSave2.Secrets.WebhookUrl);
            Assert.IsNull(effAfterCachedSave2.Secrets.WebhookAuthToken);

            // Retry 3: even a manual field-by-field copy (stripped of Provenance) carrying origin A's stale token MUST NOT rebind!
            var manualCopyWithoutProvenance = new OperatorNotificationSecrets(
                SmtpUsername: "smtp-user-5",
                SmtpPassword: cachedPreRotateSecrets.SmtpPassword,
                WebhookUrl: cachedPreRotateSecrets.WebhookUrl,
                WebhookAuthToken: cachedPreRotateSecrets.WebhookAuthToken,
                WebhookUserKey: cachedPreRotateSecrets.WebhookUserKey,
                WebhookNtfyTopic: cachedPreRotateSecrets.WebhookNtfyTopic);
            Assert.IsNull(manualCopyWithoutProvenance.Provenance);
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                manualCopyWithoutProvenance,
                CancellationToken.None).ConfigureAwait(false);
            var effAfterManualRetry = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("smtp-user-5", effAfterManualRetry.Secrets.SmtpUsername);
            Assert.IsNull(effAfterManualRetry.Secrets.WebhookAuthToken,
                "Attempt 3 (manual copy without provenance): retrying origin A's stale token MUST STILL NOT bind to origin B.");

            // Verify both test-send and live dispatcher delivery to origin B send ZERO Authorization header.
            var testB = await dispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(testB.Channels[0].Succeeded, testB.Channels[0].ReasonCode);

            var published = dispatcher.TryPublish(new OperatorNotificationEvent(
                OperatorNotificationEventKind.DiskPressure,
                OperatorNotificationSeverity.Warning,
                "disk-secret-store-b",
                "Disk pressure",
                "Live dispatch after retried direct secret store SMTP-only save.",
                DateTimeOffset.UtcNow,
                "TestCam"));
            Assert.IsTrue(published);
            Assert.AreEqual(1, await dispatcher.ProcessChannelPendingOnceAsync(WebhookNotificationTransport.ChannelName, CancellationToken.None).ConfigureAwait(false));

            Assert.HasCount(2, serverB.Captured);
            foreach (var request in serverB.Captured)
            {
                Assert.IsNull(request.Header("Authorization"),
                    "Origin B MUST NEVER receive origin A's stale token after direct IOperatorNotificationSecretStore.SaveAsync retries.");
            }

            // 3. Explicitly supplying a genuinely NEW token for origin B binds cleanly and authenticates against B.
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                new OperatorNotificationSecrets(
                    SmtpUsername: "smtp-user-5",
                    SmtpPassword: "smtp-pass-3",
                    WebhookAuthToken: "fresh-origin-b-token"),
                CancellationToken.None).ConfigureAwait(false);
            var testBWithNewToken = await dispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(testBWithNewToken.Channels[0].Succeeded, testBWithNewToken.Channels[0].ReasonCode);
            Assert.HasCount(3, serverB.Captured);
            Assert.AreEqual("Bearer fresh-origin-b-token", serverB.Captured[2].Header("Authorization"));
        }
    }

    [TestMethod]
    public async Task MultiProviderConfigurationReload_NeverExposesIntermediateSplitRelayAndCredentialsAsync()
    {
        static Dictionary<string, string?> CredentialValues(string tag) => new(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = $"user-{tag}",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = $"pass-{tag}",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = $"token-{tag}"
        };

        static Dictionary<string, string?> DestinationValues(string tag) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = $"relay-{tag}.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = $"https://origin-{tag}.example.test/hook"
        };

        using var enteredProvider2Load = new ManualResetEventSlim(false);
        using var releaseProvider2Load = new ManualResetEventSlim(false);

        var credentialsProvider = new BarrierReloadConfigurationProvider(CredentialValues("a"));
        var destinationsProvider = new BarrierReloadConfigurationProvider(
            DestinationValues("a"),
            onLoadStarted: enteredProvider2Load,
            waitBeforeApplyingLoad: releaseProvider2Load);
        var root = new ConfigurationRoot([credentialsProvider, destinationsProvider]);

        using var dir = TempStateDir.Create();
        using var configProvider = new ConfigurationOperatorNotificationConfigProvider(root);
        using var store = CreateStore(dir.Path, isDevelopment: false, configuration: root);

        // Initial state: both providers on Revision A.
        var initialConfig = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        var initialStore = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-a.example.test", initialConfig.Options.Smtp.Host);
        Assert.AreEqual("user-a", initialConfig.Secrets.SmtpUsername);
        Assert.AreEqual("relay-a.example.test", initialStore.Options.Smtp.Host);
        Assert.AreEqual("user-a", initialStore.Secrets.SmtpUsername);

        // Stage Revision B on both providers and pause ConfigurationRoot.Reload() AFTER provider 1 has updated
        // credentials to B while provider 2 STILL holds destinations for A (before RaiseChanged fires).
        credentialsProvider.StageNext(CredentialValues("b"));
        destinationsProvider.StageNext(DestinationValues("b"), armBarrier: true);

        var reloadTask = Task.Run(() => root.Reload());
        try
        {
            Assert.IsTrue(enteredProvider2Load.Wait(TimeSpan.FromSeconds(10)),
                "Provider 2 did not reach the mid-reload barrier.");

            // Confirm the raw IConfigurationRoot is currently in the torn multi-provider intermediate state!
            Assert.AreEqual("relay-a.example.test", root[OperatorNotificationsOptions.SectionKey + ":Smtp:Host"]);
            Assert.AreEqual("user-b", root[ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey]);
            Assert.AreEqual("https://origin-a.example.test/hook", root[ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey]);
            Assert.AreEqual("token-b", root[ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey]);

            // Neither ConfigurationOperatorNotificationConfigProvider nor OperatorNotificationStore may observe
            // the intermediate split state while Reload() is in flight: both stay atomically on Revision A!
            for (var i = 0; i < 10; i++)
            {
                var midConfig = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("relay-a.example.test", midConfig.Options.Smtp.Host);
                Assert.AreEqual("user-a", midConfig.Secrets.SmtpUsername);
                Assert.AreEqual("pass-a", midConfig.Secrets.SmtpPassword);
                Assert.AreEqual("https://origin-a.example.test/hook", midConfig.Secrets.WebhookUrl);
                Assert.AreEqual("token-a", midConfig.Secrets.WebhookAuthToken);

                var midStore = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("relay-a.example.test", midStore.Options.Smtp.Host);
                Assert.AreEqual("user-a", midStore.Secrets.SmtpUsername);
                Assert.AreEqual("pass-a", midStore.Secrets.SmtpPassword);
                Assert.AreEqual("https://origin-a.example.test/hook", midStore.Secrets.WebhookUrl);
                Assert.AreEqual("token-a", midStore.Secrets.WebhookAuthToken);
            }
        }
        finally
        {
            releaseProvider2Load.Set();
        }

        await reloadTask.ConfigureAwait(false);

        // Once Reload() finishes all providers and signals IChangeToken, both switch atomically to Revision B!
        var finalConfig = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-b.example.test", finalConfig.Options.Smtp.Host);
        Assert.AreEqual("user-b", finalConfig.Secrets.SmtpUsername);
        Assert.AreEqual("pass-b", finalConfig.Secrets.SmtpPassword);
        Assert.AreEqual("https://origin-b.example.test/hook", finalConfig.Secrets.WebhookUrl);
        Assert.AreEqual("token-b", finalConfig.Secrets.WebhookAuthToken);

        var finalStore = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-b.example.test", finalStore.Options.Smtp.Host);
        Assert.AreEqual("user-b", finalStore.Secrets.SmtpUsername);
        Assert.AreEqual("pass-b", finalStore.Secrets.SmtpPassword);
        Assert.AreEqual("https://origin-b.example.test/hook", finalStore.Secrets.WebhookUrl);
        Assert.AreEqual("token-b", finalStore.Secrets.WebhookAuthToken);
    }

    [TestMethod]
    public async Task CrossProcessCrashRecoveryUnreadableParentAndDescriptorFchmod_EnforceFullAncestorSyncAndNoSymlinkToctouAsync()
    {
        using var dir = TempStateDir.Create();
        var existingParent = Path.Combine(dir.Path, "existing");
        Directory.CreateDirectory(existingParent);
        var one = Path.Combine(existingParent, "one");
        var two = Path.Combine(one, "two");
        var stateDir = Path.Combine(two, "state");

        var expectedExisting = Path.TrimEndingDirectorySeparator(Path.GetFullPath(existingParent));
        var expectedOne = Path.TrimEndingDirectorySeparator(Path.GetFullPath(one));
        var expectedTwo = Path.TrimEndingDirectorySeparator(Path.GetFullPath(two));
        var expectedState = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDir));

        // 1. Cross-process crash recovery: Process 1 creates existing/one/two/state and crashes BEFORE syncing parents.
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
                stateDir,
                OperatorNotificationStore.ConfigFileName,
                Encoding.UTF8.GetBytes("{\"v\":1}"),
                beforeRename: null,
                onDirectorySynced: synced =>
                {
                    if (string.Equals(synced, expectedState, StringComparison.Ordinal))
                    {
                        throw new IOException("Simulated Process 1 crash after mkdir before syncing any parent.");
                    }
                },
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsTrue(Directory.Exists(stateDir));

        // Simulate Process 2 starting fresh with empty process-local sync maps for this storage tree!
        NotificationStateFilePublisher.ResetVerifiedDirectorySyncCacheForPathPrefixForTesting(dir.Path);

        var process2Synced = new List<string>();
        await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
            stateDir,
            OperatorNotificationStore.ConfigFileName,
            Encoding.UTF8.GetBytes("{\"v\":1}"),
            beforeRename: null,
            onDirectorySynced: process2Synced.Add,
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(process2Synced.Count >= 5,
            "Fresh Process 2 must walk and sync the full unverified ancestor chain after Process 1 crashed.");
        CollectionAssert.AreEqual(
            new[] { expectedState, expectedTwo, expectedOne, expectedExisting },
            process2Synced.Take(4).ToArray(),
            "Process 2 must sync state, two, one, AND existing (never omitting unsynced ancestors from Process 1).");
        Assert.AreEqual(expectedState, process2Synced[^1]);

        // Subsequent publish in Process 2 skips already-verified ancestors.
        var process2SecondSynced = new List<string>();
        await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
            stateDir,
            OperatorNotificationStore.ConfigFileName,
            Encoding.UTF8.GetBytes("{\"v\":2}"),
            beforeRename: null,
            onDirectorySynced: process2SecondSynced.Add,
            CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(new[] { expectedState, expectedState }, process2SecondSynced);

        // 2. Deterministic PermissionDenied handling:
        //    - Pre-existing ancestor strictly ABOVE newly created dirs / state dir -> best-effort (publish succeeds).
        //    - Newly created intermediate dir OR state dir itself -> MUST fail closed (UnauthorizedAccessException).
        //    - Non-permission EIO on pre-existing ancestor -> MUST fail closed (IOException).
        var permParent = Path.Combine(dir.Path, "perm-parent");
        Directory.CreateDirectory(permParent);
        var expectedPermParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(permParent));
        var permChild = Path.Combine(permParent, "child");
        var permState = Path.Combine(permChild, "state");
        var expectedPermChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(permChild));
        var expectedPermState = Path.TrimEndingDirectorySeparator(Path.GetFullPath(permState));

        NotificationStateFilePublisher.ResetVerifiedDirectorySyncCacheForPathPrefixForTesting(permParent);
        var bestEffortSynced = new List<string>();
        await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
            permState,
            OperatorNotificationStore.ConfigFileName,
            Encoding.UTF8.GetBytes("{\"ok\":true}"),
            beforeRename: null,
            onDirectorySynced: bestEffortSynced.Add,
            directorySyncAction: path =>
            {
                var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (string.Equals(normalized, expectedPermParent, StringComparison.Ordinal))
                {
                    throw new FileSystemFaultException(FileSystemFaultKind.PermissionDenied, "sync-directory", path);
                }
            },
            maxExistingTargetBytes: null,
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(File.Exists(Path.Combine(permState, OperatorNotificationStore.ConfigFileName)));
        CollectionAssert.Contains(bestEffortSynced, expectedPermState);
        CollectionAssert.Contains(bestEffortSynced, expectedPermChild);
        CollectionAssert.DoesNotContain(bestEffortSynced, expectedPermParent);

        // Once permissions on permParent are restored, a subsequent publish MUST retry and complete syncing permParent
        // (because the PermissionDenied skip MUST NOT have cached permParent as verified!).
        var restoredSynced = new List<string>();
        await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
            permState,
            OperatorNotificationStore.ConfigFileName,
            Encoding.UTF8.GetBytes("{\"ok\":2}"),
            beforeRename: null,
            onDirectorySynced: restoredSynced.Add,
            directorySyncAction: _ => { },
            maxExistingTargetBytes: null,
            CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.Contains(restoredSynced, expectedPermParent,
            "After a PermissionDenied skip, restoring access MUST allow a subsequent publish to retry and sync the ancestor.");

        // After permParent actually succeeded, a third publish skips re-syncing the now-verified ancestor.
        var verifiedSynced = new List<string>();
        await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
            permState,
            OperatorNotificationStore.ConfigFileName,
            Encoding.UTF8.GetBytes("{\"ok\":3}"),
            beforeRename: null,
            onDirectorySynced: verifiedSynced.Add,
            directorySyncAction: _ => { },
            maxExistingTargetBytes: null,
            CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.DoesNotContain(verifiedSynced, expectedPermParent,
            "Once the ancestor directory fsync actually succeeded, subsequent publishes should use the verified cache.");

        // Newly created intermediate directory returning PermissionDenied MUST fail closed!
        var strictNewParent = Path.Combine(dir.Path, "strict-new");
        Directory.CreateDirectory(strictNewParent);
        var strictIntermediate = Path.Combine(strictNewParent, "new-mid");
        var strictState = Path.Combine(strictIntermediate, "state");
        var expectedStrictIntermediate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(strictIntermediate));

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () =>
            await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
                strictState,
                OperatorNotificationStore.ConfigFileName,
                Encoding.UTF8.GetBytes("{\"fail\":true}"),
                beforeRename: null,
                onDirectorySynced: null,
                directorySyncAction: path =>
                {
                    var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                    if (string.Equals(normalized, expectedStrictIntermediate, StringComparison.Ordinal))
                    {
                        throw new FileSystemFaultException(FileSystemFaultKind.PermissionDenied, "sync-directory", path);
                    }
                },
                maxExistingTargetBytes: null,
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(Path.Combine(strictState, OperatorNotificationStore.ConfigFileName)));

        // Leaf state directory returning PermissionDenied MUST fail closed!
        var strictLeafDir = Path.Combine(dir.Path, "strict-leaf", "state");
        var expectedStrictLeaf = Path.TrimEndingDirectorySeparator(Path.GetFullPath(strictLeafDir));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () =>
            await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
                strictLeafDir,
                OperatorNotificationStore.ConfigFileName,
                Encoding.UTF8.GetBytes("{\"fail\":true}"),
                beforeRename: null,
                onDirectorySynced: null,
                directorySyncAction: path =>
                {
                    var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                    if (string.Equals(normalized, expectedStrictLeaf, StringComparison.Ordinal))
                    {
                        throw new FileSystemFaultException(FileSystemFaultKind.PermissionDenied, "sync-directory", path);
                    }
                },
                maxExistingTargetBytes: null,
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        // Pre-existing ancestor returning FileSystemFaultKind.Io (EIO) MUST still fail closed!
        var ioParent = Path.Combine(dir.Path, "io-parent");
        Directory.CreateDirectory(ioParent);
        var expectedIoParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ioParent));
        var ioState = Path.Combine(ioParent, "child", "state");
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
                ioState,
                OperatorNotificationStore.ConfigFileName,
                Encoding.UTF8.GetBytes("{\"fail\":true}"),
                beforeRename: null,
                onDirectorySynced: null,
                directorySyncAction: path =>
                {
                    var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                    if (string.Equals(normalized, expectedIoParent, StringComparison.Ordinal))
                    {
                        throw new FileSystemFaultException(FileSystemFaultKind.Io, "sync-directory", path);
                    }
                },
                maxExistingTargetBytes: null,
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        // 3. Real Unix non-readable pre-existing parent (0300 & 0100) + descriptor-based fchmod 0700 & symlink rejection.
        if (!OperatingSystem.IsWindows())
        {
            RequireModesEnforced();

            // 3a. 0300 (write+execute, unreadable) pre-existing parent creating a new state dir succeeds end-to-end.
            var mode0300Parent = Path.Combine(dir.Path, "parent-0300");
            Directory.CreateDirectory(mode0300Parent);
            var mode0300State = Path.Combine(mode0300Parent, "state");
            File.SetUnixFileMode(mode0300Parent, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                NotificationStateFilePublisher.ResetVerifiedDirectorySyncCacheForPathPrefixForTesting(mode0300Parent);
                await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
                    mode0300State,
                    OperatorNotificationStore.ConfigFileName,
                    Encoding.UTF8.GetBytes("{\"m\":300}"),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(File.Exists(Path.Combine(mode0300State, OperatorNotificationStore.ConfigFileName)));
            }
            finally
            {
                File.SetUnixFileMode(mode0300Parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // 3b. 0100 (execute-only, unreadable) pre-existing parent with pre-created 0700 state dir succeeds end-to-end.
            var mode0100Parent = Path.Combine(dir.Path, "parent-0100");
            var mode0100State = Path.Combine(mode0100Parent, "state");
            Directory.CreateDirectory(mode0100State);
            File.SetUnixFileMode(mode0100State, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(mode0100Parent, UnixFileMode.UserExecute);
            try
            {
                NotificationStateFilePublisher.ResetVerifiedDirectorySyncCacheForPathPrefixForTesting(mode0100Parent);
                await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
                    mode0100State,
                    OperatorNotificationStore.ConfigFileName,
                    Encoding.UTF8.GetBytes("{\"m\":100}"),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(File.Exists(Path.Combine(mode0100State, OperatorNotificationStore.ConfigFileName)));
            }
            finally
            {
                File.SetUnixFileMode(mode0100Parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // 3c. Descriptor-based fchmod tightens 0755 state dir to 0700 and refuses symlink without modifying target mode.
            const UnixFileMode permissive =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            const UnixFileMode restricted =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

            var looseDir = Path.Combine(dir.Path, "loose-0755");
            Directory.CreateDirectory(looseDir);
            File.SetUnixFileMode(looseDir, permissive);
            NotificationStateFilePublisher.EnsureRestrictedDirectory(looseDir);
            Assert.AreEqual(restricted, File.GetUnixFileMode(looseDir),
                "Descriptor-based fchmod must enforce 0700 on the verified state directory.");

            var realTarget = Path.Combine(dir.Path, "fchmod-symlink-target");
            Directory.CreateDirectory(realTarget);
            File.SetUnixFileMode(realTarget, permissive);
            var symlinkDir = Path.Combine(dir.Path, "fchmod-symlink-state");
            Directory.CreateSymbolicLink(symlinkDir, realTarget);
            Assert.ThrowsExactly<IOException>(() => NotificationStateFilePublisher.EnsureRestrictedDirectory(symlinkDir));
            Assert.AreEqual(permissive, File.GetUnixFileMode(realTarget),
                "Symlink rejection must NEVER alter the target directory's permissions.");
        }
    }

    [TestMethod]
    public async Task UnattendedHostConfigurationReload_EnablesNotificationsAndMonitorDeliversWithoutUiReadAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using var cert = WebhookNotificationTransportTests.CreateEphemeralLoopbackCertificate();
        var server = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await using (server.ConfigureAwait(false))
        {
            var loopbackPolicy = new OperatorNotificationNetworkPolicy(isDevelopment: false)
            {
                AllowLoopbackForTesting = true
            };
            var webhookUrl = $"https://127.0.0.1:{server.Port}/unattended-alerts";

            // Start with notifications DISABLED and NO saved override file on disk.
            var configProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "false"
            });
            var hostConfig = new ConfigurationRoot([configProvider]);

            using var store = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = dir.Path }),
                hostConfig,
                loopbackPolicy,
                NullLogger<OperatorNotificationStore>.Instance);
            using var webhookTransport = new WebhookNotificationTransport(loopbackPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, cert.Thumbprint, StringComparison.Ordinal)
            };
            using var telemetry = new OperatorNotificationTelemetry();
            using var dispatcher = new OperatorNotificationDispatcher(
                store,
                new SmtpNotificationTransport(loopbackPolicy, TimeProvider.System),
                webhookTransport,
                telemetry,
                TimeProvider.System);

            var fleet = new FleetRuntimeState(TimeProvider.System);
            fleet.CaptureFailed("USB camera disconnected", isCameraDisconnected: true);

            using var monitor = new OperatorNotificationMonitorService(
                store,
                dispatcher,
                Options.Create(new CameraAgentHostOptions { DisplayName = "Unattended Dome" }),
                TimeProvider.System,
                fleetRuntimeState: fleet);

            // Initially disabled: monitor skips evaluation and sends zero alerts.
            Assert.IsFalse(store.CurrentOptions.Enabled);
            Assert.IsNull(store.ConfigurationErrorReasonCode);
            Assert.IsFalse(File.Exists(store.ConfigFilePath));
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.IsEmpty(server.Captured);

            // Reload host configuration to opt in — WITHOUT calling GetSnapshotAsync / GetEffectiveConfigurationAsync!
            configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":CameraDisconnectedThresholdSeconds"] = "0",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = webhookUrl,
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "reloaded-host-token"
            });

            Assert.IsTrue(store.CurrentOptions.Enabled,
                "CurrentOptions.Enabled MUST refresh immediately on unattended host configuration reload without UI/API snapshot reads.");
            Assert.IsTrue(store.CurrentOptions.Webhook.Enabled);
            Assert.IsNull(store.ConfigurationErrorReasonCode);

            // Background monitor evaluates and dispatcher delivers the alert end-to-end without any UI read!
            await monitor.EvaluateOnceAsync(CancellationToken.None).ConfigureAwait(false);
            var processed = await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, processed);
            Assert.HasCount(1, server.Captured);
            Assert.AreEqual("Bearer reloaded-host-token", server.Captured[0].Header("Authorization"));

            // Reloading to invalid host configuration immediately surfaces InvalidConfiguration in memory.
            configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true"
            });
            Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, store.ConfigurationErrorReasonCode);

            // Reloading back to disabled clears error and disables CurrentOptions immediately.
            configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "false"
            });
            Assert.IsFalse(store.CurrentOptions.Enabled);
            Assert.IsNull(store.ConfigurationErrorReasonCode);

            // Once an explicit saved override exists (Enabled=false), subsequent host reload to Enabled=true
            // must NOT override the persisted operator setting.
            var snapBeforeOverride = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var savedDisabled = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snapBeforeOverride.Version,
                new OperatorNotificationsOptions { Enabled = false }), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedDisabled.Outcome);

            configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = webhookUrl
            });
            Assert.IsFalse(store.CurrentOptions.Enabled,
                "Persisted operator override (Enabled=false) must take precedence over host configuration reload.");
        }
    }

    [TestMethod]
    public async Task MultiProviderFileReload_CallingOnReloadInsideLoad_NeverPublishesOrDeliversMismatchedDestinationAndCredentialsAsync()
    {
        using var dir = TempStateDir.Create();
        using var cert = WebhookNotificationTransportTests.CreateEphemeralLoopbackCertificate();
        var serverA = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        var serverB = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await using (serverA.ConfigureAwait(false))
        await using (serverB.ConfigureAwait(false))
        {
            var loopbackPolicy = new OperatorNotificationNetworkPolicy(isDevelopment: false)
            {
                AllowLoopbackForTesting = true
            };
            var urlA = $"https://127.0.0.1:{serverA.Port}/hook-a";
            var urlB = $"https://127.0.0.1:{serverB.Port}/hook-b";

            // Part A: Two REAL JsonConfigurationProvider files (destinations.json + credentials.json).
            // In .NET 10, JsonConfigurationProvider.Load() calls OnReload() inside Load(), so root.Reload()
            // triggers root change callbacks after destinations.json loads B while credentials.json still holds A!
            var destJsonPath = Path.Combine(dir.Path, "destinations.json");
            var credsJsonPath = Path.Combine(dir.Path, "credentials.json");

            static string BuildDestJson(string relayHost, string webhookUrl) => JsonSerializer.Serialize(new
            {
                CameraAgent = new
                {
                    Notifications = new
                    {
                        Enabled = true,
                        TestSendMinimumIntervalSeconds = 0,
                        Smtp = new
                        {
                            Enabled = true,
                            Host = relayHost,
                            Port = 587,
                            SecurityMode = "StartTls",
                            FromAddress = "camera@example.test",
                            Recipients = ExpectedOpsRecipients
                        },
                        Webhook = new
                        {
                            Enabled = true,
                            Format = "Generic",
                            AuthHeaderName = "Authorization",
                            Url = webhookUrl
                        }
                    }
                }
            });

            static string BuildCredsJson(string user, string pass, string token) => JsonSerializer.Serialize(new
            {
                CameraAgent = new
                {
                    Notifications = new
                    {
                        Smtp = new
                        {
                            Username = user,
                            Password = pass
                        },
                        Webhook = new
                        {
                            AuthToken = token
                        }
                    }
                }
            });

            await File.WriteAllTextAsync(destJsonPath, BuildDestJson("relay-a.example.test", urlA)).ConfigureAwait(false);
            await File.WriteAllTextAsync(credsJsonPath, BuildCredsJson("user-a", "pass-a", "token-a")).ConfigureAwait(false);

            var jsonRoot = new ConfigurationBuilder()
                .AddJsonFile(destJsonPath, optional: false, reloadOnChange: false)
                .AddJsonFile(credsJsonPath, optional: false, reloadOnChange: false)
                .Build();

            using var jsonConfigProvider = new ConfigurationOperatorNotificationConfigProvider(jsonRoot, loopbackPolicy);
            using var jsonStoreDir = TempStateDir.Create();
            using var jsonStore = CreateStore(jsonStoreDir.Path, isDevelopment: false, configuration: jsonRoot);

            var initialJson = await jsonConfigProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-a.example.test", initialJson.Options.Smtp.Host);
            Assert.AreEqual("user-a", initialJson.Secrets.SmtpUsername);
            Assert.AreEqual(urlA, initialJson.Secrets.WebhookUrl);
            Assert.AreEqual("token-a", initialJson.Secrets.WebhookAuthToken);

            var observedIntermediateStates = new List<(string? SmtpHost, string? SmtpUser, string? SmtpPass, string? WebhookUrl, string? WebhookToken)>();
            using var jsonTracker = new HostConfigurationSnapshotTracker(
                jsonRoot,
                snapshot => observedIntermediateStates.Add((
                    snapshot.Options.Smtp.Host,
                    snapshot.Secrets.SmtpUsername,
                    snapshot.Secrets.SmtpPassword,
                    snapshot.Secrets.WebhookUrl,
                    snapshot.Secrets.WebhookAuthToken)));

            await File.WriteAllTextAsync(destJsonPath, BuildDestJson("relay-b.example.test", urlB)).ConfigureAwait(false);
            await File.WriteAllTextAsync(credsJsonPath, BuildCredsJson("user-b", "pass-b", "token-b")).ConfigureAwait(false);
            jsonRoot.Reload();

            Assert.IsTrue(observedIntermediateStates.Count >= 2,
                "Real JsonConfigurationProvider.Load() fires OnReload() per provider during ConfigurationRoot.Reload().");
            foreach (var observed in observedIntermediateStates)
            {
                if (string.Equals(observed.SmtpHost, "relay-b.example.test", StringComparison.Ordinal))
                {
                    Assert.AreNotEqual("user-a", observed.SmtpUser, "Rotated SMTP relay B MUST NEVER pair with relay A's username.");
                    Assert.AreNotEqual("pass-a", observed.SmtpPass, "Rotated SMTP relay B MUST NEVER pair with relay A's password.");
                }
                if (string.Equals(observed.WebhookUrl, urlB, StringComparison.Ordinal))
                {
                    Assert.AreNotEqual("token-a", observed.WebhookToken, "Rotated webhook origin B MUST NEVER pair with origin A's auth token.");
                }
            }

            var finalJson = await jsonConfigProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-b.example.test", finalJson.Options.Smtp.Host);
            Assert.AreEqual("user-b", finalJson.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", finalJson.Secrets.SmtpPassword);
            Assert.AreEqual(urlB, finalJson.Secrets.WebhookUrl);
            Assert.AreEqual("token-b", finalJson.Secrets.WebhookAuthToken);

            // Part B: Deterministic mid-Reload() pause using providers that invoke OnReload() inside Load()
            // (matching FileConfigurationProvider): Provider 1 rotates destination A -> B and signals OnReload()
            // BEFORE Provider 2 rotates credentials A -> B. Verify zero (destination B, credential A) publication
            // AND zero delivery of token A to server B mid-reload!
            static Dictionary<string, string?> DestDict(string relayHost, string webhookUrl) => new(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":TestSendMinimumIntervalSeconds"] = "0",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = relayHost,
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Generic",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:AuthHeaderName"] = "Authorization",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = webhookUrl
            };

            static Dictionary<string, string?> CredDict(string tag, string? boundEndpoint = null, string? boundOrigin = null)
            {
                var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = $"user-{tag}",
                    [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = $"pass-{tag}",
                    [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = $"token-{tag}"
                };
                if (boundEndpoint is not null)
                {
                    dict[ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = boundEndpoint;
                }
                if (boundOrigin is not null)
                {
                    dict[ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = boundOrigin;
                }
                return dict;
            }

            using var enteredCredsLoad = new ManualResetEventSlim(false);
            using var releaseCredsLoad = new ManualResetEventSlim(false);

            var destFileLike = new BarrierReloadConfigurationProvider(DestDict("relay-a.example.test", urlA), fireOnReloadInsideLoad: true);
            var credsFileLike = new BarrierReloadConfigurationProvider(
                CredDict("a"),
                onLoadStarted: enteredCredsLoad,
                waitBeforeApplyingLoad: releaseCredsLoad,
                fireOnReloadInsideLoad: true);
            var splitRoot = new ConfigurationRoot([destFileLike, credsFileLike]);

            using var splitStoreDir = TempStateDir.Create();
            using var splitConfigProvider = new ConfigurationOperatorNotificationConfigProvider(splitRoot, loopbackPolicy);
            var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
            using var splitStore = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = splitStoreDir.Path }),
                splitRoot,
                loopbackPolicy,
                NullLogger<OperatorNotificationStore>.Instance);
            using var webhookTransport = new WebhookNotificationTransport(loopbackPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, cert.Thumbprint, StringComparison.Ordinal)
            };
            using var telemetry = new OperatorNotificationTelemetry();
            using var dispatcher = new OperatorNotificationDispatcher(
                splitStore,
                new SmtpNotificationTransport(loopbackPolicy, TimeProvider.System),
                webhookTransport,
                telemetry,
                TimeProvider.System);

            destFileLike.StageNext(DestDict("relay-b.example.test", urlB));
            credsFileLike.StageNext(CredDict("b"), armBarrier: true);

            var reloadTask = Task.Run(() => splitRoot.Reload());
            try
            {
                Assert.IsTrue(enteredCredsLoad.Wait(TimeSpan.FromSeconds(10)), "Credentials provider did not enter Load().");

                // Raw root is in torn (dest B, cred A) state right after provider 1 fired OnReload()!
                Assert.AreEqual(urlB, splitRoot[ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey]);
                Assert.AreEqual("token-a", splitRoot[ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey]);

                // Neither provider nor store may publish (destination B, credential A)!
                var midConfig = await splitConfigProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("relay-b.example.test", midConfig.Options.Smtp.Host);
                Assert.IsNull(midConfig.Secrets.SmtpUsername, "Mid-reload snapshot must drop relay A's username after destination rotated to B.");
                Assert.IsNull(midConfig.Secrets.SmtpPassword, "Mid-reload snapshot must drop relay A's password after destination rotated to B.");
                Assert.AreEqual(urlB, midConfig.Secrets.WebhookUrl);
                Assert.IsNull(midConfig.Secrets.WebhookAuthToken, "Mid-reload snapshot must drop origin A's token after destination rotated to B.");

                var midStore = await splitStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsNull(midStore.Secrets.SmtpUsername);
                Assert.IsNull(midStore.Secrets.SmtpPassword);
                Assert.IsNull(midStore.Secrets.WebhookAuthToken);

                // Even an actual delivery executing mid-reload MUST NEVER send token-a to server B!
                var midDelivery = await dispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(midDelivery.Channels[0].Succeeded, midDelivery.Channels[0].ReasonCode);
                Assert.HasCount(1, serverB.Captured);
                Assert.IsNull(serverB.Captured[0].Header("Authorization"),
                    "Origin B MUST NEVER receive origin A's token during a mid-Reload() torn state.");
            }
            finally
            {
                releaseCredsLoad.Set();
            }

            await reloadTask.ConfigureAwait(false);

            // Once provider 2 finishes loading cred B, (dest B, cred B) publishes and delivers cleanly!
            var postReloadDelivery = await dispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(postReloadDelivery.Channels[0].Succeeded, postReloadDelivery.Channels[0].ReasonCode);
            Assert.HasCount(2, serverB.Captured);
            Assert.AreEqual("Bearer token-b", serverB.Captured[1].Header("Authorization"));

            // Part C: Reverse order (credentials provider 1 loads B + explicit BoundEndpoint/BoundOrigin and fires
            // OnReload() BEFORE destinations provider 2 rotates A -> B): zero (destination A, credential B) leakage!
            using var enteredDestLoad = new ManualResetEventSlim(false);
            using var releaseDestLoad = new ManualResetEventSlim(false);

            var boundCredsProvider = new BarrierReloadConfigurationProvider(
                CredDict("a", "relay-a.example.test:587:StartTls", urlA),
                fireOnReloadInsideLoad: true);
            var boundDestProvider = new BarrierReloadConfigurationProvider(
                DestDict("relay-a.example.test", urlA),
                onLoadStarted: enteredDestLoad,
                waitBeforeApplyingLoad: releaseDestLoad,
                fireOnReloadInsideLoad: true);
            var reverseRoot = new ConfigurationRoot([boundCredsProvider, boundDestProvider]);
            using var reverseProvider = new ConfigurationOperatorNotificationConfigProvider(reverseRoot, loopbackPolicy);

            var revInit = await reverseProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("token-a", revInit.Secrets.WebhookAuthToken);
            Assert.AreEqual("pass-a", revInit.Secrets.SmtpPassword);

            boundCredsProvider.StageNext(CredDict("b", "relay-b.example.test:587:StartTls", urlB));
            boundDestProvider.StageNext(DestDict("relay-b.example.test", urlB), armBarrier: true);

            var reverseReload = Task.Run(() => reverseRoot.Reload());
            try
            {
                Assert.IsTrue(enteredDestLoad.Wait(TimeSpan.FromSeconds(10)));
                var revMid = await reverseProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("relay-a.example.test", revMid.Options.Smtp.Host);
                Assert.IsNull(revMid.Secrets.SmtpUsername, "Explicit BoundEndpoint must prevent cred B from pairing with destination A.");
                Assert.IsNull(revMid.Secrets.SmtpPassword, "Explicit BoundEndpoint must prevent cred B from pairing with destination A.");
                Assert.AreEqual(urlA, revMid.Secrets.WebhookUrl);
                Assert.IsNull(revMid.Secrets.WebhookAuthToken, "Explicit BoundOrigin must prevent token B from pairing with origin A.");
            }
            finally
            {
                releaseDestLoad.Set();
            }

            await reverseReload.ConfigureAwait(false);

            var revFinal = await reverseProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-b.example.test", revFinal.Options.Smtp.Host);
            Assert.AreEqual("user-b", revFinal.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", revFinal.Secrets.SmtpPassword);
            Assert.AreEqual(urlB, revFinal.Secrets.WebhookUrl);
            Assert.AreEqual("token-b", revFinal.Secrets.WebhookAuthToken);
        }
    }

    [TestMethod]
    public async Task Constructor_WhenReloadTokenSignalsSynchronouslyDuringSubscription_DoesNotThrowNullReferenceExceptionAsync()
    {
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        var config = new SynchronouslySignalingConfiguration(
            initialValues: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "false"
            },
            reloadedValues: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://hooks.example.test/alerts"
            });

        using var store = new OperatorNotificationStore(
            dpProvider,
            Options.Create(new DeviceProvisioningOptions { StateDirectory = dir.Path }),
            config,
            new OperatorNotificationNetworkPolicy(isDevelopment: false),
            NullLogger<OperatorNotificationStore>.Instance);

        Assert.IsTrue(config.SynchronousCallbackFired,
            "Test harness must synchronously fire the reload callback during subscription inside construction.");
        Assert.IsTrue(store.CurrentOptions.Enabled,
            "Store must observe the reloaded host configuration without throwing NullReferenceException.");
        Assert.IsNull(store.ConfigurationErrorReasonCode);

        // Subsequent runtime reload after construction continues updating CurrentOptions cleanly.
        config.TriggerReload(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "false"
        });
        Assert.IsFalse(store.CurrentOptions.Enabled);
        var snap = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(snap.Options.Enabled);
    }

    [TestMethod]
    public async Task CurrentOptionsAndSnapshots_ReturnIsolatedDefensiveCopies_MutatingRecipientsCannotAffectCachedStateOrDeliveriesAsync()
    {
        using var dir = TempStateDir.Create();
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":TestSendMinimumIntervalSeconds"] = "0",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "smtp.example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "smtp-user",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "smtp-pass"
            })
            .Build();

        // 1. ConfigurationOperatorNotificationConfigProvider: mutating CurrentOptions / Snapshot / Effective does not leak.
        using var configProvider = new ConfigurationOperatorNotificationConfigProvider(hostConfig);
        configProvider.CurrentOptions.Smtp.Recipients.Add("evil@example.com");
        CollectionAssert.AreEqual(ExpectedOpsRecipients, configProvider.CurrentOptions.Smtp.Recipients);

        var configSnap = await configProvider.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        configSnap.Options.Smtp.Recipients.Add("evil-snap@example.com");
        CollectionAssert.AreEqual(ExpectedOpsRecipients, configProvider.CurrentOptions.Smtp.Recipients);

        var configEff = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        configEff.Options.Smtp.Recipients.Add("evil-eff@example.com");
        CollectionAssert.AreEqual(ExpectedOpsRecipients, configProvider.CurrentOptions.Smtp.Recipients);

        // Verify actual SMTP delivery through OperatorNotificationDispatcher never observes mutated recipients.
        var capturingSmtp = new CapturingSmtpTransport();
        using var telemetry = new OperatorNotificationTelemetry();
        using var configDispatcher = new OperatorNotificationDispatcher(
            configProvider,
            capturingSmtp,
            new WebhookNotificationTransport(new OperatorNotificationNetworkPolicy(isDevelopment: false), TimeProvider.System),
            telemetry,
            TimeProvider.System);

        var configTestResult = await configDispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(configTestResult.Channels[0].Succeeded, configTestResult.Channels[0].ReasonCode);
        Assert.HasCount(1, capturingSmtp.Captured);
        CollectionAssert.AreEqual(ExpectedOpsRecipients, capturingSmtp.Captured[0].Recipients.ToArray());

        // 2. OperatorNotificationStore (both host-absent and saved-override paths): mutating returned Recipients does not leak.
        using var store = CreateStore(dir.Path, isDevelopment: false, configuration: hostConfig);
        store.CurrentOptions.Smtp.Recipients.Add("evil@example.com");
        CollectionAssert.AreEqual(ExpectedOpsRecipients, store.CurrentOptions.Smtp.Recipients);

        var initialSnap = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var saved = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            initialSnap.Version,
            CreateSmtpOptions("smtp.example.test", 587, SmtpSecurityMode.StartTls),
            SmtpUsername: "saved-user",
            SmtpPassword: "saved-pass"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved.Outcome, saved.Message);

        saved.Snapshot!.Options.Smtp.Recipients.Add("evil-saved@example.com");
        store.CurrentOptions.Smtp.Recipients.Add("evil-current@example.com");
        CollectionAssert.AreEqual(ExpectedOpsRecipients, store.CurrentOptions.Smtp.Recipients);

        using var storeDispatcher = new OperatorNotificationDispatcher(
            store,
            capturingSmtp,
            new WebhookNotificationTransport(new OperatorNotificationNetworkPolicy(isDevelopment: false), TimeProvider.System),
            telemetry,
            TimeProvider.System);
        var storeTestResult = await storeDispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(storeTestResult.Channels[0].Succeeded, storeTestResult.Channels[0].ReasonCode);
        Assert.HasCount(2, capturingSmtp.Captured);
        CollectionAssert.AreEqual(ExpectedOpsRecipients, capturingSmtp.Captured[1].Recipients.ToArray());
    }

    [TestMethod]
    public async Task CorruptStoreFile_IgnoresSubsequentHostConfigurationReloadsAsync()
    {
        using var dir = TempStateDir.Create();
        await File.WriteAllTextAsync(
            Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName),
            "{corrupt-not-valid-json").ConfigureAwait(false);

        var configProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "false"
        });
        var hostConfig = new ConfigurationRoot([configProvider]);

        using var store = CreateStore(dir.Path, isDevelopment: false, configuration: hostConfig);
        Assert.IsFalse(store.CurrentOptions.Enabled);
        Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, store.ConfigurationErrorReasonCode);

        // Reloading host configuration to valid enabled settings MUST be ignored while the persisted store is corrupt.
        configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://hooks.example.test/alerts",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "host-secret-token"
        });

        Assert.IsFalse(store.CurrentOptions.Enabled, "Corrupt store MUST ignore host reload to Enabled=true.");
        Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, store.ConfigurationErrorReasonCode,
            "Corrupt store MUST keep StoreCorrupt reason code across host reloads.");

        // Reloading host configuration to invalid or disabled settings MUST STILL preserve StoreCorrupt.
        configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true"
        });
        Assert.IsFalse(store.CurrentOptions.Enabled);
        Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, store.ConfigurationErrorReasonCode);

        configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "false"
        });
        Assert.IsFalse(store.CurrentOptions.Enabled);
        Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, store.ConfigurationErrorReasonCode);

        var snap = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(snap.StoreCorrupt);
        Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, snap.StoreStatusReasonCode);
        Assert.IsFalse(snap.Secrets.HasWebhookUrl);
        Assert.IsFalse(snap.Secrets.HasWebhookAuthToken);

        var effective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(effective.StoreCorrupt);
        Assert.AreEqual(NotificationReasonCodes.StoreCorrupt, effective.ConfigurationErrorReasonCode);
        Assert.IsFalse(effective.Options.Enabled);
        Assert.IsTrue(effective.Secrets.IsEmpty);
    }

    [TestMethod]
    public void EnsureRestrictedDirectory_PinnedParentDescriptorFchmod_HardensMultiLevelAndRejectsNestedSymlinks()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes are not applicable on Windows.");
            return;
        }

        RequireModesEnforced();
        using var dir = TempStateDir.Create();

        const UnixFileMode permissive =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        const UnixFileMode restricted =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        // 1. Multi-level newly created directories are created and hardened component-by-component to 0700
        //    without altering the pre-existing anchor parent's permissions.
        var preExistingAnchor = Path.Combine(dir.Path, "anchor");
        Directory.CreateDirectory(preExistingAnchor);
        File.SetUnixFileMode(preExistingAnchor, permissive);

        var nestedLeaf = Path.Combine(preExistingAnchor, "sub1", "sub2", "state");
        NotificationStateFilePublisher.EnsureRestrictedDirectory(nestedLeaf);

        Assert.AreEqual(permissive, File.GetUnixFileMode(preExistingAnchor));
        Assert.AreEqual(restricted, File.GetUnixFileMode(Path.Combine(preExistingAnchor, "sub1")));
        Assert.AreEqual(restricted, File.GetUnixFileMode(Path.Combine(preExistingAnchor, "sub1", "sub2")));
        Assert.AreEqual(restricted, File.GetUnixFileMode(nestedLeaf));

        // 2. Pre-existing permissive 0755 leaf directory is tightened to 0700 via descriptor-based fchmod.
        var existingPermissive = Path.Combine(dir.Path, "existing-loose");
        Directory.CreateDirectory(existingPermissive);
        File.SetUnixFileMode(existingPermissive, permissive);
        NotificationStateFilePublisher.EnsureRestrictedDirectory(existingPermissive);
        Assert.AreEqual(restricted, File.GetUnixFileMode(existingPermissive));

        // 3. Nested symlink leaf is rejected via O_NOFOLLOW without altering target permissions.
        var target = Path.Combine(dir.Path, "nested-symlink-target");
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(target, permissive);
        var holder = Path.Combine(dir.Path, "holder");
        Directory.CreateDirectory(holder);
        var linkLeaf = Path.Combine(holder, "state");
        Directory.CreateSymbolicLink(linkLeaf, target);

        Assert.ThrowsExactly<IOException>(() => NotificationStateFilePublisher.EnsureRestrictedDirectory(linkLeaf));
        Assert.AreEqual(permissive, File.GetUnixFileMode(target));
    }

    [TestMethod]
    public async Task DirectSecretStoreSave_AfterSettingsSaveAndFreshDestinationBCredential_RejectsStaleTokenAAndPreservesFreshTokenBAsync()
    {
        using var dir = TempStateDir.Create();
        using var cert = WebhookNotificationTransportTests.CreateEphemeralLoopbackCertificate();
        var serverA = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        var serverB = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await using (serverA.ConfigureAwait(false))
        await using (serverB.ConfigureAwait(false))
        {
            var loopbackPolicy = new OperatorNotificationNetworkPolicy(isDevelopment: false)
            {
                AllowLoopbackForTesting = true
            };
            var urlA = $"https://127.0.0.1:{serverA.Port}/hook-a";
            var urlB = $"https://127.0.0.1:{serverB.Port}/hook-b";

            var configProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
            });
            var hostConfig = new ConfigurationRoot([configProvider]);
            var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
            using var store = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = dir.Path }),
                hostConfig,
                loopbackPolicy,
                NullLogger<OperatorNotificationStore>.Instance);
            using var webhookTransport = new WebhookNotificationTransport(loopbackPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, cert.Thumbprint, StringComparison.Ordinal)
            };
            var capturingSmtp = new CapturingSmtpTransport();
            using var telemetry = new OperatorNotificationTelemetry();
            using var dispatcher = new OperatorNotificationDispatcher(
                store,
                capturingSmtp,
                webhookTransport,
                telemetry,
                TimeProvider.System);

            var optionsOnRelayA = new OperatorNotificationsOptions
            {
                Enabled = true,
                TestSendMinimumIntervalSeconds = 0,
                Smtp = new SmtpNotificationOptions
                {
                    Enabled = true,
                    Host = "relay-a.example.test",
                    Port = 587,
                    SecurityMode = SmtpSecurityMode.StartTls,
                    FromAddress = "camera@example.test",
                    Recipients = ["ops@example.test"]
                },
                Webhook = new WebhookNotificationOptions
                {
                    Enabled = true,
                    Format = WebhookPayloadFormat.Generic,
                    AuthHeaderName = "Authorization"
                }
            };

            // 1. Save SMTP + webhook token A bound to origin A, then cache both provenance-preserving and manual copies.
            var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var savedInitial = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                initial.Version,
                optionsOnRelayA,
                SmtpUsername: "smtp-user-a",
                SmtpPassword: "smtp-pass-a",
                WebhookAuthToken: "origin-a-secret-token"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedInitial.Outcome, savedInitial.Message);

            var cachedPreRotate = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("origin-a-secret-token", cachedPreRotate.WebhookAuthToken);
            Assert.IsNotNull(cachedPreRotate.Provenance);

            var manualCopyWithoutProvenance = new OperatorNotificationSecrets(
                SmtpUsername: "smtp-user-manual-retry",
                SmtpPassword: cachedPreRotate.SmtpPassword,
                WebhookUrl: cachedPreRotate.WebhookUrl,
                WebhookAuthToken: cachedPreRotate.WebhookAuthToken,
                WebhookUserKey: cachedPreRotate.WebhookUserKey,
                WebhookNtfyTopic: cachedPreRotate.WebhookNtfyTopic);
            Assert.IsNull(manualCopyWithoutProvenance.Provenance);

            // 2. Host rotates Webhook:Url A -> B.
            configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
            });

            // 3. Intervening UNRELATED settings save via OperatorNotificationsUpdateRequest MUST preserve/record retired token A.
            var snapOnB = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var unrelatedOptions = new OperatorNotificationsOptions
            {
                Enabled = true,
                TestSendMinimumIntervalSeconds = 0,
                EvaluationIntervalSeconds = 45,
                Smtp = optionsOnRelayA.Smtp,
                Webhook = optionsOnRelayA.Webhook,
                Events = new OperatorNotificationEventsOptions
                {
                    DailyHeartbeatEnabled = true
                }
            };
            var savedUnrelated = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snapOnB.Version,
                unrelatedOptions), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedUnrelated.Outcome, savedUnrelated.Message);

            // 4. Fresh destination-B credential save MUST preserve retired token A tombstone (never clear it).
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                new OperatorNotificationSecrets(
                    SmtpUsername: "smtp-user-a",
                    SmtpPassword: "smtp-pass-a",
                    WebhookAuthToken: "fresh-origin-b-token"),
                CancellationToken.None).ConfigureAwait(false);

            var effAfterFreshB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("fresh-origin-b-token", effAfterFreshB.Secrets.WebhookAuthToken);

            // 5. Astra H1: Retry the manual field-by-field copy (NO provenance) carrying stale token A.
            //    Token A MUST be rejected/stripped AND B's fresh token MUST remain intact!
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                manualCopyWithoutProvenance,
                CancellationToken.None).ConfigureAwait(false);

            var effAfterManualRetry = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("smtp-user-manual-retry", effAfterManualRetry.Secrets.SmtpUsername);
            Assert.AreEqual("fresh-origin-b-token", effAfterManualRetry.Secrets.WebhookAuthToken,
                "Field-by-field stale retry after settings save + fresh B save MUST reject token A and preserve B's fresh token.");

            var deliveryAfterManual = await dispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(deliveryAfterManual.Channels[0].Succeeded, deliveryAfterManual.Channels[0].ReasonCode);
            Assert.HasCount(1, serverB.Captured);
            Assert.AreEqual("Bearer fresh-origin-b-token", serverB.Captured[0].Header("Authorization"));

            // 6. Astra M1: Retry the provenance-preserving SMTP-only update cached before rotation.
            //    It MUST apply the SMTP change while keeping B's fresh webhook token intact (never overwriting it with null)!
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                cachedPreRotate with { SmtpUsername = "smtp-user-prov-retry" },
                CancellationToken.None).ConfigureAwait(false);

            var effAfterProvRetry = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("smtp-user-prov-retry", effAfterProvRetry.Secrets.SmtpUsername);
            Assert.AreEqual("fresh-origin-b-token", effAfterProvRetry.Secrets.WebhookAuthToken,
                "Provenance-preserving stale SMTP-only retry MUST NOT erase B's fresh webhook token.");

            var deliveryAfterProv = await dispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(deliveryAfterProv.Channels[0].Succeeded, deliveryAfterProv.Channels[0].ReasonCode);
            Assert.HasCount(2, serverB.Captured);
            Assert.AreEqual("Bearer fresh-origin-b-token", serverB.Captured[1].Header("Authorization"));

            // 7. Symmetric SMTP protection (H1 + M1): rotate SMTP relay A -> B, save fresh SMTP B credentials,
            //    and verify BOTH provenance-preserving and manual webhook-only retries preserve SMTP B credentials intact!
            var cachedBeforeSmtpRotate = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("smtp-user-prov-retry", cachedBeforeSmtpRotate.SmtpUsername);

            var snapBeforeSmtpRotate = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var optionsOnRelayB = new OperatorNotificationsOptions
            {
                Enabled = true,
                TestSendMinimumIntervalSeconds = 0,
                EvaluationIntervalSeconds = 45,
                Smtp = new SmtpNotificationOptions
                {
                    Enabled = true,
                    Host = "relay-b.example.test",
                    Port = 587,
                    SecurityMode = SmtpSecurityMode.StartTls,
                    FromAddress = "camera@example.test",
                    Recipients = ["ops@example.test"]
                },
                Webhook = optionsOnRelayA.Webhook,
                Events = new OperatorNotificationEventsOptions
                {
                    DailyHeartbeatEnabled = true
                }
            };
            var savedRelayB = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snapBeforeSmtpRotate.Version,
                optionsOnRelayB,
                SmtpUsername: "smtp-user-b",
                SmtpPassword: "smtp-pass-b"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedRelayB.Outcome, savedRelayB.Message);

            // Provenance-preserving webhook-only retry from pre-SMTP-rotation read preserves SMTP B credentials.
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                cachedBeforeSmtpRotate with { WebhookAuthToken = "fresh-origin-b-token-v2" },
                CancellationToken.None).ConfigureAwait(false);

            var effAfterWebhookProvRetry = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("fresh-origin-b-token-v2", effAfterWebhookProvRetry.Secrets.WebhookAuthToken);
            Assert.AreEqual("smtp-user-b", effAfterWebhookProvRetry.Secrets.SmtpUsername,
                "Provenance-preserving stale webhook-only retry MUST NOT erase relay B's fresh SMTP username.");
            Assert.AreEqual("smtp-pass-b", effAfterWebhookProvRetry.Secrets.SmtpPassword,
                "Provenance-preserving stale webhook-only retry MUST NOT erase relay B's fresh SMTP password.");

            // Manual field-by-field copy without provenance carrying retired SMTP A credentials also preserves SMTP B.
            var manualSmtpStaleCopy = new OperatorNotificationSecrets(
                SmtpUsername: cachedBeforeSmtpRotate.SmtpUsername,
                SmtpPassword: cachedBeforeSmtpRotate.SmtpPassword,
                WebhookUrl: cachedBeforeSmtpRotate.WebhookUrl,
                WebhookAuthToken: "fresh-origin-b-token-v3");
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                manualSmtpStaleCopy,
                CancellationToken.None).ConfigureAwait(false);

            var effAfterManualSmtpRetry = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("fresh-origin-b-token-v3", effAfterManualSmtpRetry.Secrets.WebhookAuthToken);
            Assert.AreEqual("smtp-user-b", effAfterManualSmtpRetry.Secrets.SmtpUsername);
            Assert.AreEqual("smtp-pass-b", effAfterManualSmtpRetry.Secrets.SmtpPassword);
        }
    }

    [TestMethod]
    public async Task CrossProviderExplicitBinding_FromDestinationOrOtherProvider_NeverAuthorizesUnboundCredentialProviderAsync()
    {
        const string urlA = "https://origin-a.example.test/hook";
        const string urlB = "https://origin-b.example.test/hook";

        // Part A: Destination provider updates BOTH Webhook:Url -> B AND Webhook:BoundOrigin -> B
        // while the credential provider STILL holds token A (with NO BoundOrigin of its own).
        // Destination provider's BoundOrigin MUST NOT authorize credential provider's stale token A!
        var destWebhookProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA,
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlA
        });
        var credsWebhookProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "token-a"
        });
        var webhookRoot = new ConfigurationRoot([destWebhookProvider, credsWebhookProvider]);
        using var webhookDir = TempStateDir.Create();
        using var webhookConfigProvider = new ConfigurationOperatorNotificationConfigProvider(webhookRoot);
        using var webhookStore = CreateStore(webhookDir.Path, isDevelopment: false, configuration: webhookRoot);

        var initWebhook = await webhookConfigProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlA, initWebhook.Secrets.WebhookUrl);
        Assert.AreEqual("token-a", initWebhook.Secrets.WebhookAuthToken);

        destWebhookProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB,
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlB
        });

        var afterDestBoundRotateConfig = await webhookConfigProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlB, afterDestBoundRotateConfig.Secrets.WebhookUrl);
        Assert.IsNull(afterDestBoundRotateConfig.Secrets.WebhookAuthToken,
            "Destination provider's BoundOrigin MUST NOT authorize a stale token supplied by a different provider.");

        var afterDestBoundRotateStore = await webhookStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlB, afterDestBoundRotateStore.Secrets.WebhookUrl);
        Assert.IsNull(afterDestBoundRotateStore.Secrets.WebhookAuthToken);

        // Even an extra root reload notification MUST keep token-a suppressed.
        webhookRoot.Reload();
        var afterExtraReload = await webhookConfigProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(afterExtraReload.Secrets.WebhookAuthToken);

        // Once the credential provider ITSELF carries BoundOrigin = urlB, its credential is authorized cleanly.
        credsWebhookProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "token-a",
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlB
        });
        var afterSameProviderBinding = await webhookConfigProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("token-a", afterSameProviderBinding.Secrets.WebhookAuthToken);

        // Part B: Symmetric SMTP test — destination provider updates Smtp:Host -> relay-b AND Smtp:BoundEndpoint -> relay-b
        // while the credential provider STILL holds user-a/pass-a without its own BoundEndpoint.
        var destSmtpProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-a.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-a.example.test:587:StartTls"
        });
        var credsSmtpProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "user-a",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "pass-a"
        });
        var smtpRoot = new ConfigurationRoot([destSmtpProvider, credsSmtpProvider]);
        using var smtpConfigProvider = new ConfigurationOperatorNotificationConfigProvider(smtpRoot);

        var initSmtp = await smtpConfigProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("user-a", initSmtp.Secrets.SmtpUsername);
        Assert.AreEqual("pass-a", initSmtp.Secrets.SmtpPassword);

        destSmtpProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-b.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-b.example.test:587:StartTls"
        });

        var afterSmtpDestBoundRotate = await smtpConfigProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-b.example.test", afterSmtpDestBoundRotate.Options.Smtp.Host);
        Assert.IsNull(afterSmtpDestBoundRotate.Secrets.SmtpUsername,
            "Destination provider's Smtp:BoundEndpoint MUST NOT authorize a stale username from another provider.");
        Assert.IsNull(afterSmtpDestBoundRotate.Secrets.SmtpPassword,
            "Destination provider's Smtp:BoundEndpoint MUST NOT authorize a stale password from another provider.");

        // Same-provider BoundEndpoint on credsSmtpProvider authorizes cleanly.
        credsSmtpProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "user-a",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "pass-a",
            [ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-b.example.test:587:StartTls"
        });
        var afterSmtpSameProviderBinding = await smtpConfigProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("user-a", afterSmtpSameProviderBinding.Secrets.SmtpUsername);
        Assert.AreEqual("pass-a", afterSmtpSameProviderBinding.Secrets.SmtpPassword);

        // Part C: One credential provider's BoundOrigin MUST NOT authorize a sibling credential supplied by a different provider.
        var splitDest = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Ntfy",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
        });
        var splitToken = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "token-a"
        });
        var splitTopic = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = "topic-a"
        });
        var splitRoot = new ConfigurationRoot([splitDest, splitToken, splitTopic]);
        using var splitProvider = new ConfigurationOperatorNotificationConfigProvider(splitRoot);

        var splitInit = await splitProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("token-a", splitInit.Secrets.WebhookAuthToken);
        Assert.AreEqual("topic-a", splitInit.Secrets.WebhookNtfyTopic);

        splitDest.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Ntfy",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
        });
        splitToken.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "token-b",
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlB
        });

        var splitAfterTokenBind = await splitProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("token-b", splitAfterTokenBind.Secrets.WebhookAuthToken);
        Assert.IsNull(splitAfterTokenBind.Secrets.WebhookNtfyTopic,
            "Token provider's BoundOrigin MUST NOT authorize an unbound stale topic from a different provider.");
    }

    [TestMethod]
    public async Task CrossProviderSplitSiblingCredentials_RotatingOneFieldNeverReleasesUnchangedSiblingAcrossSubsequentCallbacksAsync()
    {
        const string urlA = "https://ntfy-a.example.test";
        const string urlB = "https://ntfy-b.example.test";
        const string urlC = "https://ntfy-c.example.test";

        // Part A: Webhook AuthToken + NtfyTopic split across separate providers across multiple reload callbacks.
        var webhookDest = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Ntfy",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
        });
        var webhookTokenProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "token-a"
        });
        var webhookTopicProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = "topic-a"
        });
        var unrelatedProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Unrelated:Counter"] = "0"
        });

        var webhookRoot = new ConfigurationRoot([webhookDest, webhookTokenProv, webhookTopicProv, unrelatedProv]);
        using var webhookProvider = new ConfigurationOperatorNotificationConfigProvider(webhookRoot);

        var initWebhook = await webhookProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("token-a", initWebhook.Secrets.WebhookAuthToken);
        Assert.AreEqual("topic-a", initWebhook.Secrets.WebhookNtfyTopic);

        // Callback 1: destination rotates A -> B; both unchanged token-a and topic-a are suppressed.
        webhookDest.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Ntfy",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
        });
        var afterDestRotate = await webhookProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(afterDestRotate.Secrets.WebhookAuthToken);
        Assert.IsNull(afterDestRotate.Secrets.WebhookNtfyTopic);

        // Callback 2: token rotates token-a -> token-b while topic STILL holds topic-a.
        webhookTokenProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "token-b"
        });
        var afterTokenOnlyRotate = await webhookProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("token-b", afterTokenOnlyRotate.Secrets.WebhookAuthToken);
        Assert.IsNull(afterTokenOnlyRotate.Secrets.WebhookNtfyTopic,
            "Rotating token MUST NOT release unchanged sibling topic-a on callback 2.");

        // Callbacks 3 & 4: subsequent root reload and unrelated provider reload MUST STILL keep topic-a suppressed!
        webhookRoot.Reload();
        var afterRootReload = await webhookProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("token-b", afterRootReload.Secrets.WebhookAuthToken);
        Assert.IsNull(afterRootReload.Secrets.WebhookNtfyTopic,
            "Subsequent root reload callback MUST NEVER release unchanged stale sibling topic-a.");

        unrelatedProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Unrelated:Counter"] = "1"
        });
        var afterUnrelatedReload = await webhookProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("token-b", afterUnrelatedReload.Secrets.WebhookAuthToken);
        Assert.IsNull(afterUnrelatedReload.Secrets.WebhookNtfyTopic,
            "Unrelated provider callback MUST NEVER release unchanged stale sibling topic-a.");

        // Callback 5: once topic itself rotates to topic-b, BOTH token-b and topic-b publish cleanly.
        webhookTopicProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = "topic-b"
        });
        var afterTopicRotate = await webhookProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("token-b", afterTopicRotate.Secrets.WebhookAuthToken);
        Assert.AreEqual("topic-b", afterTopicRotate.Secrets.WebhookNtfyTopic);

        // Also verify same-provider explicit binding releases an intentionally reused topic on origin C.
        webhookDest.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Ntfy",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlC
        });
        webhookTokenProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "token-c"
        });
        webhookRoot.Reload();
        var onCBeforeExplicit = await webhookProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("token-c", onCBeforeExplicit.Secrets.WebhookAuthToken);
        Assert.IsNull(onCBeforeExplicit.Secrets.WebhookNtfyTopic);

        webhookTopicProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = "topic-b",
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlC
        });
        var onCAfterExplicit = await webhookProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("token-c", onCAfterExplicit.Secrets.WebhookAuthToken);
        Assert.AreEqual("topic-b", onCAfterExplicit.Secrets.WebhookNtfyTopic);

        // Part B: SMTP Username + Password split across separate providers across multiple reload callbacks.
        var smtpDest = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-a.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test"
        });
        var smtpUserProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "user-a"
        });
        var smtpPassProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "pass-a"
        });
        var smtpUnrelatedProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Unrelated:Step"] = "0"
        });

        var smtpRoot = new ConfigurationRoot([smtpDest, smtpUserProv, smtpPassProv, smtpUnrelatedProv]);
        using var smtpProvider = new ConfigurationOperatorNotificationConfigProvider(smtpRoot);

        var initSmtp = await smtpProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("user-a", initSmtp.Secrets.SmtpUsername);
        Assert.AreEqual("pass-a", initSmtp.Secrets.SmtpPassword);

        smtpDest.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-b.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test"
        });
        smtpUserProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "user-b"
        });

        var afterUserOnlyRotate = await smtpProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("user-b", afterUserOnlyRotate.Secrets.SmtpUsername);
        Assert.IsNull(afterUserOnlyRotate.Secrets.SmtpPassword,
            "Rotating SMTP username MUST NOT release unchanged sibling password pass-a.");

        smtpRoot.Reload();
        smtpUnrelatedProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Unrelated:Step"] = "1"
        });
        var afterSmtpFollowupCallbacks = await smtpProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("user-b", afterSmtpFollowupCallbacks.Secrets.SmtpUsername);
        Assert.IsNull(afterSmtpFollowupCallbacks.Secrets.SmtpPassword,
            "Follow-up reload callbacks MUST NEVER release unchanged stale sibling password pass-a.");

        smtpPassProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "pass-b"
        });
        var afterPassRotate = await smtpProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("user-b", afterPassRotate.Secrets.SmtpUsername);
        Assert.AreEqual("pass-b", afterPassRotate.Secrets.SmtpPassword);
    }

    [TestMethod]
    public async Task LongLivedTrackerFullMultiProviderRotation_BothOrdersConvergeCleanlyWithoutHostRestartOrCrossPairingAsync()
    {
        using var dir = TempStateDir.Create();
        using var cert = WebhookNotificationTransportTests.CreateEphemeralLoopbackCertificate();
        var serverA = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        var serverB = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await using (serverA.ConfigureAwait(false))
        await using (serverB.ConfigureAwait(false))
        {
            var loopbackPolicy = new OperatorNotificationNetworkPolicy(isDevelopment: false)
            {
                AllowLoopbackForTesting = true
            };
            var urlA = $"https://127.0.0.1:{serverA.Port}/hook-a";
            var urlB = $"https://127.0.0.1:{serverB.Port}/hook-b";

            static Dictionary<string, string?> DestDict(string relayHost, string webhookUrl) => new(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":TestSendMinimumIntervalSeconds"] = "0",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = relayHost,
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Generic",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:AuthHeaderName"] = "Authorization",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = webhookUrl
            };

            static Dictionary<string, string?> CredDict(string tag) => new(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = $"user-{tag}",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = $"pass-{tag}",
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = $"token-{tag}"
            };

            // Part A: UNBOUND CREDENTIAL-FIRST order (credsProvider loads & fires OnReload() BEFORE destProvider).
            // Verifies ZERO (A, B) leak mid-reload AND full post-rotation convergence to (relay-b, user-b, pass-b, urlB, token-b)
            // on BOTH SMTP and Webhook delivery paths WITHOUT host restart!
            using var enteredDestLoad = new ManualResetEventSlim(false);
            using var releaseDestLoad = new ManualResetEventSlim(false);

            var credFirstCreds = new BarrierReloadConfigurationProvider(CredDict("a"), fireOnReloadInsideLoad: true);
            var credFirstDest = new BarrierReloadConfigurationProvider(
                DestDict("relay-a.example.test", urlA),
                onLoadStarted: enteredDestLoad,
                waitBeforeApplyingLoad: releaseDestLoad,
                fireOnReloadInsideLoad: true);
            var credFirstRoot = new ConfigurationRoot([credFirstCreds, credFirstDest]);

            using var credFirstDir = TempStateDir.Create();
            using var credFirstConfig = new ConfigurationOperatorNotificationConfigProvider(credFirstRoot, loopbackPolicy);
            var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
            using var credFirstStore = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = credFirstDir.Path }),
                credFirstRoot,
                loopbackPolicy,
                NullLogger<OperatorNotificationStore>.Instance);
            var credFirstSmtp = new CapturingSmtpTransport();
            using var webhookTransport = new WebhookNotificationTransport(loopbackPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, cert.Thumbprint, StringComparison.Ordinal)
            };
            using var telemetryA = new OperatorNotificationTelemetry();
            using var credFirstDispatcher = new OperatorNotificationDispatcher(
                credFirstStore,
                credFirstSmtp,
                webhookTransport,
                telemetryA,
                TimeProvider.System);

            var initDeliveryA = await credFirstDispatcher.SendTestAsync(NotificationTestTarget.AllEnabled, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(initDeliveryA.Channels.All(c => c.Succeeded));
            Assert.HasCount(1, credFirstSmtp.Captured);
            Assert.AreEqual("relay-a.example.test", credFirstSmtp.Captured[0].Host);
            Assert.AreEqual("user-a", credFirstSmtp.Captured[0].Username);
            Assert.AreEqual("pass-a", credFirstSmtp.Captured[0].Password);
            Assert.HasCount(1, serverA.Captured);
            Assert.AreEqual("Bearer token-a", serverA.Captured[0].Header("Authorization"));

            credFirstCreds.StageNext(CredDict("b"));
            credFirstDest.StageNext(DestDict("relay-b.example.test", urlB), armBarrier: true);

            var credFirstReload = Task.Run(() => credFirstRoot.Reload());
            try
            {
                Assert.IsTrue(enteredDestLoad.Wait(TimeSpan.FromSeconds(10)), "Destination provider did not enter Load().");

                // Raw root is in torn (dest A, cred B) state.
                Assert.AreEqual("relay-a.example.test", credFirstRoot[OperatorNotificationsOptions.SectionKey + ":Smtp:Host"]);
                Assert.AreEqual("user-b", credFirstRoot[ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey]);
                Assert.AreEqual(urlA, credFirstRoot[ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey]);
                Assert.AreEqual("token-b", credFirstRoot[ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey]);

                // Unbound credential-first reload MUST NOT leak B's credentials to destination A!
                var midConfig = await credFirstConfig.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("relay-a.example.test", midConfig.Options.Smtp.Host);
                Assert.IsNull(midConfig.Secrets.SmtpUsername, "Unbound credential-first reload MUST NOT leak user-b to relay-a.");
                Assert.IsNull(midConfig.Secrets.SmtpPassword, "Unbound credential-first reload MUST NOT leak pass-b to relay-a.");
                Assert.AreEqual(urlA, midConfig.Secrets.WebhookUrl);
                Assert.IsNull(midConfig.Secrets.WebhookAuthToken, "Unbound credential-first reload MUST NOT leak token-b to origin A.");

                var midStore = await credFirstStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsNull(midStore.Secrets.SmtpUsername);
                Assert.IsNull(midStore.Secrets.SmtpPassword);
                Assert.IsNull(midStore.Secrets.WebhookAuthToken);

                var midDelivery = await credFirstDispatcher.SendTestAsync(NotificationTestTarget.AllEnabled, "owner-1", CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(midDelivery.Channels.All(c => c.Succeeded));
                Assert.HasCount(2, credFirstSmtp.Captured);
                Assert.AreEqual("relay-a.example.test", credFirstSmtp.Captured[1].Host);
                Assert.IsNull(credFirstSmtp.Captured[1].Username);
                Assert.IsNull(credFirstSmtp.Captured[1].Password);
                Assert.HasCount(2, serverA.Captured);
                Assert.IsNull(serverA.Captured[1].Header("Authorization"));
            }
            finally
            {
                releaseDestLoad.Set();
            }

            await credFirstReload.ConfigureAwait(false);

            // Post-rotation convergence: BOTH config provider, store, SMTP delivery, and Webhook delivery
            // MUST converge cleanly to (relay-b, user-b, pass-b, urlB, token-b) WITHOUT host restart!
            var postCredFirstConfig = await credFirstConfig.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(postCredFirstConfig.ConfigurationErrorReasonCode);
            Assert.AreEqual("relay-b.example.test", postCredFirstConfig.Options.Smtp.Host);
            Assert.AreEqual("user-b", postCredFirstConfig.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", postCredFirstConfig.Secrets.SmtpPassword);
            Assert.AreEqual(urlB, postCredFirstConfig.Secrets.WebhookUrl);
            Assert.AreEqual("token-b", postCredFirstConfig.Secrets.WebhookAuthToken);

            var postCredFirstStore = await credFirstStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(postCredFirstStore.ConfigurationErrorReasonCode);
            Assert.AreEqual("relay-b.example.test", postCredFirstStore.Options.Smtp.Host);
            Assert.AreEqual("user-b", postCredFirstStore.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", postCredFirstStore.Secrets.SmtpPassword);
            Assert.AreEqual(urlB, postCredFirstStore.Secrets.WebhookUrl);
            Assert.AreEqual("token-b", postCredFirstStore.Secrets.WebhookAuthToken);

            var postCredFirstDelivery = await credFirstDispatcher.SendTestAsync(NotificationTestTarget.AllEnabled, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(postCredFirstDelivery.Channels.All(c => c.Succeeded));
            Assert.HasCount(3, credFirstSmtp.Captured);
            Assert.AreEqual("relay-b.example.test", credFirstSmtp.Captured[2].Host);
            Assert.AreEqual("user-b", credFirstSmtp.Captured[2].Username);
            Assert.AreEqual("pass-b", credFirstSmtp.Captured[2].Password);
            Assert.HasCount(1, serverB.Captured);
            Assert.AreEqual("Bearer token-b", serverB.Captured[0].Header("Authorization"));

            // Part B: UNBOUND DESTINATION-FIRST order (destProvider loads & fires OnReload() BEFORE credsProvider).
            // Verifies ZERO (B, A) leak mid-reload AND full post-rotation convergence on BOTH SMTP and Webhook!
            using var enteredCredsLoad = new ManualResetEventSlim(false);
            using var releaseCredsLoad = new ManualResetEventSlim(false);

            var destFirstDest = new BarrierReloadConfigurationProvider(DestDict("relay-a.example.test", urlA), fireOnReloadInsideLoad: true);
            var destFirstCreds = new BarrierReloadConfigurationProvider(
                CredDict("a"),
                onLoadStarted: enteredCredsLoad,
                waitBeforeApplyingLoad: releaseCredsLoad,
                fireOnReloadInsideLoad: true);
            var destFirstRoot = new ConfigurationRoot([destFirstDest, destFirstCreds]);

            using var destFirstDir = TempStateDir.Create();
            using var destFirstStore = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = destFirstDir.Path }),
                destFirstRoot,
                loopbackPolicy,
                NullLogger<OperatorNotificationStore>.Instance);
            var destFirstSmtp = new CapturingSmtpTransport();
            using var telemetryB = new OperatorNotificationTelemetry();
            using var destFirstDispatcher = new OperatorNotificationDispatcher(
                destFirstStore,
                destFirstSmtp,
                webhookTransport,
                telemetryB,
                TimeProvider.System);

            destFirstDest.StageNext(DestDict("relay-b.example.test", urlB));
            destFirstCreds.StageNext(CredDict("b"), armBarrier: true);

            var destFirstReload = Task.Run(() => destFirstRoot.Reload());
            try
            {
                Assert.IsTrue(enteredCredsLoad.Wait(TimeSpan.FromSeconds(10)), "Credentials provider did not enter Load().");

                var midDestFirst = await destFirstDispatcher.SendTestAsync(NotificationTestTarget.AllEnabled, "owner-1", CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(midDestFirst.Channels.All(c => c.Succeeded));
                Assert.HasCount(1, destFirstSmtp.Captured);
                Assert.AreEqual("relay-b.example.test", destFirstSmtp.Captured[0].Host);
                Assert.IsNull(destFirstSmtp.Captured[0].Username, "Destination-first reload MUST NOT leak user-a to relay-b.");
                Assert.IsNull(destFirstSmtp.Captured[0].Password, "Destination-first reload MUST NOT leak pass-a to relay-b.");
                Assert.HasCount(2, serverB.Captured);
                Assert.IsNull(serverB.Captured[1].Header("Authorization"), "Destination-first reload MUST NOT leak token-a to origin B.");
            }
            finally
            {
                releaseCredsLoad.Set();
            }

            await destFirstReload.ConfigureAwait(false);

            var postDestFirstDelivery = await destFirstDispatcher.SendTestAsync(NotificationTestTarget.AllEnabled, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(postDestFirstDelivery.Channels.All(c => c.Succeeded));
            Assert.HasCount(2, destFirstSmtp.Captured);
            Assert.AreEqual("relay-b.example.test", destFirstSmtp.Captured[1].Host);
            Assert.AreEqual("user-b", destFirstSmtp.Captured[1].Username);
            Assert.AreEqual("pass-b", destFirstSmtp.Captured[1].Password);
            Assert.HasCount(3, serverB.Captured);
            Assert.AreEqual("Bearer token-b", serverB.Captured[2].Header("Authorization"));

            // Part C: Independent per-provider rotations (simulating separate file watchers with no root.Reload())
            // in credential-first order followed by a second rotation b -> c on the SAME long-lived tracker!
            var watcherCreds = new RotatingHostConfigurationProvider(CredDict("a"));
            var watcherDest = new RotatingHostConfigurationProvider(DestDict("relay-a.example.test", urlA));
            var watcherRoot = new ConfigurationRoot([watcherCreds, watcherDest]);
            using var watcherProvider = new ConfigurationOperatorNotificationConfigProvider(watcherRoot, loopbackPolicy);

            watcherCreds.Rotate(CredDict("b"));
            var watcherMid = await watcherProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-a.example.test", watcherMid.Options.Smtp.Host);
            Assert.IsNull(watcherMid.Secrets.SmtpUsername);
            Assert.IsNull(watcherMid.Secrets.SmtpPassword);
            Assert.IsNull(watcherMid.Secrets.WebhookAuthToken);

            watcherDest.Rotate(DestDict("relay-b.example.test", urlB));
            var watcherConvergedB = await watcherProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-b.example.test", watcherConvergedB.Options.Smtp.Host);
            Assert.AreEqual("user-b", watcherConvergedB.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", watcherConvergedB.Secrets.SmtpPassword);
            Assert.AreEqual(urlB, watcherConvergedB.Secrets.WebhookUrl);
            Assert.AreEqual("token-b", watcherConvergedB.Secrets.WebhookAuthToken);

            // Second consecutive rotation b -> c (credential-first) on the same long-lived tracker also converges cleanly.
            watcherCreds.Rotate(CredDict("c"));
            var watcherMidC = await watcherProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(watcherMidC.Secrets.SmtpUsername);
            Assert.IsNull(watcherMidC.Secrets.WebhookAuthToken);

            watcherDest.Rotate(DestDict("relay-c.example.test", "https://origin-c.example.test/hook"));
            var watcherConvergedC = await watcherProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-c.example.test", watcherConvergedC.Options.Smtp.Host);
            Assert.AreEqual("user-c", watcherConvergedC.Secrets.SmtpUsername);
            Assert.AreEqual("pass-c", watcherConvergedC.Secrets.SmtpPassword);
            Assert.AreEqual("https://origin-c.example.test/hook", watcherConvergedC.Secrets.WebhookUrl);
            Assert.AreEqual("token-c", watcherConvergedC.Secrets.WebhookAuthToken);
        }
    }

    [TestMethod]
    public void EnsureRestrictedDirectory_WhenNonSymlinkAnchorIsSwappedToSymlinkBeforeOpen_FailsClosedWithOnoFollow()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("POSIX O_DIRECTORY | O_NOFOLLOW anchor pinning is Unix-only.");
            return;
        }

        RequireModesEnforced();
        using var dir = TempStateDir.Create();

        const UnixFileMode permissive =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        var anchor = Path.Combine(dir.Path, "real-anchor");
        Directory.CreateDirectory(anchor);
        var outsideTarget = Path.Combine(dir.Path, "outside-target");
        Directory.CreateDirectory(outsideTarget);
        File.SetUnixFileMode(outsideTarget, permissive);

        var targetStateDir = Path.Combine(anchor, "state");

        // Deterministically swap the validated non-symlink anchor to a symlink immediately before anchor open.
        // Because NotificationStateFilePublisher MUST NOT re-probe anchorParent by pathname to disable O_NOFOLLOW,
        // DurableSync opens anchorParent with O_DIRECTORY | O_NOFOLLOW and rejects the swapped symlink fail-closed!
        Assert.ThrowsExactly<IOException>(() =>
            NotificationStateFilePublisher.EnsureRestrictedDirectory(
                targetStateDir,
                onDirectorySynced: null,
                directorySyncAction: null,
                beforeAnchorOpen: anchorPath =>
                {
                    Directory.Delete(anchorPath);
                    Directory.CreateSymbolicLink(anchorPath, outsideTarget);
                }));

        Assert.IsFalse(Directory.Exists(Path.Combine(outsideTarget, "state")),
            "Symlink substituted at a previously non-symlink anchor MUST NEVER create directories in the symlink target.");
        Assert.AreEqual(permissive, File.GetUnixFileMode(outsideTarget),
            "Symlink rejection MUST NEVER alter the symlink target's permissions.");
    }

    [TestMethod]
    public async Task SameDestinationCredentialRotationAndHighCardinalityChurn_RetireEarlierACredentialsAcrossStoreAndTrackerAsync()
    {
        // H1: Rotating A(tok1) -> A(tok2) -> B(tokB) (and SMTP A(u1/p1) -> A(u2/p2) -> B(uB/pB)) must retire tok1/u1/p1
        // on the same-destination rotation itself so a field-by-field retry carrying the EARLIER A credential is rejected
        // against B and B's credentials remain intact, even after >16 subsequent same-origin rotations on B.
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        const string urlA = "https://origin-a.example.test/hook";
        const string urlB = "https://origin-b.example.test/hook";

        var configProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-a.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
        });
        var hostConfig = new ConfigurationRoot([configProvider]);
        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);

        var optionsRelayA = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-a.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };
        var optionsRelayB = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-b.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };

        // 1. Save A(tok1, user1, pass1), then rotate IN PLACE on A -> A(tok2, user2, pass2).
        var s0 = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var s1 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s0.Version, optionsRelayA,
            SmtpUsername: "smtp-user-a1",
            SmtpPassword: "smtp-pass-a1",
            WebhookAuthToken: "webhook-tok-a1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, s1.Outcome, s1.Message);

        var s2 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s1.Snapshot!.Version, optionsRelayA,
            SmtpUsername: "smtp-user-a2",
            SmtpPassword: "smtp-pass-a2",
            WebhookAuthToken: "webhook-tok-a2"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, s2.Outcome, s2.Message);

        // Also test direct SecretStore in-place rotation A(tok2) -> A(tok3).
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            new OperatorNotificationSecrets(
                SmtpUsername: "smtp-user-a3",
                SmtpPassword: "smtp-pass-a3",
                WebhookAuthToken: "webhook-tok-a3"),
            CancellationToken.None).ConfigureAwait(false);

        // 2. Rotate BOTH destinations A -> B and save B's credentials.
        configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
        });
        var sBeforeB = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var sOnB = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            sBeforeB.Version, optionsRelayB,
            SmtpUsername: "smtp-user-b",
            SmtpPassword: "smtp-pass-b",
            WebhookAuthToken: "webhook-tok-b"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, sOnB.Outcome, sOnB.Message);

        // 3. Perform 24 same-origin rotations on B (> legacy 16-entry cap!) to verify B churn NEVER evicts A's earlier tombstones.
        var latestVersion = sOnB.Snapshot!.Version;
        for (var i = 1; i <= 24; i++)
        {
            var rotatedB = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                latestVersion, optionsRelayB,
                SmtpUsername: $"smtp-user-b-{i}",
                SmtpPassword: $"smtp-pass-b-{i}",
                WebhookAuthToken: $"webhook-tok-b-{i}"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, rotatedB.Outcome, rotatedB.Message);
            latestVersion = rotatedB.Snapshot!.Version;
        }

        // 4. Retry provenance-stripped field-by-field copies carrying EARLIER A credentials (tok1, user1/pass1 and tok2, user2/pass2).
        foreach (var (staleUser, stalePass, staleTok) in new[]
        {
            ("smtp-user-a1", "smtp-pass-a1", "webhook-tok-a1"),
            ("smtp-user-a2", "smtp-pass-a2", "webhook-tok-a2"),
            ("smtp-user-a3", "smtp-pass-a3", "webhook-tok-a3")
        })
        {
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                new OperatorNotificationSecrets(
                    SmtpUsername: staleUser,
                    SmtpPassword: stalePass,
                    WebhookAuthToken: staleTok),
                CancellationToken.None).ConfigureAwait(false);

            var eff = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("smtp-user-b-24", eff.Secrets.SmtpUsername, $"Earlier A username '{staleUser}' must NOT bind to B.");
            Assert.AreEqual("smtp-pass-b-24", eff.Secrets.SmtpPassword, $"Earlier A password '{stalePass}' must NOT bind to B.");
            Assert.AreEqual("webhook-tok-b-24", eff.Secrets.WebhookAuthToken, $"Earlier A webhook token '{staleTok}' must NOT bind to B.");
        }

        // 5. HostConfigurationSnapshotTracker H1: same-destination rotation A(tok1) -> A(tok2..tok25) -> B(tokB)
        //    must still reject an unbound provider surfacing earlier tok1 at B!
        var trackerDest = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
        });
        var trackerCreds = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "tracker-tok-a1",
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlA
        });
        var trackerRoot = new ConfigurationRoot([trackerDest, trackerCreds]);
        using var trackerProvider = new ConfigurationOperatorNotificationConfigProvider(trackerRoot);

        Assert.AreEqual("tracker-tok-a1", (await trackerProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false)).Secrets.WebhookAuthToken);

        for (var i = 2; i <= 25; i++)
        {
            trackerCreds.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = $"tracker-tok-a{i}",
                [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlA
            });
        }

        // Rotate destination A -> B and drop explicit binding while surfacing earlier A credential tracker-tok-a1.
        trackerDest.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
        });
        trackerCreds.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "tracker-tok-a1"
        });

        var effOnBWithStaleA1 = await trackerProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlB, effOnBWithStaleA1.Secrets.WebhookUrl);
        Assert.IsNull(effOnBWithStaleA1.Secrets.WebhookAuthToken,
            "Tracker must retire earlier A credential tracker-tok-a1 on same-destination rotations and reject it at B even after >16 rotations.");

        // After the intervening retired-credential callback consumed DestinationChangedWhileEmpty (H6), an unbound
        // credential at unchanged urlB MUST stay withheld; explicit same-provider BoundOrigin authorizes it cleanly!
        trackerCreds.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "tracker-tok-b"
        });
        var effOnBUnbound = await trackerProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(effOnBUnbound.Secrets.WebhookAuthToken,
            "Intervening retired-credential callback must consume DestinationChangedWhileEmpty and withhold a later unbound token at unchanged urlB.");

        trackerCreds.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "tracker-tok-b",
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlB
        });
        var effOnBFresh = await trackerProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("tracker-tok-b", effOnBFresh.Secrets.WebhookAuthToken);
    }

    [TestMethod]
    public async Task CopiedProtectedWebhookUrlReplay_NeverRevertsDestinationBToRetiredA_WhileExplicitRollbackStillWorksAsync()
    {
        // H2: A provenance-stripped field-by-field copy carrying a copied protected webhook URL A + retired token A
        // must NEVER roll rotated destination B back to A when retrying an SMTP update; intentional explicit rollback
        // to A still succeeds when backed by current-revision provenance or a fresh non-retired credential.
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        const string urlA = "https://protected-a.example.test/hook";
        const string urlB = "https://protected-b.example.test/hook";

        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider);
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-shared.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };

        // 1. Save protected webhook URL A + tokA1, rotate on A -> tokA2, cache provenance-less copy of (URL A, tokA1) and (URL A, tokA2).
        var init = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var savedA1 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            init.Version, options,
            SmtpUsername: "smtp-user-1",
            SmtpPassword: "smtp-pass-1",
            WebhookUrl: urlA,
            WebhookAuthToken: "tok-a-1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedA1.Outcome, savedA1.Message);

        var staleCopyA1 = new OperatorNotificationSecrets(
            SmtpUsername: "smtp-user-retry-1",
            SmtpPassword: "smtp-pass-1",
            WebhookUrl: urlA,
            WebhookAuthToken: "tok-a-1");

        var savedA2 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            savedA1.Snapshot!.Version, options,
            WebhookAuthToken: "tok-a-2"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedA2.Outcome, savedA2.Message);

        var staleCopyA2 = new OperatorNotificationSecrets(
            SmtpUsername: "smtp-user-retry-2",
            SmtpPassword: "smtp-pass-1",
            WebhookUrl: urlA,
            WebhookAuthToken: "tok-a-2");

        // 2. Rotate protected webhook URL A -> B with tokB.
        var savedB = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            savedA2.Snapshot!.Version, options,
            WebhookUrl: urlB,
            WebhookAuthToken: "tok-b"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedB.Outcome, savedB.Message);

        // 3. Replay provenance-stripped copies carrying (URL A, tok-a-2) and (URL A, tok-a-1) alongside SMTP edits.
        await ((IOperatorNotificationSecretStore)store).SaveAsync(staleCopyA2, CancellationToken.None).ConfigureAwait(false);
        var afterReplayA2 = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("smtp-user-retry-2", afterReplayA2.Secrets.SmtpUsername);
        Assert.AreEqual(urlB, afterReplayA2.Secrets.WebhookUrl, "Stale field-by-field replay MUST NOT roll webhook URL back from B to retired A.");
        Assert.AreEqual("tok-b", afterReplayA2.Secrets.WebhookAuthToken, "Stale field-by-field replay MUST preserve B's webhook token.");

        await ((IOperatorNotificationSecretStore)store).SaveAsync(staleCopyA1, CancellationToken.None).ConfigureAwait(false);
        var afterReplayA1 = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("smtp-user-retry-1", afterReplayA1.Secrets.SmtpUsername);
        Assert.AreEqual(urlB, afterReplayA1.Secrets.WebhookUrl, "Stale replay with earlier retired token A1 MUST NOT roll webhook URL back to A.");
        Assert.AreEqual("tok-b", afterReplayA1.Secrets.WebhookAuthToken);

        // 4. Intentional rollback backed by fresh current-revision ReadAsync provenance moves WebhookUrl back to A
        //    without inheriting B's provenance exemption for retired tok-a-2 during the destination change itself;
        //    a subsequent fresh read AT urlA (written destination == read provenance) allows re-applying tok-a-2 cleanly.
        var freshReadOnB = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlB, freshReadOnB.WebhookUrl);
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            freshReadOnB with { WebhookUrl = urlA, WebhookAuthToken = "tok-a-2" },
            CancellationToken.None).ConfigureAwait(false);
        var afterUrlRollback = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlA, afterUrlRollback.Secrets.WebhookUrl);
        Assert.IsNull(afterUrlRollback.Secrets.WebhookAuthToken,
            "Destination-changing direct save (B -> A) MUST NOT inherit B's provenance exemption for retired credentials.");

        var freshReadAfterRollbackOnA = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlA, freshReadAfterRollbackOnA.WebhookUrl);
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            freshReadAfterRollbackOnA with { WebhookAuthToken = "tok-a-2" },
            CancellationToken.None).ConfigureAwait(false);
        var afterIntentionalRollback = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlA, afterIntentionalRollback.Secrets.WebhookUrl);
        Assert.AreEqual("tok-a-2", afterIntentionalRollback.Secrets.WebhookAuthToken);

        // 5. Move back to B with current provenance, then verify provenance-less rollback to retired A is STILL
        //    rejected even with a fresh token, while current-provenance rollback succeeds cleanly (H2).
        var freshReadOnA = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            freshReadOnA with { WebhookUrl = urlB, WebhookAuthToken = "tok-b-2" },
            CancellationToken.None).ConfigureAwait(false);
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            new OperatorNotificationSecrets(
                SmtpUsername: "smtp-user-retry-1",
                SmtpPassword: "smtp-pass-1",
                WebhookUrl: urlA,
                WebhookAuthToken: "brand-new-tok-for-a"),
            CancellationToken.None).ConfigureAwait(false);
        var afterUnversionedRollbackAttempt = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlB, afterUnversionedRollbackAttempt.Secrets.WebhookUrl,
            "Unversioned save without current destination provenance MUST NOT overwrite rotated webhook B back to A.");
        Assert.AreEqual("tok-b-2", afterUnversionedRollbackAttempt.Secrets.WebhookAuthToken);

        var freshReadOnB2 = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            freshReadOnB2 with { WebhookUrl = urlA, WebhookAuthToken = "brand-new-tok-for-a" },
            CancellationToken.None).ConfigureAwait(false);
        var afterFreshRollback = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlA, afterFreshRollback.Secrets.WebhookUrl);
        Assert.AreEqual("brand-new-tok-for-a", afterFreshRollback.Secrets.WebhookAuthToken);
    }

    [TestMethod]
    public async Task ConcurrentFirstSavesFromAbsentStore_PreserveBothChannelsInBothDirectionsAsync()
    {
        // M1: Reading an absent store must attach baseline read provenance so concurrent first saves
        // on SMTP and Webhook preserve BOTH channels' first credentials in BOTH directions.
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-init.example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true"
            })
            .Build();

        // Direction 1: Both callers read absent store -> Caller 1 saves SMTP -> Caller 2 saves Webhook.
        using (var dir1 = TempStateDir.Create())
        {
            using var store1 = CreateStore(dir1.Path, isDevelopment: false, configuration: hostConfig);
            var readA = await store1.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            var readB = await store1.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(readA.IsEmpty);
            Assert.IsNotNull(readA.Provenance, "Absent-store ReadAsync must attach baseline read provenance.");

            await store1.SaveAsync(
                readA with { SmtpUsername = "first-smtp-user", SmtpPassword = "first-smtp-pass" },
                CancellationToken.None).ConfigureAwait(false);
            await store1.SaveAsync(
                readB with { WebhookUrl = "https://first-hook.example.test/alerts", WebhookAuthToken = "first-wh-token" },
                CancellationToken.None).ConfigureAwait(false);

            var eff1 = await store1.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("first-smtp-user", eff1.Secrets.SmtpUsername, "Webhook-second first save must preserve SMTP username.");
            Assert.AreEqual("first-smtp-pass", eff1.Secrets.SmtpPassword, "Webhook-second first save must preserve SMTP password.");
            Assert.AreEqual("https://first-hook.example.test/alerts", eff1.Secrets.WebhookUrl);
            Assert.AreEqual("first-wh-token", eff1.Secrets.WebhookAuthToken);
        }

        // Direction 2: Both callers read absent store -> Caller 1 saves Webhook -> Caller 2 saves SMTP.
        using (var dir2 = TempStateDir.Create())
        {
            using var store2 = CreateStore(dir2.Path, isDevelopment: false, configuration: hostConfig);
            var readA = await store2.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            var readB = await store2.ReadAsync(CancellationToken.None).ConfigureAwait(false);

            await store2.SaveAsync(
                readA with { WebhookUrl = "https://first-hook.example.test/alerts", WebhookAuthToken = "first-wh-token" },
                CancellationToken.None).ConfigureAwait(false);
            await store2.SaveAsync(
                readB with { SmtpUsername = "first-smtp-user", SmtpPassword = "first-smtp-pass" },
                CancellationToken.None).ConfigureAwait(false);

            var eff2 = await store2.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("first-smtp-user", eff2.Secrets.SmtpUsername);
            Assert.AreEqual("first-smtp-pass", eff2.Secrets.SmtpPassword);
            Assert.AreEqual("https://first-hook.example.test/alerts", eff2.Secrets.WebhookUrl, "SMTP-second first save must preserve Webhook URL.");
            Assert.AreEqual("first-wh-token", eff2.Secrets.WebhookAuthToken, "SMTP-second first save must preserve Webhook auth token.");
        }
    }

    [TestMethod]
    public async Task StaleEditToRotatedChannel_RejectsNewlyTypedSecretsForBothSmtpAndWebhookAsync()
    {
        // H3: When read provenance was captured on destination A and destination subsequently rotated to B,
        // a provenance-preserving save carrying BRAND-NEW secrets typed for A must NEVER bind them to B!
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        const string urlA = "https://origin-a.example.test/hook";
        const string urlB = "https://origin-b.example.test/hook";

        var configProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
        });
        var hostConfig = new ConfigurationRoot([configProvider]);
        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);

        var optionsRelayA = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-a.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };
        var optionsRelayB = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-b.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };

        var init = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var savedA = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            init.Version, optionsRelayA,
            SmtpUsername: "old-user-a",
            SmtpPassword: "old-pass-a",
            WebhookAuthToken: "old-tok-a"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedA.Outcome, savedA.Message);

        // Operator reads while SMTP is relay-a and Webhook is host origin-a.
        var readOnA = await store.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(readOnA.Provenance);

        // Case 1: Rotate BOTH destinations A -> B with NO credentials on B yet.
        configProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
        });
        var snapBeforeRelayB = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var savedRelayBNoCreds = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapBeforeRelayB.Version, optionsRelayB), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedRelayBNoCreds.Outcome, savedRelayBNoCreds.Message);

        // Stale operator submits BRAND-NEW secrets typed while believing destinations were still A!
        await store.SaveAsync(
            readOnA with
            {
                SmtpUsername = "brand-new-user-for-a",
                SmtpPassword = "brand-new-pass-for-a",
                WebhookAuthToken = "brand-new-tok-for-a",
                WebhookUserKey = "brand-new-key-for-a",
                WebhookNtfyTopic = "brand-new-topic-for-a"
            },
            CancellationToken.None).ConfigureAwait(false);

        var effAfterStaleNewOnEmptyB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-b.example.test", effAfterStaleNewOnEmptyB.Options.Smtp.Host);
        Assert.IsNull(effAfterStaleNewOnEmptyB.Secrets.SmtpUsername, "Newly typed SMTP username from stale A read must NOT bind to B.");
        Assert.IsNull(effAfterStaleNewOnEmptyB.Secrets.SmtpPassword, "Newly typed SMTP password from stale A read must NOT bind to B.");
        Assert.AreEqual(urlB, effAfterStaleNewOnEmptyB.Secrets.WebhookUrl);
        Assert.IsNull(effAfterStaleNewOnEmptyB.Secrets.WebhookAuthToken, "Newly typed Webhook token from stale A read must NOT bind to B.");
        Assert.IsNull(effAfterStaleNewOnEmptyB.Secrets.WebhookUserKey, "Newly typed Webhook user key from stale A read must NOT bind to B.");
        Assert.IsNull(effAfterStaleNewOnEmptyB.Secrets.WebhookNtfyTopic, "Newly typed Webhook ntfy topic from stale A read must NOT bind to B.");

        // Case 2: Now B has fresh protected URL + fresh B credentials; stale A edit with brand-new secrets MUST preserve B!
        var snapOnB = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var savedBWithCreds = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapOnB.Version, optionsRelayB,
            SmtpUsername: "fresh-user-b",
            SmtpPassword: "fresh-pass-b",
            WebhookUrl: "https://protected-b.example.test/hook",
            WebhookAuthToken: "fresh-tok-b"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedBWithCreds.Outcome, savedBWithCreds.Message);

        await store.SaveAsync(
            readOnA with
            {
                SmtpUsername = "another-new-user-for-a",
                SmtpPassword = "another-new-pass-for-a",
                WebhookAuthToken = "another-new-tok-for-a"
            },
            CancellationToken.None).ConfigureAwait(false);

        var effAfterStaleNewOnPopulatedB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("fresh-user-b", effAfterStaleNewOnPopulatedB.Secrets.SmtpUsername);
        Assert.AreEqual("fresh-pass-b", effAfterStaleNewOnPopulatedB.Secrets.SmtpPassword);
        Assert.AreEqual("https://protected-b.example.test/hook", effAfterStaleNewOnPopulatedB.Secrets.WebhookUrl);
        Assert.AreEqual("fresh-tok-b", effAfterStaleNewOnPopulatedB.Secrets.WebhookAuthToken);
    }

    [TestMethod]
    public async Task ChainedConfiguration_UnwrapsInnerLeafProvidersAndFailsClosedOnOpaqueChainedConfigurationAsync()
    {
        // H4: ConfigurationBuilder().AddConfiguration(innerRoot) exposes a ChainedConfigurationProvider.
        // We MUST recursively unwrap IConfigurationRoot to identify true leaf providers (so destination provider's
        // BoundOrigin/BoundEndpoint never authorizes a sibling credential provider's stale credential, while
        // same-leaf-provider bindings inside innerRoot still authorize), and fail closed on opaque non-root IConfiguration.
        const string urlA = "https://chained-a.example.test/hook";
        const string urlB = "https://chained-b.example.test/hook";

        // Part A: Nested AddConfiguration(midRoot) -> AddConfiguration(innerRoot) with split destProvider & credsProvider.
        var destProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-a.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA,
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlA,
            [ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-a.example.test:587:StartTls"
        });
        var credsProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "user-a",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "pass-a",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "token-a"
        });

        var innerRoot = new ConfigurationRoot([destProvider, credsProvider]);
        var midRoot = new ConfigurationBuilder().AddConfiguration(innerRoot).Build();
        var outerRoot = new ConfigurationBuilder().AddConfiguration(midRoot).Build();
        using var chainedProvider = new ConfigurationOperatorNotificationConfigProvider(outerRoot);

        var init = await chainedProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("user-a", init.Secrets.SmtpUsername);
        Assert.AreEqual("pass-a", init.Secrets.SmtpPassword);
        Assert.AreEqual("token-a", init.Secrets.WebhookAuthToken);

        // Destination leaf provider moves A -> B WITH BoundOrigin=B and BoundEndpoint=B; creds leaf provider still holds A!
        destProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-b.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB,
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlB,
            [ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-b.example.test:587:StartTls"
        });

        var afterDestRotate = await chainedProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-b.example.test", afterDestRotate.Options.Smtp.Host);
        Assert.IsNull(afterDestRotate.Secrets.SmtpUsername, "Chained destination provider's BoundEndpoint MUST NOT authorize sibling leaf provider's stale SMTP username.");
        Assert.IsNull(afterDestRotate.Secrets.SmtpPassword, "Chained destination provider's BoundEndpoint MUST NOT authorize sibling leaf provider's stale SMTP password.");
        Assert.AreEqual(urlB, afterDestRotate.Secrets.WebhookUrl);
        Assert.IsNull(afterDestRotate.Secrets.WebhookAuthToken, "Chained destination provider's BoundOrigin MUST NOT authorize sibling leaf provider's stale webhook token.");

        // Same-leaf-provider explicit binding inside innerRoot DOES authorize cleanly (even reusing token-a intentionally).
        credsProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "user-a",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "pass-a",
            [ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-b.example.test:587:StartTls",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "token-a",
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlB
        });

        var afterSameLeafBind = await chainedProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("user-a", afterSameLeafBind.Secrets.SmtpUsername);
        Assert.AreEqual("pass-a", afterSameLeafBind.Secrets.SmtpPassword);
        Assert.AreEqual("token-a", afterSameLeafBind.Secrets.WebhookAuthToken);

        // Part B: Opaque non-IConfigurationRoot chained configuration fails closed (never proves same-provider co-location).
        var opaqueDest = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA,
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlA
        });
        var opaqueCreds = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "opaque-token-a"
        });
        var opaqueWrapper = new OpaqueConfigurationWrapper(new ConfigurationRoot([opaqueDest, opaqueCreds]));
        var opaqueOuterRoot = new ConfigurationBuilder().AddConfiguration(opaqueWrapper).Build();
        using var opaqueProvider = new ConfigurationOperatorNotificationConfigProvider(opaqueOuterRoot);

        Assert.AreEqual("opaque-token-a", (await opaqueProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false)).Secrets.WebhookAuthToken);

        opaqueDest.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB,
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlB
        });
        var afterOpaqueRotate = await opaqueProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlB, afterOpaqueRotate.Secrets.WebhookUrl);
        Assert.IsNull(afterOpaqueRotate.Secrets.WebhookAuthToken,
            "Opaque non-IConfigurationRoot chained provider MUST fail closed and never authorize a stale token across hidden inner providers.");
    }

    [TestMethod]
    public async Task SplitSmtpHostPortAndCredentialsProviders_FailClosedWithoutExplicitBindingAndConvergeWithBoundEndpointAsync()
    {
        // H4 fail-closed invariant: When SMTP Host, Port, and Credentials live in 3 separate providers, live-reload
        // credential promotion across destination changes MUST NEVER guess partial-tuple completion across ANY reload
        // order (Host->Creds->Port, Port->Creds->Host, Creds->Host->Port) without explicit same-provider
        // Smtp:BoundEndpoint (or atomic single-provider update / explicit operator store save / host restart).
        static Dictionary<string, string?> HostDict(string host) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = host,
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test"
        };
        static Dictionary<string, string?> PortDict(int port) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        static Dictionary<string, string?> CredsDict(string tag, string? boundEndpoint = null)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = $"user-{tag}",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = $"pass-{tag}"
            };
            if (boundEndpoint is not null)
            {
                dict[ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = boundEndpoint;
            }
            return dict;
        }

        // Order 1: Host -> Creds -> Port.
        {
            var hostProv = new RotatingHostConfigurationProvider(HostDict("relay-a.example.test"));
            var portProv = new RotatingHostConfigurationProvider(PortDict(587));
            var credsProv = new RotatingHostConfigurationProvider(CredsDict("a"));
            var root = new ConfigurationRoot([hostProv, portProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            var init = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("user-a", init.Secrets.SmtpUsername);
            Assert.AreEqual("pass-a", init.Secrets.SmtpPassword);

            hostProv.Rotate(HostDict("relay-b.example.test"));
            var afterHost = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-b.example.test", afterHost.Options.Smtp.Host);
            Assert.IsNull(afterHost.Secrets.SmtpUsername, "Host change MUST suppress stale user-a.");
            Assert.IsNull(afterHost.Secrets.SmtpPassword, "Host change MUST suppress stale pass-a.");

            credsProv.Rotate(CredsDict("b"));
            var mid = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(mid.Secrets.SmtpUsername, "Intermediate (hostB, 587) MUST withhold unbound user-b.");
            Assert.IsNull(mid.Secrets.SmtpPassword, "Intermediate (hostB, 587) MUST withhold unbound pass-b.");

            portProv.Rotate(PortDict(2525));
            var unboundAfterPort = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-b.example.test", unboundAfterPort.Options.Smtp.Host);
            Assert.AreEqual(2525, unboundAfterPort.Options.Smtp.Port);
            Assert.IsNull(unboundAfterPort.Secrets.SmtpUsername, "Split SMTP without Smtp:BoundEndpoint MUST fail closed rather than guessing tuple completion.");
            Assert.IsNull(unboundAfterPort.Secrets.SmtpPassword, "Split SMTP without Smtp:BoundEndpoint MUST fail closed rather than guessing tuple completion.");

            // Explicit same-provider Smtp:BoundEndpoint unambiguously promotes user-b/pass-b on the exact target tuple.
            credsProv.Rotate(CredsDict("b", "relay-b.example.test:2525:StartTls"));
            var converged = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("user-b", converged.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", converged.Secrets.SmtpPassword);

            root.Reload();
            var afterReload = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("user-b", afterReload.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", afterReload.Secrets.SmtpPassword);

            // Moving Host AGAIN (hostB -> hostC) WITHOUT updating BoundEndpoint immediately suppresses user-b/pass-b.
            hostProv.Rotate(HostDict("relay-c.example.test"));
            var afterHostC = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(afterHostC.Secrets.SmtpUsername, "Moving hostB -> hostC MUST suppress user-b.");
            Assert.IsNull(afterHostC.Secrets.SmtpPassword, "Moving hostB -> hostC MUST suppress pass-b.");
        }

        // Order 2: Port -> Creds -> Host (explicit BoundEndpoint stays withheld until Host settles, then converges!).
        {
            var hostProv = new RotatingHostConfigurationProvider(HostDict("relay-a.example.test"));
            var portProv = new RotatingHostConfigurationProvider(PortDict(587));
            var credsProv = new RotatingHostConfigurationProvider(CredsDict("a"));
            var root = new ConfigurationRoot([hostProv, portProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            portProv.Rotate(PortDict(2525));
            var afterPort = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(afterPort.Secrets.SmtpUsername, "Port change MUST suppress stale user-a.");
            Assert.IsNull(afterPort.Secrets.SmtpPassword, "Port change MUST suppress stale pass-a.");

            credsProv.Rotate(CredsDict("b", "relay-b.example.test:2525:StartTls"));
            var intermediateHostAPortB = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-a.example.test", intermediateHostAPortB.Options.Smtp.Host);
            Assert.AreEqual(2525, intermediateHostAPortB.Options.Smtp.Port);
            Assert.IsNull(intermediateHostAPortB.Secrets.SmtpUsername, "Intermediate (hostA, portB) snapshot MUST withhold user-b.");
            Assert.IsNull(intermediateHostAPortB.Secrets.SmtpPassword, "Intermediate (hostA, portB) snapshot MUST withhold pass-b.");

            hostProv.Rotate(HostDict("relay-b.example.test"));
            var converged = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-b.example.test", converged.Options.Smtp.Host);
            Assert.AreEqual(2525, converged.Options.Smtp.Port);
            Assert.AreEqual("user-b", converged.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", converged.Secrets.SmtpPassword);
        }

        // Order 3: Creds -> Host -> Port (host restart on settled config also converges cleanly without BoundEndpoint).
        {
            var hostProv = new RotatingHostConfigurationProvider(HostDict("relay-a.example.test"));
            var portProv = new RotatingHostConfigurationProvider(PortDict(587));
            var credsProv = new RotatingHostConfigurationProvider(CredsDict("a"));
            var root = new ConfigurationRoot([hostProv, portProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            credsProv.Rotate(CredsDict("b"));
            var afterCreds = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(afterCreds.Secrets.SmtpUsername, "Unbound creds-first MUST NOT leak user-b onto (hostA, portA).");
            Assert.IsNull(afterCreds.Secrets.SmtpPassword, "Unbound creds-first MUST NOT leak pass-b onto (hostA, portA).");

            hostProv.Rotate(HostDict("relay-b.example.test"));
            portProv.Rotate(PortDict(2525));

            var withheldWithoutBinding = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(withheldWithoutBinding.Secrets.SmtpUsername, "Unbound split SMTP fails closed on live reload.");
            Assert.IsNull(withheldWithoutBinding.Secrets.SmtpPassword, "Unbound split SMTP fails closed on live reload.");

            using var restartedProvider = new ConfigurationOperatorNotificationConfigProvider(root);
            var afterRestart = await restartedProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-b.example.test", afterRestart.Options.Smtp.Host);
            Assert.AreEqual(2525, afterRestart.Options.Smtp.Port);
            Assert.AreEqual("user-b", afterRestart.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", afterRestart.Secrets.SmtpPassword);
        }
    }

    [TestMethod]
    public async Task PreviouslyEmptyCredentialField_AfterDestinationFirstRotation_ConvergesImmediatelyWhenFirstCredentialArrivesAsync()
    {
        // M2: Starting at A with empty credentials, rotating destination A -> B while still empty (observing B empty),
        // and then supplying B's first credentials at B must immediately converge to (B, credB) for BOTH SMTP and Webhook.
        const string urlA = "https://empty-a.example.test/hook";
        const string urlB = "https://empty-b.example.test/hook";

        var destProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-a.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Ntfy",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
        });
        var credsProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var root = new ConfigurationRoot([destProv, credsProv]);
        using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

        // 1. Observe A with empty credentials.
        var onAEmpty = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-a.example.test", onAEmpty.Options.Smtp.Host);
        Assert.IsNull(onAEmpty.Secrets.SmtpUsername);
        Assert.IsNull(onAEmpty.Secrets.SmtpPassword);
        Assert.IsNull(onAEmpty.Secrets.WebhookAuthToken);
        Assert.IsNull(onAEmpty.Secrets.WebhookNtfyTopic);

        // 2. Destination-first rotation A -> B while credentials remain empty (observing B empty!).
        destProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-b.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Ntfy",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
        });
        var onBEmpty = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-b.example.test", onBEmpty.Options.Smtp.Host);
        Assert.AreEqual(urlB, onBEmpty.Secrets.WebhookUrl);
        Assert.IsNull(onBEmpty.Secrets.SmtpUsername);
        Assert.IsNull(onBEmpty.Secrets.WebhookAuthToken);

        // 3. Supply B's first credentials at B -> MUST converge immediately without withholding!
        credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "first-user-b",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "first-pass-b",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "first-tok-b",
            [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = "first-topic-b"
        });
        var convergedOnB = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("first-user-b", convergedOnB.Secrets.SmtpUsername, "Empty->non-empty SMTP username at B must converge immediately.");
        Assert.AreEqual("first-pass-b", convergedOnB.Secrets.SmtpPassword, "Empty->non-empty SMTP password at B must converge immediately.");
        Assert.AreEqual("first-tok-b", convergedOnB.Secrets.WebhookAuthToken, "Empty->non-empty Webhook token at B must converge immediately.");
        Assert.AreEqual("first-topic-b", convergedOnB.Secrets.WebhookNtfyTopic, "Empty->non-empty Webhook topic at B must converge immediately.");

        // 4. Clear credentials on B (retiring first-*-b for B!), rotate B -> C, and verify stale first-*-b is STILL rejected at C
        //    even through an empty transition, while a fresh C credential converges immediately!
        credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        destProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-c.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Ntfy",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://empty-c.example.test/hook"
        });
        credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "first-user-b",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "first-tok-b"
        });
        var staleReplayOnC = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(staleReplayOnC.Secrets.SmtpUsername, "Previously cleared B username must stay retired and be rejected at C.");
        Assert.IsNull(staleReplayOnC.Secrets.WebhookAuthToken, "Previously cleared B token must stay retired and be rejected at C.");
    }

    [TestMethod]
    public void EnsureRestrictedDirectory_WhenNonSymlinkAnchorIsSwappedImmediatelyAfterSingleObservation_CannotReclassifyAsSymlinkAndFailsClosed()
    {
        // M3: Anchor classification must rely on a SINGLE non-following observation. Swapping a classified
        // normal directory to a symlink immediately after that single observation (where a second pathname
        // check would previously have reclassified it as a permitted symlink) must NOT disable O_NOFOLLOW!
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("POSIX O_DIRECTORY | O_NOFOLLOW anchor pinning is Unix-only.");
            return;
        }

        RequireModesEnforced();
        using var dir = TempStateDir.Create();

        const UnixFileMode permissive =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        // Case 1: outsideTarget has NO state subdir -> swap immediately after single non-following classification.
        var anchor1 = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(dir.Path, "anchor-single-obs-1")));
        Directory.CreateDirectory(anchor1);
        var outside1 = Path.Combine(dir.Path, "outside-single-obs-1");
        Directory.CreateDirectory(outside1);
        File.SetUnixFileMode(outside1, permissive);
        var swapped1 = false;

        Assert.ThrowsExactly<IOException>(() =>
            NotificationStateFilePublisher.EnsureRestrictedDirectory(
                Path.Combine(anchor1, "state"),
                onDirectorySynced: null,
                directorySyncAction: null,
                beforeAnchorOpen: null,
                afterAncestorClassified: classifiedPath =>
                {
                    if (string.Equals(classifiedPath, anchor1, StringComparison.Ordinal))
                    {
                        swapped1 = true;
                        Directory.Delete(anchor1);
                        Directory.CreateSymbolicLink(anchor1, outside1);
                    }
                }));

        Assert.IsTrue(swapped1, "Single-observation ancestor classification hook must have fired for anchor1.");
        Assert.IsFalse(Directory.Exists(Path.Combine(outside1, "state")),
            "Swapping a classified non-symlink anchor immediately after single observation must NEVER create directories in the symlink target.");
        Assert.AreEqual(permissive, File.GetUnixFileMode(outside1));

        // Case 2: outsideTarget ALREADY has a permissive 'state' dir -> swap immediately after classification
        // must still enforce O_NOFOLLOW and NEVER harden outside2/state!
        var anchor2 = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(dir.Path, "anchor-single-obs-2")));
        Directory.CreateDirectory(anchor2);
        var outside2 = Path.Combine(dir.Path, "outside-single-obs-2");
        var outside2State = Path.Combine(outside2, "state");
        Directory.CreateDirectory(outside2State);
        File.SetUnixFileMode(outside2, permissive);
        File.SetUnixFileMode(outside2State, permissive);
        var swapped2 = false;

        Assert.ThrowsExactly<IOException>(() =>
            NotificationStateFilePublisher.EnsureRestrictedDirectory(
                Path.Combine(anchor2, "state"),
                onDirectorySynced: null,
                directorySyncAction: null,
                beforeAnchorOpen: null,
                afterAncestorClassified: classifiedPath =>
                {
                    if (string.Equals(classifiedPath, anchor2, StringComparison.Ordinal))
                    {
                        swapped2 = true;
                        Directory.Delete(anchor2);
                        Directory.CreateSymbolicLink(anchor2, outside2);
                    }
                }));

        Assert.IsTrue(swapped2, "Single-observation ancestor classification hook must have fired for anchor2.");
        Assert.AreEqual(permissive, File.GetUnixFileMode(outside2State),
            "Swapping a classified non-symlink anchor must NEVER chmod directories inside the symlink target.");
    }

    [TestMethod]
    public async Task BoundedEviction_StoreOver128AndTrackerOver1024RotationsAtA_StillRejectsEarliestACredentialAtBAsync()
    {
        // Delta11 H1: Churning >128 rotations at A in the persisted store AND >1,024 rotations at A in
        // HostConfigurationSnapshotTracker must NEVER silently admit A's earliest (#1) credential at B!
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        const string urlA = "https://h1-origin-a.example.test/hook";
        const string urlB = "https://h1-origin-b.example.test/hook";

        var hostProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-h1-a.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
        });
        var hostConfig = new ConfigurationRoot([hostProvider]);
        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);

        var optionsA = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-h1-a.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };
        var optionsB = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-h1-b.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };

        // 1. Persisted store: save initial #1 credential at A, then churn 140 rotations (> MaxRetiredBindingsPerChannel = 128).
        var initSnap = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var savedA1 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            initSnap.Version,
            optionsA,
            SmtpUsername: "store-user-a-1",
            SmtpPassword: "store-pass-a-1",
            WebhookAuthToken: "store-tok-a-1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedA1.Outcome, savedA1.Message);

        for (var i = 2; i <= OperatorNotificationStore.MaxRetiredBindingsPerChannel + 12; i++)
        {
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                new OperatorNotificationSecrets(
                    SmtpUsername: $"store-user-a-{i}",
                    SmtpPassword: $"store-pass-a-{i}",
                    WebhookAuthToken: $"store-tok-a-{i}"),
                CancellationToken.None).ConfigureAwait(false);
        }

        // Rotate both destinations A -> B (first with NO B credentials yet!).
        hostProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
        });
        var snapOnBEmpty = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var savedBEmpty = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapOnBEmpty.Version, optionsB), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedBEmpty.Outcome, savedBEmpty.Message);

        // Unversioned replay of A's VERY FIRST (#1) credentials MUST still be rejected/withheld at B!
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            new OperatorNotificationSecrets(
                SmtpUsername: "store-user-a-1",
                SmtpPassword: "store-pass-a-1",
                WebhookAuthToken: "store-tok-a-1"),
            CancellationToken.None).ConfigureAwait(false);

        var effAfterReplayOnEmptyB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-h1-b.example.test", effAfterReplayOnEmptyB.Options.Smtp.Host);
        Assert.IsNull(effAfterReplayOnEmptyB.Secrets.SmtpUsername, "Earliest (#1) A SMTP username must stay rejected at B after >128 rotations.");
        Assert.IsNull(effAfterReplayOnEmptyB.Secrets.SmtpPassword, "Earliest (#1) A SMTP password must stay rejected at B after >128 rotations.");
        Assert.AreEqual(urlB, effAfterReplayOnEmptyB.Secrets.WebhookUrl);
        Assert.IsNull(effAfterReplayOnEmptyB.Secrets.WebhookAuthToken, "Earliest (#1) A webhook token must stay rejected at B after >128 rotations.");

        // Versioned save on B (UpdateRequest AND ReadAsync + SaveAsync with current provenance) still works cleanly.
        var snapBeforeB = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var savedB = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapBeforeB.Version,
            optionsB,
            SmtpUsername: "store-user-b-1",
            SmtpPassword: "store-pass-b-1",
            WebhookAuthToken: "store-tok-b-1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedB.Outcome, savedB.Message);

        // Unversioned replay of #1 A credential against populated B must also preserve B's fresh credentials.
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            new OperatorNotificationSecrets(
                SmtpUsername: "store-user-a-1",
                SmtpPassword: "store-pass-a-1",
                WebhookAuthToken: "store-tok-a-1"),
            CancellationToken.None).ConfigureAwait(false);
        var effPreservedB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("store-user-b-1", effPreservedB.Secrets.SmtpUsername);
        Assert.AreEqual("store-pass-b-1", effPreservedB.Secrets.SmtpPassword);
        Assert.AreEqual("store-tok-b-1", effPreservedB.Secrets.WebhookAuthToken);

        var freshReadB = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            freshReadB with { SmtpUsername = "store-user-b-2", WebhookAuthToken = "store-tok-b-2" },
            CancellationToken.None).ConfigureAwait(false);
        var effAfterVersionedB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("store-user-b-2", effAfterVersionedB.Secrets.SmtpUsername);
        Assert.AreEqual("store-tok-b-2", effAfterVersionedB.Secrets.WebhookAuthToken);

        // 2. HostConfigurationSnapshotTracker: churn >1,024 rotations at A (1,050 rotations), then rotate A -> B
        //    and replay A's #1 credential unbound -> MUST remain withheld for both SMTP and Webhook!
        var trackerDest = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-h1-a.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
        });
        var trackerCreds = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "tracker-user-a-1",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "tracker-pass-a-1",
            [ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-h1-a.example.test:587:StartTls",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "tracker-tok-a-1",
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlA
        });
        var trackerRoot = new ConfigurationRoot([trackerDest, trackerCreds]);
        using var trackerProvider = new ConfigurationOperatorNotificationConfigProvider(trackerRoot);

        var initTracker = await trackerProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("tracker-user-a-1", initTracker.Secrets.SmtpUsername);
        Assert.AreEqual("tracker-tok-a-1", initTracker.Secrets.WebhookAuthToken);

        for (var i = 2; i <= HostConfigurationSnapshotTracker.MaxRetiredFieldBindings + 32; i++)
        {
            trackerCreds.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = $"tracker-user-a-{i}",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = $"tracker-pass-a-{i}",
                [ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-h1-a.example.test:587:StartTls",
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = $"tracker-tok-a-{i}",
                [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlA
            });
        }

        // Rotate A -> B and replay A's #1 credential without an explicit binding for B.
        trackerDest.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-h1-b.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
        });
        trackerCreds.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "tracker-user-a-1",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "tracker-pass-a-1",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "tracker-tok-a-1"
        });

        var effTrackerOnB = await trackerProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-h1-b.example.test", effTrackerOnB.Options.Smtp.Host);
        Assert.IsNull(effTrackerOnB.Secrets.SmtpUsername, "Tracker must still withhold #1 A SMTP username at B after >1,024 rotations.");
        Assert.IsNull(effTrackerOnB.Secrets.SmtpPassword, "Tracker must still withhold #1 A SMTP password at B after >1,024 rotations.");
        Assert.AreEqual(urlB, effTrackerOnB.Secrets.WebhookUrl);
        Assert.IsNull(effTrackerOnB.Secrets.WebhookAuthToken, "Tracker must still withhold #1 A webhook token at B after >1,024 rotations.");

        // Explicit same-provider binding for B still converges cleanly.
        trackerCreds.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "tracker-user-b-1",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "tracker-pass-b-1",
            [ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-h1-b.example.test:587:StartTls",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "tracker-tok-b-1",
            [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlB
        });
        var effTrackerBoundB = await trackerProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("tracker-user-b-1", effTrackerBoundB.Secrets.SmtpUsername);
        Assert.AreEqual("tracker-pass-b-1", effTrackerBoundB.Secrets.SmtpPassword);
        Assert.AreEqual("tracker-tok-b-1", effTrackerBoundB.Secrets.WebhookAuthToken);
    }

    [TestMethod]
    public async Task UrlOnlyProtectedWebhook_UnversionedSmtpEditCarryingStaleUrlA_NeverRollsBackDestinationBAsync()
    {
        // Delta11 H2: A URL-only webhook (authenticated purely by its secret URL, with no token/userKey/topic)
        // must track its retired URL fingerprint AND require current destination provenance so an unversioned
        // SMTP edit carrying stale URL A NEVER rolls rotated destination B back to A!
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        const string urlA = "https://hooks.example.test/services/T000/B000/SECRET_URL_A";
        const string urlB = "https://hooks.example.test/services/T000/B000/SECRET_URL_B";
        const string urlC = "https://other-hooks.example.test/services/SECRET_URL_C";

        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider);
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-h2.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };

        // 1. Configure URL-only webhook A (no token, userKey, or ntfyTopic!) + SMTP credentials.
        var s0 = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var savedA = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s0.Version,
            options,
            SmtpUsername: "smtp-user-1",
            SmtpPassword: "smtp-pass-1",
            WebhookUrl: urlA), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedA.Outcome, savedA.Message);

        // Read stale snapshot while at URL-only webhook A, plus create a provenance-stripped unversioned copy.
        var staleReadOnA = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlA, staleReadOnA.WebhookUrl);
        Assert.IsNull(staleReadOnA.WebhookAuthToken);
        Assert.IsNull(staleReadOnA.WebhookUserKey);
        Assert.IsNull(staleReadOnA.WebhookNtfyTopic);

        var unversionedCopyWithUrlA = new OperatorNotificationSecrets(
            SmtpUsername: "smtp-user-edited-unversioned",
            SmtpPassword: staleReadOnA.SmtpPassword,
            WebhookUrl: urlA);

        // 2. Rotate URL-only webhook A -> B (same origin, different secret path!).
        var savedB = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            savedA.Snapshot!.Version,
            options,
            WebhookUrl: urlB), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedB.Outcome, savedB.Message);

        // 3. Submit unversioned SMTP edit carrying stale URL-only webhook A -> MUST stay at B!
        await ((IOperatorNotificationSecretStore)store).SaveAsync(unversionedCopyWithUrlA, CancellationToken.None).ConfigureAwait(false);
        var effAfterUnversionedSameOrigin = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("smtp-user-edited-unversioned", effAfterUnversionedSameOrigin.Secrets.SmtpUsername);
        Assert.AreEqual(urlB, effAfterUnversionedSameOrigin.Secrets.WebhookUrl,
            "Unversioned SMTP edit carrying stale URL-only webhook A MUST NOT roll same-origin secret path B back to A.");

        // Also submit provenance-bearing stale snapshot carrying URL A -> MUST stay at B!
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            staleReadOnA with { SmtpUsername = "smtp-user-edited-stale-prov" },
            CancellationToken.None).ConfigureAwait(false);
        var effAfterStaleProv = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("smtp-user-edited-stale-prov", effAfterStaleProv.Secrets.SmtpUsername);
        Assert.AreEqual(urlB, effAfterStaleProv.Secrets.WebhookUrl,
            "Stale provenance snapshot carrying URL-only webhook A MUST NOT roll B back to A.");

        // 4. Rotate to cross-origin URL-only webhook C and replay unversioned copy carrying URL A -> MUST stay at C!
        var snapOnB = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var savedC = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapOnB.Version,
            options,
            WebhookUrl: urlC), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedC.Outcome, savedC.Message);

        await ((IOperatorNotificationSecretStore)store).SaveAsync(unversionedCopyWithUrlA, CancellationToken.None).ConfigureAwait(false);
        var effAfterCrossOriginReplay = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlC, effAfterCrossOriginReplay.Secrets.WebhookUrl,
            "Unversioned SMTP edit carrying stale URL-only webhook A MUST NOT roll cross-origin C back to A.");

        // 5. Intentional rollback with fresh current-revision ReadAsync provenance succeeds cleanly.
        var freshReadOnC = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            freshReadOnC with { WebhookUrl = urlA },
            CancellationToken.None).ConfigureAwait(false);
        var effAfterIntentionalRollback = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlA, effAfterIntentionalRollback.Secrets.WebhookUrl);
    }

    [TestMethod]
    public async Task SameOriginWebhookPathRotation_ChangesEditorVersionAndRejectsStaleSecretSaveAsync()
    {
        // Delta11 H3: Rotating a host-configured webhook between two paths (or queries) on the SAME origin
        // MUST change the opaque editor version AND reject saving a newly typed secret from a stale editor
        // or stale ReadAsync provenance, without ever exposing the webhook URL path in the version token.
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        const string pathA = "https://hooks.example.com/receiver-a";
        const string pathB = "https://hooks.example.com/receiver-b";
        const string pathBCanonical = "https://HOOKS.EXAMPLE.COM:443/receiver-b";
        const string pathBQuery = "https://hooks.example.com/receiver-b?tenant=2";

        var hostProvider = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = pathA
        });
        var hostConfig = new ConfigurationRoot([hostProvider]);
        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);
        using var configOnlyProvider = new ConfigurationOperatorNotificationConfigProvider(hostConfig);

        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };

        // Capture editor snapshot and direct secret store read at /receiver-a.
        var snapA = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var configSnapA = await configOnlyProvider.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var directReadA = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.DoesNotContain("receiver-a", snapA.Version, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("receiver-a", configSnapA.Version, StringComparison.OrdinalIgnoreCase);

        // Rotate host-configured webhook on the SAME origin: /receiver-a -> /receiver-b.
        hostProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = pathB
        });

        var snapB = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var configSnapB = await configOnlyProvider.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(snapA.Version, snapB.Version,
            "Same-origin webhook path rotation (/receiver-a -> /receiver-b) MUST change the store editor version.");
        Assert.AreNotEqual(configSnapA.Version, configSnapB.Version,
            "Same-origin webhook path rotation (/receiver-a -> /receiver-b) MUST change the config-provider editor version.");
        Assert.DoesNotContain("receiver-b", snapB.Version, StringComparison.OrdinalIgnoreCase);

        // Saving a newly typed secret from the stale editor (holding versionA) MUST be rejected with Conflict!
        var staleEditorSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapA.Version,
            options,
            WebhookAuthToken: "secret-typed-for-receiver-a"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Conflict, staleEditorSave.Outcome,
            "Stale editor saving a token after same-origin path rotation MUST receive 409 Conflict.");

        // Saving a newly typed secret from the stale direct ReadAsync snapshot MUST also be rejected!
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            directReadA with { WebhookAuthToken = "direct-secret-typed-for-receiver-a" },
            CancellationToken.None).ConfigureAwait(false);
        var effAfterStaleDirect = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pathB, effAfterStaleDirect.Secrets.WebhookUrl);
        Assert.IsNull(effAfterStaleDirect.Secrets.WebhookAuthToken,
            "Stale ReadAsync provenance from /receiver-a MUST NOT bind a newly typed secret onto /receiver-b.");

        // Canonical equivalent URL (uppercase host + explicit default port 443) produces the same fingerprint/version,
        // while query-string change (?tenant=2) changes the version and rejects stale saves.
        var versionBeforeCanonical = (await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Version;
        hostProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = pathBCanonical
        });
        var versionAfterCanonical = (await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).Version;
        Assert.AreEqual(versionBeforeCanonical, versionAfterCanonical);

        hostProvider.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = pathBQuery
        });
        var snapBQuery = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(versionAfterCanonical, snapBQuery.Version,
            "Webhook query-string change MUST change the editor version.");
        var staleQuerySave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            versionAfterCanonical,
            options,
            WebhookAuthToken: "secret-for-b-no-query"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Conflict, staleQuerySave.Outcome);

        // Fresh version at /receiver-b?tenant=2 saves cleanly.
        var freshSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapBQuery.Version,
            options,
            WebhookAuthToken: "secret-for-b-query-2"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, freshSave.Outcome, freshSave.Message);
        Assert.AreEqual("secret-for-b-query-2", (await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false)).Secrets.WebhookAuthToken);
    }

    [TestMethod]
    public async Task ChainedConfiguration_EmptyOrNullInnerKeyFallsThroughToLowerOuterProvider_NotTreatedAsCoLocatedAsync()
    {
        // Delta11 H4: .NET's ChainedConfigurationProvider.TryGet returns !string.IsNullOrEmpty(_config[key]),
        // declining empty/null inner values and falling through to lower outer providers. Provider ownership
        // resolution MUST match that exact precedence and NEVER treat the lower outer credential as co-located
        // with the destination-owning inner leaf provider!
        const string urlA = "https://h4-chained-a.example.test/hook";
        const string urlB = "https://h4-chained-b.example.test/hook";

        foreach (var innerEmptyValue in new string?[] { string.Empty, null })
        {
            var lowerOuterCreds = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "outer-user-a",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "outer-pass-a",
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "outer-tok-a"
            });

            var innerDestWithEmptyCreds = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-h4-a.example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA,
                // Inner destination provider holds empty/null for credential keys, so ChainedConfigurationProvider.TryGet
                // declines them and outerRoot falls through to lowerOuterCreds!
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = innerEmptyValue,
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = innerEmptyValue,
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = innerEmptyValue
            });

            var innerRoot = new ConfigurationRoot([innerDestWithEmptyCreds]);
            var outerRoot = new ConfigurationBuilder()
                .Add(new ExistingProviderConfigurationSource(lowerOuterCreds))
                .AddConfiguration(innerRoot)
                .Build();

            // Verify .NET runtime actually falls through ChainedConfigurationProvider to lowerOuterCreds.
            Assert.AreEqual("outer-user-a", outerRoot[ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey]);
            Assert.AreEqual("outer-pass-a", outerRoot[ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey]);
            Assert.AreEqual("outer-tok-a", outerRoot[ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey]);

            using var provider = new ConfigurationOperatorNotificationConfigProvider(outerRoot);
            var init = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("outer-user-a", init.Secrets.SmtpUsername);
            Assert.AreEqual("outer-pass-a", init.Secrets.SmtpPassword);
            Assert.AreEqual("outer-tok-a", init.Secrets.WebhookAuthToken);

            // Rotate inner destination A -> B (still keeping empty/null credential keys in innerDestWithEmptyCreds,
            // while lowerOuterCreds still holds outer-*-a!). If falsely treated as co-located, outer-*-a would leak to B!
            innerDestWithEmptyCreds.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-h4-b.example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB,
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = innerEmptyValue,
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = innerEmptyValue,
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = innerEmptyValue
            });

            var afterRotate = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h4-b.example.test", afterRotate.Options.Smtp.Host);
            Assert.AreEqual(urlB, afterRotate.Secrets.WebhookUrl);
            Assert.IsNull(afterRotate.Secrets.SmtpUsername,
                $"Inner {(innerEmptyValue is null ? "null" : "empty")} SMTP username MUST NOT grant false co-location to lower outer provider.");
            Assert.IsNull(afterRotate.Secrets.SmtpPassword,
                $"Inner {(innerEmptyValue is null ? "null" : "empty")} SMTP password MUST NOT grant false co-location to lower outer provider.");
            Assert.IsNull(afterRotate.Secrets.WebhookAuthToken,
                $"Inner {(innerEmptyValue is null ? "null" : "empty")} webhook token MUST NOT grant false co-location to lower outer provider.");
        }
    }

    [TestMethod]
    public async Task SplitSmtpPortFirstRotation_IntermediateHostAPortBWithholdsCredentialsAndNeverAuthenticatesHostA_ThenConvergesOnHostBAsync()
    {
        // Delta11 H5 / Delta14 H4: During a split-SMTP port-first rotation ((hostA, portA) -> (hostA, portB) + credB -> (hostB, portB) + credB),
        // BOTH the tracker AND OperatorNotificationDispatcher MUST withhold credentials at the intermediate (hostA, portB)
        // snapshot (never leaking credB to hostA), keep unbound split SMTP withheld even after hostB arrives, and
        // converge ONLY once explicit same-provider Smtp:BoundEndpoint matches (hostB, portB, StartTls)!
        static Dictionary<string, string?> HostDict(string host) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":TestSendMinimumIntervalSeconds"] = "0",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = host,
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test"
        };
        static Dictionary<string, string?> PortDict(int port) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        static Dictionary<string, string?> CredsDict(string tag, string? boundEndpoint = null)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = $"user-{tag}",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = $"pass-{tag}"
            };
            if (boundEndpoint is not null)
            {
                dict[ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = boundEndpoint;
            }

            return dict;
        }

        // Sequence 1: Port -> Creds -> Host (exercising BOTH tracker and OperatorNotificationDispatcher).
        using (var dir = TempStateDir.Create())
        {
            var hostProv = new RotatingHostConfigurationProvider(HostDict("relay-h5-a.example.test"));
            var portProv = new RotatingHostConfigurationProvider(PortDict(587));
            var credsProv = new RotatingHostConfigurationProvider(CredsDict("a"));
            var root = new ConfigurationRoot([hostProv, portProv, credsProv]);

            using var configProvider = new ConfigurationOperatorNotificationConfigProvider(root);
            using var store = CreateStore(dir.Path, isDevelopment: false, configuration: root);
            var capturingSmtp = new CapturingSmtpTransport();
            using var telemetry = new OperatorNotificationTelemetry();
            using var dispatcher = new OperatorNotificationDispatcher(
                store,
                capturingSmtp,
                new WebhookNotificationTransport(new OperatorNotificationNetworkPolicy(isDevelopment: false), TimeProvider.System),
                telemetry,
                TimeProvider.System);

            var initDelivery = await dispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(initDelivery.Channels[0].Succeeded);
            Assert.HasCount(1, capturingSmtp.Captured);
            Assert.AreEqual("relay-h5-a.example.test", capturingSmtp.Captured[0].Host);
            Assert.AreEqual(587, capturingSmtp.Captured[0].Port);
            Assert.AreEqual("user-a", capturingSmtp.Captured[0].Username);
            Assert.AreEqual("pass-a", capturingSmtp.Captured[0].Password);

            // Step 1: Advance port first (587 -> 2525) while host is still hostA and creds are still A.
            portProv.Rotate(PortDict(2525));
            var afterPortOnly = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h5-a.example.test", afterPortOnly.Options.Smtp.Host);
            Assert.AreEqual(2525, afterPortOnly.Options.Smtp.Port);
            Assert.IsNull(afterPortOnly.Secrets.SmtpUsername);
            Assert.IsNull(afterPortOnly.Secrets.SmtpPassword);

            // Step 2: Advance credentials to credB while host is STILL hostA!
            // Explicitly assert intermediate (hostA, portB) snapshot WITHHOLDS credB in tracker, store, AND dispatcher!
            credsProv.Rotate(CredsDict("b"));
            var intermediateConfig = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h5-a.example.test", intermediateConfig.Options.Smtp.Host);
            Assert.AreEqual(2525, intermediateConfig.Options.Smtp.Port);
            Assert.IsNull(intermediateConfig.Secrets.SmtpUsername, "Intermediate (hostA, portB) MUST NOT publish user-b to hostA.");
            Assert.IsNull(intermediateConfig.Secrets.SmtpPassword, "Intermediate (hostA, portB) MUST NOT publish pass-b to hostA.");

            var intermediateStore = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(intermediateStore.Secrets.SmtpUsername);
            Assert.IsNull(intermediateStore.Secrets.SmtpPassword);

            var intermediateDelivery = await dispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(intermediateDelivery.Channels[0].Succeeded);
            Assert.HasCount(2, capturingSmtp.Captured);
            Assert.AreEqual("relay-h5-a.example.test", capturingSmtp.Captured[1].Host);
            Assert.AreEqual(2525, capturingSmtp.Captured[1].Port);
            Assert.IsNull(capturingSmtp.Captured[1].Username, "Dispatcher MUST NOT send user-b to hostA at intermediate (hostA, portB).");
            Assert.IsNull(capturingSmtp.Captured[1].Password, "Dispatcher MUST NOT send pass-b to hostA at intermediate (hostA, portB).");

            // Step 3: Advance host to hostB -> under Delta14 H4 fail-closed invariant, unbound split SMTP stays withheld!
            hostProv.Rotate(HostDict("relay-h5-b.example.test"));
            var unboundAfterHostB = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h5-b.example.test", unboundAfterHostB.Options.Smtp.Host);
            Assert.AreEqual(2525, unboundAfterHostB.Options.Smtp.Port);
            Assert.IsNull(unboundAfterHostB.Secrets.SmtpUsername, "Delta14 H4: split SMTP without explicit BoundEndpoint must stay withheld across destination change.");
            Assert.IsNull(unboundAfterHostB.Secrets.SmtpPassword, "Delta14 H4: split SMTP without explicit BoundEndpoint must stay withheld across destination change.");

            // Step 4: Explicit same-provider Smtp:BoundEndpoint authorizes convergence to (hostB, portB, user-b, pass-b)!
            credsProv.Rotate(CredsDict("b", boundEndpoint: "relay-h5-b.example.test:2525:StartTls"));
            var converged = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h5-b.example.test", converged.Options.Smtp.Host);
            Assert.AreEqual(2525, converged.Options.Smtp.Port);
            Assert.AreEqual("user-b", converged.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", converged.Secrets.SmtpPassword);

            var finalDelivery = await dispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(finalDelivery.Channels[0].Succeeded);
            Assert.HasCount(3, capturingSmtp.Captured);
            Assert.AreEqual("relay-h5-b.example.test", capturingSmtp.Captured[2].Host);
            Assert.AreEqual(2525, capturingSmtp.Captured[2].Port);
            Assert.AreEqual("user-b", capturingSmtp.Captured[2].Username);
            Assert.AreEqual("pass-b", capturingSmtp.Captured[2].Password);
        }

        // Sequence 2: Creds (explicitly bound to hostB:2525:StartTls) -> Port -> Host
        // (explicit intermediate checks at BOTH Creds and Port!).
        {
            var hostProv = new RotatingHostConfigurationProvider(HostDict("relay-h5-a.example.test"));
            var portProv = new RotatingHostConfigurationProvider(PortDict(587));
            var credsProv = new RotatingHostConfigurationProvider(CredsDict("a"));
            var root = new ConfigurationRoot([hostProv, portProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            credsProv.Rotate(CredsDict("b", boundEndpoint: "relay-h5-b.example.test:2525:StartTls"));
            var afterCreds = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(afterCreds.Secrets.SmtpUsername);
            Assert.IsNull(afterCreds.Secrets.SmtpPassword);

            portProv.Rotate(PortDict(2525));
            var afterPort = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h5-a.example.test", afterPort.Options.Smtp.Host);
            Assert.AreEqual(2525, afterPort.Options.Smtp.Port);
            Assert.IsNull(afterPort.Secrets.SmtpUsername, "Creds->Port->Host intermediate (hostA, portB) MUST withhold user-b.");
            Assert.IsNull(afterPort.Secrets.SmtpPassword, "Creds->Port->Host intermediate (hostA, portB) MUST withhold pass-b.");

            hostProv.Rotate(HostDict("relay-h5-b.example.test"));
            var converged = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h5-b.example.test", converged.Options.Smtp.Host);
            Assert.AreEqual(2525, converged.Options.Smtp.Port);
            Assert.AreEqual("user-b", converged.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", converged.Secrets.SmtpPassword);
        }
    }

    [TestMethod]
    public async Task PreviouslyUnauthenticatedDestination_CredentialFirstWithholdsAtAWhileDestinationFirstAndCoLocatedConvergeAsync()
    {
        // Delta11/Delta12 H6: When unauthenticated destination A is already established:
        // 1. Destination-first (A(empty) -> B(empty) -> B(credB)): converges cleanly to (B, credB).
        // 2. Credential-first (separate creds provider reloads credB while destination is still A):
        //    WITHHOLDS credB at A (never leaks B's first credential to A!), even when destination A's provider
        //    reloads with identical destination values (e.g. an unrelated edit), then converges ONLY when
        //    destination moves A -> B (or credential provider carries explicit BoundEndpoint/BoundOrigin for A).
        // 3. Co-located atomic provider adding first credential to A: succeeds immediately!
        static Dictionary<string, string?> DestDict(string host, string url) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = host,
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Ntfy",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = url
        };

        const string urlA = "https://h6-unauth-a.example.test/hook";
        const string urlB = "https://h6-unauth-b.example.test/hook";

        // Case 1: Destination-first (A(empty) -> B(empty) -> B(credB)) converges cleanly.
        {
            var destProv = new RotatingHostConfigurationProvider(DestDict("relay-h6-a.example.test", urlA));
            var credsProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var root = new ConfigurationRoot([destProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            var init = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(init.Secrets.SmtpUsername);
            Assert.IsNull(init.Secrets.WebhookAuthToken);

            destProv.Rotate(DestDict("relay-h6-b.example.test", urlB));
            var onBEmpty = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h6-b.example.test", onBEmpty.Options.Smtp.Host);
            Assert.IsNull(onBEmpty.Secrets.SmtpUsername);

            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "dest-first-user-b",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "dest-first-pass-b",
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "dest-first-tok-b",
                [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = "dest-first-topic-b"
            });

            var converged = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("dest-first-user-b", converged.Secrets.SmtpUsername);
            Assert.AreEqual("dest-first-pass-b", converged.Secrets.SmtpPassword);
            Assert.AreEqual("dest-first-tok-b", converged.Secrets.WebhookAuthToken);
            Assert.AreEqual("dest-first-topic-b", converged.Secrets.WebhookNtfyTopic);
        }

        // Case 2a: Credential-first at established unauthenticated A MUST withhold credB at A,
        //          even across unrelated reloads, then converge to (B, credB) when destination moves A -> B!
        {
            var destProv = new RotatingHostConfigurationProvider(DestDict("relay-h6-a.example.test", urlA));
            var credsProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var unrelatedProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Unrelated:Tick"] = "0"
            });
            var root = new ConfigurationRoot([destProv, credsProv, unrelatedProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            var establishedEmpty = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h6-a.example.test", establishedEmpty.Options.Smtp.Host);
            Assert.AreEqual(urlA, establishedEmpty.Secrets.WebhookUrl);
            Assert.IsNull(establishedEmpty.Secrets.SmtpUsername);
            Assert.IsNull(establishedEmpty.Secrets.WebhookAuthToken);

            // Credential provider reloads FIRST with B's secrets while destination is still A!
            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "cred-first-user-b",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "cred-first-pass-b",
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "cred-first-tok-b",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUserKeyConfigKey] = "cred-first-key-b",
                [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = "cred-first-topic-b"
            });

            var intermediateOnA = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h6-a.example.test", intermediateOnA.Options.Smtp.Host);
            Assert.AreEqual(urlA, intermediateOnA.Secrets.WebhookUrl);
            Assert.IsNull(intermediateOnA.Secrets.SmtpUsername, "Credential-first reload MUST NOT leak B's first SMTP username to unauthenticated A.");
            Assert.IsNull(intermediateOnA.Secrets.SmtpPassword, "Credential-first reload MUST NOT leak B's first SMTP password to unauthenticated A.");
            Assert.IsNull(intermediateOnA.Secrets.WebhookAuthToken, "Credential-first reload MUST NOT leak B's first webhook token to unauthenticated A.");
            Assert.IsNull(intermediateOnA.Secrets.WebhookUserKey, "Credential-first reload MUST NOT leak B's first webhook user key to unauthenticated A.");
            Assert.IsNull(intermediateOnA.Secrets.WebhookNtfyTopic, "Credential-first reload MUST NOT leak B's first ntfy topic to unauthenticated A.");

            // Unrelated reload must STILL keep credB withheld at A.
            unrelatedProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Unrelated:Tick"] = "1" });
            var afterUnrelated = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(afterUnrelated.Secrets.SmtpUsername);
            Assert.IsNull(afterUnrelated.Secrets.WebhookAuthToken);

            // Now destination rotates A -> B -> MUST converge cleanly to (B, credB)!
            destProv.Rotate(DestDict("relay-h6-b.example.test", urlB));
            var convergedOnB = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h6-b.example.test", convergedOnB.Options.Smtp.Host);
            Assert.AreEqual(urlB, convergedOnB.Secrets.WebhookUrl);
            Assert.AreEqual("cred-first-user-b", convergedOnB.Secrets.SmtpUsername);
            Assert.AreEqual("cred-first-pass-b", convergedOnB.Secrets.SmtpPassword);
            Assert.AreEqual("cred-first-tok-b", convergedOnB.Secrets.WebhookAuthToken);
            Assert.AreEqual("cred-first-key-b", convergedOnB.Secrets.WebhookUserKey);
            Assert.AreEqual("cred-first-topic-b", convergedOnB.Secrets.WebhookNtfyTopic);
        }

        // Case 2b (Delta12 H6): Reloading unchanged destination A (including unrelated edits in A's provider)
        // MUST keep B's withheld first credential withheld at A until destination B (or explicit binding / co-location) arrives!
        {
            var destProv = new RotatingHostConfigurationProvider(DestDict("relay-h6-a.example.test", urlA));
            var credsProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var root = new ConfigurationRoot([destProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "withheld-user-b",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "withheld-pass-b",
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "withheld-tok-b"
            });
            var withheldBeforeReload = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(withheldBeforeReload.Secrets.SmtpUsername);
            Assert.IsNull(withheldBeforeReload.Secrets.SmtpPassword);
            Assert.IsNull(withheldBeforeReload.Secrets.WebhookAuthToken);

            // Reload destination provider with IDENTICAL destination A values.
            destProv.Rotate(DestDict("relay-h6-a.example.test", urlA));
            var afterIdenticalReload = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h6-a.example.test", afterIdenticalReload.Options.Smtp.Host);
            Assert.AreEqual(urlA, afterIdenticalReload.Secrets.WebhookUrl);
            Assert.IsNull(afterIdenticalReload.Secrets.SmtpUsername,
                "Reloading unchanged destination A MUST NOT release a withheld unbound SMTP username at A.");
            Assert.IsNull(afterIdenticalReload.Secrets.SmtpPassword,
                "Reloading unchanged destination A MUST NOT release a withheld unbound SMTP password at A.");
            Assert.IsNull(afterIdenticalReload.Secrets.WebhookAuthToken,
                "Reloading unchanged destination A MUST NOT release a withheld unbound webhook token at A.");

            // Unrelated setting edit inside destination A's provider (destination A unchanged) must STILL withhold.
            var unrelatedEditInA = DestDict("relay-h6-a.example.test", urlA);
            unrelatedEditInA[OperatorNotificationsOptions.SectionKey + ":Smtp:FromDisplayName"] = "Updated Display Name";
            destProv.Rotate(unrelatedEditInA);
            var afterUnrelatedEditInA = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("Updated Display Name", afterUnrelatedEditInA.Options.Smtp.FromDisplayName);
            Assert.IsNull(afterUnrelatedEditInA.Secrets.SmtpUsername,
                "Unrelated edit in destination A's provider MUST NOT release a withheld unbound SMTP username at A.");
            Assert.IsNull(afterUnrelatedEditInA.Secrets.SmtpPassword,
                "Unrelated edit in destination A's provider MUST NOT release a withheld unbound SMTP password at A.");
            Assert.IsNull(afterUnrelatedEditInA.Secrets.WebhookAuthToken,
                "Unrelated edit in destination A's provider MUST NOT release a withheld unbound webhook token at A.");

            // Once destination actually moves A -> B, B's credential converges cleanly!
            destProv.Rotate(DestDict("relay-h6-b.example.test", urlB));
            var convergedAtB = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h6-b.example.test", convergedAtB.Options.Smtp.Host);
            Assert.AreEqual(urlB, convergedAtB.Secrets.WebhookUrl);
            Assert.AreEqual("withheld-user-b", convergedAtB.Secrets.SmtpUsername);
            Assert.AreEqual("withheld-pass-b", convergedAtB.Secrets.SmtpPassword);
            Assert.AreEqual("withheld-tok-b", convergedAtB.Secrets.WebhookAuthToken);
        }

        // Case 2c: Explicit same-provider BoundEndpoint/BoundOrigin from the credential provider admits first credentials at unchanged A.
        {
            var destProv = new RotatingHostConfigurationProvider(DestDict("relay-h6-a.example.test", urlA));
            var credsProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var root = new ConfigurationRoot([destProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "explicit-user-a",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "explicit-pass-a",
                [ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-h6-a.example.test:587:StartTls",
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "explicit-tok-a",
                [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlA
            });
            var afterExplicitBind = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("explicit-user-a", afterExplicitBind.Secrets.SmtpUsername);
            Assert.AreEqual("explicit-pass-a", afterExplicitBind.Secrets.SmtpPassword);
            Assert.AreEqual("explicit-tok-a", afterExplicitBind.Secrets.WebhookAuthToken);
        }

        // Case 3: Co-located atomic provider adding first credentials to A succeeds immediately!
        {
            var combinedProv = new RotatingHostConfigurationProvider(DestDict("relay-h6-a.example.test", urlA));
            var root = new ConfigurationRoot([combinedProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            var initEmpty = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(initEmpty.Secrets.SmtpUsername);
            Assert.IsNull(initEmpty.Secrets.WebhookAuthToken);

            var withCreds = DestDict("relay-h6-a.example.test", urlA);
            withCreds[ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "colocated-user-a";
            withCreds[ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "colocated-pass-a";
            withCreds[ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "colocated-tok-a";
            combinedProv.Rotate(withCreds);

            var afterColocated = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("colocated-user-a", afterColocated.Secrets.SmtpUsername);
            Assert.AreEqual("colocated-pass-a", afterColocated.Secrets.SmtpPassword);
            Assert.AreEqual("colocated-tok-a", afterColocated.Secrets.WebhookAuthToken);
        }
    }

    [TestMethod]
    public async Task StaleDirectSecretStoreSave_EditingOneFieldNeverOverwritesSupersededSiblingCredentialAsync()
    {
        // Delta11 M-Add-1: Reading r1 (user1, pass1), rotating password to pass2 (r2), then saving stale r1
        // editing ONLY username to user2 must NEVER overwrite pass2 back with stale pass1 (for both SMTP and
        // Webhook sibling fields, with or without provenance)!
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider);

        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-m1.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions
            {
                Enabled = true,
                Format = WebhookPayloadFormat.Pushover
            }
        };

        var s0 = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var s1 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s0.Version,
            options,
            SmtpUsername: "user1",
            SmtpPassword: "pass1",
            WebhookUrl: "https://api.pushover.net/1/messages.json",
            WebhookAuthToken: "tok1",
            WebhookUserKey: "ukey1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, s1.Outcome, s1.Message);

        // Read r1 containing (user1, pass1, tok1, ukey1).
        var r1 = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("user1", r1.SmtpUsername);
        Assert.AreEqual("pass1", r1.SmtpPassword);
        Assert.AreEqual("tok1", r1.WebhookAuthToken);
        Assert.AreEqual("ukey1", r1.WebhookUserKey);

        // Concurrent save r2 rotates SMTP password pass1 -> pass2 and webhook token tok1 -> tok2.
        var s2 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s1.Snapshot!.Version,
            options,
            SmtpPassword: "pass2",
            WebhookAuthToken: "tok2"), "owner-2", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, s2.Outcome, s2.Message);

        // Stale r1 edits ONLY SMTP username (user1 -> user2) and webhook user key (ukey1 -> ukey2).
        // Newer sibling credentials pass2 and tok2 MUST be preserved (NEVER rolled back to pass1 / tok1)!
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            r1 with { SmtpUsername = "user2", WebhookUserKey = "ukey2" },
            CancellationToken.None).ConfigureAwait(false);

        var effAfterStaleProv = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("user2", effAfterStaleProv.Secrets.SmtpUsername);
        Assert.AreEqual("pass2", effAfterStaleProv.Secrets.SmtpPassword,
            "Stale r1 editing only SMTP username MUST preserve rotated sibling password pass2 (never roll back to pass1).");
        Assert.AreEqual("tok2", effAfterStaleProv.Secrets.WebhookAuthToken,
            "Stale r1 editing only Webhook userKey MUST preserve rotated sibling token tok2 (never roll back to tok1).");
        Assert.AreEqual("ukey2", effAfterStaleProv.Secrets.WebhookUserKey);

        // Even a provenance-stripped manual copy carrying superseded pass1 / tok1 while editing user2 -> user3
        // MUST preserve pass2 and tok2!
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            new OperatorNotificationSecrets(
                SmtpUsername: "user3",
                SmtpPassword: "pass1",
                WebhookUrl: "https://api.pushover.net/1/messages.json",
                WebhookAuthToken: "tok1",
                WebhookUserKey: "ukey3"),
            CancellationToken.None).ConfigureAwait(false);

        var effAfterManualCopy = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("user3", effAfterManualCopy.Secrets.SmtpUsername);
        Assert.AreEqual("pass2", effAfterManualCopy.Secrets.SmtpPassword,
            "Provenance-stripped copy carrying superseded pass1 MUST NOT roll back pass2.");
        Assert.AreEqual("tok2", effAfterManualCopy.Secrets.WebhookAuthToken,
            "Provenance-stripped copy carrying superseded tok1 MUST NOT roll back tok2.");
        Assert.AreEqual("ukey3", effAfterManualCopy.Secrets.WebhookUserKey);
    }

    [TestMethod]
    public void EnsureRestrictedDirectory_RetryDoesNotReuseStaleAllowSymlinkedParentWhenCurrentWalkClassifiesParentAsOrdinary()
    {
        // Delta11 M-Add-2: A failed first attempt where parent was a pre-existing symlink must NEVER leave a
        // cached allowSymlinkedParent=true flag that a retry reuses once parent is classified as an ordinary
        // directory by the retry's own single non-following walk!
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("POSIX symlink parent O_NOFOLLOW verification is Unix-only.");
            return;
        }

        RequireModesEnforced();
        using var dir = TempStateDir.Create();

        var realParent1 = Path.Combine(dir.Path, "real-parent-1");
        Directory.CreateDirectory(realParent1);
        var outsideTarget = Path.Combine(dir.Path, "outside-target");
        Directory.CreateDirectory(outsideTarget);

        var parentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(dir.Path, "parent-slot")));
        var childPath = Path.Combine(parentPath, "child");
        var statePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(childPath, "state")));

        // Attempt 1: parent-slot is a pre-existing SYMLINK -> realParent1. Fail immediately after state sync
        // so 'child' remains pending in PendingParentSyncDirectories!
        Directory.CreateSymbolicLink(parentPath, realParent1);
        Assert.ThrowsExactly<IOException>(() =>
            NotificationStateFilePublisher.EnsureRestrictedDirectory(
                statePath,
                onDirectorySynced: synced =>
                {
                    if (string.Equals(synced, statePath, StringComparison.Ordinal))
                    {
                        throw new IOException("Simulated interruption after state sync before parent sync.");
                    }
                },
                directorySyncAction: null));

        // Replace parent-slot with an ORDINARY directory containing child/state before retry!
        File.Delete(parentPath);
        Directory.CreateDirectory(statePath);
        var parkedOrdinaryParent = Path.Combine(dir.Path, "parked-ordinary-parent");
        bool? observedAllowSymlinkForParent = null;

        // Attempt 2 (retry): current walk classifies parent-slot as an ORDINARY directory.
        // Swap parent-slot to a symlink right before directory sync -> MUST observe allowSymlinkedParent==false
        // AND reject the symlink replacement with IOException (O_NOFOLLOW)!
        try
        {
            Assert.ThrowsExactly<IOException>(() =>
                NotificationStateFilePublisher.EnsureRestrictedDirectory(
                    statePath,
                    onDirectorySynced: null,
                    directorySyncAction: null,
                    beforeAnchorOpen: null,
                    afterAncestorClassified: null,
                    beforeDirectorySync: (syncTarget, allowSymlinkedParent) =>
                    {
                        if (string.Equals(syncTarget, parentPath, StringComparison.Ordinal))
                        {
                            observedAllowSymlinkForParent = allowSymlinkedParent;
                            Directory.Move(parentPath, parkedOrdinaryParent);
                            Directory.CreateSymbolicLink(parentPath, outsideTarget);
                        }
                    }));

            Assert.IsNotNull(observedAllowSymlinkForParent, "Retry must attempt to sync pending parent-slot.");
            Assert.IsFalse(observedAllowSymlinkForParent.Value,
                "Retry MUST NOT reuse Attempt 1's stale allowSymlinkedParent=true flag when current walk classifies parent as ordinary.");
        }
        finally
        {
            if (File.Exists(parentPath) || Directory.Exists(parentPath))
            {
                if (new DirectoryInfo(parentPath).LinkTarget is not null)
                {
                    Directory.Delete(parentPath);
                }
            }
            if (Directory.Exists(parkedOrdinaryParent) && !Directory.Exists(parentPath))
            {
                Directory.Move(parkedOrdinaryParent, parentPath);
            }
        }

        // Attempt 3: clean retry with ordinary parent succeeds and clears pending state.
        bool? cleanAllowSymlink = null;
        NotificationStateFilePublisher.EnsureRestrictedDirectory(
            statePath,
            onDirectorySynced: null,
            directorySyncAction: null,
            beforeAnchorOpen: null,
            afterAncestorClassified: null,
            beforeDirectorySync: (syncTarget, allowSymlinkedParent) =>
            {
                if (string.Equals(syncTarget, parentPath, StringComparison.Ordinal))
                {
                    cleanAllowSymlink = allowSymlinkedParent;
                }
            });
        Assert.AreEqual(false, cleanAllowSymlink);
    }

    [TestMethod]
    public async Task ProtectedStateReads_RejectSymlinksAndFifosWithoutBlockingAndEnforceMaxBytesViaDescriptorAsync()
    {
        // Delta11 M-Add-3: Startup reads, async reads, ReadBoundedBytes*, and heartbeat reads must all use
        // pinned parent dirFd + openat(O_RDONLY|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC) + fstat(fd), rejecting
        // symlinks and FIFOs immediately without blocking and enforcing max-bytes before reading full payload.
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("POSIX FIFO/openat/fstat descriptor verification is Unix-only.");
            return;
        }

        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "fallback.example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-secret"
            })
            .Build();

        // 1. FIFO at operator-notifications.json (no writer): startup, async read, GetSnapshot, ReadBoundedBytes*
        //    MUST complete immediately without blocking and fail closed as StoreUnreadable (CanPersist=false)!
        var fifoDir = Path.Combine(dir.Path, "fifo-state");
        Directory.CreateDirectory(fifoDir);
        var fifoFile = Path.Combine(fifoDir, OperatorNotificationStore.ConfigFileName);
        using (var mkfifo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo")
        {
            ArgumentList = { fifoFile },
            UseShellExecute = false
        }))
        {
            Assert.IsNotNull(mkfifo);
            using var mkfifoCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await mkfifo.WaitForExitAsync(mkfifoCts.Token).ConfigureAwait(false);
            Assert.AreEqual(0, mkfifo.ExitCode);
        }

        var fifoReadTask = Task.Run(async () =>
        {
            Assert.ThrowsExactly<IOException>(() => NotificationStateFilePublisher.ReadBoundedBytes(fifoFile, 1024));
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await NotificationStateFilePublisher.ReadBoundedBytesAsync(fifoFile, 1024, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            var syncReadFifo = NotificationStateFilePublisher.ReadProtectedStateFile(fifoDir, OperatorNotificationStore.ConfigFileName, 1024);
            Assert.AreEqual(ProtectedStateFileReadStatus.Unreadable, syncReadFifo.Status);
            Assert.IsFalse(syncReadFifo.CanReplace);

            var asyncReadFifo = await NotificationStateFilePublisher.ReadProtectedStateFileAsync(
                fifoDir, OperatorNotificationStore.ConfigFileName, 1024, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProtectedStateFileReadStatus.Unreadable, asyncReadFifo.Status);
            Assert.IsFalse(asyncReadFifo.CanReplace);

            using var storeOnFifo = CreateStore(fifoDir, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);
            Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, storeOnFifo.ConfigurationErrorReasonCode);
            var snapFifo = await storeOnFifo.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(snapFifo.StoreCorrupt);
            Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, snapFifo.StoreStatusReasonCode);
            Assert.IsFalse(snapFifo.CanPersist);
            Assert.IsFalse(snapFifo.Secrets.HasSmtpPassword);

            var effFifo = await storeOnFifo.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(effFifo.StoreCorrupt);
            Assert.IsTrue(effFifo.Secrets.IsEmpty);
        });

        try
        {
            await fifoReadTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TryUnblockFifoReader(fifoFile);
            Assert.Fail("Reading a FIFO protected state path blocked past the 10s bounded timeout (O_NONBLOCK regression).");
        }

        // 2. Symlink at target file: ReadBoundedBytes* and ReadProtectedStateFile* MUST reject with O_NOFOLLOW!
        var symlinkDir = Path.Combine(dir.Path, "symlink-state");
        Directory.CreateDirectory(symlinkDir);
        var externalSecretFile = Path.Combine(dir.Path, "outside-secret.json");
        await File.WriteAllTextAsync(externalSecretFile, "{\"secret\":true}").ConfigureAwait(false);
        var symlinkFile = Path.Combine(symlinkDir, OperatorNotificationStore.ConfigFileName);
        File.CreateSymbolicLink(symlinkFile, externalSecretFile);

        Assert.ThrowsExactly<IOException>(() => NotificationStateFilePublisher.ReadBoundedBytes(symlinkFile, 1024));
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await NotificationStateFilePublisher.ReadBoundedBytesAsync(symlinkFile, 1024, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        var syncReadLink = NotificationStateFilePublisher.ReadProtectedStateFile(symlinkDir, OperatorNotificationStore.ConfigFileName, 1024);
        Assert.AreEqual(ProtectedStateFileReadStatus.Unreadable, syncReadLink.Status);
        Assert.IsTrue(syncReadLink.IsSymlink);
        Assert.IsFalse(syncReadLink.CanReplace);

        // 3. Oversized regular file rejected via fstat(fd) before reading payload bytes!
        var oversizedDir = Path.Combine(dir.Path, "oversized-state");
        Directory.CreateDirectory(oversizedDir);
        var oversizedFile = Path.Combine(oversizedDir, OperatorNotificationStore.ConfigFileName);
        await File.WriteAllBytesAsync(oversizedFile, new byte[4097]).ConfigureAwait(false);

        var boundedSync = NotificationStateFilePublisher.ReadBoundedBytes(oversizedFile, 4096);
        Assert.IsTrue(boundedSync.ExceededLimit);
        Assert.AreEqual(0, boundedSync.Content.Length);

        var boundedAsync = await NotificationStateFilePublisher.ReadBoundedBytesAsync(oversizedFile, 4096, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(boundedAsync.ExceededLimit);
        Assert.AreEqual(0, boundedAsync.Content.Length);

        var protectedOversized = await NotificationStateFilePublisher.ReadProtectedStateFileAsync(
            oversizedDir, OperatorNotificationStore.ConfigFileName, 4096, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProtectedStateFileReadStatus.ExceededLimit, protectedOversized.Status);
        Assert.IsFalse(protectedOversized.CanReplace);
        Assert.AreEqual(4097, protectedOversized.SizeBytes);
    }

    [TestMethod]
    public async Task LegacyFull128EntryRetiredHistoryUpgrade_WithoutTruncationMarkers_ConservativelyRejectsEvictedCred1AtBAsync()
    {
        // Delta12 H1: A legacy delta10 store envelope persisted up to 128 retired entries without truncation markers
        // after evicting older entries. If the store had already moved to B before upgrading, loading that legacy
        // 128-entry envelope MUST conservatively mark truncation so unversioned retries carrying evicted cred #1
        // from A are rejected at B, while versioned saves at B continue to succeed.
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        const string originA = "HTTPS://LEGACY-A.EXAMPLE.TEST:443";
        const string endpointA = "RELAY-LEGACY-A.EXAMPLE.TEST:587:StartTls";
        const string urlB = "https://legacy-b.example.test/hook";
        const string salt = "0123456789abcdef0123456789abcdef";

        static string SaltedHash(string s, string value)
            => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{s}:{value}")));

        var legacyRetiredSmtps = new List<object>(OperatorNotificationStore.MaxRetiredBindingsPerChannel);
        var legacyRetiredWebhooks = new List<object>(OperatorNotificationStore.MaxRetiredBindingsPerChannel);
        // Simulate cred #1 already evicted under delta10: list holds exactly 128 entries (#2..#129).
        for (var i = 2; i <= OperatorNotificationStore.MaxRetiredBindingsPerChannel + 1; i++)
        {
            legacyRetiredSmtps.Add(new
            {
                endpoint = endpointA,
                usernameHash = SaltedHash(salt, $"legacy-user-a-{i}"),
                passwordHash = SaltedHash(salt, $"legacy-pass-a-{i}")
            });
            legacyRetiredWebhooks.Add(new
            {
                origin = originA,
                authTokenHash = SaltedHash(salt, $"legacy-tok-a-{i}")
            });
        }

        // Legacy delta10 payload omits truncatedWebhookOrigins, anyWebhookHistoryTruncated,
        // truncatedSmtpEndpoints, and anySmtpHistoryTruncated completely.
        var legacyProtectedPayload = new
        {
            webhookUrl = urlB,
            retiredSalt = salt,
            retiredWebhooks = legacyRetiredWebhooks,
            retiredSmtps = legacyRetiredSmtps
        };

        var protector = dpProvider.CreateProtector("CameraAgent", "OperatorNotifications", "v1");
        var protectedBlob = protector.Protect(JsonSerializer.Serialize(legacyProtectedPayload, WebJsonOptions));
        var optionsB = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-legacy-b.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };
        var legacyEnvelope = new
        {
            schemaVersion = 1,
            revision = "legacy-delta10-rev-1",
            options = optionsB,
            protectedSecrets = protectedBlob
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName),
            JsonSerializer.Serialize(legacyEnvelope, WebJsonOptions),
            CancellationToken.None).ConfigureAwait(false);

        using (var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider))
        {
            // Unversioned retry carrying evicted cred #1 from A MUST be rejected at B!
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                new OperatorNotificationSecrets(
                    SmtpUsername: "legacy-user-a-1",
                    SmtpPassword: "legacy-pass-a-1",
                    WebhookUrl: urlB,
                    WebhookAuthToken: "legacy-tok-a-1"),
                CancellationToken.None).ConfigureAwait(false);

            var effAfterRetryOnEmptyB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-legacy-b.example.test", effAfterRetryOnEmptyB.Options.Smtp.Host);
            Assert.AreEqual(urlB, effAfterRetryOnEmptyB.Secrets.WebhookUrl);
            Assert.IsNull(effAfterRetryOnEmptyB.Secrets.SmtpUsername,
                "Upgraded full 128-entry legacy envelope MUST conservatively reject evicted cred #1 SMTP username at B.");
            Assert.IsNull(effAfterRetryOnEmptyB.Secrets.SmtpPassword,
                "Upgraded full 128-entry legacy envelope MUST conservatively reject evicted cred #1 SMTP password at B.");
            Assert.IsNull(effAfterRetryOnEmptyB.Secrets.WebhookAuthToken,
                "Upgraded full 128-entry legacy envelope MUST conservatively reject evicted cred #1 webhook token at B.");

            // Versioned save at B still succeeds cleanly and persists the conservative truncation flag.
            var snapB = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var savedB = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snapB.Version,
                optionsB,
                SmtpUsername: "legacy-user-b-1",
                SmtpPassword: "legacy-pass-b-1",
                WebhookAuthToken: "legacy-tok-b-1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedB.Outcome, savedB.Message);
        }

        // Reloading from disk after the versioned save preserves truncation protection AND fresh-provenance saves.
        using (var reloadedStore = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider))
        {
            await ((IOperatorNotificationSecretStore)reloadedStore).SaveAsync(
                new OperatorNotificationSecrets(
                    SmtpUsername: "legacy-user-a-1",
                    SmtpPassword: "legacy-pass-a-1",
                    WebhookUrl: urlB,
                    WebhookAuthToken: "legacy-tok-a-1"),
                CancellationToken.None).ConfigureAwait(false);

            var effPreservedB = await reloadedStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("legacy-user-b-1", effPreservedB.Secrets.SmtpUsername);
            Assert.AreEqual("legacy-pass-b-1", effPreservedB.Secrets.SmtpPassword);
            Assert.AreEqual("legacy-tok-b-1", effPreservedB.Secrets.WebhookAuthToken);

            var freshRead = await ((IOperatorNotificationSecretStore)reloadedStore).ReadAsync(CancellationToken.None).ConfigureAwait(false);
            await ((IOperatorNotificationSecretStore)reloadedStore).SaveAsync(
                freshRead with { SmtpUsername = "legacy-user-b-2", WebhookAuthToken = "legacy-tok-b-2" },
                CancellationToken.None).ConfigureAwait(false);
            var effAfterFresh = await reloadedStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("legacy-user-b-2", effAfterFresh.Secrets.SmtpUsername);
            Assert.AreEqual("legacy-tok-b-2", effAfterFresh.Secrets.WebhookAuthToken);
        }
    }

    [TestMethod]
    public async Task SplitSmtpRemovingSeparatePortOverride_RetainsSplitGuardAtHostAPortB_ThenConvergesOnHostBAsync()
    {
        // Delta12 H5: Removing a separate Smtp:Port override drops effective port back to the host provider or
        // default 587 while hostA has NOT moved and credentials remain in a separate provider. Retaining the prior
        // split state MUST keep credB withheld at (hostA, 587) and converge cleanly once hostB arrives!
        static Dictionary<string, string?> HostDict(string host, int? explicitPort = null)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":TestSendMinimumIntervalSeconds"] = "0",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = host,
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test"
            };
            if (explicitPort is not null)
            {
                dict[OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] =
                    explicitPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return dict;
        }
        static Dictionary<string, string?> PortDict(int port) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        static Dictionary<string, string?> CredsDict(string tag) => new(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = $"user-{tag}",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = $"pass-{tag}"
        };

        // Sequence 1 (RemovePort -> Creds -> Host), covering BOTH fallback to default 587 AND fallback to host provider's 587.
        foreach (int? hostFallbackPort in new int?[] { null, 587 })
        {
            using var dir = TempStateDir.Create();
            var hostProv = new RotatingHostConfigurationProvider(HostDict("relay-rmport-a.example.test", hostFallbackPort));
            var portProv = new RotatingHostConfigurationProvider(PortDict(2525));
            var credsProv = new RotatingHostConfigurationProvider(CredsDict("a"));
            var root = new ConfigurationRoot([hostProv, portProv, credsProv]);

            using var configProvider = new ConfigurationOperatorNotificationConfigProvider(root);
            using var store = CreateStore(dir.Path, isDevelopment: false, configuration: root);
            var capturingSmtp = new CapturingSmtpTransport();
            using var telemetry = new OperatorNotificationTelemetry();
            using var dispatcher = new OperatorNotificationDispatcher(
                store,
                capturingSmtp,
                new WebhookNotificationTransport(new OperatorNotificationNetworkPolicy(isDevelopment: false), TimeProvider.System),
                telemetry,
                TimeProvider.System);

            var init = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-rmport-a.example.test", init.Options.Smtp.Host);
            Assert.AreEqual(2525, init.Options.Smtp.Port);
            Assert.AreEqual("user-a", init.Secrets.SmtpUsername);
            Assert.AreEqual("pass-a", init.Secrets.SmtpPassword);

            // Step 1: Separate port provider removes its override -> effective port falls back to 587 at hostA.
            portProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var afterRemovePort = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-rmport-a.example.test", afterRemovePort.Options.Smtp.Host);
            Assert.AreEqual(587, afterRemovePort.Options.Smtp.Port);
            Assert.IsNull(afterRemovePort.Secrets.SmtpUsername);
            Assert.IsNull(afterRemovePort.Secrets.SmtpPassword);

            // Step 2: Separate creds provider publishes credB while host is STILL hostA -> MUST stay withheld!
            credsProv.Rotate(CredsDict("b"));
            var intermediate = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-rmport-a.example.test", intermediate.Options.Smtp.Host);
            Assert.AreEqual(587, intermediate.Options.Smtp.Port);
            Assert.IsNull(intermediate.Secrets.SmtpUsername,
                "Removing separate port override MUST retain split guard and withhold user-b at (hostA, 587).");
            Assert.IsNull(intermediate.Secrets.SmtpPassword,
                "Removing separate port override MUST retain split guard and withhold pass-b at (hostA, 587).");

            var deliveryAtHostA = await dispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(deliveryAtHostA.Channels[0].Succeeded);
            Assert.HasCount(1, capturingSmtp.Captured);
            Assert.AreEqual("relay-rmport-a.example.test", capturingSmtp.Captured[0].Host);
            Assert.AreEqual(587, capturingSmtp.Captured[0].Port);
            Assert.IsNull(capturingSmtp.Captured[0].Username, "Dispatcher MUST NOT send user-b to hostA after port override removal.");
            Assert.IsNull(capturingSmtp.Captured[0].Password, "Dispatcher MUST NOT send pass-b to hostA after port override removal.");

            // Step 3: Host moves to hostB -> unbound split-originated rotation stays withheld until explicit
            //         same-provider Smtp:BoundEndpoint authorizes (hostB, 587, StartTls) unambiguously!
            hostProv.Rotate(HostDict("relay-rmport-b.example.test", hostFallbackPort));
            var withheldUnbound = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-rmport-b.example.test", withheldUnbound.Options.Smtp.Host);
            Assert.AreEqual(587, withheldUnbound.Options.Smtp.Port);
            Assert.IsNull(withheldUnbound.Secrets.SmtpUsername);
            Assert.IsNull(withheldUnbound.Secrets.SmtpPassword);

            var boundB = CredsDict("b");
            boundB[ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-rmport-b.example.test:587:StartTls";
            credsProv.Rotate(boundB);
            var converged = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-rmport-b.example.test", converged.Options.Smtp.Host);
            Assert.AreEqual(587, converged.Options.Smtp.Port);
            Assert.AreEqual("user-b", converged.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", converged.Secrets.SmtpPassword);

            var deliveryAtHostB = await dispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(deliveryAtHostB.Channels[0].Succeeded);
            Assert.HasCount(2, capturingSmtp.Captured);
            Assert.AreEqual("relay-rmport-b.example.test", capturingSmtp.Captured[1].Host);
            Assert.AreEqual(587, capturingSmtp.Captured[1].Port);
            Assert.AreEqual("user-b", capturingSmtp.Captured[1].Username);
            Assert.AreEqual("pass-b", capturingSmtp.Captured[1].Password);
        }

        // Sequence 2 (Creds -> RemovePort -> Host): credB arrives first with explicit BoundEndpoint, withheld at hostA, converges on hostB.
        {
            var hostProv = new RotatingHostConfigurationProvider(HostDict("relay-rmport-a.example.test"));
            var portProv = new RotatingHostConfigurationProvider(PortDict(2525));
            var credsProv = new RotatingHostConfigurationProvider(CredsDict("a"));
            var root = new ConfigurationRoot([hostProv, portProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            var boundB = CredsDict("b");
            boundB[ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-rmport-b.example.test:587:StartTls";
            credsProv.Rotate(boundB);
            var afterCreds = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(afterCreds.Secrets.SmtpUsername);
            Assert.IsNull(afterCreds.Secrets.SmtpPassword);

            portProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var afterRemovePort = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-rmport-a.example.test", afterRemovePort.Options.Smtp.Host);
            Assert.AreEqual(587, afterRemovePort.Options.Smtp.Port);
            Assert.IsNull(afterRemovePort.Secrets.SmtpUsername);
            Assert.IsNull(afterRemovePort.Secrets.SmtpPassword);

            hostProv.Rotate(HostDict("relay-rmport-b.example.test"));
            var converged = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-rmport-b.example.test", converged.Options.Smtp.Host);
            Assert.AreEqual(587, converged.Options.Smtp.Port);
            Assert.AreEqual("user-b", converged.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", converged.Secrets.SmtpPassword);
        }

        // Sequence 3 (Host -> Creds -> RemovePort): intermediate (hostB, 2525) withholds B's credentials;
        // explicit BoundEndpoint converges cleanly once port override is removed and (hostB, 587, StartTls) settles!
        {
            using var dir = TempStateDir.Create();
            var hostProv = new RotatingHostConfigurationProvider(HostDict("relay-rmport-a.example.test"));
            var portProv = new RotatingHostConfigurationProvider(PortDict(2525));
            var credsProv = new RotatingHostConfigurationProvider(CredsDict("a"));
            var root = new ConfigurationRoot([hostProv, portProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);
            using var store = CreateStore(dir.Path, isDevelopment: false, configuration: root);
            var capturingSmtp = new CapturingSmtpTransport();
            using var telemetry = new OperatorNotificationTelemetry();
            using var dispatcher = new OperatorNotificationDispatcher(
                store,
                capturingSmtp,
                new WebhookNotificationTransport(new OperatorNotificationNetworkPolicy(isDevelopment: false), TimeProvider.System),
                telemetry,
                TimeProvider.System);

            hostProv.Rotate(HostDict("relay-rmport-b.example.test"));
            var boundB = CredsDict("b");
            boundB[ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-rmport-b.example.test:587:StartTls";
            credsProv.Rotate(boundB);
            var atHostBPortA = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-rmport-b.example.test", atHostBPortA.Options.Smtp.Host);
            Assert.AreEqual(2525, atHostBPortA.Options.Smtp.Port);
            Assert.IsNull(atHostBPortA.Secrets.SmtpUsername,
                "Intermediate split (hostB, 2525) MUST withhold user-b before (hostB, 587) arrives.");
            Assert.IsNull(atHostBPortA.Secrets.SmtpPassword,
                "Intermediate split (hostB, 2525) MUST withhold pass-b before (hostB, 587) arrives.");

            var deliveryAtIntermediate = await dispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(deliveryAtIntermediate.Channels[0].Succeeded);
            Assert.HasCount(1, capturingSmtp.Captured);
            Assert.AreEqual("relay-rmport-b.example.test", capturingSmtp.Captured[0].Host);
            Assert.AreEqual(2525, capturingSmtp.Captured[0].Port);
            Assert.IsNull(capturingSmtp.Captured[0].Username, "Dispatcher MUST NOT send user-b to intermediate hostB:2525.");
            Assert.IsNull(capturingSmtp.Captured[0].Password, "Dispatcher MUST NOT send pass-b to intermediate hostB:2525.");

            portProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var converged = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-rmport-b.example.test", converged.Options.Smtp.Host);
            Assert.AreEqual(587, converged.Options.Smtp.Port);
            Assert.AreEqual("user-b", converged.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", converged.Secrets.SmtpPassword);

            var deliveryAtFinal = await dispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(deliveryAtFinal.Channels[0].Succeeded);
            Assert.HasCount(2, capturingSmtp.Captured);
            Assert.AreEqual("relay-rmport-b.example.test", capturingSmtp.Captured[1].Host);
            Assert.AreEqual(587, capturingSmtp.Captured[1].Port);
            Assert.AreEqual("user-b", capturingSmtp.Captured[1].Username);
            Assert.AreEqual("pass-b", capturingSmtp.Captured[1].Password);
        }
    }

    [TestMethod]
    public async Task DirectSecretStoreWebhookPathEdit_StripsCopiedTokenAndRespectsExplicitlyClearedSiblingSecretAsync()
    {
        // Delta12 High-new: A fresh direct-store save changing /receiver-a -> /receiver-b on the same origin
        // MUST strip a copied token AND NEVER restore that token or an explicitly cleared sibling secret (userKey).
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider);

        const string pathA = "https://hooks.example.com/receiver-a";
        const string pathB = "https://hooks.example.com/receiver-b";
        const string pathC = "https://hooks.example.com/receiver-c";

        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Webhook = new WebhookNotificationOptions
            {
                Enabled = true,
                Format = WebhookPayloadFormat.Pushover
            }
        };

        var s0 = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var savedA = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s0.Version,
            options,
            WebhookUrl: pathA,
            WebhookAuthToken: "tok-receiver-a",
            WebhookUserKey: "ukey-receiver-a"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedA.Outcome, savedA.Message);

        // Fresh read at /receiver-a, then change URL to /receiver-b while keeping copied tok-receiver-a and explicitly clearing userKey.
        var freshReadA = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pathA, freshReadA.WebhookUrl);
        Assert.AreEqual("tok-receiver-a", freshReadA.WebhookAuthToken);
        Assert.AreEqual("ukey-receiver-a", freshReadA.WebhookUserKey);

        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            freshReadA with { WebhookUrl = pathB, WebhookUserKey = null },
            CancellationToken.None).ConfigureAwait(false);

        var effOnB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pathB, effOnB.Secrets.WebhookUrl);
        Assert.IsNull(effOnB.Secrets.WebhookAuthToken,
            "Changing /receiver-a -> /receiver-b via direct store save MUST NOT restore copied token from /receiver-a.");
        Assert.IsNull(effOnB.Secrets.WebhookUserKey,
            "Changing /receiver-a -> /receiver-b via direct store save MUST NOT restore an explicitly cleared userKey.");

        // Fresh save setting fresh creds on /receiver-b succeeds, then changing /receiver-b -> /receiver-c with a fresh
        // token while explicitly clearing userKey preserves only the fresh token and keeps userKey cleared.
        var freshReadB = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            freshReadB with { WebhookAuthToken = "tok-receiver-b", WebhookUserKey = "ukey-receiver-b" },
            CancellationToken.None).ConfigureAwait(false);

        var freshReadBPopulated = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            freshReadBPopulated with { WebhookUrl = pathC, WebhookAuthToken = "tok-receiver-c", WebhookUserKey = null },
            CancellationToken.None).ConfigureAwait(false);

        var effOnC = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pathC, effOnC.Secrets.WebhookUrl);
        Assert.AreEqual("tok-receiver-c", effOnC.Secrets.WebhookAuthToken);
        Assert.IsNull(effOnC.Secrets.WebhookUserKey);
    }

    [TestMethod]
    public async Task ProvenanceFreeManualCopyEdit_AfterSameEndpointEviction_PreservesCurrentSiblingPasswordAndTokenAsync()
    {
        // Delta12 M-Add-1: After rotating pass1 -> pass2 (and tok1 -> tok2) at endpoint A and churning enough
        // subsequent edits at A to evict pass1/tok1 from the 128-entry retired list, a provenance-free manual-copy
        // username/userKey edit carrying evicted pass1/tok1 MUST still preserve pass2/tok2!
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider);

        const string urlA = "https://m1-evict-a.example.test/hook";
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-m1-evict.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions
            {
                Enabled = true,
                Format = WebhookPayloadFormat.Pushover
            }
        };

        var s0 = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var s1 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s0.Version,
            options,
            SmtpUsername: "user-1",
            SmtpPassword: "pass-1",
            WebhookUrl: urlA,
            WebhookAuthToken: "tok-1",
            WebhookUserKey: "ukey-1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, s1.Outcome, s1.Message);

        // Rotate pass-1 -> pass-2 and tok-1 -> tok-2 (retiring pass-1 and tok-1 at endpoint A).
        var s2 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s1.Snapshot!.Version,
            options,
            SmtpPassword: "pass-2",
            WebhookAuthToken: "tok-2"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, s2.Outcome, s2.Message);

        // Churn >128 username and userKey updates at the SAME endpoint A with fresh provenance so pass-1 / tok-1
        // are evicted from A's retired list while pass-2 / tok-2 remain active!
        for (var i = 2; i <= OperatorNotificationStore.MaxRetiredBindingsPerChannel + 12; i++)
        {
            var fresh = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                fresh with { SmtpUsername = $"user-{i}", WebhookUserKey = $"ukey-{i}" },
                CancellationToken.None).ConfigureAwait(false);
        }

        // Provenance-free manual-copy edit carrying evicted pass-1 and tok-1 MUST preserve pass-2 and tok-2!
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            new OperatorNotificationSecrets(
                SmtpUsername: "manual-copy-user",
                SmtpPassword: "pass-1",
                WebhookUrl: urlA,
                WebhookAuthToken: "tok-1",
                WebhookUserKey: "manual-copy-ukey"),
            CancellationToken.None).ConfigureAwait(false);

        var effAfterManualCopy = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("pass-2", effAfterManualCopy.Secrets.SmtpPassword,
            "Provenance-free manual copy carrying evicted pass-1 after same-endpoint truncation MUST preserve pass-2.");
        Assert.AreEqual("tok-2", effAfterManualCopy.Secrets.WebhookAuthToken,
            "Provenance-free manual copy carrying evicted tok-1 after same-endpoint truncation MUST preserve tok-2.");

        // Fresh-provenance edits at A still work cleanly after same-endpoint truncation.
        var freshAfter = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            freshAfter with { SmtpUsername = "fresh-user-after-evict", WebhookUserKey = "fresh-ukey-after-evict" },
            CancellationToken.None).ConfigureAwait(false);
        var effAfterFresh = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("fresh-user-after-evict", effAfterFresh.Secrets.SmtpUsername);
        Assert.AreEqual("pass-2", effAfterFresh.Secrets.SmtpPassword);
        Assert.AreEqual("tok-2", effAfterFresh.Secrets.WebhookAuthToken);
        Assert.AreEqual("fresh-ukey-after-evict", effAfterFresh.Secrets.WebhookUserKey);
    }

    [TestMethod]
    public async Task StableProviderReturningFreshCancellationChangeToken_ProducesValidActiveSnapshotAndReloadsCleanlyAsync()
    {
        // Delta12 Medium-new: A stable IConfigurationProvider returning a fresh unsignaled CancellationChangeToken
        // wrapper on every GetReloadToken() call MUST produce a valid active snapshot (never invalid-configuration)
        // AND still reload cleanly when its CancellationTokenSource signals!
        static Dictionary<string, string?> Values(string tag) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = $"relay-ct-{tag}.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = $"user-{tag}",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = $"pass-{tag}",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = $"https://ct-{tag}.example.test/hook",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = $"tok-{tag}"
        };

        using var freshProvider = new FreshCancellationTokenConfigurationProvider(Values("a"));
        var root = new ConfigurationRoot([freshProvider]);

        // Direct stateless read must succeed with zero binding error.
        var direct = ConfigurationOperatorNotificationConfigProvider.ReadHostSnapshot(root);
        Assert.IsNull(direct.BindingErrorReasonCode, "Fresh unsignaled CancellationChangeToken wrappers must not fail stability.");
        Assert.IsTrue(direct.Options.Enabled);
        Assert.AreEqual("relay-ct-a.example.test", direct.Options.Smtp.Host);
        Assert.AreEqual("user-a", direct.Secrets.SmtpUsername);
        Assert.AreEqual("tok-a", direct.Secrets.WebhookAuthToken);

        // Tracked provider must report active valid configuration and reload cleanly when the token fires.
        using var configProvider = new ConfigurationOperatorNotificationConfigProvider(root);
        Assert.IsNull(configProvider.ConfigurationErrorReasonCode);
        var effA = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(effA.ConfigurationErrorReasonCode);
        Assert.IsTrue(effA.Options.Enabled);
        Assert.AreEqual("relay-ct-a.example.test", effA.Options.Smtp.Host);
        Assert.AreEqual("user-a", effA.Secrets.SmtpUsername);
        Assert.AreEqual("pass-a", effA.Secrets.SmtpPassword);
        Assert.AreEqual("https://ct-a.example.test/hook", effA.Secrets.WebhookUrl);
        Assert.AreEqual("tok-a", effA.Secrets.WebhookAuthToken);

        freshProvider.Rotate(Values("b"));
        var effB = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(effB.ConfigurationErrorReasonCode);
        Assert.AreEqual("relay-ct-b.example.test", effB.Options.Smtp.Host);
        Assert.AreEqual("user-b", effB.Secrets.SmtpUsername);
        Assert.AreEqual("pass-b", effB.Secrets.SmtpPassword);
        Assert.AreEqual("https://ct-b.example.test/hook", effB.Secrets.WebhookUrl);
        Assert.AreEqual("tok-b", effB.Secrets.WebhookAuthToken);

        // Also verify a non-ConfigurationRoot IConfiguration returning fresh CancellationChangeToken wrappers.
        using var nonRootWrapper = new FreshTokenConfigurationWrapper(root);
        var wrappedSnap = ConfigurationOperatorNotificationConfigProvider.ReadHostSnapshot(nonRootWrapper);
        Assert.IsNull(wrappedSnap.BindingErrorReasonCode);
        Assert.AreEqual("relay-ct-b.example.test", wrappedSnap.Options.Smtp.Host);
        Assert.AreEqual("tok-b", wrappedSnap.Secrets.WebhookAuthToken);
    }

    [TestMethod]
    public async Task SameOriginWebhookPathChange_NeverCarriesRefillsOrDeliversPriorPathSecretsAcrossStoreMergeAndTrackerAsync()
    {
        // Delta13 Finding #1: Full canonical webhook destination (scheme + host + port + path + query) MUST be used
        // consistently across direct save, versioned settings save, effective-secret merge, reload tracker, and live delivery.
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using var cert = WebhookNotificationTransportTests.CreateEphemeralLoopbackCertificate();
        var server = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await using (server.ConfigureAwait(false))
        {
            var loopbackPolicy = new OperatorNotificationNetworkPolicy(isDevelopment: false)
            {
                AllowLoopbackForTesting = true
            };
            var urlA = $"https://127.0.0.1:{server.Port}/receiver-a";
            var urlB = $"https://127.0.0.1:{server.Port}/receiver-b";

            using var transport = new WebhookNotificationTransport(loopbackPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, cert.Thumbprint, StringComparison.Ordinal)
            };

            // Part A: Host config holds /receiver-a with host-tok-a / host-ukey-a; direct save AND versioned settings save
            // changing to /receiver-b MUST NEVER carry stored credentials OR refill from host config, and delivery to
            // /receiver-b MUST send NO Authorization header or userKey unless explicitly supplied for /receiver-b!
            var hostConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                    [OperatorNotificationsOptions.SectionKey + ":TestSendMinimumIntervalSeconds"] = "0",
                    [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                    [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Generic",
                    [OperatorNotificationsOptions.SectionKey + ":Webhook:AuthHeaderName"] = "Authorization",
                    [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA,
                    [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "host-tok-receiver-a",
                    [ConfigurationOperatorNotificationConfigProvider.WebhookUserKeyConfigKey] = "host-ukey-receiver-a"
                })
                .Build();

            using var store = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = dir.Path }),
                hostConfig,
                loopbackPolicy,
                NullLogger<OperatorNotificationStore>.Instance);
            using var telemetry = new OperatorNotificationTelemetry();
            using var dispatcher = new OperatorNotificationDispatcher(
                store,
                new SmtpNotificationTransport(loopbackPolicy, TimeProvider.System),
                transport,
                telemetry,
                TimeProvider.System);

            var genericOptions = new OperatorNotificationsOptions
            {
                Enabled = true,
                TestSendMinimumIntervalSeconds = 0,
                Webhook = new WebhookNotificationOptions
                {
                    Enabled = true,
                    Format = WebhookPayloadFormat.Generic,
                    AuthHeaderName = "Authorization"
                }
            };

            // Save protected credentials on /receiver-a first.
            var s0 = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var savedA = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                s0.Version,
                genericOptions,
                WebhookUrl: urlA,
                WebhookAuthToken: "protected-tok-receiver-a",
                WebhookUserKey: "protected-ukey-receiver-a"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedA.Outcome, savedA.Message);

            // 1. Direct secret-store save changing /receiver-a -> /receiver-b (carrying copied token + cleared userKey)
            //    MUST NOT carry protected-tok-receiver-a AND MUST NOT refill host-tok-receiver-a / host-ukey-receiver-a!
            var freshReadA = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                freshReadA with { WebhookUrl = urlB, WebhookUserKey = null },
                CancellationToken.None).ConfigureAwait(false);

            var effAfterDirect = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(urlB, effAfterDirect.Secrets.WebhookUrl);
            Assert.IsNull(effAfterDirect.Secrets.WebhookAuthToken,
                "Direct save /receiver-a -> /receiver-b must not carry stored token or refill host token.");
            Assert.IsNull(effAfterDirect.Secrets.WebhookUserKey,
                "Direct save /receiver-a -> /receiver-b must not carry stored userKey or refill host userKey.");

            var directDelivery = await dispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(directDelivery.Channels[0].Succeeded, directDelivery.Channels[0].ReasonCode);
            Assert.HasCount(1, server.Captured);
            Assert.IsNull(server.Captured[0].Header("Authorization"),
                "Delivery to /receiver-b after direct path change MUST NOT send /receiver-a's Authorization header.");

            // 2. Versioned settings save on /receiver-a with protected creds, then changing ONLY URL to /receiver-b
            //    MUST drop protected creds AND never refill host creds from /receiver-a!
            var snapBeforeResetA = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var backToA = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snapBeforeResetA.Version,
                genericOptions,
                WebhookUrl: urlA,
                WebhookAuthToken: "settings-tok-receiver-a",
                WebhookUserKey: "settings-ukey-receiver-a"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, backToA.Outcome, backToA.Message);

            var settingsMoveToB = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                backToA.Snapshot!.Version,
                genericOptions,
                WebhookUrl: urlB), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, settingsMoveToB.Outcome, settingsMoveToB.Message);
            Assert.IsFalse(settingsMoveToB.Snapshot!.Secrets.HasWebhookAuthToken,
                "Versioned settings save /receiver-a -> /receiver-b MUST NOT retain /receiver-a's protected or host token.");
            Assert.IsFalse(settingsMoveToB.Snapshot.Secrets.HasWebhookUserKey,
                "Versioned settings save /receiver-a -> /receiver-b MUST NOT retain /receiver-a's protected or host userKey.");

            var effAfterSettingsMove = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(effAfterSettingsMove.Secrets.WebhookAuthToken);
            Assert.IsNull(effAfterSettingsMove.Secrets.WebhookUserKey);

            var settingsMoveDelivery = await dispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(settingsMoveDelivery.Channels[0].Succeeded, settingsMoveDelivery.Channels[0].ReasonCode);
            Assert.HasCount(2, server.Captured);
            Assert.IsNull(server.Captured[1].Header("Authorization"),
                "Delivery to /receiver-b after versioned settings path change MUST send NO Authorization header.");

            // 3. Explicitly supplying /receiver-b's own credentials delivers /receiver-b's Authorization header cleanly.
            var explicitB = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                settingsMoveToB.Snapshot.Version,
                genericOptions,
                WebhookAuthToken: "explicit-tok-receiver-b",
                WebhookUserKey: "explicit-ukey-receiver-b"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, explicitB.Outcome, explicitB.Message);
            var explicitBDelivery = await dispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(explicitBDelivery.Channels[0].Succeeded, explicitBDelivery.Channels[0].ReasonCode);
            Assert.HasCount(3, server.Captured);
            Assert.AreEqual("Bearer explicit-tok-receiver-b", server.Captured[2].Header("Authorization"));

            // Part B: HostConfigurationSnapshotTracker uses full canonical webhook destination identity:
            // rotating /receiver-a -> /receiver-b on the SAME origin across split providers MUST suppress /receiver-a's
            // token/userKey (even if cred provider carries BoundOrigin = /receiver-a!) until fresh /receiver-b creds arrive!
            var destProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":TestSendMinimumIntervalSeconds"] = "0",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Generic",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:AuthHeaderName"] = "Authorization",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlA
            });
            var credsProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "tracker-tok-a",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUserKeyConfigKey] = "tracker-ukey-a",
                [ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = urlA
            });
            var trackerRoot = new ConfigurationRoot([destProv, credsProv]);
            using var trackerProvider = new ConfigurationOperatorNotificationConfigProvider(trackerRoot, loopbackPolicy);
            using var trackerTelemetry = new OperatorNotificationTelemetry();
            using var trackerDispatcher = new OperatorNotificationDispatcher(
                trackerProvider,
                new SmtpNotificationTransport(loopbackPolicy, TimeProvider.System),
                transport,
                trackerTelemetry,
                TimeProvider.System);

            var trackerInit = await trackerProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("tracker-tok-a", trackerInit.Secrets.WebhookAuthToken);
            Assert.AreEqual("tracker-ukey-a", trackerInit.Secrets.WebhookUserKey);

            // Rotate ONLY destination path on the SAME origin: /receiver-a -> /receiver-b.
            destProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":TestSendMinimumIntervalSeconds"] = "0",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Generic",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:AuthHeaderName"] = "Authorization",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = urlB
            });

            var trackerAfterPathChange = await trackerProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(urlB, trackerAfterPathChange.Secrets.WebhookUrl);
            Assert.IsNull(trackerAfterPathChange.Secrets.WebhookAuthToken,
                "Reload tracker MUST withhold /receiver-a's token when destination path rotates to /receiver-b on the same origin.");
            Assert.IsNull(trackerAfterPathChange.Secrets.WebhookUserKey,
                "Reload tracker MUST withhold /receiver-a's userKey when destination path rotates to /receiver-b on the same origin.");

            var trackerIntermediateDelivery = await trackerDispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(trackerIntermediateDelivery.Channels[0].Succeeded, trackerIntermediateDelivery.Channels[0].ReasonCode);
            Assert.HasCount(4, server.Captured);
            Assert.IsNull(server.Captured[3].Header("Authorization"),
                "Tracker delivery to /receiver-b MUST NOT send /receiver-a's bearer token.");

            // Fresh credentials bound to /receiver-b converge cleanly.
            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "tracker-tok-b",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUserKeyConfigKey] = "tracker-ukey-b"
            });
            var trackerConverged = await trackerProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("tracker-tok-b", trackerConverged.Secrets.WebhookAuthToken);
            Assert.AreEqual("tracker-ukey-b", trackerConverged.Secrets.WebhookUserKey);
        }
    }

    [TestMethod]
    public async Task DirectSecretStoreSave_ChangingWebhookPathWithFreshProvenance_RejectsOlderRetiredTokenFromPriorPathAsync()
    {
        // Delta13 Finding #2: A fresh ReadAsync() of /receiver-a changing WebhookUrl to /receiver-b MUST NOT inherit
        // /receiver-a's provenance exemption in the retired-entry loop; an older retired token/userKey from /receiver-a
        // MUST be stripped and NEVER persisted for /receiver-b!
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        const string pathA = "https://hooks.example.com/receiver-a";
        const string pathB = "https://hooks.example.com/receiver-b";

        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = pathA,
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "host-tok-a"
            })
            .Build();
        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);

        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Webhook = new WebhookNotificationOptions
            {
                Enabled = true,
                Format = WebhookPayloadFormat.Pushover
            }
        };

        // 1. Save /receiver-a with tok-a-1 / ukey-a-1, then rotate on /receiver-a -> tok-a-2 / ukey-a-2
        //    so tok-a-1 and ukey-a-1 are recorded in RetiredWebhooks for /receiver-a.
        var s0 = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var s1 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s0.Version,
            options,
            WebhookUrl: pathA,
            WebhookAuthToken: "tok-a-1",
            WebhookUserKey: "ukey-a-1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, s1.Outcome, s1.Message);

        var s2 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s1.Snapshot!.Version,
            options,
            WebhookAuthToken: "tok-a-2",
            WebhookUserKey: "ukey-a-2"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, s2.Outcome, s2.Message);

        // 2. Fresh ReadAsync() at /receiver-a (current revision & provenance!), then save /receiver-b carrying
        //    OLDER retired tok-a-1 and ukey-a-1 from /receiver-a -> MUST strip BOTH and NOT persist them for /receiver-b!
        var freshReadA = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pathA, freshReadA.WebhookUrl);
        Assert.AreEqual("tok-a-2", freshReadA.WebhookAuthToken);

        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            freshReadA with
            {
                WebhookUrl = pathB,
                WebhookAuthToken = "tok-a-1",
                WebhookUserKey = "ukey-a-1"
            },
            CancellationToken.None).ConfigureAwait(false);

        var effOnB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pathB, effOnB.Secrets.WebhookUrl);
        Assert.IsNull(effOnB.Secrets.WebhookAuthToken,
            "Fresh ReadAsync of /receiver-a changing URL to /receiver-b MUST NOT exempt older retired tok-a-1 from /receiver-a.");
        Assert.IsNull(effOnB.Secrets.WebhookUserKey,
            "Fresh ReadAsync of /receiver-a changing URL to /receiver-b MUST NOT exempt older retired ukey-a-1 from /receiver-a.");

        var readBackB = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pathB, readBackB.WebhookUrl);
        Assert.IsNull(readBackB.WebhookAuthToken);
        Assert.IsNull(readBackB.WebhookUserKey);

        // 3. Fresh save at /receiver-b supplying fresh non-retired credentials for /receiver-b succeeds cleanly.
        await ((IOperatorNotificationSecretStore)store).SaveAsync(
            readBackB with { WebhookAuthToken = "tok-b-1", WebhookUserKey = "ukey-b-1" },
            CancellationToken.None).ConfigureAwait(false);
        var effOnBPopulated = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("tok-b-1", effOnBPopulated.Secrets.WebhookAuthToken);
        Assert.AreEqual("ukey-b-1", effOnBPopulated.Secrets.WebhookUserKey);
    }

    [TestMethod]
    public async Task Legacy128EntryEnvelopeWithExplicitFalseTruncationFlagsAndEmptyArrays_ConservativelyRejectsEvictedCred1AtBAsync()
    {
        // Delta13 Finding #3 (H1): A 128-entry delta10 envelope rewritten by delta11 contains EXPLICIT `false`
        // truncation flags and empty arrays WITHOUT RetiredCredentialHistoryVersion >= 2. Loading it in delta13
        // MUST STILL conservatively treat both channels as truncated, persist truncation on next save, and reject
        // evicted credential #1 from A at B (while genuine v2 128-entry envelopes without eviction stay non-truncated).
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        const string originA = "HTTPS://DELTA11-A.EXAMPLE.TEST:443";
        const string endpointA = "RELAY-DELTA11-A.EXAMPLE.TEST:587:StartTls";
        const string urlB = "https://delta11-b.example.test/hook";
        const string salt = "fedcba9876543210fedcba9876543210";

        static string SaltedHash(string s, string value)
            => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{s}:{value}")));

        var retiredSmtps = new List<object>(OperatorNotificationStore.MaxRetiredBindingsPerChannel);
        var retiredWebhooks = new List<object>(OperatorNotificationStore.MaxRetiredBindingsPerChannel);
        // Simulate cred #1 already evicted under delta10 and rewritten by delta11 with explicit false flags & empty arrays.
        for (var i = 2; i <= OperatorNotificationStore.MaxRetiredBindingsPerChannel + 1; i++)
        {
            retiredSmtps.Add(new
            {
                endpoint = endpointA,
                usernameHash = SaltedHash(salt, $"d11-user-a-{i}"),
                passwordHash = SaltedHash(salt, $"d11-pass-a-{i}")
            });
            retiredWebhooks.Add(new
            {
                origin = originA,
                authTokenHash = SaltedHash(salt, $"d11-tok-a-{i}")
            });
        }

        var delta11RewrittenPayload = new
        {
            webhookUrl = urlB,
            retiredSalt = salt,
            retiredWebhooks,
            retiredSmtps,
            truncatedWebhookOrigins = Array.Empty<string>(),
            anyWebhookHistoryTruncated = false,
            truncatedSmtpEndpoints = Array.Empty<string>(),
            anySmtpHistoryTruncated = false
        };

        var protector = dpProvider.CreateProtector("CameraAgent", "OperatorNotifications", "v1");
        var optionsB = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-delta11-b.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };
        var envelope = new
        {
            schemaVersion = 1,
            revision = "delta11-rewritten-rev-1",
            options = optionsB,
            protectedSecrets = protector.Protect(JsonSerializer.Serialize(delta11RewrittenPayload, WebJsonOptions))
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName),
            JsonSerializer.Serialize(envelope, WebJsonOptions),
            CancellationToken.None).ConfigureAwait(false);

        using (var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider))
        {
            // Unversioned retry carrying evicted cred #1 from A MUST be rejected at B despite explicit `false` flags!
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                new OperatorNotificationSecrets(
                    SmtpUsername: "d11-user-a-1",
                    SmtpPassword: "d11-pass-a-1",
                    WebhookUrl: urlB,
                    WebhookAuthToken: "d11-tok-a-1"),
                CancellationToken.None).ConfigureAwait(false);

            var effEmptyB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(effEmptyB.Secrets.SmtpUsername,
                "128-entry pre-v2 envelope with explicit false truncation flags MUST reject evicted SMTP username #1 at B.");
            Assert.IsNull(effEmptyB.Secrets.SmtpPassword,
                "128-entry pre-v2 envelope with explicit false truncation flags MUST reject evicted SMTP password #1 at B.");
            Assert.IsNull(effEmptyB.Secrets.WebhookAuthToken,
                "128-entry pre-v2 envelope with explicit false truncation flags MUST reject evicted webhook token #1 at B.");

            // Versioned save on B persists conservative truncation with v2 marker.
            var snapB = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var savedB = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snapB.Version,
                optionsB,
                SmtpUsername: "d11-user-b-1",
                SmtpPassword: "d11-pass-b-1",
                WebhookAuthToken: "d11-tok-b-1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedB.Outcome, savedB.Message);
        }

        // Reloading from disk after the v2 save preserves truncation protection AND rejects evicted #1 at B.
        using (var reloadedStore = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider))
        {
            await ((IOperatorNotificationSecretStore)reloadedStore).SaveAsync(
                new OperatorNotificationSecrets(
                    SmtpUsername: "d11-user-a-1",
                    SmtpPassword: "d11-pass-a-1",
                    WebhookUrl: urlB,
                    WebhookAuthToken: "d11-tok-a-1"),
                CancellationToken.None).ConfigureAwait(false);

            var effPreservedB = await reloadedStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("d11-user-b-1", effPreservedB.Secrets.SmtpUsername);
            Assert.AreEqual("d11-pass-b-1", effPreservedB.Secrets.SmtpPassword);
            Assert.AreEqual("d11-tok-b-1", effPreservedB.Secrets.WebhookAuthToken);
        }

        // Delta15 H4: A delta13-rewritten v2 envelope (`retiredCredentialHistoryVersion: 2`, `any*HistoryTruncated: false`)
        // MUST ALSO fail closed on v3 upgrade (forcing truncation for both channels so unversioned saves cannot admit
        // evicted credentials at B), while a genuine v3 envelope (`retiredCredentialHistoryVersion: 3`,
        // `any*HistoryTruncated: false`) trusts the v3 metadata and admits a fresh non-retired credential at B!
        using var dirV2 = TempStateDir.Create();
        var dpV2 = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dirV2.Path, "dp-keys")));
        var protectorV2 = dpV2.CreateProtector("CameraAgent", "OperatorNotifications", "v1");
        var delta13V2Payload = new
        {
            webhookUrl = urlB,
            retiredSalt = salt,
            retiredWebhooks,
            retiredSmtps,
            anyWebhookHistoryTruncated = false,
            anySmtpHistoryTruncated = false,
            retiredCredentialHistoryVersion = 2
        };
        var envelopeV2 = new
        {
            schemaVersion = 1,
            revision = "v2-untrusted-rev-1",
            options = optionsB,
            protectedSecrets = protectorV2.Protect(JsonSerializer.Serialize(delta13V2Payload, WebJsonOptions))
        };
        await File.WriteAllTextAsync(
            Path.Combine(dirV2.Path, OperatorNotificationStore.ConfigFileName),
            JsonSerializer.Serialize(envelopeV2, WebJsonOptions),
            CancellationToken.None).ConfigureAwait(false);

        using (var v2Store = CreateStore(dirV2.Path, isDevelopment: false, dpProvider: dpV2))
        {
            await ((IOperatorNotificationSecretStore)v2Store).SaveAsync(
                new OperatorNotificationSecrets(
                    SmtpUsername: "d11-user-a-1",
                    SmtpPassword: "d11-pass-a-1",
                    WebhookUrl: urlB,
                    WebhookAuthToken: "d11-tok-a-1"),
                CancellationToken.None).ConfigureAwait(false);
            var effV2 = await v2Store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(effV2.Secrets.SmtpUsername, "Pre-v3 (v2) envelope with explicit false flags MUST force truncation and reject evicted #1.");
            Assert.IsNull(effV2.Secrets.SmtpPassword, "Pre-v3 (v2) envelope with explicit false flags MUST force truncation and reject evicted #1.");
            Assert.IsNull(effV2.Secrets.WebhookAuthToken, "Pre-v3 (v2) envelope with explicit false flags MUST force truncation and reject evicted #1.");
        }

        // Genuine v3 envelope with exactly 128 entries and zero evictions (`retiredCredentialHistoryVersion: 3`,
        // `any*HistoryTruncated: false`) trusts the v3 metadata and admits a fresh non-retired credential at B!
        using var dirV3 = TempStateDir.Create();
        var dpV3 = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dirV3.Path, "dp-keys")));
        var protectorV3 = dpV3.CreateProtector("CameraAgent", "OperatorNotifications", "v1");
        var genuineV3Payload = new
        {
            webhookUrl = urlB,
            retiredSalt = salt,
            retiredWebhooks,
            retiredSmtps,
            anyWebhookHistoryTruncated = false,
            anySmtpHistoryTruncated = false,
            retiredCredentialHistoryVersion = 3
        };
        var envelopeV3 = new
        {
            schemaVersion = 1,
            revision = "v3-untruncated-rev-1",
            options = optionsB,
            protectedSecrets = protectorV3.Protect(JsonSerializer.Serialize(genuineV3Payload, WebJsonOptions))
        };
        await File.WriteAllTextAsync(
            Path.Combine(dirV3.Path, OperatorNotificationStore.ConfigFileName),
            JsonSerializer.Serialize(envelopeV3, WebJsonOptions),
            CancellationToken.None).ConfigureAwait(false);

        using (var v3Store = CreateStore(dirV3.Path, isDevelopment: false, dpProvider: dpV3))
        {
            // Retired #2 is still rejected, while fresh non-retired B credential is accepted.
            await ((IOperatorNotificationSecretStore)v3Store).SaveAsync(
                new OperatorNotificationSecrets(
                    SmtpUsername: "d11-user-a-2",
                    SmtpPassword: "brand-new-v3-pass-b",
                    WebhookUrl: urlB,
                    WebhookAuthToken: "brand-new-v3-tok-b"),
                CancellationToken.None).ConfigureAwait(false);
            var effV3 = await v3Store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(effV3.Secrets.SmtpUsername, "Retired entry #2 must still be rejected.");
            Assert.AreEqual("brand-new-v3-pass-b", effV3.Secrets.SmtpPassword);
            Assert.AreEqual("brand-new-v3-tok-b", effV3.Secrets.WebhookAuthToken);
        }
    }

    [TestMethod]
    public async Task SplitSmtpEndpointAdmission_WithholdsAtAllPartialIntermediatesAndNeverCarriesUnchangedCredAcrossLaterComponentChangeAsync()
    {
        // Delta13 Finding #4: Treat SMTP destination identity as the COMPLETE (host, port, securityMode) tuple.
        // 1. With 3 independent destination providers (host, port, securityMode) + separate creds provider, NEVER admit
        //    credB at ANY partially-rotated intermediate tuple (including (hostB, portA, modeA) or (hostB, portB, modeA));
        //    only authenticate once ALL split components settle on (hostB, portB, modeB).
        // 2. Once credB IS bound to (hostB, portB, modeB), a later port or security-mode change MUST NOT carry unchanged
        //    credB onto the changed tuple (no continuation rule leak!).
        static Dictionary<string, string?> HostDict(string host) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":TestSendMinimumIntervalSeconds"] = "0",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = host,
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test"
        };
        static Dictionary<string, string?> PortDict(int port) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        static Dictionary<string, string?> ModeDict(SmtpSecurityMode mode) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = mode.ToString()
        };
        static Dictionary<string, string?> CredsDict(string tag) => new(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = $"user-{tag}",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = $"pass-{tag}"
        };

        using var dir = TempStateDir.Create();
        var hostProv = new RotatingHostConfigurationProvider(HostDict("relay-d13-a.example.test"));
        var portProv = new RotatingHostConfigurationProvider(PortDict(2525));
        var modeProv = new RotatingHostConfigurationProvider(ModeDict(SmtpSecurityMode.StartTls));
        var credsProv = new RotatingHostConfigurationProvider(CredsDict("a"));
        var root = new ConfigurationRoot([hostProv, portProv, modeProv, credsProv]);

        using var provider = new ConfigurationOperatorNotificationConfigProvider(root);
        using var store = CreateStore(dir.Path, isDevelopment: false, configuration: root);
        var capturingSmtp = new CapturingSmtpTransport();
        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(
            store,
            capturingSmtp,
            new WebhookNotificationTransport(new OperatorNotificationNetworkPolicy(isDevelopment: false), TimeProvider.System),
            telemetry,
            TimeProvider.System);

        var init = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("user-a", init.Secrets.SmtpUsername);
        Assert.AreEqual("pass-a", init.Secrets.SmtpPassword);

        // Step 1: Host moves A -> B, creds move A -> B while Port (2525) and SecurityMode (StartTls) are still A!
        hostProv.Rotate(HostDict("relay-d13-b.example.test"));
        credsProv.Rotate(CredsDict("b"));
        var atHostBPortAModeA = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-d13-b.example.test", atHostBPortAModeA.Options.Smtp.Host);
        Assert.AreEqual(2525, atHostBPortAModeA.Options.Smtp.Port);
        Assert.IsNull(atHostBPortAModeA.Secrets.SmtpUsername, "Partial intermediate (hostB, portA, modeA) MUST withhold user-b.");
        Assert.IsNull(atHostBPortAModeA.Secrets.SmtpPassword, "Partial intermediate (hostB, portA, modeA) MUST withhold pass-b.");

        // Step 2: Port moves 2525 -> 465 while SecurityMode is STILL StartTls (not yet ImplicitTls)!
        portProv.Rotate(PortDict(465));
        var atHostBPortBModeA = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(465, atHostBPortBModeA.Options.Smtp.Port);
        Assert.AreEqual(SmtpSecurityMode.StartTls, atHostBPortBModeA.Options.Smtp.SecurityMode);
        Assert.IsNull(atHostBPortBModeA.Secrets.SmtpUsername, "Partial intermediate (hostB, portB, modeA) MUST withhold user-b.");
        Assert.IsNull(atHostBPortBModeA.Secrets.SmtpPassword, "Partial intermediate (hostB, portB, modeA) MUST withhold pass-b.");

        var intermediateSend = await dispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(intermediateSend.Channels[0].Succeeded);
        Assert.HasCount(1, capturingSmtp.Captured);
        Assert.IsNull(capturingSmtp.Captured[0].Username);
        Assert.IsNull(capturingSmtp.Captured[0].Password);

        // Step 3: SecurityMode finishes moving StartTls -> ImplicitTls -> unbound split SMTP STILL fails closed
        //         (never guesses completion); explicit same-provider Smtp:BoundEndpoint authorizes (hostB, 465, ImplicitTls)!
        modeProv.Rotate(ModeDict(SmtpSecurityMode.ImplicitTls));
        var unboundFinal = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-d13-b.example.test", unboundFinal.Options.Smtp.Host);
        Assert.AreEqual(465, unboundFinal.Options.Smtp.Port);
        Assert.AreEqual(SmtpSecurityMode.ImplicitTls, unboundFinal.Options.Smtp.SecurityMode);
        Assert.IsNull(unboundFinal.Secrets.SmtpUsername, "Split SMTP without explicit BoundEndpoint MUST fail closed on live reload.");
        Assert.IsNull(unboundFinal.Secrets.SmtpPassword, "Split SMTP without explicit BoundEndpoint MUST fail closed on live reload.");

        var boundCredsB = CredsDict("b");
        boundCredsB[ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-d13-b.example.test:465:ImplicitTls";
        credsProv.Rotate(boundCredsB);
        var converged = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("user-b", converged.Secrets.SmtpUsername);
        Assert.AreEqual("pass-b", converged.Secrets.SmtpPassword);

        var finalSend = await dispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(finalSend.Channels[0].Succeeded);
        Assert.HasCount(2, capturingSmtp.Captured);
        Assert.AreEqual("user-b", capturingSmtp.Captured[1].Username);
        Assert.AreEqual("pass-b", capturingSmtp.Captured[1].Password);

        // Step 4: Later port change on hostB (465 -> 587) with UNCHANGED credB MUST immediately withhold credB!
        portProv.Rotate(PortDict(587));
        var afterLaterPortChange = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(587, afterLaterPortChange.Options.Smtp.Port);
        Assert.IsNull(afterLaterPortChange.Secrets.SmtpUsername,
            "Unchanged user-b MUST NOT follow a later port change on hostB.");
        Assert.IsNull(afterLaterPortChange.Secrets.SmtpPassword,
            "Unchanged pass-b MUST NOT follow a later port change on hostB.");
    }

    [TestMethod]
    public async Task SplitSmtpPortOverrideAddedAfterBinding_RetainsSplitGuardAcrossUnderlyingPortChangeAndRemovalAsync()
    {
        // Delta13/Delta14 H4/H5: Bind credA at (hostA, 587) with NO separate port override active yet; then add a
        // separate port override (2525) while creds stay unchanged; change hostProv's underlying port (587 -> 465) while
        // masked; then remove the override AND supply fresh unbound credB -> credB MUST stay withheld at hostA AND
        // on live-reload hostB without explicit BoundEndpoint, then converge cleanly once explicit BoundEndpoint arrives!
        static Dictionary<string, string?> HostDict(string host, int port, SmtpSecurityMode mode = SmtpSecurityMode.StartTls) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":TestSendMinimumIntervalSeconds"] = "0",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = host,
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = mode.ToString(),
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test"
        };
        static Dictionary<string, string?> CredsDict(string tag, string? boundEndpoint = null)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = $"user-{tag}",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = $"pass-{tag}"
            };
            if (boundEndpoint is not null)
            {
                dict[ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = boundEndpoint;
            }
            return dict;
        }

        // Part A: Port override added AFTER initial binding.
        {
            using var dir = TempStateDir.Create();
            var hostProv = new RotatingHostConfigurationProvider(HostDict("relay-h5-late-a.example.test", 587));
            var portOverrideProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var credsProv = new RotatingHostConfigurationProvider(CredsDict("a"));
            var root = new ConfigurationRoot([hostProv, portOverrideProv, credsProv]);

            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);
            using var store = CreateStore(dir.Path, isDevelopment: false, configuration: root);
            var capturingSmtp = new CapturingSmtpTransport();
            using var telemetry = new OperatorNotificationTelemetry();
            using var dispatcher = new OperatorNotificationDispatcher(
                store,
                capturingSmtp,
                new WebhookNotificationTransport(new OperatorNotificationNetworkPolicy(isDevelopment: false), TimeProvider.System),
                telemetry,
                TimeProvider.System);

            // 1. Initially bound cleanly at (hostA, 587) with NO split provider yet.
            var init = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h5-late-a.example.test", init.Options.Smtp.Host);
            Assert.AreEqual(587, init.Options.Smtp.Port);
            Assert.AreEqual("user-a", init.Secrets.SmtpUsername);
            Assert.AreEqual("pass-a", init.Secrets.SmtpPassword);

            // 2. Separate port override appears AFTER binding (port -> 2525) while creds stay unchanged (user-a/pass-a)!
            portOverrideProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "2525"
            });
            var masked = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2525, masked.Options.Smtp.Port);
            Assert.IsNull(masked.Secrets.SmtpUsername);
            Assert.IsNull(masked.Secrets.SmtpPassword);

            // 3. While masked by portOverrideProv, hostProv changes its underlying port 587 -> 465 (still hostA!).
            hostProv.Rotate(HostDict("relay-h5-late-a.example.test", 465));
            var stillMasked = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2525, stillMasked.Options.Smtp.Port);
            Assert.IsNull(stillMasked.Secrets.SmtpUsername);

            // 4. Remove port override AND supply fresh credB -> effective is (hostA, 465); credB MUST stay withheld at hostA!
            portOverrideProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            credsProv.Rotate(CredsDict("b"));
            var afterRemoveAndCredB = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h5-late-a.example.test", afterRemoveAndCredB.Options.Smtp.Host);
            Assert.AreEqual(465, afterRemoveAndCredB.Options.Smtp.Port);
            Assert.IsNull(afterRemoveAndCredB.Secrets.SmtpUsername,
                "Late port override addition + removal MUST retain split guard and withhold user-b at (hostA, 465).");
            Assert.IsNull(afterRemoveAndCredB.Secrets.SmtpPassword,
                "Late port override addition + removal MUST retain split guard and withhold pass-b at (hostA, 465).");

            var deliveryAtHostA = await dispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(deliveryAtHostA.Channels[0].Succeeded);
            Assert.HasCount(1, capturingSmtp.Captured);
            Assert.AreEqual("relay-h5-late-a.example.test", capturingSmtp.Captured[0].Host);
            Assert.AreEqual(465, capturingSmtp.Captured[0].Port);
            Assert.IsNull(capturingSmtp.Captured[0].Username, "Dispatcher MUST NOT send user-b to hostA:465.");
            Assert.IsNull(capturingSmtp.Captured[0].Password, "Dispatcher MUST NOT send pass-b to hostA:465.");

            // 5. Host advances hostA -> hostB: unbound split-originated creds stay withheld; explicit BoundEndpoint converges!
            hostProv.Rotate(HostDict("relay-h5-late-b.example.test", 465));
            var withheldOnHostB = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(withheldOnHostB.Secrets.SmtpUsername);
            Assert.IsNull(withheldOnHostB.Secrets.SmtpPassword);

            credsProv.Rotate(CredsDict("b", "relay-h5-late-b.example.test:465:StartTls"));
            var converged = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h5-late-b.example.test", converged.Options.Smtp.Host);
            Assert.AreEqual(465, converged.Options.Smtp.Port);
            Assert.AreEqual("user-b", converged.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", converged.Secrets.SmtpPassword);
        }

        // Part B: Symmetric SecurityMode override added AFTER initial binding.
        {
            var hostProv = new RotatingHostConfigurationProvider(HostDict("relay-h5-mode-a.example.test", 587, SmtpSecurityMode.StartTls));
            var modeOverrideProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var credsProv = new RotatingHostConfigurationProvider(CredsDict("a"));
            var root = new ConfigurationRoot([hostProv, modeOverrideProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            Assert.AreEqual("pass-a", (await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false)).Secrets.SmtpPassword);

            modeOverrideProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = nameof(SmtpSecurityMode.ImplicitTls)
            });
            hostProv.Rotate(HostDict("relay-h5-mode-a.example.test", 587, SmtpSecurityMode.None));
            modeOverrideProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            credsProv.Rotate(CredsDict("b"));

            var atHostANone = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h5-mode-a.example.test", atHostANone.Options.Smtp.Host);
            Assert.AreEqual(SmtpSecurityMode.None, atHostANone.Options.Smtp.SecurityMode);
            Assert.IsNull(atHostANone.Secrets.SmtpUsername, "Late SecurityMode override MUST retain split guard and withhold user-b at hostA.");
            Assert.IsNull(atHostANone.Secrets.SmtpPassword, "Late SecurityMode override MUST retain split guard and withhold pass-b at hostA.");

            hostProv.Rotate(HostDict("relay-h5-mode-b.example.test", 587, SmtpSecurityMode.ImplicitTls));
            Assert.IsNull((await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false)).Secrets.SmtpPassword);

            credsProv.Rotate(CredsDict("b", "relay-h5-mode-b.example.test:587:ImplicitTls"));
            var converged = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("user-b", converged.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", converged.Secrets.SmtpPassword);
        }
    }

    [TestMethod]
    public async Task DestinationChangedWhileEmpty_ClearsAcrossUnchangedReloadsAndWithholdsLaterFirstCredentialForBothChannelsAsync()
    {
        // Delta13 Finding #6 (H6): Reaching empty destination A via an earlier destination change sets
        // DestinationChangedWhileEmpty for THAT single transition ONLY; any subsequent unchanged reload (unrelated
        // reload, root.Reload(), or identical destination reload) MUST clear DestinationChangedWhileEmpty so a later
        // unbound credential-first reload intended for B stays withheld at A (for BOTH SMTP and Webhook)!
        static Dictionary<string, string?> DestDict(string host, string url, string displayName = "HVO Camera") => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = host,
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromDisplayName"] = displayName,
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Ntfy",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = url
        };

        const string urlX = "https://h6-d13-x.example.test/hook";
        const string urlA = "https://h6-d13-a.example.test/hook";
        const string urlB = "https://h6-d13-b.example.test/hook";

        // Test all 3 unchanged-reload triggers after X(empty) -> A(empty): unrelated provider reload, root.Reload(), and same-dest reload.
        foreach (var unchangedTrigger in new[] { "unrelated", "root-reload", "same-dest" })
        {
            var destProv = new RotatingHostConfigurationProvider(DestDict("relay-h6-x.example.test", urlX));
            var credsProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var unrelatedProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Unrelated:Tick"] = "0"
            });
            var root = new ConfigurationRoot([destProv, credsProv, unrelatedProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            // 1. Destination moves X(empty) -> A(empty), setting DestinationChangedWhileEmpty for that single transition.
            destProv.Rotate(DestDict("relay-h6-a.example.test", urlA));
            var onAEmpty = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h6-a.example.test", onAEmpty.Options.Smtp.Host);
            Assert.AreEqual(urlA, onAEmpty.Secrets.WebhookUrl);
            Assert.IsNull(onAEmpty.Secrets.SmtpUsername);
            Assert.IsNull(onAEmpty.Secrets.WebhookAuthToken);

            // 2. Unchanged reload occurs at A while credentials remain empty -> MUST clear DestinationChangedWhileEmpty!
            switch (unchangedTrigger)
            {
                case "unrelated":
                    unrelatedProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Unrelated:Tick"] = "1" });
                    break;
                case "root-reload":
                    root.Reload();
                    break;
                case "same-dest":
                    destProv.Rotate(DestDict("relay-h6-a.example.test", urlA, "Updated Display Name"));
                    break;
            }

            // 3. Separate credential provider adds B's first credentials while destination is STILL A -> MUST stay withheld at A!
            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "first-user-b",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "first-pass-b",
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "first-tok-b",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUserKeyConfigKey] = "first-ukey-b",
                [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = "first-topic-b"
            });

            var withheldAtA = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h6-a.example.test", withheldAtA.Options.Smtp.Host);
            Assert.AreEqual(urlA, withheldAtA.Secrets.WebhookUrl);
            Assert.IsNull(withheldAtA.Secrets.SmtpUsername, $"Trigger '{unchangedTrigger}': SMTP username must stay withheld at unchanged A.");
            Assert.IsNull(withheldAtA.Secrets.SmtpPassword, $"Trigger '{unchangedTrigger}': SMTP password must stay withheld at unchanged A.");
            Assert.IsNull(withheldAtA.Secrets.WebhookAuthToken, $"Trigger '{unchangedTrigger}': Webhook token must stay withheld at unchanged A.");
            Assert.IsNull(withheldAtA.Secrets.WebhookUserKey, $"Trigger '{unchangedTrigger}': Webhook userKey must stay withheld at unchanged A.");
            Assert.IsNull(withheldAtA.Secrets.WebhookNtfyTopic, $"Trigger '{unchangedTrigger}': Webhook ntfyTopic must stay withheld at unchanged A.");

            // 4. Destination moves A -> B -> MUST converge cleanly to (B, credB)!
            destProv.Rotate(DestDict("relay-h6-b.example.test", urlB));
            var convergedAtB = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-h6-b.example.test", convergedAtB.Options.Smtp.Host);
            Assert.AreEqual(urlB, convergedAtB.Secrets.WebhookUrl);
            Assert.AreEqual("first-user-b", convergedAtB.Secrets.SmtpUsername);
            Assert.AreEqual("first-pass-b", convergedAtB.Secrets.SmtpPassword);
            Assert.AreEqual("first-tok-b", convergedAtB.Secrets.WebhookAuthToken);
            Assert.AreEqual("first-ukey-b", convergedAtB.Secrets.WebhookUserKey);
            Assert.AreEqual("first-topic-b", convergedAtB.Secrets.WebhookNtfyTopic);
        }
    }

    [TestMethod]
    public async Task TopLevelNonConfigurationRootWrapper_FailsClosedOnCredentialOnlyRotationAndConvergesOnDestinationMoveAsync()
    {
        // Delta13 Finding #7: Any top-level IConfiguration that is NOT an IConfigurationRoot cannot prove single-provider
        // ownership and MUST fail closed (treating destination and credentials as potentially split), withholding
        // credential-only rotations at an established destination until destination moves to B!
        static Dictionary<string, string?> DestDict(string host, string url, bool includeBindings = false)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = host,
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Ntfy",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = url
            };
            if (includeBindings)
            {
                dict[ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = $"{host}:587:StartTls";
                dict[ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = url;
            }
            return dict;
        }
        static Dictionary<string, string?> CredsDict(string tag) => new(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = $"wrap-user-{tag}",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = $"wrap-pass-{tag}",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = $"wrap-tok-{tag}",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUserKeyConfigKey] = $"wrap-ukey-{tag}",
            [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = $"wrap-topic-{tag}"
        };

        const string urlA = "https://wrap-a.example.test/receiver-a";
        const string urlB = "https://wrap-b.example.test/receiver-b";
        const string urlBOtherPath = "https://wrap-b.example.test/receiver-c";

        using var dir = TempStateDir.Create();
        var destProv = new RotatingHostConfigurationProvider(DestDict("relay-wrap-a.example.test", urlA, includeBindings: true));
        var credsProv = new RotatingHostConfigurationProvider(CredsDict("a"));
        var innerRoot = new ConfigurationRoot([destProv, credsProv]);
        // Pass a top-level non-IConfigurationRoot wrapper directly into BOTH provider and store!
        IConfiguration topLevelWrapper = new OpaqueConfigurationWrapper(innerRoot);
        Assert.IsNotInstanceOfType<IConfigurationRoot>(topLevelWrapper);

        using var configProvider = new ConfigurationOperatorNotificationConfigProvider(topLevelWrapper);
        using var store = CreateStore(dir.Path, isDevelopment: false, configuration: topLevelWrapper);

        // 1. Initial establishment at A succeeds cleanly.
        var initConfig = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-wrap-a.example.test", initConfig.Options.Smtp.Host);
        Assert.AreEqual("wrap-user-a", initConfig.Secrets.SmtpUsername);
        Assert.AreEqual("wrap-pass-a", initConfig.Secrets.SmtpPassword);
        Assert.AreEqual("wrap-tok-a", initConfig.Secrets.WebhookAuthToken);
        Assert.AreEqual("wrap-ukey-a", initConfig.Secrets.WebhookUserKey);
        Assert.AreEqual("wrap-topic-a", initConfig.Secrets.WebhookNtfyTopic);

        // 2. Rotate ONLY credentials (a -> b) while destination stays A -> MUST fail closed and withhold ALL 5 fields at A!
        credsProv.Rotate(CredsDict("b"));
        var withheldConfig = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-wrap-a.example.test", withheldConfig.Options.Smtp.Host);
        Assert.AreEqual(urlA, withheldConfig.Secrets.WebhookUrl);
        Assert.IsNull(withheldConfig.Secrets.SmtpUsername, "Non-IConfigurationRoot wrapper MUST withhold rotated SMTP username at unchanged A.");
        Assert.IsNull(withheldConfig.Secrets.SmtpPassword, "Non-IConfigurationRoot wrapper MUST withhold rotated SMTP password at unchanged A.");
        Assert.IsNull(withheldConfig.Secrets.WebhookAuthToken, "Non-IConfigurationRoot wrapper MUST withhold rotated webhook token at unchanged A.");
        Assert.IsNull(withheldConfig.Secrets.WebhookUserKey, "Non-IConfigurationRoot wrapper MUST withhold rotated webhook userKey at unchanged A.");
        Assert.IsNull(withheldConfig.Secrets.WebhookNtfyTopic, "Non-IConfigurationRoot wrapper MUST withhold rotated webhook ntfyTopic at unchanged A.");

        var withheldStore = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(withheldStore.Secrets.SmtpUsername);
        Assert.IsNull(withheldStore.Secrets.SmtpPassword);
        Assert.IsNull(withheldStore.Secrets.WebhookAuthToken);
        Assert.IsNull(withheldStore.Secrets.WebhookUserKey);
        Assert.IsNull(withheldStore.Secrets.WebhookNtfyTopic);

        // 3. Move destination A -> B:
        //    - Webhook (single-key destination) converges to (urlB, wrap-*-b).
        //    - SMTP (3-component tuple behind opaque non-IConfigurationRoot wrapper) FAILS CLOSED on live reload
        //      (never guessing whether Port/SecurityMode are split inside the opaque wrapper!), and recovers cleanly
        //      via host restart or explicit operator store save!
        destProv.Rotate(DestDict("relay-wrap-b.example.test", urlB, includeBindings: true));
        var convergedConfig = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-wrap-b.example.test", convergedConfig.Options.Smtp.Host);
        Assert.AreEqual(urlB, convergedConfig.Secrets.WebhookUrl);
        Assert.IsNull(convergedConfig.Secrets.SmtpUsername,
            "Opaque non-IConfigurationRoot wrapper MUST fail closed on live-reload SMTP credential promotion.");
        Assert.IsNull(convergedConfig.Secrets.SmtpPassword,
            "Opaque non-IConfigurationRoot wrapper MUST fail closed on live-reload SMTP credential promotion.");
        Assert.AreEqual("wrap-tok-b", convergedConfig.Secrets.WebhookAuthToken);
        Assert.AreEqual("wrap-ukey-b", convergedConfig.Secrets.WebhookUserKey);
        Assert.AreEqual("wrap-topic-b", convergedConfig.Secrets.WebhookNtfyTopic);

        var convergedStore = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(convergedStore.Secrets.SmtpUsername);
        Assert.IsNull(convergedStore.Secrets.SmtpPassword);
        Assert.AreEqual("wrap-tok-b", convergedStore.Secrets.WebhookAuthToken);

        // Restarting provider on settled opaque config (or saving via operator store) promotes SMTP cleanly.
        using (var restartedOpaque = new ConfigurationOperatorNotificationConfigProvider(topLevelWrapper))
        {
            var afterRestart = await restartedOpaque.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("wrap-user-b", afterRestart.Secrets.SmtpUsername);
            Assert.AreEqual("wrap-pass-b", afterRestart.Secrets.SmtpPassword);
        }

        // 4. Same-origin webhook path move (/receiver-b -> /receiver-c) inside the wrapper withholds /receiver-b's creds!
        destProv.Rotate(DestDict("relay-wrap-b.example.test", urlBOtherPath, includeBindings: true));
        var afterPathMove = await configProvider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(urlBOtherPath, afterPathMove.Secrets.WebhookUrl);
        Assert.IsNull(afterPathMove.Secrets.WebhookAuthToken,
            "Same-origin path move inside non-IConfigurationRoot wrapper MUST suppress prior path's token.");
        Assert.IsNull(afterPathMove.Secrets.WebhookUserKey);
        Assert.IsNull(afterPathMove.Secrets.WebhookNtfyTopic);
    }

    [TestMethod]
    public async Task LegacyWebhookWithoutBoundWebhookDestination_WithholdsOnReadDeliveryAndNeverStampsOnUnrelatedSaveAsync()
    {
        // Delta14 H1: Pre-delta13 envelope containing encrypted WebhookUrl=/receiver-b alongside retained
        // /receiver-a token & userKey (with origin-only binding, NO BoundWebhookDestination) MUST withhold
        // credentials on read and live delivery, and an unrelated settings save MUST NEVER stamp a v2 binding!
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using var cert = WebhookNotificationTransportTests.CreateEphemeralLoopbackCertificate();
        var server = WebhookNotificationTransportTests.InProcessHttpsWebhookServer.Start(
            cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await using (server.ConfigureAwait(false))
        {
            var loopbackPolicy = new OperatorNotificationNetworkPolicy(isDevelopment: false)
            {
                AllowLoopbackForTesting = true
            };
            var urlB = $"https://127.0.0.1:{server.Port}/receiver-b";
            var origin = $"HTTPS://127.0.0.1:{server.Port}";

            var protector = dpProvider.CreateProtector("CameraAgent", "OperatorNotifications", "v1");
            var legacyPayload = JsonSerializer.Serialize(new
            {
                webhookUrl = urlB,
                webhookAuthToken = "legacy-tok-from-receiver-a",
                webhookUserKey = "legacy-ukey-from-receiver-a",
                boundWebhookOrigin = origin
            }, WebJsonOptions);
            var options = new OperatorNotificationsOptions
            {
                Enabled = true,
                TestSendMinimumIntervalSeconds = 0,
                Webhook = new WebhookNotificationOptions
                {
                    Enabled = true,
                    Format = WebhookPayloadFormat.Generic,
                    AuthHeaderName = "Authorization"
                }
            };
            var legacyEnvelope = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                revision = "pre-delta13-origin-only-rev",
                options,
                protectedSecrets = protector.Protect(legacyPayload)
            }, WebJsonOptions);
            await File.WriteAllTextAsync(
                Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName),
                legacyEnvelope,
                CancellationToken.None).ConfigureAwait(false);

            using var transport = new WebhookNotificationTransport(loopbackPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, cert.Thumbprint, StringComparison.Ordinal)
            };

            using (var store = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = dir.Path }),
                new ConfigurationBuilder().Build(),
                loopbackPolicy,
                NullLogger<OperatorNotificationStore>.Instance))
            {
                using var telemetry = new OperatorNotificationTelemetry();
                using var dispatcher = new OperatorNotificationDispatcher(
                    store,
                    new SmtpNotificationTransport(loopbackPolicy, TimeProvider.System),
                    transport,
                    telemetry,
                    TimeProvider.System);

                var snap = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(snap.Secrets.HasWebhookUrl);
                Assert.IsFalse(snap.Secrets.HasWebhookAuthToken, "Unbound legacy webhook token must not report present.");
                Assert.IsFalse(snap.Secrets.HasWebhookUserKey, "Unbound legacy webhook userKey must not report present.");

                var eff = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(urlB, eff.Secrets.WebhookUrl);
                Assert.IsNull(eff.Secrets.WebhookAuthToken);
                Assert.IsNull(eff.Secrets.WebhookUserKey);

                var directRead = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(urlB, directRead.WebhookUrl);
                Assert.IsNull(directRead.WebhookAuthToken);
                Assert.IsNull(directRead.WebhookUserKey);

                var deliveryBeforeSave = await dispatcher.SendTestAsync(NotificationTestTarget.Webhook, "owner-1", CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(deliveryBeforeSave.Channels[0].Succeeded, deliveryBeforeSave.Channels[0].ReasonCode);
                Assert.HasCount(1, server.Captured);
                Assert.IsNull(server.Captured[0].Header("Authorization"),
                    "Live delivery must NOT send unbound legacy webhook credentials to /receiver-b.");

                // Resubmitting the unbound legacy token on a versioned save MUST fail validation.
                var copiedReject = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                    snap.Version,
                    options,
                    WebhookAuthToken: "legacy-tok-from-receiver-a"), "owner-1", CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, copiedReject.Outcome);
                Assert.IsTrue(copiedReject.Problems!.ContainsKey("webhook.authToken"));

                // Unrelated settings save MUST NOT stamp the unbound legacy credentials with a v2 binding!
                var unrelatedOptions = new OperatorNotificationsOptions
                {
                    Enabled = options.Enabled,
                    EvaluationIntervalSeconds = 45,
                    TestSendMinimumIntervalSeconds = options.TestSendMinimumIntervalSeconds,
                    Webhook = options.Webhook
                };
                var unrelatedSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                    snap.Version,
                    unrelatedOptions), "owner-1", CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, unrelatedSave.Outcome, unrelatedSave.Message);
                Assert.IsFalse(unrelatedSave.Snapshot!.Secrets.HasWebhookAuthToken);
                Assert.IsFalse(unrelatedSave.Snapshot.Secrets.HasWebhookUserKey);
            }

            // Reload from disk and verify unbound credentials stay withheld (never stamped with a v2 binding).
            using (var reloaded = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = dir.Path }),
                new ConfigurationBuilder().Build(),
                loopbackPolicy,
                NullLogger<OperatorNotificationStore>.Instance))
            {
                var reloadedEff = await reloaded.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(urlB, reloadedEff.Secrets.WebhookUrl);
                Assert.IsNull(reloadedEff.Secrets.WebhookAuthToken);
                Assert.IsNull(reloadedEff.Secrets.WebhookUserKey);

                // Fresh operator authorization binds cleanly and delivers the new token.
                var snapReloaded = await reloaded.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                var freshAuth = await reloaded.SaveAsync(new OperatorNotificationsUpdateRequest(
                    snapReloaded.Version,
                    snapReloaded.Options,
                    WebhookAuthToken: "fresh-tok-receiver-b"), "owner-1", CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, freshAuth.Outcome, freshAuth.Message);
                var afterFresh = await reloaded.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("fresh-tok-receiver-b", afterFresh.Secrets.WebhookAuthToken);
            }
        }
    }

    [TestMethod]
    public async Task LegacyPreV2TruncatedHistories_16EntryAnd1EntryAndScalar_ConservativelyRejectEvictedCredentialsAtBAsync()
    {
        // Delta14 H2: Treat ALL pre-v2 histories (RetiredCredentialHistoryVersion < 2 — including 16-entry delta9
        // writers, 1-entry lists, and legacy scalar single-entry writers, regardless of stored false flags) as
        // potentially truncated so unversioned saves can NEVER admit forgotten credentials from A at destination B!
        static string SaltedHash(string s, string value)
            => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{s}:{value}")));

        const string originA = "HTTPS://PREV2-A.EXAMPLE.TEST:443";
        const string endpointA = "RELAY-PREV2-A.EXAMPLE.TEST:587:StartTls";
        const string urlB = "https://prev2-b.example.test/hook";
        const string salt = "11223344556677889900aabbccddeeff";

        var optionsB = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-prev2-b.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };

        foreach (var mode in new[] { "16-entry-with-false-flags", "1-entry-list", "1-entry-scalar" })
        {
            using var dir = TempStateDir.Create();
            var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
            var protector = dpProvider.CreateProtector("CameraAgent", "OperatorNotifications", "v1");

            object protectedPayload;
            if (mode == "1-entry-scalar")
            {
                protectedPayload = new
                {
                    webhookUrl = urlB,
                    retiredSalt = salt,
                    retiredWebhookOrigin = originA,
                    retiredWebhookAuthTokenHash = SaltedHash(salt, "evicted-tok-2"),
                    retiredSmtpEndpoint = endpointA,
                    retiredSmtpUsernameHash = SaltedHash(salt, "evicted-user-2"),
                    retiredSmtpPasswordHash = SaltedHash(salt, "evicted-pass-2")
                };
            }
            else
            {
                var count = mode == "16-entry-with-false-flags" ? 16 : 1;
                var smtps = new List<object>(count);
                var webhooks = new List<object>(count);
                for (var i = 2; i < 2 + count; i++)
                {
                    smtps.Add(new
                    {
                        endpoint = endpointA,
                        usernameHash = SaltedHash(salt, $"evicted-user-{i}"),
                        passwordHash = SaltedHash(salt, $"evicted-pass-{i}")
                    });
                    webhooks.Add(new
                    {
                        origin = originA,
                        authTokenHash = SaltedHash(salt, $"evicted-tok-{i}")
                    });
                }

                protectedPayload = new
                {
                    webhookUrl = urlB,
                    retiredSalt = salt,
                    retiredWebhooks = webhooks,
                    retiredSmtps = smtps,
                    anyWebhookHistoryTruncated = false,
                    anySmtpHistoryTruncated = false
                };
            }

            var envelope = new
            {
                schemaVersion = 1,
                revision = $"prev2-{mode}-rev",
                options = optionsB,
                protectedSecrets = protector.Protect(JsonSerializer.Serialize(protectedPayload, WebJsonOptions))
            };
            await File.WriteAllTextAsync(
                Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName),
                JsonSerializer.Serialize(envelope, WebJsonOptions),
                CancellationToken.None).ConfigureAwait(false);

            using (var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider))
            {
                // Unversioned save carrying forgotten cred #1 from pre-v2 history MUST be rejected at B!
                await ((IOperatorNotificationSecretStore)store).SaveAsync(
                    new OperatorNotificationSecrets(
                        SmtpUsername: "evicted-user-1",
                        SmtpPassword: "evicted-pass-1",
                        WebhookUrl: urlB,
                        WebhookAuthToken: "evicted-tok-1"),
                    CancellationToken.None).ConfigureAwait(false);

                var eff = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsNull(eff.Secrets.SmtpUsername, $"Mode '{mode}' must reject evicted SMTP username #1 at B.");
                Assert.IsNull(eff.Secrets.SmtpPassword, $"Mode '{mode}' must reject evicted SMTP password #1 at B.");
                Assert.IsNull(eff.Secrets.WebhookAuthToken, $"Mode '{mode}' must reject evicted webhook token #1 at B.");

                // Versioned save at B succeeds and persists conservative truncation into v2.
                var snap = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                var saved = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                    snap.Version,
                    optionsB,
                    SmtpUsername: "fresh-user-b",
                    SmtpPassword: "fresh-pass-b",
                    WebhookAuthToken: "fresh-tok-b"), "owner-1", CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved.Outcome, saved.Message);
            }

            // Reloaded v2 store retains conservative truncation and STILL rejects evicted #1 on unversioned retry.
            using var reloaded = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider);
            await ((IOperatorNotificationSecretStore)reloaded).SaveAsync(
                new OperatorNotificationSecrets(
                    SmtpUsername: "evicted-user-1",
                    SmtpPassword: "evicted-pass-1",
                    WebhookUrl: urlB,
                    WebhookAuthToken: "evicted-tok-1"),
                CancellationToken.None).ConfigureAwait(false);
            var effAfterReload = await reloaded.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("fresh-user-b", effAfterReload.Secrets.SmtpUsername, $"Mode '{mode}' must preserve B username after v2 migration.");
            Assert.AreEqual("fresh-pass-b", effAfterReload.Secrets.SmtpPassword, $"Mode '{mode}' must preserve B password after v2 migration.");
            Assert.AreEqual("fresh-tok-b", effAfterReload.Secrets.WebhookAuthToken, $"Mode '{mode}' must preserve B token after v2 migration.");
        }
    }

    [TestMethod]
    public async Task VersionedSettingsSave_ResubmittingCopiedOrRetiredCredentialsOnDestinationChange_FailsValidationAsync()
    {
        // Delta14 H1/H3: A versioned settings save changing webhook canonical URL/path OR SMTP (host, port, securityMode)
        // MUST reject any supplied credential matching the previously active credential OR any retired credential bound
        // to another destination; only genuinely fresh credentials (or explicit clear/empty) may be bound!
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        const string pathA = "https://hooks.example.test/receiver-a";
        const string pathB = "https://hooks.example.test/receiver-b";

        var hostConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-host.example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-user-a",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-pass-a",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = pathA,
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "host-tok-a"
            })
            .Build();
        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: hostConfig);

        // 1. From Absent store (active host credentials on relay-host:587:StartTls and pathA):
        //    changing port or path while resubmitting host credentials MUST fail validation.
        var s0 = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var hostCopyReject = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s0.Version,
            new OperatorNotificationsOptions
            {
                Enabled = true,
                Smtp = new SmtpNotificationOptions
                {
                    Enabled = true,
                    Host = "relay-host.example.test",
                    Port = 2525,
                    SecurityMode = SmtpSecurityMode.StartTls,
                    FromAddress = "camera@example.test",
                    Recipients = ["ops@example.test"]
                },
                Webhook = new WebhookNotificationOptions { Enabled = true }
            },
            SmtpUsername: "host-user-a",
            SmtpPassword: "host-pass-a",
            WebhookUrl: pathB,
            WebhookAuthToken: "host-tok-a"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, hostCopyReject.Outcome);
        Assert.IsTrue(hostCopyReject.Problems!.ContainsKey("smtp.username"));
        Assert.IsTrue(hostCopyReject.Problems.ContainsKey("smtp.password"));
        Assert.IsTrue(hostCopyReject.Problems.ContainsKey("webhook.authToken"));

        // 2. Establish protected credentials on (relay-a:587:StartTls) + pathA, then rotate once on same destination
        //    so v1 creds are retired and v2 creds are active.
        var optionsA = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-a.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions
            {
                Enabled = true,
                Format = WebhookPayloadFormat.Pushover
            }
        };
        var s1 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s0.Version,
            optionsA,
            SmtpUsername: "user-a-v1",
            SmtpPassword: "pass-a-v1",
            WebhookUrl: pathA,
            WebhookAuthToken: "tok-a-v1",
            WebhookUserKey: "ukey-a-v1",
            WebhookNtfyTopic: "topic-a-v1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, s1.Outcome, s1.Message);

        var s2 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s1.Snapshot!.Version,
            optionsA,
            SmtpUsername: "user-a-v2",
            SmtpPassword: "pass-a-v2",
            WebhookAuthToken: "tok-a-v2",
            WebhookUserKey: "ukey-a-v2",
            WebhookNtfyTopic: "topic-a-v2"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, s2.Outcome, s2.Message);

        // 3. Changing webhook path (/receiver-a -> /receiver-b) while resubmitting ACTIVE or RETIRED token/userKey/topic MUST fail!
        var activeWebhookCopy = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s2.Snapshot!.Version,
            optionsA,
            WebhookUrl: pathB,
            WebhookAuthToken: "tok-a-v2",
            WebhookUserKey: "ukey-a-v2",
            WebhookNtfyTopic: "topic-a-v2"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, activeWebhookCopy.Outcome);
        Assert.IsTrue(activeWebhookCopy.Problems!.ContainsKey("webhook.authToken"));
        Assert.IsTrue(activeWebhookCopy.Problems.ContainsKey("webhook.userKey"));
        Assert.IsTrue(activeWebhookCopy.Problems.ContainsKey("webhook.ntfyTopic"));

        var retiredWebhookCopy = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s2.Snapshot.Version,
            optionsA,
            WebhookUrl: pathB,
            WebhookAuthToken: "tok-a-v1",
            WebhookUserKey: "ukey-a-v1",
            WebhookNtfyTopic: "topic-a-v1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, retiredWebhookCopy.Outcome);
        Assert.IsTrue(retiredWebhookCopy.Problems!.ContainsKey("webhook.authToken"));
        Assert.IsTrue(retiredWebhookCopy.Problems.ContainsKey("webhook.userKey"));
        Assert.IsTrue(retiredWebhookCopy.Problems.ContainsKey("webhook.ntfyTopic"));

        // 4. Changing SMTP port (587 -> 2525) or SecurityMode (StartTls -> ImplicitTls) while resubmitting ACTIVE or RETIRED creds MUST fail!
        var portChangeOptions = new OperatorNotificationsOptions
        {
            Enabled = optionsA.Enabled,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = optionsA.Smtp.Enabled,
                Host = optionsA.Smtp.Host,
                Port = 2525,
                SecurityMode = optionsA.Smtp.SecurityMode,
                FromAddress = optionsA.Smtp.FromAddress,
                Recipients = [.. optionsA.Smtp.Recipients]
            },
            Webhook = optionsA.Webhook
        };
        var activeSmtpPortCopy = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s2.Snapshot.Version,
            portChangeOptions,
            SmtpUsername: "user-a-v2",
            SmtpPassword: "pass-a-v2"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, activeSmtpPortCopy.Outcome);
        Assert.IsTrue(activeSmtpPortCopy.Problems!.ContainsKey("smtp.username"));
        Assert.IsTrue(activeSmtpPortCopy.Problems.ContainsKey("smtp.password"));

        var modeChangeOptions = new OperatorNotificationsOptions
        {
            Enabled = optionsA.Enabled,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = optionsA.Smtp.Enabled,
                Host = optionsA.Smtp.Host,
                Port = optionsA.Smtp.Port,
                SecurityMode = SmtpSecurityMode.ImplicitTls,
                FromAddress = optionsA.Smtp.FromAddress,
                Recipients = [.. optionsA.Smtp.Recipients]
            },
            Webhook = optionsA.Webhook
        };
        var retiredSmtpModeCopy = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s2.Snapshot.Version,
            modeChangeOptions,
            SmtpUsername: "user-a-v1",
            SmtpPassword: "pass-a-v1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, retiredSmtpModeCopy.Outcome);
        Assert.IsTrue(retiredSmtpModeCopy.Problems!.ContainsKey("smtp.username"));
        Assert.IsTrue(retiredSmtpModeCopy.Problems.ContainsKey("smtp.password"));

        // Store remained intact on A throughout all rejected saves.
        var unchangedEff = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(587, unchangedEff.Options.Smtp.Port);
        Assert.AreEqual(pathA, unchangedEff.Secrets.WebhookUrl);
        Assert.AreEqual("tok-a-v2", unchangedEff.Secrets.WebhookAuthToken);

        // 5. Supplying genuinely fresh credentials on changed SMTP port + webhook path succeeds cleanly!
        var freshChange = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s2.Snapshot.Version,
            portChangeOptions,
            SmtpUsername: "user-b-fresh",
            SmtpPassword: "pass-b-fresh",
            WebhookUrl: pathB,
            WebhookAuthToken: "tok-b-fresh",
            WebhookUserKey: "ukey-b-fresh",
            WebhookNtfyTopic: "topic-b-fresh"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, freshChange.Outcome, freshChange.Message);
        var effB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2525, effB.Options.Smtp.Port);
        Assert.AreEqual("user-b-fresh", effB.Secrets.SmtpUsername);
        Assert.AreEqual("pass-b-fresh", effB.Secrets.SmtpPassword);
        Assert.AreEqual(pathB, effB.Secrets.WebhookUrl);
        Assert.AreEqual("tok-b-fresh", effB.Secrets.WebhookAuthToken);
        Assert.AreEqual("ukey-b-fresh", effB.Secrets.WebhookUserKey);
        Assert.AreEqual("topic-b-fresh", effB.Secrets.WebhookNtfyTopic);
    }

    [TestMethod]
    public async Task SingleLeafProviderDestinationOnlyEditAndOverrideRemoval_WithholdsUnchangedAndRetiredCredentialsAsync()
    {
        // Delta14 H1/H3 (Finding 4): Provider co-location is NOT proof of credential rebinding. Editing ONLY a
        // webhook path, SMTP port, or SMTP security mode inside one leaf provider — OR removing a higher-precedence
        // override so an underlying leaf provider wins all fields — MUST withhold unchanged or retired credentials!
        static Dictionary<string, string?> CombinedDict(
            string host, int port, SmtpSecurityMode mode, string user, string pass,
            string url, string tok, string ukey, string topic) => new(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = host,
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = mode.ToString(),
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = user,
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = pass,
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = "Ntfy",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = url,
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = tok,
                [ConfigurationOperatorNotificationConfigProvider.WebhookUserKeyConfigKey] = ukey,
                [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = topic
            };

        const string urlA = "https://single-leaf.example.test/receiver-a";
        const string urlB = "https://single-leaf.example.test/receiver-b";

        // Part A: Single leaf provider destination-only edits.
        {
            var singleProv = new RotatingHostConfigurationProvider(
                CombinedDict("relay-leaf.example.test", 587, SmtpSecurityMode.StartTls, "user-a", "pass-a", urlA, "tok-a", "ukey-a", "topic-a"));
            var root = new ConfigurationRoot([singleProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            var init = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("user-a", init.Secrets.SmtpUsername);
            Assert.AreEqual("pass-a", init.Secrets.SmtpPassword);
            Assert.AreEqual("tok-a", init.Secrets.WebhookAuthToken);

            // Edit ONLY webhook path + SMTP port while leaving ALL credentials unchanged -> MUST withhold ALL 5!
            singleProv.Rotate(
                CombinedDict("relay-leaf.example.test", 2525, SmtpSecurityMode.StartTls, "user-a", "pass-a", urlB, "tok-a", "ukey-a", "topic-a"));
            var afterPathAndPort = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2525, afterPathAndPort.Options.Smtp.Port);
            Assert.AreEqual(urlB, afterPathAndPort.Secrets.WebhookUrl);
            Assert.IsNull(afterPathAndPort.Secrets.SmtpUsername, "Single-provider SMTP port-only edit MUST withhold unchanged username.");
            Assert.IsNull(afterPathAndPort.Secrets.SmtpPassword, "Single-provider SMTP port-only edit MUST withhold unchanged password.");
            Assert.IsNull(afterPathAndPort.Secrets.WebhookAuthToken, "Single-provider webhook path-only edit MUST withhold unchanged token.");
            Assert.IsNull(afterPathAndPort.Secrets.WebhookUserKey, "Single-provider webhook path-only edit MUST withhold unchanged userKey.");
            Assert.IsNull(afterPathAndPort.Secrets.WebhookNtfyTopic, "Single-provider webhook path-only edit MUST withhold unchanged topic.");

            // Edit ONLY SMTP securityMode while leaving credentials unchanged -> MUST withhold SMTP creds!
            singleProv.Rotate(
                CombinedDict("relay-leaf.example.test", 587, SmtpSecurityMode.ImplicitTls, "user-a", "pass-a", urlA, "tok-a", "ukey-a", "topic-a"));
            var afterModeOnly = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(SmtpSecurityMode.ImplicitTls, afterModeOnly.Options.Smtp.SecurityMode);
            Assert.IsNull(afterModeOnly.Secrets.SmtpUsername, "Single-provider SecurityMode-only edit MUST withhold unchanged username.");
            Assert.IsNull(afterModeOnly.Secrets.SmtpPassword, "Single-provider SecurityMode-only edit MUST withhold unchanged password.");
            // Reverting webhook back to bound urlA restores tok-a on urlA.
            Assert.AreEqual("tok-a", afterModeOnly.Secrets.WebhookAuthToken);

            // Rotate tok-a -> tok-a2 on urlA (retiring tok-a for urlA), then change to urlB carrying retired tok-a -> MUST withhold!
            singleProv.Rotate(
                CombinedDict("relay-leaf.example.test", 587, SmtpSecurityMode.StartTls, "user-a", "pass-a", urlA, "tok-a2", "ukey-a", "topic-a"));
            Assert.AreEqual("tok-a2", (await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false)).Secrets.WebhookAuthToken);

            singleProv.Rotate(
                CombinedDict("relay-leaf.example.test", 587, SmtpSecurityMode.StartTls, "user-a", "pass-a", urlB, "tok-a", "ukey-a", "topic-a"));
            var afterRetiredOnB = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(afterRetiredOnB.Secrets.WebhookAuthToken, "Single-provider edit moving to urlB with retired tok-a MUST withhold.");

            // Changing to urlB + port 2525 with genuinely fresh credentials converges cleanly!
            singleProv.Rotate(
                CombinedDict("relay-leaf.example.test", 2525, SmtpSecurityMode.StartTls, "user-b", "pass-b", urlB, "tok-b", "ukey-b", "topic-b"));
            var freshConverged = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("user-b", freshConverged.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", freshConverged.Secrets.SmtpPassword);
            Assert.AreEqual("tok-b", freshConverged.Secrets.WebhookAuthToken);
            Assert.AreEqual("ukey-b", freshConverged.Secrets.WebhookUserKey);
            Assert.AreEqual("topic-b", freshConverged.Secrets.WebhookNtfyTopic);
        }

        // Part B: Removing a higher-precedence override so the underlying single leaf provider wins all fields.
        {
            var baseProv = new RotatingHostConfigurationProvider(
                CombinedDict("relay-leaf.example.test", 2525, SmtpSecurityMode.StartTls, "shared-user", "shared-pass", urlB, "shared-tok", "shared-ukey", "shared-topic"));
            var overrideProv = new RotatingHostConfigurationProvider(
                CombinedDict("relay-leaf.example.test", 587, SmtpSecurityMode.StartTls, "shared-user", "shared-pass", urlA, "shared-tok", "shared-ukey", "shared-topic"));
            var root = new ConfigurationRoot([baseProv, overrideProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            var init = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(587, init.Options.Smtp.Port);
            Assert.AreEqual(urlA, init.Secrets.WebhookUrl);
            Assert.AreEqual("shared-pass", init.Secrets.SmtpPassword);
            Assert.AreEqual("shared-tok", init.Secrets.WebhookAuthToken);

            // Removing overrideProv makes baseProv sole winner for ALL fields at (2525, urlB) with UNCHANGED creds -> MUST withhold!
            overrideProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var afterOverrideRemoval = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2525, afterOverrideRemoval.Options.Smtp.Port);
            Assert.AreEqual(urlB, afterOverrideRemoval.Secrets.WebhookUrl);
            Assert.IsNull(afterOverrideRemoval.Secrets.SmtpUsername, "Override removal MUST NOT carry unchanged username onto underlying port.");
            Assert.IsNull(afterOverrideRemoval.Secrets.SmtpPassword, "Override removal MUST NOT carry unchanged password onto underlying port.");
            Assert.IsNull(afterOverrideRemoval.Secrets.WebhookAuthToken, "Override removal MUST NOT carry unchanged token onto underlying path.");
            Assert.IsNull(afterOverrideRemoval.Secrets.WebhookUserKey, "Override removal MUST NOT carry unchanged userKey onto underlying path.");
            Assert.IsNull(afterOverrideRemoval.Secrets.WebhookNtfyTopic, "Override removal MUST NOT carry unchanged topic onto underlying path.");
        }
    }

    [TestMethod]
    public async Task SplitAndOpaqueSmtp_CombinedPortModeAndConstantPort_FailClosedWithoutBindingAndConvergeWithBoundEndpointAsync()
    {
        // Delta14 H4: Eliminate heuristic partial-tuple completion guessing!
        // (1) Combined separate provider owning BOTH Port + SecurityMode: changing only Port while Mode is stale MUST
        //     withhold, and unbound completion never guesses.
        // (2) Constant separately supplied Port (587 stays 587 while Host & Mode change): unbound fails closed (no guess),
        //     while explicit same-provider Smtp:BoundEndpoint converges immediately without deadlock!
        static Dictionary<string, string?> HostDict(string host) => new(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = host,
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test"
        };

        // Case 1: One separate provider owns BOTH Port and SecurityMode.
        {
            var hostProv = new RotatingHostConfigurationProvider(HostDict("relay-pm-a.example.test"));
            var portModeProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = nameof(SmtpSecurityMode.StartTls)
            });
            var credsProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "user-a",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "pass-a"
            });
            var root = new ConfigurationRoot([hostProv, portModeProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            Assert.AreEqual("pass-a", (await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false)).Secrets.SmtpPassword);

            hostProv.Rotate(HostDict("relay-pm-b.example.test"));
            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "user-b",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "pass-b"
            });
            // Change ONLY Port (587 -> 465) while SecurityMode is STILL StartTls!
            portModeProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "465",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = nameof(SmtpSecurityMode.StartTls)
            });
            var partial = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(partial.Secrets.SmtpPassword,
                "Changing only Port in a combined Port+Mode provider MUST NOT release pass-b while SecurityMode is stale.");

            // Explicit Smtp:BoundEndpoint for final (hostB, 465, ImplicitTls) stays withheld at StartTls and converges on ImplicitTls!
            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "user-b",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "pass-b",
                [ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-pm-b.example.test:465:ImplicitTls"
            });
            Assert.IsNull((await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false)).Secrets.SmtpPassword);

            portModeProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "465",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = nameof(SmtpSecurityMode.ImplicitTls)
            });
            var finalEff = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("user-b", finalEff.Secrets.SmtpUsername);
            Assert.AreEqual("pass-b", finalEff.Secrets.SmtpPassword);
        }

        // Case 2: Separately supplied Port legitimately stays constant (587) while Host + SecurityMode rotate.
        {
            var hostProv = new RotatingHostConfigurationProvider(HostDict("relay-const-a.example.test"));
            var portProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587"
            });
            var modeProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = nameof(SmtpSecurityMode.StartTls)
            });
            var credsProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "user-a",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "pass-a"
            });
            var root = new ConfigurationRoot([hostProv, portProv, modeProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            hostProv.Rotate(HostDict("relay-const-b.example.test"));
            modeProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = nameof(SmtpSecurityMode.ImplicitTls)
            });
            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "user-b",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "pass-b",
                [ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = "relay-const-b.example.test:587:ImplicitTls"
            });
            var converged = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("user-b", converged.Secrets.SmtpUsername,
                "Constant separately supplied port must NOT deadlock when credential provider supplies matching Smtp:BoundEndpoint.");
            Assert.AreEqual("pass-b", converged.Secrets.SmtpPassword);
        }
    }

    [TestMethod]
    public async Task TemporaryAbsenceOfDestinationAndCredentials_PreservesRetiredSplitAndEmptyGuardsAcrossPartialRestoreAsync()
    {
        // Delta14 H5: When BOTH destination and credentials become temporarily absent, the tracker MUST retain retired
        // credentials AND preserve established-empty + prior-split guards (never resetting to cold-start admission!).
        var hostProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-abs-a.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test"
        });
        var portProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "2525"
        });
        var webhookDestProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://abs-a.example.test/hook"
        });
        var credsProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "abs-user-a",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "abs-pass-a",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "abs-tok-a"
        });
        var root = new ConfigurationRoot([hostProv, portProv, webhookDestProv, credsProv]);
        using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

        var init = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("abs-pass-a", init.Secrets.SmtpPassword);
        Assert.AreEqual("abs-tok-a", init.Secrets.WebhookAuthToken);

        // Temporarily remove ALL destinations AND ALL credentials!
        var empty = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        hostProv.Rotate(empty);
        portProv.Rotate(empty);
        webhookDestProv.Rotate(empty);
        credsProv.Rotate(empty);

        var whileAbsent = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(whileAbsent.Secrets.SmtpPassword);
        Assert.IsNull(whileAbsent.Secrets.WebhookAuthToken);

        // Partially restore SMTP (hostB with default 587, no portProv) + restore webhook urlA + unbound fresh creds:
        // - Split SMTP prior guard MUST still withhold unbound abs-pass-b (cannot take cold-start admission!).
        // - Webhook established-empty guard (urlA) MUST still withhold unbound abs-tok-b at urlA!
        hostProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-abs-b.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test"
        });
        webhookDestProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://abs-a.example.test/hook"
        });
        credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "abs-user-b",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "abs-pass-b",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "abs-tok-b"
        });

        var afterPartialRestore = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(afterPartialRestore.Secrets.SmtpUsername,
            "Temporary absence MUST preserve prior split-SMTP guard and withhold unbound username on partial restore.");
        Assert.IsNull(afterPartialRestore.Secrets.SmtpPassword,
            "Temporary absence MUST preserve prior split-SMTP guard and withhold unbound password on partial restore.");
        Assert.IsNull(afterPartialRestore.Secrets.WebhookAuthToken,
            "Temporary absence MUST preserve established-empty guard for urlA and withhold unbound token on restore.");

        // Retired abs-tok-a MUST ALSO stay rejected even if webhook moves to urlB.
        webhookDestProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://abs-b.example.test/hook"
        });
        credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "abs-tok-a"
        });
        Assert.IsNull((await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false)).Secrets.WebhookAuthToken,
            "Retired abs-tok-a must stay rejected at urlB even after temporary absence.");
    }

    [TestMethod]
    public async Task DestinationChangedWhileEmpty_ClearedUnconditionallyAcrossBindingErrorAndRetiredCredentialCallbacksAsync()
    {
        // Delta14 H6: DestinationChangedWhileEmpty MUST be consumed unconditionally on EVERY reload callback for
        // BOTH channels BEFORE any early return — including binding-error callbacks AND retired-credential callbacks!
        static Dictionary<string, string?> DestDict(string host, string url, string? badTimeout = null)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = host,
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = url
            };
            if (badTimeout is not null)
            {
                dict[OperatorNotificationsOptions.SectionKey + ":RequestTimeoutSeconds"] = badTimeout;
            }
            return dict;
        }

        // Part A: Intervening binding-error callback clears DestinationChangedWhileEmpty for BOTH SMTP and Webhook.
        {
            var destProv = new RotatingHostConfigurationProvider(DestDict("relay-err-x.example.test", "https://err-x.example.test/hook"));
            var credsProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var root = new ConfigurationRoot([destProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            // Empty move X -> A sets DestinationChangedWhileEmpty.
            destProv.Rotate(DestDict("relay-err-a.example.test", "https://err-a.example.test/hook"));
            // Intervening binding-error callback MUST clear DestinationChangedWhileEmpty!
            destProv.Rotate(DestDict("relay-err-a.example.test", "https://err-a.example.test/hook", badTimeout: "not-an-int"));
            Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, provider.ConfigurationErrorReasonCode);

            // Recover from binding error back to A and supply fresh unbound B credentials while destination is still A.
            destProv.Rotate(DestDict("relay-err-a.example.test", "https://err-a.example.test/hook"));
            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "err-user-b",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "err-pass-b",
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "err-tok-b"
            });

            var withheld = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(withheld.Secrets.SmtpUsername, "Intervening binding-error callback MUST clear DestinationChangedWhileEmpty for SMTP.");
            Assert.IsNull(withheld.Secrets.SmtpPassword, "Intervening binding-error callback MUST clear DestinationChangedWhileEmpty for SMTP.");
            Assert.IsNull(withheld.Secrets.WebhookAuthToken, "Intervening binding-error callback MUST clear DestinationChangedWhileEmpty for Webhook.");

            // Moving A -> B converges cleanly.
            destProv.Rotate(DestDict("relay-err-b.example.test", "https://err-b.example.test/hook"));
            var converged = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("err-user-b", converged.Secrets.SmtpUsername);
            Assert.AreEqual("err-pass-b", converged.Secrets.SmtpPassword);
            Assert.AreEqual("err-tok-b", converged.Secrets.WebhookAuthToken);
        }

        // Part B: Intervening retired-credential callback clears DestinationChangedWhileEmpty for BOTH SMTP and Webhook.
        {
            var destProv = new RotatingHostConfigurationProvider(DestDict("relay-ret-x.example.test", "https://ret-x.example.test/hook"));
            var credsProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "ret-user-x",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "ret-pass-x",
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "ret-tok-x"
            });
            var root = new ConfigurationRoot([destProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            Assert.AreEqual("ret-pass-x", (await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false)).Secrets.SmtpPassword);

            // Clear X credentials (retiring them for X), then empty move X -> A (setting DestinationChangedWhileEmpty).
            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            destProv.Rotate(DestDict("relay-ret-a.example.test", "https://ret-a.example.test/hook"));

            // Intervening callback surfaces retired X credentials at A -> rejected AND MUST consume DestinationChangedWhileEmpty!
            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "ret-user-x",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "ret-pass-x",
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "ret-tok-x"
            });
            var afterRetired = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(afterRetired.Secrets.SmtpPassword);
            Assert.IsNull(afterRetired.Secrets.WebhookAuthToken);

            // Subsequent callback supplies fresh unbound B credentials while destination is STILL A -> MUST stay withheld!
            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "ret-user-b",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "ret-pass-b",
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "ret-tok-b"
            });
            var afterFreshAtA = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(afterFreshAtA.Secrets.SmtpUsername, "Intervening retired-credential callback MUST consume DestinationChangedWhileEmpty for SMTP.");
            Assert.IsNull(afterFreshAtA.Secrets.SmtpPassword, "Intervening retired-credential callback MUST consume DestinationChangedWhileEmpty for SMTP.");
            Assert.IsNull(afterFreshAtA.Secrets.WebhookAuthToken, "Intervening retired-credential callback MUST consume DestinationChangedWhileEmpty for Webhook.");

            // Moving A -> B converges cleanly.
            destProv.Rotate(DestDict("relay-ret-b.example.test", "https://ret-b.example.test/hook"));
            var converged = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("ret-user-b", converged.Secrets.SmtpUsername);
            Assert.AreEqual("ret-pass-b", converged.Secrets.SmtpPassword);
            Assert.AreEqual("ret-tok-b", converged.Secrets.WebhookAuthToken);
        }
    }

    [TestMethod]
    public async Task RetiredWebhookHistory_UsesSaltedDestinationFingerprintsWithinByteBudgetAndNeverRetainsPlaintextUrlsAsync()
    {
        // Delta14 M1: Rotating 128 distinct 2,048-character webhook URLs MUST stay well under MaximumEnvelopeBytes
        // (256 KiB), and after clearing the webhook URL NO plaintext URL/path/query canary may remain inside the
        // decrypted protected payload JSON!
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider);

        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };

        var snap = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var version = snap.Version;
        var canaries = new List<string>(OperatorNotificationStore.MaxRetiredBindingsPerChannel);
        var padding = new string('p', 1940);

        for (var i = 1; i <= OperatorNotificationStore.MaxRetiredBindingsPerChannel; i++)
        {
            var canary = $"secret-seg-{i:D4}-q{i:D4}";
            canaries.Add(canary);
            var longUrl = $"https://hooks-m1.example.test/{canary}/{padding}?k={canary}";
            Assert.IsLessThanOrEqualTo(OperatorNotificationValidation.MaximumWebhookUrlLength, longUrl.Length);

            var saved = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                version,
                options,
                WebhookUrl: longUrl,
                WebhookAuthToken: $"m1-long-url-tok-{i:D4}"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, saved.Outcome,
                $"Rotation #{i} of 2048-char webhook URL must stay within MaximumEnvelopeBytes: {saved.Message}");
            version = saved.Snapshot!.Version;
        }

        // Clear the webhook URL & token (disabling webhook) and verify zero recoverable URLs remain in decrypted JSON!
        var disabledOptions = new OperatorNotificationsOptions { Enabled = false };
        var cleared = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            version,
            disabledOptions,
            ClearWebhookUrl: true,
            ClearWebhookAuthToken: true), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, cleared.Outcome, cleared.Message);

        var diskBytes = await File.ReadAllBytesAsync(store.ConfigFilePath, CancellationToken.None).ConfigureAwait(false);
        Assert.IsLessThanOrEqualTo(OperatorNotificationStore.MaximumEnvelopeBytes, diskBytes.Length,
            "Envelope with 128 retired 2048-char webhook rotations must fit well inside MaximumEnvelopeBytes.");

        using var envelopeDoc = JsonDocument.Parse(diskBytes);
        var protectedBlob = envelopeDoc.RootElement.GetProperty("protectedSecrets").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(protectedBlob));
        var protector = dpProvider.CreateProtector("CameraAgent", "OperatorNotifications", "v1");
        var unprotectedJson = protector.Unprotect(protectedBlob!);

        Assert.DoesNotContain("hooks-m1.example.test", unprotectedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(padding, unprotectedJson, StringComparison.Ordinal);
        foreach (var canary in canaries)
        {
            Assert.DoesNotContain(canary, unprotectedJson, StringComparison.Ordinal);
        }

        // Retired token #128 is still recognized and rejected at a different destination!
        var rejectRetiredAtOther = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            cleared.Snapshot!.Version,
            options,
            WebhookUrl: "https://other-m1.example.test/hook",
            WebhookAuthToken: "m1-long-url-tok-0128"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, rejectRetiredAtOther.Outcome);
        Assert.IsTrue(rejectRetiredAtOther.Problems!.ContainsKey("webhook.authToken"));
    }

    [TestMethod]
    public void PublicMergeSecretsAndBuildSecretPresenceOverloads_FailClosedOnUnboundProtectedCredentials()
    {
        // Delta14 M3: ALL public MergeSecrets and BuildSecretPresence overloads MUST fail closed and withhold
        // unbound protected credentials unless explicit ProtectedSecretBindings prove full destination binding!
        const string urlA = "https://m3-a.example.test/receiver-a";
        const string urlB = "https://m3-a.example.test/receiver-b";

        var smtpOptions = CreateSmtpOptions("relay-m3.example.test", 587, SmtpSecurityMode.StartTls);
        var configSecrets = new OperatorNotificationSecrets(
            WebhookUrl: urlA,
            WebhookAuthToken: "host-tok-a");

        // 1. Credential-only protected input paired with config URL -> BOTH 2-arg and 4-arg overloads withhold protected creds!
        var credOnlyProtected = new OperatorNotificationSecrets(
            SmtpUsername: "protected-unbound-user",
            SmtpPassword: "protected-unbound-pass",
            WebhookAuthToken: "protected-unbound-tok",
            WebhookUserKey: "protected-unbound-ukey",
            WebhookNtfyTopic: "protected-unbound-topic");

        var merged2 = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(credOnlyProtected, configSecrets);
        Assert.IsNull(merged2.SmtpUsername);
        Assert.IsNull(merged2.SmtpPassword);
        Assert.AreEqual(urlA, merged2.WebhookUrl);
        Assert.AreEqual("host-tok-a", merged2.WebhookAuthToken, "Unbound protected token must not override host token.");
        Assert.IsNull(merged2.WebhookUserKey);
        Assert.IsNull(merged2.WebhookNtfyTopic);

        var presence2 = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(credOnlyProtected, configSecrets);
        Assert.IsFalse(presence2.HasSmtpUsername);
        Assert.IsFalse(presence2.HasSmtpPassword);
        Assert.AreEqual(NotificationSecretSource.Configuration, presence2.WebhookAuthTokenSource);
        Assert.IsFalse(presence2.HasWebhookUserKey);
        Assert.IsFalse(presence2.HasWebhookNtfyTopic);

        var merged4 = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(credOnlyProtected, OperatorNotificationSecrets.Empty, smtpOptions, smtpOptions);
        Assert.IsNull(merged4.SmtpUsername);
        Assert.IsNull(merged4.SmtpPassword);
        Assert.IsNull(merged4.WebhookAuthToken);
        Assert.IsNull(merged4.WebhookUserKey);
        Assert.IsNull(merged4.WebhookNtfyTopic);

        var presence4 = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(credOnlyProtected, OperatorNotificationSecrets.Empty, smtpOptions, smtpOptions);
        Assert.IsFalse(presence4.HasSmtpUsername);
        Assert.IsFalse(presence4.HasSmtpPassword);
        Assert.IsFalse(presence4.HasWebhookAuthToken);

        // 2. Protected input WITH WebhookUrl + WebhookAuthToken but NO explicit bindings STILL withholds unbound credentials!
        var urlAndCredProtected = credOnlyProtected with { WebhookUrl = urlB };
        var mergedWithUrlNoBinding = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(urlAndCredProtected, configSecrets);
        Assert.AreEqual(urlB, mergedWithUrlNoBinding.WebhookUrl);
        Assert.IsNull(mergedWithUrlNoBinding.WebhookAuthToken, "Encrypted URL without explicit binding must NOT admit protected token.");
        Assert.IsNull(mergedWithUrlNoBinding.WebhookUserKey);
        Assert.IsNull(mergedWithUrlNoBinding.WebhookNtfyTopic);

        // 3. Binding-aware overload admits protected credentials ONLY when explicit bindings match exact full destinations!
        var validBindings = new ProtectedSecretBindings(
            WebhookOrigin: ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(urlB),
            SmtpEndpoint: ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(smtpOptions.Smtp),
            WebhookDestination: ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(urlB));
        var mergedBound = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(
            urlAndCredProtected, configSecrets, smtpOptions, smtpOptions, validBindings, smtpOptions);
        Assert.AreEqual("protected-unbound-user", mergedBound.SmtpUsername);
        Assert.AreEqual("protected-unbound-pass", mergedBound.SmtpPassword);
        Assert.AreEqual("protected-unbound-tok", mergedBound.WebhookAuthToken);
        Assert.AreEqual("protected-unbound-ukey", mergedBound.WebhookUserKey);
        Assert.AreEqual("protected-unbound-topic", mergedBound.WebhookNtfyTopic);

        // Mismatched path binding withholds webhook credentials while still admitting matching SMTP credentials.
        var wrongPathBindings = validBindings with
        {
            WebhookDestination = ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(urlA)
        };
        var mergedWrongPath = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(
            urlAndCredProtected, configSecrets, smtpOptions, smtpOptions, wrongPathBindings, smtpOptions);
        Assert.AreEqual("protected-unbound-pass", mergedWrongPath.SmtpPassword);
        Assert.IsNull(mergedWrongPath.WebhookAuthToken);
        Assert.IsNull(mergedWrongPath.WebhookUserKey);
        Assert.IsNull(mergedWrongPath.WebhookNtfyTopic);
    }

    [TestMethod]
    public async Task VersionedSettingsSave_AfterHistoryEvictionOrTruncation_RejectsCredentialsOnDestinationChangeForSmtpAndWebhookAsync()
    {
        // Delta15 H1: Destination-changing versioned saves MUST enforce the exact same fail-closed history truncation
        // invariant as unversioned saves — once a channel's retired history has evicted entries or Any*HistoryTruncated
        // is set, a destination-changing versioned save MUST reject submitted credentials (both evicted and unverified)
        // and require explicit two-step re-authorization (commit destination change first, then refresh and save creds).
        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider);

        const string urlA = "https://d15-h1-a.example.test/hook";
        const string urlB = "https://d15-h1-b.example.test/hook";
        const string urlC = "https://d15-h1-c.example.test/hook";

        var optionsA = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-d15-h1-a.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions
            {
                Enabled = true,
                Format = WebhookPayloadFormat.Generic
            }
        };
        var optionsB = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-d15-h1-b.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions
            {
                Enabled = true,
                Format = WebhookPayloadFormat.Generic
            }
        };

        // 1. Persist credential #1 at A, then churn > MaxRetiredBindingsPerChannel (128) rotations at A so #1 is evicted.
        var s0 = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var s1 = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            s0.Version,
            optionsA,
            SmtpUsername: "d15-h1-user-a-1",
            SmtpPassword: "d15-h1-pass-a-1",
            WebhookUrl: urlA,
            WebhookAuthToken: "d15-h1-tok-a-1",
            WebhookUserKey: "d15-h1-ukey-a-1",
            WebhookNtfyTopic: "d15-h1-topic-a-1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, s1.Outcome, s1.Message);

        for (var i = 2; i <= OperatorNotificationStore.MaxRetiredBindingsPerChannel + 8; i++)
        {
            var fresh = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                fresh with
                {
                    SmtpUsername = $"d15-h1-user-a-{i}",
                    SmtpPassword = $"d15-h1-pass-a-{i}",
                    WebhookAuthToken = $"d15-h1-tok-a-{i}",
                    WebhookUserKey = $"d15-h1-ukey-a-{i}",
                    WebhookNtfyTopic = $"d15-h1-topic-a-{i}"
                },
                CancellationToken.None).ConfigureAwait(false);
        }

        // 2. Destination-changing versioned save (A -> B) submitting EVICTED #1 credentials MUST fail validation on ALL 5 fields!
        var snapAtA = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var evictedReject = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapAtA.Version,
            optionsB,
            SmtpUsername: "d15-h1-user-a-1",
            SmtpPassword: "d15-h1-pass-a-1",
            WebhookUrl: urlB,
            WebhookAuthToken: "d15-h1-tok-a-1",
            WebhookUserKey: "d15-h1-ukey-a-1",
            WebhookNtfyTopic: "d15-h1-topic-a-1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, evictedReject.Outcome);
        Assert.IsTrue(evictedReject.Problems!.ContainsKey("smtp.username"), "Truncated SMTP history must reject evicted username on destination change.");
        Assert.IsTrue(evictedReject.Problems.ContainsKey("smtp.password"), "Truncated SMTP history must reject evicted password on destination change.");
        Assert.IsTrue(evictedReject.Problems.ContainsKey("webhook.authToken"), "Truncated webhook history must reject evicted authToken on destination change.");
        Assert.IsTrue(evictedReject.Problems.ContainsKey("webhook.userKey"), "Truncated webhook history must reject evicted userKey on destination change.");
        Assert.IsTrue(evictedReject.Problems.ContainsKey("webhook.ntfyTopic"), "Truncated webhook history must reject evicted ntfyTopic on destination change.");

        // 3. Even an unverified fresh-looking credential submitted in a single-step destination-changing save MUST fail closed
        //    under truncated history (matching the unversioned invariant with zero bypass).
        var singleStepReject = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapAtA.Version,
            optionsB,
            SmtpPassword: "d15-h1-unverified-pass-b",
            WebhookUrl: urlB,
            WebhookAuthToken: "d15-h1-unverified-tok-b"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, singleStepReject.Outcome);
        Assert.IsTrue(singleStepReject.Problems!.ContainsKey("smtp.password"));
        Assert.IsTrue(singleStepReject.Problems.ContainsKey("webhook.authToken"));

        // Store remained intact on A after rejected saves.
        var stillA = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-d15-h1-a.example.test", stillA.Options.Smtp.Host);
        Assert.AreEqual(urlA, stillA.Secrets.WebhookUrl);

        // 4. Two-step explicit re-authorization works cleanly: Step 1 commits destination change A -> B without credentials;
        //    Step 2 refreshes snapshot at B and binds fresh credentials on same destination B.
        var step1DestMove = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapAtA.Version,
            optionsB,
            WebhookUrl: urlB), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, step1DestMove.Outcome, step1DestMove.Message);

        var effAfterStep1 = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("relay-d15-h1-b.example.test", effAfterStep1.Options.Smtp.Host);
        Assert.IsNull(effAfterStep1.Secrets.SmtpPassword);
        Assert.AreEqual(urlB, effAfterStep1.Secrets.WebhookUrl);
        Assert.IsNull(effAfterStep1.Secrets.WebhookAuthToken);

        var step2FreshAtB = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            step1DestMove.Snapshot!.Version,
            optionsB,
            SmtpUsername: "d15-h1-fresh-user-b",
            SmtpPassword: "d15-h1-fresh-pass-b",
            WebhookAuthToken: "d15-h1-fresh-tok-b",
            WebhookUserKey: "d15-h1-fresh-ukey-b",
            WebhookNtfyTopic: "d15-h1-fresh-topic-b"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, step2FreshAtB.Outcome, step2FreshAtB.Message);

        var effAtB = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("d15-h1-fresh-user-b", effAtB.Secrets.SmtpUsername);
        Assert.AreEqual("d15-h1-fresh-pass-b", effAtB.Secrets.SmtpPassword);
        Assert.AreEqual("d15-h1-fresh-tok-b", effAtB.Secrets.WebhookAuthToken);
        Assert.AreEqual("d15-h1-fresh-ukey-b", effAtB.Secrets.WebhookUserKey);
        Assert.AreEqual("d15-h1-fresh-topic-b", effAtB.Secrets.WebhookNtfyTopic);

        // Moving B -> C in one step with credentials STILL fails closed because truncated history persists across saves.
        var bToCReject = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
            step2FreshAtB.Snapshot!.Version,
            optionsB,
            WebhookUrl: urlC,
            WebhookAuthToken: "d15-h1-tok-c"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, bToCReject.Outcome);
        Assert.IsTrue(bToCReject.Problems!.ContainsKey("webhook.authToken"));
    }

    [TestMethod]
    public async Task VersionedAndUnversionedSaves_RejectHostRetiredWithheldAndTruncatedHostCredentialsOnDifferentDestinationAsync()
    {
        // Delta15 H2: OperatorNotificationStore MUST consult the live host tracker's read-only non-secret
        // retired/withheld/bound fingerprint and truncation checks on BOTH versioned and unversioned saves so
        // credentials retired or withheld by the host provider for destination A can NEVER be accepted for B!
        static Dictionary<string, string?> HostDict(
            string smtpHost, int smtpPort, string smtpUser, string smtpPass,
            string webhookUrl, string webhookTok, string webhookUkey, string webhookTopic,
            string? smtpBound = null, string? webhookBound = null)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = smtpHost,
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = smtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = nameof(SmtpSecurityMode.StartTls),
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = smtpUser,
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = smtpPass,
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = nameof(WebhookPayloadFormat.Generic),
                [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = webhookUrl,
                [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = webhookTok,
                [ConfigurationOperatorNotificationConfigProvider.WebhookUserKeyConfigKey] = webhookUkey,
                [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = webhookTopic
            };
            if (smtpBound is not null)
            {
                dict[ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = smtpBound;
            }
            if (webhookBound is not null)
            {
                dict[ConfigurationOperatorNotificationConfigProvider.WebhookBoundOriginConfigKey] = webhookBound;
            }
            return dict;
        }

        const string urlA = "https://d15-h2-a.example.test/hook";
        const string urlB = "https://d15-h2-b.example.test/hook";
        const string urlC = "https://d15-h2-c.example.test/hook";

        var optionsB = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-d15-h2-b.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions
            {
                Enabled = true,
                Format = WebhookPayloadFormat.Generic
            }
        };

        // Part A: Host in-place rotation retires v1 creds BEFORE capacity; store rejects them on versioned & unversioned saves to B.
        {
            using var dir = TempStateDir.Create();
            var hostProv = new RotatingHostConfigurationProvider(HostDict(
                "relay-d15-h2-a.example.test", 587, "host-user-a-1", "host-pass-a-1",
                urlA, "host-tok-a-1", "host-ukey-a-1", "host-topic-a-1"));
            var root = new ConfigurationRoot([hostProv]);

            using var configProvider = new ConfigurationOperatorNotificationConfigProvider(root);
            using var store = CreateStore(dir.Path, isDevelopment: false, configuration: root);

            // Host rotates v1 -> v2 on destination A (retiring v1 into live tracker history only; v1 is no longer in configSecrets!).
            hostProv.Rotate(HostDict(
                "relay-d15-h2-a.example.test", 587, "host-user-a-2", "host-pass-a-2",
                urlA, "host-tok-a-2", "host-ukey-a-2", "host-topic-a-2"));

            // Verify read-only non-secret API on ConfigurationOperatorNotificationConfigProvider reports retired-for-other cleanly.
            Assert.IsTrue(configProvider.IsCredentialRetiredOrBoundForOtherDestination(
                HostCredentialField.SmtpPassword, "host-pass-a-1", "relay-d15-h2-b.example.test:587:StartTls"));
            Assert.IsFalse(configProvider.IsCredentialRetiredOrBoundForOtherDestination(
                HostCredentialField.SmtpPassword, "host-pass-a-1", "relay-d15-h2-a.example.test:587:StartTls"));
            Assert.IsTrue(configProvider.IsCredentialRetiredOrBoundForOtherDestination(
                HostCredentialField.WebhookAuthToken, "host-tok-a-1", urlB));
            Assert.IsFalse(configProvider.IsCredentialRetiredOrBoundForOtherDestination(
                HostCredentialField.WebhookAuthToken, "host-tok-a-1", urlA));

            // Versioned save to DIFFERENT destination B submitting host-retired v1 credentials MUST fail validation on ALL 5 fields!
            var snap0 = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var versionedReject = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snap0.Version,
                optionsB,
                SmtpUsername: "host-user-a-1",
                SmtpPassword: "host-pass-a-1",
                WebhookUrl: urlB,
                WebhookAuthToken: "host-tok-a-1",
                WebhookUserKey: "host-ukey-a-1",
                WebhookNtfyTopic: "host-topic-a-1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, versionedReject.Outcome);
            Assert.IsTrue(versionedReject.Problems!.ContainsKey("smtp.username"));
            Assert.IsTrue(versionedReject.Problems.ContainsKey("smtp.password"));
            Assert.IsTrue(versionedReject.Problems.ContainsKey("webhook.authToken"));
            Assert.IsTrue(versionedReject.Problems.ContainsKey("webhook.userKey"));
            Assert.IsTrue(versionedReject.Problems.ContainsKey("webhook.ntfyTopic"));

            // Move store to destination B cleanly (no credentials), then verify BOTH versioned AND unversioned saves at B
            // STILL reject host-retired v1 credentials from A!
            var movedB = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snap0.Version,
                optionsB,
                WebhookUrl: urlB), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, movedB.Outcome, movedB.Message);

            var versionedAtBReject = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                movedB.Snapshot!.Version,
                optionsB,
                SmtpPassword: "host-pass-a-1",
                WebhookAuthToken: "host-tok-a-1",
                WebhookUserKey: "host-ukey-a-1",
                WebhookNtfyTopic: "host-topic-a-1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, versionedAtBReject.Outcome);
            Assert.IsTrue(versionedAtBReject.Problems!.ContainsKey("smtp.password"));
            Assert.IsTrue(versionedAtBReject.Problems.ContainsKey("webhook.authToken"));
            Assert.IsTrue(versionedAtBReject.Problems.ContainsKey("webhook.userKey"));
            Assert.IsTrue(versionedAtBReject.Problems.ContainsKey("webhook.ntfyTopic"));

            var readB = await ((IOperatorNotificationSecretStore)store).ReadAsync(CancellationToken.None).ConfigureAwait(false);
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                readB with
                {
                    SmtpUsername = "host-user-a-1",
                    SmtpPassword = "host-pass-a-1",
                    WebhookAuthToken = "host-tok-a-1",
                    WebhookUserKey = "host-ukey-a-1",
                    WebhookNtfyTopic = "host-topic-a-1"
                },
                CancellationToken.None).ConfigureAwait(false);
            var effAfterUnversioned = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(effAfterUnversioned.Secrets.SmtpUsername, "Unversioned save at B must strip host-retired SMTP username from A.");
            Assert.IsNull(effAfterUnversioned.Secrets.SmtpPassword, "Unversioned save at B must strip host-retired SMTP password from A.");
            Assert.IsNull(effAfterUnversioned.Secrets.WebhookAuthToken, "Unversioned save at B must strip host-retired webhook token from A.");
            Assert.IsNull(effAfterUnversioned.Secrets.WebhookUserKey, "Unversioned save at B must strip host-retired webhook userKey from A.");
            Assert.IsNull(effAfterUnversioned.Secrets.WebhookNtfyTopic, "Unversioned save at B must strip host-retired webhook ntfyTopic from A.");

            // Part B: Host edits destination A -> B leaving v2 unchanged (so v2 is WITHHELD by tracker, NOT in configSecrets!).
            // Versioned save to B or C submitting withheld v2 MUST STILL be rejected!
            hostProv.Rotate(HostDict(
                "relay-d15-h2-b.example.test", 587, "host-user-a-2", "host-pass-a-2",
                urlB, "host-tok-a-2", "host-ukey-a-2", "host-topic-a-2"));

            var snapAfterHostWithhold = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var withheldReject = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snapAfterHostWithhold.Version,
                optionsB,
                SmtpPassword: "host-pass-a-2",
                WebhookUrl: urlC,
                WebhookAuthToken: "host-tok-a-2"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, withheldReject.Outcome);
            Assert.IsTrue(withheldReject.Problems!.ContainsKey("smtp.password"), "Host-withheld SMTP password bound to A must be rejected at B.");
            Assert.IsTrue(withheldReject.Problems.ContainsKey("webhook.authToken"), "Host-withheld webhook token bound to A must be rejected at C.");

            // Brand-new fresh credentials on B succeed cleanly!
            var freshSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snapAfterHostWithhold.Version,
                optionsB,
                SmtpUsername: "fresh-user-b",
                SmtpPassword: "fresh-pass-b",
                WebhookAuthToken: "fresh-tok-b"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, freshSave.Outcome, freshSave.Message);
        }

        // Part C: Cold-start mismatched explicit BoundEndpoint / BoundOrigin withheld by host provider is rejected on store save to B!
        {
            using var dir = TempStateDir.Create();
            var hostProv = new RotatingHostConfigurationProvider(HostDict(
                "relay-d15-h2-b.example.test", 587, "cold-bound-user-a", "cold-bound-pass-a",
                urlB, "cold-bound-tok-a", "cold-bound-ukey-a", "cold-bound-topic-a",
                smtpBound: "relay-d15-h2-a.example.test:587:StartTls",
                webhookBound: urlA));
            var root = new ConfigurationRoot([hostProv]);
            using var store = CreateStore(dir.Path, isDevelopment: false, configuration: root);

            var snap = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var coldReject = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snap.Version,
                optionsB,
                SmtpPassword: "cold-bound-pass-a",
                WebhookUrl: urlB,
                WebhookAuthToken: "cold-bound-tok-a"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, coldReject.Outcome);
            Assert.IsTrue(coldReject.Problems!.ContainsKey("smtp.password"));
            Assert.IsTrue(coldReject.Problems.ContainsKey("webhook.authToken"));
        }

        // Part D: Host tracker truncation (>1024 rotations at A) rejects evicted host credential #1 on destination-changing save A -> B.
        {
            using var dir = TempStateDir.Create();
            var hostProv = new RotatingHostConfigurationProvider(HostDict(
                "relay-d15-h2-a.example.test", 587, "trunc-user-a-1", "trunc-pass-a-1",
                urlA, "trunc-tok-a-1", "trunc-ukey-a-1", "trunc-topic-a-1"));
            var root = new ConfigurationRoot([hostProv]);
            using var store = CreateStore(dir.Path, isDevelopment: false, configuration: root);

            for (var i = 2; i <= HostConfigurationSnapshotTracker.MaxRetiredFieldBindings + 8; i++)
            {
                hostProv.Rotate(HostDict(
                    "relay-d15-h2-a.example.test", 587, $"trunc-user-a-{i}", $"trunc-pass-a-{i}",
                    urlA, $"trunc-tok-a-{i}", $"trunc-ukey-a-{i}", $"trunc-topic-a-{i}"));
            }

            var snap = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var truncReject = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snap.Version,
                optionsB,
                SmtpPassword: "trunc-pass-a-1",
                WebhookUrl: urlB,
                WebhookAuthToken: "trunc-tok-a-1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, truncReject.Outcome);
            Assert.IsTrue(truncReject.Problems!.ContainsKey("smtp.password"), "Truncated host tracker SMTP history must fail closed on destination change.");
            Assert.IsTrue(truncReject.Problems.ContainsKey("webhook.authToken"), "Truncated host tracker webhook history must fail closed on destination change.");
        }
    }

    [TestMethod]
    public async Task SplitOrOpaqueSmtpGuard_OverrideRemovalRevealingWaitingLeafAndTemporaryAbsenceSingleLeafRestore_StayWithheldAsync()
    {
        // Delta15 H3: Enforce HasPriorSplitOrOpaqueSmtp BEFORE allowing single-leaf release, and NEVER clear
        // HasPriorSplitOrOpaqueSmtp solely because an override was removed or SMTP fields temporarily disappeared
        // and reappeared in a single leaf provider.
        static Dictionary<string, string?> FullLeafDict(
            string host, int port, SmtpSecurityMode mode, string user, string pass, string? boundEndpoint = null)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = host,
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = mode.ToString(),
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = user,
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = pass
            };
            if (boundEndpoint is not null)
            {
                dict[ConfigurationOperatorNotificationConfigProvider.SmtpBoundEndpointConfigKey] = boundEndpoint;
            }
            return dict;
        }

        // Part A: Override removal revealing a waiting leaf credential at an intermediate tuple (different AND same tuple).
        {
            // baseLeaf holds waiting credentials for relay-wait-b:587:StartTls; overrideProv splits Host+Creds
            // (inheriting Port=587 and SecurityMode=StartTls from baseLeaf -> split SMTP -> HasPriorSplitOrOpaqueSmtp=true!).
            var baseLeaf = new RotatingHostConfigurationProvider(
                FullLeafDict("relay-wait-b.example.test", 587, SmtpSecurityMode.StartTls, "waiting-user-b", "waiting-pass-b"));
            var overrideProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-wait-a.example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "override-user-a",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "override-pass-a"
            });
            var root = new ConfigurationRoot([baseLeaf, overrideProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            var init = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-wait-a.example.test", init.Options.Smtp.Host);
            Assert.AreEqual("override-user-a", init.Secrets.SmtpUsername);
            Assert.AreEqual("override-pass-a", init.Secrets.SmtpPassword);

            // Remove overrideProv -> baseLeaf now wins ALL SMTP keys with waiting-user-b/waiting-pass-b at intermediate tuple!
            overrideProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var afterOverrideRemoval = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-wait-b.example.test", afterOverrideRemoval.Options.Smtp.Host);
            Assert.AreEqual(587, afterOverrideRemoval.Options.Smtp.Port);
            Assert.IsNull(afterOverrideRemoval.Secrets.SmtpUsername,
                "Removing an SMTP override after prior split state MUST NOT release waiting leaf username.");
            Assert.IsNull(afterOverrideRemoval.Secrets.SmtpPassword,
                "Removing an SMTP override after prior split state MUST NOT release waiting leaf password.");

            // Guard MUST NOT have been cleared: even a subsequent unbound edit in baseLeaf stays withheld!
            baseLeaf.Rotate(FullLeafDict("relay-wait-c.example.test", 2525, SmtpSecurityMode.StartTls, "unbound-user-c", "unbound-pass-c"));
            var afterUnboundEdit = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(afterUnboundEdit.Secrets.SmtpUsername,
                "HasPriorSplitOrOpaqueSmtp must stay armed until explicit same-provider Smtp:BoundEndpoint.");
            Assert.IsNull(afterUnboundEdit.Secrets.SmtpPassword);

            // Explicit same-provider Smtp:BoundEndpoint on the clean non-split leaf authorizes cleanly!
            baseLeaf.Rotate(FullLeafDict(
                "relay-wait-c.example.test", 2525, SmtpSecurityMode.StartTls, "bound-user-c", "bound-pass-c",
                boundEndpoint: "relay-wait-c.example.test:2525:StartTls"));
            var afterExplicitBind = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("bound-user-c", afterExplicitBind.Secrets.SmtpUsername);
            Assert.AreEqual("bound-pass-c", afterExplicitBind.Secrets.SmtpPassword);
        }

        // Part A2: Override removal at the SAME tuple revealing a different waiting leaf credential.
        {
            var baseLeaf = new RotatingHostConfigurationProvider(
                FullLeafDict("relay-same-tuple.example.test", 587, SmtpSecurityMode.StartTls, "waiting-same-user", "waiting-same-pass"));
            var overrideProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-same-tuple.example.test",
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "override-same-user",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "override-same-pass"
            });
            var root = new ConfigurationRoot([baseLeaf, overrideProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            Assert.AreEqual("override-same-pass", (await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false)).Secrets.SmtpPassword);

            overrideProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var afterRemove = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(afterRemove.Secrets.SmtpUsername, "Same-tuple override removal after split state must NOT release waiting leaf username.");
            Assert.IsNull(afterRemove.Secrets.SmtpPassword, "Same-tuple override removal after split state must NOT release waiting leaf password.");
        }

        // Part B: Temporary SMTP absence followed by single-leaf restoration after prior split/opaque state.
        {
            var hostProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-abs-split-a.example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = nameof(SmtpSecurityMode.StartTls),
                [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test"
            });
            var portProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "2525"
            });
            var credsProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "split-abs-user-a",
                [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "split-abs-pass-a"
            });
            var root = new ConfigurationRoot([hostProv, portProv, credsProv]);
            using var provider = new ConfigurationOperatorNotificationConfigProvider(root);

            var init = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("split-abs-pass-a", init.Secrets.SmtpPassword);

            // Temporary absence across ALL SMTP providers (Host, Port, SecurityMode, Credentials all disappear).
            hostProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            portProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            credsProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var duringAbsence = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(duringAbsence.Secrets.SmtpPassword);

            // Single-leaf restoration: hostProv alone restores Host + Port + SecurityMode + fresh creds (while portProv is still absent!).
            hostProv.Rotate(FullLeafDict(
                "relay-abs-split-b.example.test", 587, SmtpSecurityMode.StartTls, "restored-single-user", "restored-single-pass"));
            var afterSingleLeafRestore = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-abs-split-b.example.test", afterSingleLeafRestore.Options.Smtp.Host);
            Assert.AreEqual(587, afterSingleLeafRestore.Options.Smtp.Port);
            Assert.IsNull(afterSingleLeafRestore.Secrets.SmtpUsername,
                "Temporary absence followed by single-leaf restoration MUST preserve HasPriorSplitOrOpaqueSmtp and withhold username.");
            Assert.IsNull(afterSingleLeafRestore.Secrets.SmtpPassword,
                "Temporary absence followed by single-leaf restoration MUST preserve HasPriorSplitOrOpaqueSmtp and withhold password.");

            // Explicit same-provider Smtp:BoundEndpoint on the clean restored single leaf converges cleanly!
            hostProv.Rotate(FullLeafDict(
                "relay-abs-split-b.example.test", 587, SmtpSecurityMode.StartTls, "restored-single-user", "restored-single-pass",
                boundEndpoint: "relay-abs-split-b.example.test:587:StartTls"));
            var afterExplicitReauth = await provider.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("restored-single-user", afterExplicitReauth.Secrets.SmtpUsername);
            Assert.AreEqual("restored-single-pass", afterExplicitReauth.Secrets.SmtpPassword);
        }
    }

    [TestMethod]
    public async Task Delta13V2RewrittenEnvelope_WithStampedWebhookBindingAndFalseTruncationFlags_FailsClosedUntilV3ReauthAsync()
    {
        // Delta15 H4: A delta13-rewritten v2 envelope (`retiredCredentialHistoryVersion: 2`) that stamped
        // BoundWebhookDestination on an unrelated save AND wrote a short legacy history with explicit `false`
        // truncation flags MUST fail closed on v3 upgrade: withhold the v2 webhook credentials, never stamp them
        // into v3 on unrelated saves, force history truncation for both channels, and trust fresh v3 steady-state.
        static string SaltedHash(string s, string value)
            => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{s}:{value}")));

        const string urlB = "https://d15-h4.example.test/receiver-b";
        const string urlC = "https://d15-h4.example.test/receiver-c";
        const string originB = "HTTPS://D15-H4.EXAMPLE.TEST:443";
        const string destB = "HTTPS://D15-H4.EXAMPLE.TEST:443/receiver-b";
        const string endpointA = "RELAY-D15-H4-A.EXAMPLE.TEST:587:StartTls";
        const string salt = "aabbccddeeff00112233445566778899";

        var optionsB = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "relay-d15-h4-b.example.test",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions
            {
                Enabled = true,
                Format = WebhookPayloadFormat.Generic
            }
        };

        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        var protector = dpProvider.CreateProtector("CameraAgent", "OperatorNotifications", "v1");

        // Short 4-entry retired history (#2..#5; cred #1 was evicted before delta13 rewrote as v2 with false flags!).
        var shortSmtps = new List<object>();
        var shortWebhooks = new List<object>();
        for (var i = 2; i <= 5; i++)
        {
            shortSmtps.Add(new
            {
                endpoint = endpointA,
                usernameHash = SaltedHash(salt, $"d13-user-{i}"),
                passwordHash = SaltedHash(salt, $"d13-pass-{i}")
            });
            shortWebhooks.Add(new
            {
                origin = "HTTPS://D15-H4-A.EXAMPLE.TEST:443",
                authTokenHash = SaltedHash(salt, $"d13-tok-{i}")
            });
        }

        var delta13RewrittenV2Payload = new
        {
            webhookUrl = urlB,
            webhookAuthToken = "delta13-stamped-tok",
            webhookUserKey = "delta13-stamped-ukey",
            boundWebhookOrigin = originB,
            boundWebhookDestination = destB,
            retiredSalt = salt,
            retiredWebhooks = shortWebhooks,
            retiredSmtps = shortSmtps,
            anyWebhookHistoryTruncated = false,
            anySmtpHistoryTruncated = false,
            retiredCredentialHistoryVersion = 2
        };
        var envelope = new
        {
            schemaVersion = 1,
            revision = "delta13-v2-rewritten-rev",
            options = optionsB,
            protectedSecrets = protector.Protect(JsonSerializer.Serialize(delta13RewrittenV2Payload, WebJsonOptions))
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName),
            JsonSerializer.Serialize(envelope, WebJsonOptions),
            CancellationToken.None).ConfigureAwait(false);

        using (var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider))
        {
            // 1. Delta13 v2 BoundWebhookDestination MUST NOT be trusted -> webhook credentials withheld!
            var snap = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(snap.Secrets.HasWebhookUrl);
            Assert.IsFalse(snap.Secrets.HasWebhookAuthToken, "Delta13 v2 webhook token must be withheld until v3 re-authorization.");
            Assert.IsFalse(snap.Secrets.HasWebhookUserKey, "Delta13 v2 webhook userKey must be withheld until v3 re-authorization.");

            var eff = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(urlB, eff.Secrets.WebhookUrl);
            Assert.IsNull(eff.Secrets.WebhookAuthToken);
            Assert.IsNull(eff.Secrets.WebhookUserKey);

            // 2. Resubmitting the untrusted v2 webhook token MUST fail validation!
            var resubmitV2Reject = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snap.Version,
                optionsB,
                WebhookAuthToken: "delta13-stamped-tok"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, resubmitV2Reject.Outcome);
            Assert.IsTrue(resubmitV2Reject.Problems!.ContainsKey("webhook.authToken"));

            // 3. Destination-changing versioned save B -> C with evicted cred #1 MUST fail closed (v2 false flags overridden to truncated)!
            var destChangeReject = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snap.Version,
                new OperatorNotificationsOptions
                {
                    Enabled = true,
                    Smtp = new SmtpNotificationOptions
                    {
                        Enabled = true,
                        Host = "relay-d15-h4-c.example.test",
                        Port = 587,
                        SecurityMode = SmtpSecurityMode.StartTls,
                        FromAddress = "camera@example.test",
                        Recipients = ["ops@example.test"]
                    },
                    Webhook = optionsB.Webhook
                },
                SmtpPassword: "d13-pass-1",
                WebhookUrl: urlC,
                WebhookAuthToken: "d13-tok-1"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, destChangeReject.Outcome);
            Assert.IsTrue(destChangeReject.Problems!.ContainsKey("smtp.password"));
            Assert.IsTrue(destChangeReject.Problems.ContainsKey("webhook.authToken"));

            // 4. Unversioned save at B with evicted cred #1 MUST ALSO reject (forced truncation from v2 -> v3)!
            await ((IOperatorNotificationSecretStore)store).SaveAsync(
                new OperatorNotificationSecrets(
                    SmtpPassword: "d13-pass-1",
                    WebhookUrl: urlB,
                    WebhookAuthToken: "d13-tok-1"),
                CancellationToken.None).ConfigureAwait(false);
            var effAfterUnversioned = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(effAfterUnversioned.Secrets.SmtpPassword);
            Assert.IsNull(effAfterUnversioned.Secrets.WebhookAuthToken);

            // 5. Unrelated settings save MUST NOT stamp the untrusted v2 webhook credentials into v3!
            var snapBeforeUnrelated = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var unrelatedOptions = new OperatorNotificationsOptions
            {
                Enabled = optionsB.Enabled,
                EvaluationIntervalSeconds = 45,
                Smtp = optionsB.Smtp,
                Webhook = optionsB.Webhook
            };
            var unrelatedSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                snapBeforeUnrelated.Version,
                unrelatedOptions), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, unrelatedSave.Outcome, unrelatedSave.Message);
            Assert.IsFalse(unrelatedSave.Snapshot!.Secrets.HasWebhookAuthToken);
            Assert.IsFalse(unrelatedSave.Snapshot.Secrets.HasWebhookUserKey);
        }

        // Verify disk was upgraded to v3 with forced truncation preserved and v2 creds dropped!
        var diskBytes = await File.ReadAllBytesAsync(Path.Combine(dir.Path, OperatorNotificationStore.ConfigFileName), CancellationToken.None).ConfigureAwait(false);
        using (var envelopeDoc = JsonDocument.Parse(diskBytes))
        {
            var innerJson = protector.Unprotect(envelopeDoc.RootElement.GetProperty("protectedSecrets").GetString()!);
            using var innerDoc = JsonDocument.Parse(innerJson);
            Assert.AreEqual(3, innerDoc.RootElement.GetProperty("retiredCredentialHistoryVersion").GetInt32());
            Assert.IsTrue(innerDoc.RootElement.GetProperty("anyWebhookHistoryTruncated").GetBoolean());
            Assert.IsTrue(innerDoc.RootElement.GetProperty("anySmtpHistoryTruncated").GetBoolean());
        }

        // 6. Fresh operator re-authorization on same destination B persists trusted v3 binding across reload!
        using (var reloaded = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider))
        {
            var snapReloaded = await reloaded.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(snapReloaded.Secrets.HasWebhookAuthToken);

            var freshV3 = await reloaded.SaveAsync(new OperatorNotificationsUpdateRequest(
                snapReloaded.Version,
                snapReloaded.Options,
                WebhookAuthToken: "fresh-v3-tok-b"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, freshV3.Outcome, freshV3.Message);
            Assert.IsTrue(freshV3.Snapshot!.Secrets.HasWebhookAuthToken);
        }

        using (var steadyV3 = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider))
        {
            var effV3 = await steadyV3.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("fresh-v3-tok-b", effV3.Secrets.WebhookAuthToken, "Fresh v3 binding must be trusted across restarts.");
        }
    }

    [TestMethod]
    public async Task LiveHostTracker_ClearedWebhookUrlAndCanaries_NeverRetainedInPlaintextMemoryStructuresAsync()
    {
        // Delta15 M1: Live tracker MUST store ONLY fixed-size 32-byte-salted HMAC-SHA256 destination & credential
        // fingerprints in retired, truncated, bound, and empty-guard state — ZERO plaintext canonical webhook URLs
        // (including host, path, and query canaries) or plaintext secrets may remain in tracker memory after clearing!
        const string hostCanary = "m1-mem-canary-host.example.test";
        const string pathCanary = "m1-secret-path-canary";
        const string queryCanary = "m1_query_canary=secret98765";
        const string secretTok = "m1-mem-secret-tok-1";
        const string secretUkey = "m1-mem-secret-ukey-1";
        const string secretTopic = "m1-mem-secret-topic-1";
        var canaryUrl = $"https://{hostCanary}/{pathCanary}/hook?{queryCanary}";

        var hostProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = canaryUrl,
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = secretTok,
            [ConfigurationOperatorNotificationConfigProvider.WebhookUserKeyConfigKey] = secretUkey,
            [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = secretTopic
        });
        var root = new ConfigurationRoot([hostProv]);
        using var tracker = new HostConfigurationSnapshotTracker(root);

        Assert.AreEqual(secretTok, tracker.Current.Secrets.WebhookAuthToken);

        // Rotate once at the same URL (retiring secretTok into _retired) and then clear host configuration completely!
        hostProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = canaryUrl,
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "m1-mem-secret-tok-2",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUserKeyConfigKey] = "m1-mem-secret-ukey-2",
            [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = "m1-mem-secret-topic-2"
        });
        hostProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        Assert.IsNull(tracker.Current.Secrets.WebhookUrl);
        Assert.IsNull(tracker.Current.Secrets.WebhookAuthToken);

        // Walk ALL reachable fields inside HostConfigurationSnapshotTracker (excluding the external IConfiguration reference)
        // and verify ZERO plaintext URLs, path/query canaries, or credential strings remain anywhere in tracker memory!
        var reachableStrings = CollectReachableStringsExcludingConfiguration(tracker);
        foreach (var value in reachableStrings)
        {
            Assert.DoesNotContain(hostCanary, value, StringComparison.OrdinalIgnoreCase, "Cleared webhook host canary leaked in tracker memory.");
            Assert.DoesNotContain(pathCanary, value, StringComparison.Ordinal, "Cleared webhook path canary leaked in tracker memory.");
            Assert.DoesNotContain(queryCanary, value, StringComparison.Ordinal, "Cleared webhook query canary leaked in tracker memory.");
            Assert.DoesNotContain(secretTok, value, StringComparison.Ordinal, "Cleared webhook authToken leaked in tracker memory.");
            Assert.DoesNotContain(secretUkey, value, StringComparison.Ordinal, "Cleared webhook userKey leaked in tracker memory.");
            Assert.DoesNotContain(secretTopic, value, StringComparison.Ordinal, "Cleared webhook ntfyTopic leaked in tracker memory.");
            Assert.DoesNotContain("m1-mem-secret-tok-2", value, StringComparison.Ordinal, "Cleared rotated webhook authToken leaked in tracker memory.");
        }

        // Verify salted HMAC fingerprints STILL actively reject retired credentials at a different destination!
        Assert.IsTrue(tracker.IsCredentialRetiredOrBoundForOtherDestination(
            HostCredentialField.WebhookAuthToken, secretTok, "https://other-m1.example.test/hook"));
        Assert.IsTrue(tracker.IsCredentialRetiredOrBoundForOtherDestination(
            HostCredentialField.WebhookAuthToken, "m1-mem-secret-tok-2", "https://other-m1.example.test/hook"));
        Assert.IsFalse(tracker.IsCredentialRetiredOrBoundForOtherDestination(
            HostCredentialField.WebhookAuthToken, secretTok, canaryUrl));

        await Task.CompletedTask.ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OptionsOnlySave_FollowedByMigratingSameAuthorizedHostCredential_SucceedsAtSameDestinationAndRejectsAtDifferentDestinationAsync()
    {
        // Delta15 M2: After an options-only save (no existing protected bindings), migrating the currently authorized
        // non-withheld host webhook token/userKey/topic or SMTP username/password into protected storage at the EXACT
        // SAME canonical destination MUST succeed with fresh v3 bindings, while submitting that same host credential
        // to a DIFFERENT destination (or submitting a withheld host credential at the same destination) MUST fail!
        const string hostWebhookUrl = "https://d15-m2-host.example.test/alerts/prod?channel=ops";
        const string otherWebhookUrl = "https://d15-m2-host.example.test/alerts/staging?channel=ops";

        var hostProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-d15-m2.example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = nameof(SmtpSecurityMode.StartTls),
            [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
            [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-m2-user",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-m2-pass",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Format"] = nameof(WebhookPayloadFormat.Pushover),
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = hostWebhookUrl,
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "host-m2-tok",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUserKeyConfigKey] = "host-m2-ukey",
            [ConfigurationOperatorNotificationConfigProvider.WebhookNtfyTopicConfigKey] = "host-m2-topic"
        });
        var root = new ConfigurationRoot([hostProv]);

        using var dir = TempStateDir.Create();
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir.Path, "dp-keys")));
        using (var store = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider, configuration: root))
        {
            // Step 1: Options-only save (persists Valid store with empty ProtectedSecrets / default ProtectedBindings).
            var s0 = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var optionsOnly = new OperatorNotificationsOptions
            {
                Enabled = true,
                EvaluationIntervalSeconds = 45,
                Smtp = s0.Options.Smtp,
                Webhook = s0.Options.Webhook
            };
            var savedOptionsOnly = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                s0.Version,
                optionsOnly), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, savedOptionsOnly.Outcome, savedOptionsOnly.Message);
            Assert.IsTrue(savedOptionsOnly.Snapshot!.HasSavedOverrides);
            Assert.AreEqual(NotificationSecretSource.Configuration, savedOptionsOnly.Snapshot.Secrets.SmtpPasswordSource);
            Assert.AreEqual(NotificationSecretSource.Configuration, savedOptionsOnly.Snapshot.Secrets.WebhookAuthTokenSource);

            // Step 2: Submitting the same host credentials to a DIFFERENT SMTP port or DIFFERENT webhook path MUST REJECT!
            var diffDestOptions = new OperatorNotificationsOptions
            {
                Enabled = true,
                EvaluationIntervalSeconds = 45,
                Smtp = new SmtpNotificationOptions
                {
                    Enabled = true,
                    Host = "relay-d15-m2.example.test",
                    Port = 2525,
                    SecurityMode = SmtpSecurityMode.StartTls,
                    FromAddress = "camera@example.test",
                    Recipients = ["ops@example.test"]
                },
                Webhook = s0.Options.Webhook
            };
            var diffDestReject = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                savedOptionsOnly.Snapshot.Version,
                diffDestOptions,
                SmtpUsername: "host-m2-user",
                SmtpPassword: "host-m2-pass",
                WebhookUrl: otherWebhookUrl,
                WebhookAuthToken: "host-m2-tok",
                WebhookUserKey: "host-m2-ukey",
                WebhookNtfyTopic: "host-m2-topic"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, diffDestReject.Outcome);
            Assert.IsTrue(diffDestReject.Problems!.ContainsKey("smtp.username"));
            Assert.IsTrue(diffDestReject.Problems.ContainsKey("smtp.password"));
            Assert.IsTrue(diffDestReject.Problems.ContainsKey("webhook.authToken"));
            Assert.IsTrue(diffDestReject.Problems.ContainsKey("webhook.userKey"));
            Assert.IsTrue(diffDestReject.Problems.ContainsKey("webhook.ntfyTopic"));

            // Step 3: Migrating the exact same currently authorized host credentials into protected storage at the
            //         EXACT SAME canonical SMTP endpoint and webhook URL MUST SUCCEED!
            var migrateSameDest = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                savedOptionsOnly.Snapshot.Version,
                optionsOnly,
                SmtpUsername: "host-m2-user",
                SmtpPassword: "host-m2-pass",
                WebhookUrl: hostWebhookUrl,
                WebhookAuthToken: "host-m2-tok",
                WebhookUserKey: "host-m2-ukey",
                WebhookNtfyTopic: "host-m2-topic"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, migrateSameDest.Outcome, migrateSameDest.Message);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, migrateSameDest.Snapshot!.Secrets.SmtpUsernameSource);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, migrateSameDest.Snapshot.Secrets.SmtpPasswordSource);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, migrateSameDest.Snapshot.Secrets.WebhookUrlSource);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, migrateSameDest.Snapshot.Secrets.WebhookAuthTokenSource);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, migrateSameDest.Snapshot.Secrets.WebhookUserKeySource);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, migrateSameDest.Snapshot.Secrets.WebhookNtfyTopicSource);
        }

        // Reload with EMPTY host config: migrated credentials persist cleanly in protected storage with v3 bindings!
        using (var reloadedNoHost = CreateStore(dir.Path, isDevelopment: false, dpProvider: dpProvider))
        {
            var eff = await reloadedNoHost.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("host-m2-user", eff.Secrets.SmtpUsername);
            Assert.AreEqual("host-m2-pass", eff.Secrets.SmtpPassword);
            Assert.AreEqual(hostWebhookUrl, eff.Secrets.WebhookUrl);
            Assert.AreEqual("host-m2-tok", eff.Secrets.WebhookAuthToken);
            Assert.AreEqual("host-m2-ukey", eff.Secrets.WebhookUserKey);
            Assert.AreEqual("host-m2-topic", eff.Secrets.WebhookNtfyTopic);
        }

        // Step 4: If a host credential is WITHHELD (not currently authorized at the destination), an options-only save
        //         followed by submitting that withheld value at the same destination MUST still be REJECTED fail-closed!
        using var dirWithheld = TempStateDir.Create();
        var withheldHostProv = new RotatingHostConfigurationProvider(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://d15-m2-old.example.test/hook",
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "withheld-host-tok"
        });
        var withheldRoot = new ConfigurationRoot([withheldHostProv]);
        using var withheldStore = CreateStore(dirWithheld.Path, isDevelopment: false, configuration: withheldRoot);

        // Move host URL old -> hostWebhookUrl leaving token unchanged -> tracker withholds "withheld-host-tok"!
        withheldHostProv.Rotate(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
            [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
            [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = hostWebhookUrl,
            [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "withheld-host-tok"
        });
        var snapW0 = await withheldStore.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var optW = await withheldStore.SaveAsync(new OperatorNotificationsUpdateRequest(
            snapW0.Version,
            new OperatorNotificationsOptions
            {
                Enabled = true,
                EvaluationIntervalSeconds = 45,
                Webhook = new WebhookNotificationOptions { Enabled = true }
            }), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, optW.Outcome, optW.Message);

        var rejectWithheld = await withheldStore.SaveAsync(new OperatorNotificationsUpdateRequest(
            optW.Snapshot!.Version,
            optW.Snapshot.Options,
            WebhookAuthToken: "withheld-host-tok"), "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorNotificationsSaveOutcome.Invalid, rejectWithheld.Outcome);
        Assert.IsTrue(rejectWithheld.Problems!.ContainsKey("webhook.authToken"),
            "Withheld host credential bound to a prior destination MUST be rejected even after an options-only save.");
    }

    private static List<string> CollectReachableStringsExcludingConfiguration(object root)
    {
        var found = new List<string>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object?>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is null or IConfiguration or IConfigurationProvider or IChangeToken)
            {
                continue;
            }

            if (current is string s)
            {
                found.Add(s);
                continue;
            }

            var type = current.GetType();
            if (type.IsPrimitive || type.IsEnum)
            {
                continue;
            }

            if (!type.IsValueType && !visited.Add(current))
            {
                continue;
            }

            if (current is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    stack.Push(item);
                }
            }

            for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            {
                var fields = t.GetFields(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.DeclaredOnly);
                foreach (var field in fields)
                {
                    if (typeof(IConfiguration).IsAssignableFrom(field.FieldType) ||
                        typeof(IConfigurationProvider).IsAssignableFrom(field.FieldType) ||
                        typeof(IChangeToken).IsAssignableFrom(field.FieldType) ||
                        typeof(Delegate).IsAssignableFrom(field.FieldType))
                    {
                        continue;
                    }

                    stack.Push(field.GetValue(current));
                }
            }
        }

        return found;
    }

    private static void TryUnblockFifoReader(string fifoPath)
    {
        try
        {
            using var _ = File.Open(fifoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FreshCancellationTokenConfigurationProvider : IConfigurationProvider, IDisposable
    {
        private readonly object _gate = new();
        private Dictionary<string, string?> _data;
        private CancellationTokenSource _cts = new();

        public FreshCancellationTokenConfigurationProvider(IDictionary<string, string?> initial)
        {
            _data = new Dictionary<string, string?>(initial, StringComparer.OrdinalIgnoreCase);
        }

        public bool TryGet(string key, out string? value)
        {
            lock (_gate)
            {
                return _data.TryGetValue(key, out value);
            }
        }

        public void Set(string key, string? value)
        {
            lock (_gate)
            {
                _data[key] = value;
            }
        }

        public IChangeToken GetReloadToken()
        {
            lock (_gate)
            {
                return new CancellationChangeToken(_cts.Token);
            }
        }

        public void Load()
        {
        }

        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
        {
            var results = new List<string>();
            var prefix = parentPath is null ? string.Empty : parentPath + ConfigurationPath.KeyDelimiter;
            lock (_gate)
            {
                foreach (var key in _data.Keys)
                {
                    if (prefix.Length == 0 || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var remainder = key[prefix.Length..];
                        var delimiterIndex = remainder.IndexOf(ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
                        results.Add(delimiterIndex < 0 ? remainder : remainder[..delimiterIndex]);
                    }
                }
            }
            results.AddRange(earlierKeys);
            results.Sort(ConfigurationKeyComparer.Instance);
            return results;
        }

        public void Rotate(IDictionary<string, string?> next)
        {
            CancellationTokenSource previous;
            lock (_gate)
            {
                _data = new Dictionary<string, string?>(next, StringComparer.OrdinalIgnoreCase);
                previous = _cts;
                _cts = new CancellationTokenSource();
            }
            previous.Cancel();
            previous.Dispose();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _cts.Dispose();
            }
        }
    }

    private sealed class FreshTokenConfigurationWrapper(IConfiguration inner) : IConfiguration, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public string? this[string key]
        {
            get => inner[key];
            set => inner[key] = value;
        }

        public IEnumerable<IConfigurationSection> GetChildren() => inner.GetChildren();
        public IChangeToken GetReloadToken() => new CancellationChangeToken(_cts.Token);
        public IConfigurationSection GetSection(string key) => inner.GetSection(key);
        public void Dispose() => _cts.Dispose();
    }

    private sealed class ExistingProviderConfigurationSource(IConfigurationProvider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }

    private sealed class OpaqueConfigurationWrapper(IConfiguration inner) : IConfiguration
    {
        public string? this[string key]
        {
            get => inner[key];
            set => inner[key] = value;
        }

        public IEnumerable<IConfigurationSection> GetChildren() => inner.GetChildren();
        public IChangeToken GetReloadToken() => inner.GetReloadToken();
        public IConfigurationSection GetSection(string key) => inner.GetSection(key);
    }

    private sealed class CapturingSmtpTransport : ISmtpNotificationTransport
    {
        public List<SmtpDeliveryRequest> Captured { get; } = [];

        public Task<OperatorNotificationDeliveryResult> SendAsync(SmtpDeliveryRequest request, CancellationToken cancellationToken)
        {
            Captured.Add(request);
            return Task.FromResult(OperatorNotificationDeliveryResult.Success(SmtpNotificationTransport.ChannelName, TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class SynchronouslySignalingConfiguration : IConfiguration
    {
        private readonly object _gate = new();
        private Dictionary<string, string?> _values;
        private readonly Dictionary<string, string?> _reloadedValues;
        private int _signalOnFirstRegister = 1;
        private ConfigurationReloadToken _currentToken = new();

        public bool SynchronousCallbackFired { get; private set; }

        public SynchronouslySignalingConfiguration(
            IDictionary<string, string?> initialValues,
            IDictionary<string, string?> reloadedValues)
        {
            _values = new Dictionary<string, string?>(initialValues, StringComparer.OrdinalIgnoreCase);
            _reloadedValues = new Dictionary<string, string?>(reloadedValues, StringComparer.OrdinalIgnoreCase);
        }

        public string? this[string key]
        {
            get
            {
                lock (_gate)
                {
                    return _values.TryGetValue(key, out var value) ? value : null;
                }
            }
            set
            {
                lock (_gate)
                {
                    _values[key] = value;
                }
            }
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IConfigurationSection GetSection(string key) => new ConfigurationSectionProxy(this, key);

        public IChangeToken GetReloadToken()
        {
            if (Volatile.Read(ref _signalOnFirstRegister) != 0)
            {
                return new ImmediateFirstCallbackToken(this);
            }
            return Volatile.Read(ref _currentToken);
        }

        public void TriggerReload(IDictionary<string, string?> nextValues)
        {
            ConfigurationReloadToken previous;
            lock (_gate)
            {
                _values = new Dictionary<string, string?>(nextValues, StringComparer.OrdinalIgnoreCase);
                previous = Interlocked.Exchange(ref _currentToken, new ConfigurationReloadToken());
            }
            previous.OnReload();
        }

        private IDisposable RegisterFirstToken(Action<object?> callback, object? state)
        {
            if (Interlocked.Exchange(ref _signalOnFirstRegister, 0) == 1)
            {
                lock (_gate)
                {
                    _values = new Dictionary<string, string?>(_reloadedValues, StringComparer.OrdinalIgnoreCase);
                }
                SynchronousCallbackFired = true;
                callback(state);
                return NoOpDisposable.Instance;
            }
            return Volatile.Read(ref _currentToken).RegisterChangeCallback(callback, state);
        }

        private sealed class ImmediateFirstCallbackToken(SynchronouslySignalingConfiguration owner) : IChangeToken
        {
            public bool HasChanged => false;
            public bool ActiveChangeCallbacks => true;
            public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
                => owner.RegisterFirstToken(callback, state);
        }

        private sealed class NoOpDisposable : IDisposable
        {
            public static NoOpDisposable Instance { get; } = new();
            public void Dispose() { }
        }

        private sealed class ConfigurationSectionProxy(SynchronouslySignalingConfiguration root, string path) : IConfigurationSection
        {
            public string Key => ConfigurationPath.GetSectionKey(path);
            public string Path => path;
            public string? Value
            {
                get => root[path];
                set => root[path] = value;
            }
            public string? this[string key]
            {
                get => root[ConfigurationPath.Combine(path, key)];
                set => root[ConfigurationPath.Combine(path, key)] = value;
            }
            public IConfigurationSection GetSection(string key) => new ConfigurationSectionProxy(root, ConfigurationPath.Combine(path, key));
            public IChangeToken GetReloadToken() => root.GetReloadToken();
            public IEnumerable<IConfigurationSection> GetChildren()
            {
                var prefix = path + ConfigurationPath.KeyDelimiter;
                var childKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                lock (root._gate)
                {
                    foreach (var fullKey in root._values.Keys)
                    {
                        if (!fullKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        var remainder = fullKey[prefix.Length..];
                        var sepIndex = remainder.IndexOf(ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
                        var segment = sepIndex >= 0 ? remainder[..sepIndex] : remainder;
                        childKeys.Add(segment);
                    }
                }
                return childKeys.Select(segment => new ConfigurationSectionProxy(root, ConfigurationPath.Combine(path, segment)));
            }
        }
    }

    private sealed class RotatingHostConfigurationProvider : ConfigurationProvider
    {
        public RotatingHostConfigurationProvider(IDictionary<string, string?> initial)
        {
            Data = new Dictionary<string, string?>(initial, StringComparer.OrdinalIgnoreCase);
        }

        public void Rotate(IDictionary<string, string?> next)
        {
            Data = new Dictionary<string, string?>(next, StringComparer.OrdinalIgnoreCase);
            OnReload();
        }
    }

    private sealed class BarrierReloadConfigurationProvider : ConfigurationProvider
    {
        private readonly ManualResetEventSlim? _onLoadStarted;
        private readonly ManualResetEventSlim? _waitBeforeApplyingLoad;
        private readonly bool _fireOnReloadInsideLoad;
        private Dictionary<string, string?> _staged;
        private bool _armBarrier;

        public BarrierReloadConfigurationProvider(
            IDictionary<string, string?> initial,
            ManualResetEventSlim? onLoadStarted = null,
            ManualResetEventSlim? waitBeforeApplyingLoad = null,
            bool fireOnReloadInsideLoad = false)
        {
            _onLoadStarted = onLoadStarted;
            _waitBeforeApplyingLoad = waitBeforeApplyingLoad;
            _fireOnReloadInsideLoad = fireOnReloadInsideLoad;
            _staged = new Dictionary<string, string?>(initial, StringComparer.OrdinalIgnoreCase);
            Data = new Dictionary<string, string?>(initial, StringComparer.OrdinalIgnoreCase);
        }

        public void StageNext(IDictionary<string, string?> next, bool armBarrier = false)
        {
            _staged = new Dictionary<string, string?>(next, StringComparer.OrdinalIgnoreCase);
            _armBarrier = armBarrier;
        }

        public override void Load()
        {
            if (_armBarrier)
            {
                _armBarrier = false;
                _onLoadStarted?.Set();
                _waitBeforeApplyingLoad?.Wait(TimeSpan.FromSeconds(10));
            }
            Data = new Dictionary<string, string?>(_staged, StringComparer.OrdinalIgnoreCase);
            if (_fireOnReloadInsideLoad)
            {
                OnReload();
            }
        }
    }

    private sealed class MidReadRotatingConfigurationProvider : ConfigurationProvider
    {
        private readonly Dictionary<string, string?> _next;
        private readonly string _triggerKey;
        private readonly bool _fireReloadToken;
        private int _rotated;

        public bool HasRotated => Volatile.Read(ref _rotated) != 0;

        public MidReadRotatingConfigurationProvider(
            IDictionary<string, string?> initial,
            IDictionary<string, string?> next,
            string triggerKey,
            bool fireReloadToken)
        {
            Data = new Dictionary<string, string?>(initial, StringComparer.OrdinalIgnoreCase);
            _next = new Dictionary<string, string?>(next, StringComparer.OrdinalIgnoreCase);
            _triggerKey = triggerKey;
            _fireReloadToken = fireReloadToken;
        }

        public override bool TryGet(string key, out string? value)
        {
            if (string.Equals(key, _triggerKey, StringComparison.OrdinalIgnoreCase) &&
                Interlocked.CompareExchange(ref _rotated, 1, 0) == 0)
            {
                Data = new Dictionary<string, string?>(_next, StringComparer.OrdinalIgnoreCase);
                if (_fireReloadToken)
                {
                    OnReload();
                }
            }
            return base.TryGet(key, out value);
        }
    }

    private static OperatorNotificationsOptions CreateSmtpOptions(string host, int port, SmtpSecurityMode securityMode) => new()
    {
        Enabled = true,
        Smtp = new SmtpNotificationOptions
        {
            Enabled = true,
            Host = host,
            Port = port,
            SecurityMode = securityMode,
            FromAddress = "camera@example.test",
            Recipients = ["ops@example.test"]
        }
    };

    private sealed class NonSeekableCountingStream(long totalLength) : Stream
    {
        private long _remaining = totalLength;
        public long TotalBytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0 || count <= 0)
            {
                return 0;
            }
            var toCopy = (int)Math.Min(count, _remaining);
            Array.Fill(buffer, (byte)'x', offset, toCopy);
            _remaining -= toCopy;
            TotalBytesRead += toCopy;
            return toCopy;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void RequireModesEnforced()
    {
        if (Environment.UserName == "root")
        {
            Assert.Inconclusive("Needs a non-root user, for whom modes 0000 and 0200 are enforced.");
        }
    }

    private static OperatorNotificationStore CreateStore(
        string stateDirectory,
        bool isDevelopment,
        IDataProtectionProvider? dpProvider = null,
        IConfiguration? configuration = null)
    {
        dpProvider ??= DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(stateDirectory, ".dp-keys")));
        configuration ??= new ConfigurationBuilder().Build();
        return new OperatorNotificationStore(
            dpProvider,
            Options.Create(new DeviceProvisioningOptions { StateDirectory = stateDirectory }),
            configuration,
            new OperatorNotificationNetworkPolicy(isDevelopment),
            NullLogger<OperatorNotificationStore>.Instance);
    }

    private sealed class FaultInjectingDataProtectionProvider(IDataProtectionProvider inner) : IDataProtectionProvider
    {
        public bool FailProtect { get; set; }

        public IDataProtector CreateProtector(string purpose) => new FaultInjectingProtector(inner.CreateProtector(purpose), this);

        private sealed class FaultInjectingProtector(IDataProtector inner, FaultInjectingDataProtectionProvider owner) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => new FaultInjectingProtector(inner.CreateProtector(purpose), owner);

            public byte[] Protect(byte[] plaintext)
            {
                if (owner.FailProtect)
                {
                    throw new CryptographicException("Simulated DataProtection failure during key rotation.");
                }
                return inner.Protect(plaintext);
            }

            public byte[] Unprotect(byte[] protectedData) => inner.Unprotect(protectedData);
        }
    }

    private sealed class TempStateDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hvo-notif-store-{Guid.NewGuid():N}");
        private TempStateDir() => Directory.CreateDirectory(Path);
        public static TempStateDir Create() => new();
        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
