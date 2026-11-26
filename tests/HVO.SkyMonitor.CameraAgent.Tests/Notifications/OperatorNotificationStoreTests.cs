using System.Security.Cryptography;
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
