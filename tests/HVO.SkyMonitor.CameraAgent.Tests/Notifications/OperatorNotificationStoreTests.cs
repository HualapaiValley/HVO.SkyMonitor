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
        // 1. Direct origin-binding unit checks across scheme, host, and effective port variations.
        var hostSecrets = new OperatorNotificationSecrets(
            WebhookUrl: "https://hooks.example.test/host-path",
            WebhookAuthToken: "host-token-alpha",
            WebhookUserKey: "host-user-alpha",
            WebhookNtfyTopic: "host-topic-alpha");

        // Same origin (case-insensitive host, explicit default port 443, different path) -> inherits host secrets.
        var sameOriginProtected = new OperatorNotificationSecrets(WebhookUrl: "https://HOOKS.EXAMPLE.TEST:443/other-path");
        var sameOriginMerged = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(sameOriginProtected, hostSecrets);
        var sameOriginPresence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(sameOriginProtected, hostSecrets);
        Assert.AreEqual("host-token-alpha", sameOriginMerged.WebhookAuthToken);
        Assert.AreEqual("host-user-alpha", sameOriginMerged.WebhookUserKey);
        Assert.AreEqual("host-topic-alpha", sameOriginMerged.WebhookNtfyTopic);
        Assert.IsTrue(sameOriginPresence.HasWebhookAuthToken);
        Assert.AreEqual(NotificationSecretSource.Configuration, sameOriginPresence.WebhookAuthTokenSource);

        // Different host, different port, or different scheme -> NEVER inherits host webhook secrets.
        foreach (var crossOriginUrl in new[]
        {
            "https://other.example.test/host-path",
            "https://hooks.example.test:8443/host-path",
            "http://hooks.example.test/host-path"
        })
        {
            var crossProtected = new OperatorNotificationSecrets(WebhookUrl: crossOriginUrl);
            var crossMerged = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(crossProtected, hostSecrets);
            var crossPresence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(crossProtected, hostSecrets);
            Assert.IsNull(crossMerged.WebhookAuthToken, $"Cross-origin URL '{crossOriginUrl}' must NOT inherit host WebhookAuthToken.");
            Assert.IsNull(crossMerged.WebhookUserKey, $"Cross-origin URL '{crossOriginUrl}' must NOT inherit host WebhookUserKey.");
            Assert.IsNull(crossMerged.WebhookNtfyTopic, $"Cross-origin URL '{crossOriginUrl}' must NOT inherit host WebhookNtfyTopic.");
            Assert.IsFalse(crossPresence.HasWebhookAuthToken, $"Cross-origin URL '{crossOriginUrl}' must report HasWebhookAuthToken=false.");
            Assert.AreEqual(NotificationSecretSource.None, crossPresence.WebhookAuthTokenSource);
            Assert.IsFalse(crossPresence.HasWebhookUserKey);
            Assert.IsFalse(crossPresence.HasWebhookNtfyTopic);
        }

        // 2. End-to-end store + real WebhookNotificationTransport delivery across two loopback HTTPS origins (distinct ports).
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
                "Verifying origin-bound webhook token inheritance.",
                new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero),
                "TestCam");

            // Step A: Replacing path on SAME origin (serverA) without entering token inherits host token and sends Authorization header.
            var initial = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var sameOriginSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                initial.Version,
                webhookOptions,
                WebhookUrl: $"https://127.0.0.1:{serverA.Port}/same-origin-new-path"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, sameOriginSave.Outcome, sameOriginSave.Message);
            Assert.IsTrue(sameOriginSave.Snapshot!.Secrets.HasWebhookAuthToken);
            Assert.AreEqual(NotificationSecretSource.Configuration, sameOriginSave.Snapshot.Secrets.WebhookAuthTokenSource);

            var sameOriginEffective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("host-token-server-a", sameOriginEffective.Secrets.WebhookAuthToken);
            var sendA = await transport.SendAsync(new WebhookDeliveryRequest(
                sameOriginEffective.Secrets.WebhookUrl!,
                sameOriginEffective.Options.Webhook.Format,
                sameOriginEffective.Options.Webhook.AuthHeaderName,
                sameOriginEffective.Secrets.WebhookNtfyTopic,
                sameOriginEffective.Options.Webhook.AllowPrivateNetwork,
                sameOriginEffective.Secrets.WebhookAuthToken,
                sameOriginEffective.Secrets.WebhookUserKey,
                sampleEvent,
                1, 0, true, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(sendA.Succeeded, sendA.ReasonCode);
            Assert.HasCount(1, serverA.Captured);
            Assert.AreEqual("Bearer host-token-server-a", serverA.Captured[0].Header("Authorization"));

            // Step B: Replacing URL with DIFFERENT origin (serverB, different port) and omitting/clearing token MUST NOT inherit
            // host token and MUST send NO Authorization header on delivery!
            var crossOriginSave = await store.SaveAsync(new OperatorNotificationsUpdateRequest(
                sameOriginSave.Snapshot.Version,
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

        // Legacy envelope handling (lacking explicit BoundWebhookOrigin / BoundSmtpEndpoint fields):
        // (A) Legacy envelope that explicitly supplies encrypted WebhookUrl + WebhookAuthToken continues to work for webhook,
        //     but legacy SMTP credentials without encrypted BoundSmtpEndpoint MUST fail closed (requiring operator re-entry)
        //     so plaintext SMTP option edits can never redirect legacy SMTP credentials to another relay.
        // (B) Legacy envelope with WebhookUrl=null + WebhookAuthToken fails safe and does NOT apply to host origin B.
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
            Assert.IsTrue(snap.Secrets.HasWebhookAuthToken);

            var eff = await legacyExplicitStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(eff.StoreCorrupt);
            Assert.IsNull(eff.Secrets.SmtpUsername,
                "Legacy SMTP credentials without encrypted BoundSmtpEndpoint must NOT be released.");
            Assert.IsNull(eff.Secrets.SmtpPassword,
                "Legacy SMTP credentials without encrypted BoundSmtpEndpoint must NOT be released.");
            Assert.AreEqual("https://legacy-origin.example.test/alerts", eff.Secrets.WebhookUrl);
            Assert.AreEqual("legacy-explicit-token", eff.Secrets.WebhookAuthToken);

            // Re-entering SMTP credentials explicitly writes an encrypted BoundSmtpEndpoint and restores ProtectedStore delivery.
            var rebound = await legacyExplicitStore.SaveAsync(new OperatorNotificationsUpdateRequest(
                snap.Version,
                snap.Options,
                SmtpUsername: "reentered-smtp-user",
                SmtpPassword: "reentered-smtp-pass"), "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OperatorNotificationsSaveOutcome.Applied, rebound.Outcome, rebound.Message);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, rebound.Snapshot!.Secrets.SmtpUsernameSource);
            Assert.AreEqual(NotificationSecretSource.ProtectedStore, rebound.Snapshot.Secrets.SmtpPasswordSource);
            var afterReentry = await legacyExplicitStore.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("reentered-smtp-user", afterReentry.Secrets.SmtpUsername);
            Assert.AreEqual("reentered-smtp-pass", afterReentry.Secrets.SmtpPassword);
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
