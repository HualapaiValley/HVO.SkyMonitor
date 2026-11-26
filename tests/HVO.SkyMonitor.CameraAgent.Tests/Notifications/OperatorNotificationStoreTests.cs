using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Notifications;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", CameraAgentTestAreas.Services)]
public sealed class OperatorNotificationStoreTests
{
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

        CollectionAssert.AreEqual(
            new[] { expectedFullState, expectedLevel2, expectedLevel1, expectedExisting, expectedFullState },
            syncedDirectories,
            "First publication into nested non-existent directories must fsync the target directory, every newly created directory's parent bottom-up, and the target directory after rename.");

        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(nestedStateDir));
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
