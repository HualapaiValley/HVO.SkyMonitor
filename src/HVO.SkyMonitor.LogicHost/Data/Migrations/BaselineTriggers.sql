-- TR_CentralArtifactDownloadAuthorizations_Immutable ON CentralArtifactDownloadAuthorizations
CREATE TRIGGER [TR_CentralArtifactDownloadAuthorizations_Immutable]
ON [CentralArtifactDownloadAuthorizations]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    THROW 51000, 'Central artifact download authorization evidence is immutable.', 1;
END
GO

-- TR_CentralFrames_InstallationImmutable ON CentralFrames
CREATE TRIGGER [TR_CentralFrames_InstallationImmutable]
ON [CentralFrames]
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF UPDATE([LogicalCameraInstallationId]) AND EXISTS (
        SELECT 1
        FROM inserted AS current_row
        INNER JOIN deleted AS previous_row ON previous_row.[Id] = current_row.[Id]
        WHERE current_row.[LogicalCameraInstallationId] <> previous_row.[LogicalCameraInstallationId]
           OR current_row.[LogicalCameraInstallationId] IS NULL
              AND previous_row.[LogicalCameraInstallationId] IS NOT NULL
           OR current_row.[LogicalCameraInstallationId] IS NOT NULL
              AND previous_row.[LogicalCameraInstallationId] IS NULL)
    BEGIN
        THROW 51000, 'Capture installation authority is immutable.', 1;
    END
END;
GO

-- TR_CentralProcessingOverrideVersions_Transitions ON CentralProcessingOverrideVersions
CREATE TRIGGER [TR_CentralProcessingOverrideVersions_Transitions]
ON [CentralProcessingOverrideVersions]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted d
        LEFT JOIN inserted i ON i.[Id] = d.[Id]
        WHERE i.[Id] IS NULL
           OR d.[SupersededAtUtc] IS NOT NULL
           OR i.[SupersededAtUtc] IS NULL
           OR i.[ObservatoryId] <> d.[ObservatoryId]
           OR i.[Version] <> d.[Version]
           OR ISNULL(i.[CloudTransmissionThresholdMillionths], -1) <> ISNULL(d.[CloudTransmissionThresholdMillionths], -1)
           OR ISNULL(CONVERT(int, i.[CentralValidationEnabled]), -1) <> ISNULL(CONVERT(int, d.[CentralValidationEnabled]), -1)
           OR i.[EffectiveFromUtc] <> d.[EffectiveFromUtc]
           OR i.[ActorUserId] <> d.[ActorUserId]
           OR i.[ReasonCode] <> d.[ReasonCode])
    BEGIN
        THROW 51000, 'Central processing override history is immutable.', 1;
    END
END
GO

-- TR_CentralTransientAssessmentObservations_Immutable ON CentralTransientAssessmentObservations
CREATE TRIGGER [TR_CentralTransientAssessmentObservations_Immutable]
ON [CentralTransientAssessmentObservations]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientAssessments_Immutable ON CentralTransientAssessments
CREATE TRIGGER [TR_CentralTransientAssessments_Immutable]
ON [CentralTransientAssessments]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientDerivativeBackgrounds_Closed ON CentralTransientDerivativeBackgrounds
CREATE TRIGGER [TR_CentralTransientDerivativeBackgrounds_Closed]
ON [CentralTransientDerivativeBackgrounds]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        INNER JOIN [CentralTransientDerivatives] AS derivative
            ON derivative.[DerivativeId] = i.[DerivativeId]
        INNER JOIN [CentralTransientDerivativeJobs] AS job
            ON job.[CentralDerivativeJobId] = derivative.[CentralDerivativeJobId]
        WHERE job.[CommittedAtUtc] IS NOT NULL)
        THROW 51000, 'Committed transient derivative bundles are closed.', 1;
END
GO

-- TR_CentralTransientDerivativeBackgrounds_Immutable ON CentralTransientDerivativeBackgrounds
CREATE TRIGGER [TR_CentralTransientDerivativeBackgrounds_Immutable]
ON [CentralTransientDerivativeBackgrounds]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientDerivativeJobs_CommittedImmutable ON CentralTransientDerivativeJobs
CREATE TRIGGER [TR_CentralTransientDerivativeJobs_CommittedImmutable]
ON [CentralTransientDerivativeJobs]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        LEFT JOIN inserted AS i ON i.[CentralDerivativeJobId] = d.[CentralDerivativeJobId]
        WHERE i.[CentralDerivativeJobId] IS NULL
           OR d.[CommittedAtUtc] IS NOT NULL
           OR i.[CentralTransientEventId] <> d.[CentralTransientEventId]
           OR i.[SourceEventVersionId] <> d.[SourceEventVersionId]
           OR i.[RequestIdentitySha256] <> d.[RequestIdentitySha256]
           OR i.[ProducerSchemaVersion] <> d.[ProducerSchemaVersion]
           OR i.[ProducerName] <> d.[ProducerName]
           OR i.[ProducerVersion] <> d.[ProducerVersion]
           OR i.[RecipeIdentitySha256] <> d.[RecipeIdentitySha256]
           OR i.[OptionsIdentitySha256] <> d.[OptionsIdentitySha256]
           OR i.[CanonicalRequestJson] <> d.[CanonicalRequestJson]
           OR i.[CanonicalRequestSha256] <> d.[CanonicalRequestSha256]
           OR i.[CanonicalRequestByteLength] <> d.[CanonicalRequestByteLength]
           OR i.[ExpectedOutputCount] <> d.[ExpectedOutputCount]
           OR i.[CreatedAtUtc] <> d.[CreatedAtUtc])
        THROW 51000, 'Transient derivative job identity is immutable.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        WHERE i.[CommittedAtUtc] IS NOT NULL
          AND (i.[CommittedAtUtc] < i.[CreatedAtUtc]
               OR (SELECT COUNT(*) FROM [CentralTransientDerivativeOutputIntents] AS intent
                   WHERE intent.[CentralDerivativeJobId] = i.[CentralDerivativeJobId]
                     AND intent.[CommittedAtUtc] IS NOT NULL) <> i.[ExpectedOutputCount]
               OR (SELECT COUNT(*) FROM [CentralTransientDerivatives] AS derivative
                   WHERE derivative.[CentralDerivativeJobId] = i.[CentralDerivativeJobId])
                   <> i.[ExpectedOutputCount]))
        THROW 51000, 'Committed transient derivative bundle is incomplete.', 1;
END
GO

-- TR_CentralTransientDerivativeOutputIntents_Closed ON CentralTransientDerivativeOutputIntents
CREATE TRIGGER [TR_CentralTransientDerivativeOutputIntents_Closed]
ON [CentralTransientDerivativeOutputIntents]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        INNER JOIN [CentralTransientDerivativeJobs] AS job
            ON job.[CentralTransientEventId] = i.[CentralTransientEventId]
           AND job.[CentralDerivativeJobId] = i.[CentralDerivativeJobId]
        WHERE job.[CommittedAtUtc] IS NOT NULL)
        THROW 51000, 'Committed transient derivative bundles are closed.', 1;
END
GO

-- TR_CentralTransientDerivativeOutputIntents_TerminalImmutable ON CentralTransientDerivativeOutputIntents
CREATE TRIGGER [TR_CentralTransientDerivativeOutputIntents_TerminalImmutable]
ON [CentralTransientDerivativeOutputIntents]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        LEFT JOIN inserted AS i ON i.[Id] = d.[Id]
        WHERE i.[Id] IS NULL
           OR (d.[CommittedAtUtc] IS NOT NULL AND NOT (
               d.[ObjectState] = N'Available'
                AND i.[ObjectState] = N'Expired'
                AND i.[CommittedAtUtc] = d.[CommittedAtUtc]
                AND i.[ObjectVerifiedAtUtc] = d.[ObjectVerifiedAtUtc]
                AND i.[StorageETag] = d.[StorageETag]))
           OR i.[CentralDerivativeJobId] <> d.[CentralDerivativeJobId]
           OR i.[CentralTransientEventId] <> d.[CentralTransientEventId]
           OR i.[Kind] <> d.[Kind]
           OR i.[DerivativeId] <> d.[DerivativeId]
           OR i.[ArtifactId] <> d.[ArtifactId]
           OR i.[ArtifactRole] <> d.[ArtifactRole]
           OR i.[ArtifactVariant] <> d.[ArtifactVariant]
           OR i.[MediaType] <> d.[MediaType]
           OR i.[ByteLength] <> d.[ByteLength]
           OR i.[ChecksumSha256] <> d.[ChecksumSha256]
           OR i.[OutputIdentitySha256] <> d.[OutputIdentitySha256]
           OR i.[StorageReference] <> d.[StorageReference]
           OR i.[CreatedAtUtc] <> d.[CreatedAtUtc])
        THROW 51000, 'Transient derivative output identity is immutable.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        WHERE i.[CommittedAtUtc] IS NOT NULL
          AND (i.[CommittedAtUtc] < i.[CreatedAtUtc]
               OR i.[ObjectVerifiedAtUtc] < i.[CreatedAtUtc]
               OR i.[ObjectVerifiedAtUtc] > i.[CommittedAtUtc]
               OR NOT EXISTS (
                   SELECT 1 FROM [CentralTransientDerivatives] AS derivative
                   WHERE derivative.[CentralTransientEventId] = i.[CentralTransientEventId]
                     AND derivative.[CentralDerivativeJobId] = i.[CentralDerivativeJobId]
                     AND derivative.[OutputIntentId] = i.[Id]
                     AND derivative.[DerivativeId] = i.[DerivativeId]
                     AND derivative.[ArtifactId] = i.[ArtifactId]
                     AND derivative.[ArtifactRole] = i.[ArtifactRole]
                     AND derivative.[ArtifactVariant] = i.[ArtifactVariant]
                     AND derivative.[MediaType] = i.[MediaType]
                     AND derivative.[ByteLength] = i.[ByteLength]
                     AND derivative.[ArtifactChecksumSha256] = i.[ChecksumSha256]
                     AND derivative.[OutputIdentitySha256] = i.[OutputIdentitySha256])))
        THROW 51000, 'Committed transient derivative output evidence is incomplete.', 1;
END
GO

-- TR_CentralTransientDerivatives_Closed ON CentralTransientDerivatives
CREATE TRIGGER [TR_CentralTransientDerivatives_Closed]
ON [CentralTransientDerivatives]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        INNER JOIN [CentralTransientDerivativeJobs] AS job
            ON job.[CentralDerivativeJobId] = i.[CentralDerivativeJobId]
        INNER JOIN [CentralTransientDerivativeOutputIntents] AS intent
            ON intent.[Id] = i.[OutputIntentId]
        WHERE job.[CommittedAtUtc] IS NOT NULL OR intent.[CommittedAtUtc] IS NOT NULL)
        THROW 51000, 'Committed transient derivative bundles are closed.', 1;
END
GO

-- TR_CentralTransientDerivatives_Immutable ON CentralTransientDerivatives
CREATE TRIGGER [TR_CentralTransientDerivatives_Immutable]
ON [CentralTransientDerivatives]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientDerivativeSources_Closed ON CentralTransientDerivativeSources
CREATE TRIGGER [TR_CentralTransientDerivativeSources_Closed]
ON [CentralTransientDerivativeSources]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        INNER JOIN [CentralTransientDerivatives] AS derivative
            ON derivative.[DerivativeId] = i.[DerivativeId]
        INNER JOIN [CentralTransientDerivativeJobs] AS job
            ON job.[CentralDerivativeJobId] = derivative.[CentralDerivativeJobId]
        WHERE job.[CommittedAtUtc] IS NOT NULL)
        THROW 51000, 'Committed transient derivative bundles are closed.', 1;
END
GO

-- TR_CentralTransientDerivativeSources_Immutable ON CentralTransientDerivativeSources
CREATE TRIGGER [TR_CentralTransientDerivativeSources_Immutable]
ON [CentralTransientDerivativeSources]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientEvents_Immutable ON CentralTransientEvents
CREATE TRIGGER [TR_CentralTransientEvents_Immutable]
ON [CentralTransientEvents]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientEventVersionAssessments_Immutable ON CentralTransientEventVersionAssessments
CREATE TRIGGER [TR_CentralTransientEventVersionAssessments_Immutable]
ON [CentralTransientEventVersionAssessments]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientEventVersionDerivatives_Immutable ON CentralTransientEventVersionDerivatives
CREATE TRIGGER [TR_CentralTransientEventVersionDerivatives_Immutable]
ON [CentralTransientEventVersionDerivatives]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientEventVersionNotifications_Immutable ON CentralTransientEventVersionNotifications
CREATE TRIGGER [TR_CentralTransientEventVersionNotifications_Immutable]
ON [CentralTransientEventVersionNotifications]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientEventVersionObservations_Immutable ON CentralTransientEventVersionObservations
CREATE TRIGGER [TR_CentralTransientEventVersionObservations_Immutable]
ON [CentralTransientEventVersionObservations]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientEventVersionReviews_Immutable ON CentralTransientEventVersionReviews
CREATE TRIGGER [TR_CentralTransientEventVersionReviews_Immutable]
ON [CentralTransientEventVersionReviews]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientEventVersions_Immutable ON CentralTransientEventVersions
CREATE TRIGGER [TR_CentralTransientEventVersions_Immutable]
ON [CentralTransientEventVersions]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientExtractionReceipts_Immutable ON CentralTransientExtractionReceipts
CREATE TRIGGER [TR_CentralTransientExtractionReceipts_Immutable]
ON [CentralTransientExtractionReceipts]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientExtractionSources_Immutable ON CentralTransientExtractionSources
CREATE TRIGGER [TR_CentralTransientExtractionSources_Immutable]
ON [CentralTransientExtractionSources]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientNotificationDispatches_Insert ON CentralTransientNotificationDispatches
CREATE TRIGGER [TR_CentralTransientNotificationDispatches_Insert]
ON [CentralTransientNotificationDispatches]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted WHERE [State] NOT IN (N'Pending', N'Failed', N'Suppressed'))
        THROW 51000, 'Initial transient notification dispatch state is invalid.', 1;
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        INNER JOIN [CentralTransientNotifications] AS notification
            ON notification.[CentralTransientEventId] = i.[CentralTransientEventId]
           AND notification.[NotificationId] = i.[InitialNotificationId]
        INNER JOIN [CentralTransientReviews] AS review
            ON review.[CentralTransientEventId] = i.[CentralTransientEventId]
           AND review.[ReviewId] = i.[ReviewId]
        WHERE i.[InitialNotificationId] <> i.[LatestNotificationId]
           OR notification.[AssessmentId] <> i.[AssessmentId]
           OR notification.[Channel] <> i.[Channel]
           OR notification.[State] <> i.[State]
           OR review.[AssessmentId] <> i.[AssessmentId])
        THROW 51000, 'Transient notification dispatch lineage is invalid.', 1;
END
GO

-- TR_CentralTransientNotificationDispatches_Transition ON CentralTransientNotificationDispatches
CREATE TRIGGER [TR_CentralTransientNotificationDispatches_Transition]
ON [CentralTransientNotificationDispatches]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM deleted AS d
        LEFT JOIN inserted AS i ON i.[DispatchId] = d.[DispatchId]
        WHERE i.[DispatchId] IS NULL
           OR i.[CentralTransientEventId] <> d.[CentralTransientEventId]
           OR i.[AssessmentId] <> d.[AssessmentId]
           OR i.[ReviewId] <> d.[ReviewId]
           OR i.[InitialNotificationId] <> d.[InitialNotificationId]
           OR i.[Channel] <> d.[Channel]
           OR ISNULL(i.[Recipient], N'') <> ISNULL(d.[Recipient], N'')
           OR ISNULL(i.[RecipientIdentitySha256], '') <> ISNULL(d.[RecipientIdentitySha256], '')
           OR i.[CreatedUtc] <> d.[CreatedUtc]
           OR ISNULL(i.[SupersedesDispatchId], '00000000-0000-0000-0000-000000000000') <>
              ISNULL(d.[SupersedesDispatchId], '00000000-0000-0000-0000-000000000000')
           OR ISNULL(i.[RequestedByActorIdentity], N'') <> ISNULL(d.[RequestedByActorIdentity], N'')
           OR ISNULL(i.[IdempotencyKey], N'') <> ISNULL(d.[IdempotencyKey], N'')
           OR ISNULL(i.[CanonicalRequestSha256], '') <> ISNULL(d.[CanonicalRequestSha256], '')
           OR d.[State] IN (N'Sent', N'Failed', N'Suppressed')
           OR NOT ((d.[State] = N'Pending' AND i.[State] = N'Fenced' AND
                    d.[FencedUtc] IS NULL AND i.[FencedUtc] IS NOT NULL AND
                    d.[CompletedUtc] IS NULL AND i.[CompletedUtc] IS NULL)
               OR (d.[State] = N'Fenced' AND i.[State] IN (N'Sent', N'Failed') AND
                   i.[FencedUtc] = d.[FencedUtc] AND i.[CompletedUtc] IS NOT NULL))
    )
        THROW 51000, 'Transient notification dispatch transition is invalid.', 1;
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        INNER JOIN deleted AS d ON d.[DispatchId] = i.[DispatchId]
        INNER JOIN [CentralTransientNotifications] AS notification
            ON notification.[CentralTransientEventId] = i.[CentralTransientEventId]
           AND notification.[NotificationId] = i.[LatestNotificationId]
        WHERE notification.[AssessmentId] <> i.[AssessmentId]
           OR notification.[Channel] <> i.[Channel]
           OR (i.[State] IN (N'Sent', N'Failed') AND notification.[State] <> i.[State])
           OR (i.[LatestNotificationId] <> d.[LatestNotificationId] AND
               notification.[SupersedesNotificationId] <> d.[LatestNotificationId]))
        THROW 51000, 'Transient notification dispatch lineage is invalid.', 1;
END
GO

-- TR_CentralTransientNotifications_Immutable ON CentralTransientNotifications
CREATE TRIGGER [TR_CentralTransientNotifications_Immutable]
ON [CentralTransientNotifications]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientObservationBackgrounds_Immutable ON CentralTransientObservationBackgrounds
CREATE TRIGGER [TR_CentralTransientObservationBackgrounds_Immutable]
ON [CentralTransientObservationBackgrounds]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientObservations_Immutable ON CentralTransientObservations
CREATE TRIGGER [TR_CentralTransientObservations_Immutable]
ON [CentralTransientObservations]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientObservationSources_Immutable ON CentralTransientObservationSources
CREATE TRIGGER [TR_CentralTransientObservationSources_Immutable]
ON [CentralTransientObservationSources]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientPayloadReleaseItems_Closed ON CentralTransientPayloadReleaseItems
CREATE TRIGGER [TR_CentralTransientPayloadReleaseItems_Closed]
ON [CentralTransientPayloadReleaseItems]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        INNER JOIN [CentralTransientPayloadReleases] AS release
            ON release.[ReleaseId] = i.[ReleaseId]
        WHERE release.[State] <> N'Pending' OR i.[Outcome] <> N'Pending')
        THROW 51000, 'Transient payload release items are closed.', 1;
END
GO

-- TR_CentralTransientPayloadReleaseItems_Transition ON CentralTransientPayloadReleaseItems
CREATE TRIGGER [TR_CentralTransientPayloadReleaseItems_Transition]
ON [CentralTransientPayloadReleaseItems]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        LEFT JOIN inserted AS i
            ON i.[ReleaseId] = d.[ReleaseId] AND i.[Ordinal] = d.[Ordinal]
        WHERE i.[ReleaseId] IS NULL
           OR i.[Kind] <> d.[Kind]
           OR i.[RecordId] <> d.[RecordId]
           OR d.[Outcome] <> N'Pending'
           OR i.[Outcome] NOT IN (N'Pending', N'Released', N'PreservedHeld', N'Failed')
           OR i.[RetryCount] < d.[RetryCount]
           OR (i.[Outcome] = N'Pending' AND
               (i.[ReleasedUtc] IS NOT NULL OR i.[FailureReasonCode] IS NOT NULL))
           OR (i.[Outcome] IN (N'Released', N'PreservedHeld') AND
               (i.[ReleasedUtc] IS NULL OR i.[FailureReasonCode] IS NOT NULL OR
                i.[ReservationToken] IS NOT NULL OR i.[RetryAtUtc] IS NOT NULL))
           OR (i.[Outcome] = N'Failed' AND
               (i.[ReleasedUtc] IS NULL OR i.[FailureReasonCode] IS NULL OR
                i.[ReservationToken] IS NOT NULL OR i.[RetryAtUtc] IS NOT NULL)))
        THROW 51000, 'Transient payload release item transition is invalid.', 1;
END
GO

-- TR_CentralTransientPayloadReleases_Insert ON CentralTransientPayloadReleases
CREATE TRIGGER [TR_CentralTransientPayloadReleases_Insert]
ON [CentralTransientPayloadReleases]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted WHERE [State] <> N'Pending')
        THROW 51000, 'Initial transient payload release state is invalid.', 1;
END
GO

-- TR_CentralTransientPayloadReleases_Transition ON CentralTransientPayloadReleases
CREATE TRIGGER [TR_CentralTransientPayloadReleases_Transition]
ON [CentralTransientPayloadReleases]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM deleted AS d
        LEFT JOIN inserted AS i ON i.[ReleaseId] = d.[ReleaseId]
        WHERE i.[ReleaseId] IS NULL
           OR d.[State] <> N'Pending'
           OR i.[CentralTransientEventId] <> d.[CentralTransientEventId]
           OR i.[ActorIdentity] <> d.[ActorIdentity]
           OR i.[IdempotencyKey] <> d.[IdempotencyKey]
           OR i.[CanonicalRequestSha256] <> d.[CanonicalRequestSha256]
           OR i.[CreatedUtc] <> d.[CreatedUtc]
           OR i.[State] NOT IN (N'Completed', N'Failed')
           OR (i.[State] = N'Completed' AND EXISTS (
               SELECT 1 FROM [CentralTransientPayloadReleaseItems] AS item
               WHERE item.[ReleaseId] = i.[ReleaseId]
                 AND item.[Outcome] IN (N'Pending', N'Failed')))
           OR (i.[State] = N'Failed' AND
               (EXISTS (
                   SELECT 1 FROM [CentralTransientPayloadReleaseItems] AS item
                   WHERE item.[ReleaseId] = i.[ReleaseId] AND item.[Outcome] = N'Pending')
                OR NOT EXISTS (
                   SELECT 1 FROM [CentralTransientPayloadReleaseItems] AS item
                   WHERE item.[ReleaseId] = i.[ReleaseId] AND item.[Outcome] = N'Failed'))))
        THROW 51000, 'Transient payload release transition is invalid.', 1;
END
GO

-- TR_CentralTransientReprocessingJobs_Immutable ON CentralTransientReprocessingJobs
CREATE TRIGGER [TR_CentralTransientReprocessingJobs_Immutable]
ON [CentralTransientReprocessingJobs]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM deleted AS d
        LEFT JOIN inserted AS i ON i.[CentralDerivativeJobId] = d.[CentralDerivativeJobId]
        WHERE i.[CentralDerivativeJobId] IS NULL
           OR d.[CommittedUtc] IS NOT NULL
           OR i.[CentralTransientEventId] <> d.[CentralTransientEventId]
           OR i.[SourceEventVersionId] <> d.[SourceEventVersionId]
           OR i.[ActorIdentity] <> d.[ActorIdentity]
           OR i.[IdempotencyKey] <> d.[IdempotencyKey]
           OR i.[CanonicalRequestJson] <> d.[CanonicalRequestJson]
           OR i.[CanonicalRequestSha256] <> d.[CanonicalRequestSha256]
           OR i.[CanonicalRequestByteLength] <> d.[CanonicalRequestByteLength]
           OR i.[RequestIdentitySha256] <> d.[RequestIdentitySha256]
           OR i.[ProducerName] <> d.[ProducerName]
           OR i.[ProducerVersion] <> d.[ProducerVersion]
           OR i.[RecipeIdentitySha256] <> d.[RecipeIdentitySha256]
           OR i.[OptionsIdentitySha256] <> d.[OptionsIdentitySha256]
           OR i.[OptionsJson] <> d.[OptionsJson]
           OR i.[CreatedUtc] <> d.[CreatedUtc]
           OR i.[CommittedUtc] IS NULL)
        THROW 51000, 'Transient reprocessing evidence is immutable.', 1;
END
GO

-- TR_CentralTransientReprocessingRequests_Immutable ON CentralTransientReprocessingRequests
CREATE TRIGGER [TR_CentralTransientReprocessingRequests_Immutable]
ON [CentralTransientReprocessingRequests]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientReviewMutations_Immutable ON CentralTransientReviewMutations
CREATE TRIGGER [TR_CentralTransientReviewMutations_Immutable]
ON [CentralTransientReviewMutations]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientReviews_Immutable ON CentralTransientReviews
CREATE TRIGGER [TR_CentralTransientReviews_Immutable]
ON [CentralTransientReviews]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Committed transient evidence is immutable.', 1;
END
GO

-- TR_CentralTransientSubmissionAudits_Immutable ON CentralTransientSubmissionAudits
CREATE TRIGGER [TR_CentralTransientSubmissionAudits_Immutable]
ON [CentralTransientSubmissionAudits]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    THROW 51000, 'Hybrid transient submission audit evidence is immutable.', 1;
END
GO

-- TR_CentralTransientValidationIdentitySlots_TerminalImmutable ON CentralTransientValidationIdentitySlots
CREATE TRIGGER [TR_CentralTransientValidationIdentitySlots_TerminalImmutable]
ON [CentralTransientValidationIdentitySlots]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        LEFT JOIN inserted AS i ON i.[Id] = d.[Id]
        WHERE i.[Id] IS NULL
           OR d.[State] <> N'Reserved'
           OR i.[CentralDerivativeJobId] <> d.[CentralDerivativeJobId]
           OR i.[Ordinal] <> d.[Ordinal]
           OR i.[AgentId] <> d.[AgentId]
           OR i.[SubmittedEventId] <> d.[SubmittedEventId]
           OR i.[CandidateId] <> d.[CandidateId]
           OR i.[ObservationId] <> d.[ObservationId]
           OR i.[AssessmentId] <> d.[AssessmentId]
           OR (d.[AdoptedEventId] IS NOT NULL AND
               (i.[AdoptedEventId] IS NULL OR i.[AdoptedEventId] <> d.[AdoptedEventId]))
           OR (d.[AssociationIdentitySha256] IS NOT NULL AND
               (i.[AssociationIdentitySha256] IS NULL OR
                i.[AssociationIdentitySha256] <> d.[AssociationIdentitySha256])))
    BEGIN
        THROW 51000, 'Terminal transient validation identity slots are immutable.', 1;
    END
END
GO

-- TR_CentralTransientValidationJobs_CommittedImmutable ON CentralTransientValidationJobs
CREATE TRIGGER [TR_CentralTransientValidationJobs_CommittedImmutable]
ON [CentralTransientValidationJobs]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        LEFT JOIN inserted AS i ON i.[CentralDerivativeJobId] = d.[CentralDerivativeJobId]
        WHERE i.[CentralDerivativeJobId] IS NULL
           OR d.[CommittedAtUtc] IS NOT NULL
           OR d.[OutcomeRecordedAtUtc] IS NOT NULL
           OR i.[AgentId] <> d.[AgentId]
           OR i.[SubmissionSchemaVersion] <> d.[SubmissionSchemaVersion]
           OR i.[SubmissionIdentitySha256] <> d.[SubmissionIdentitySha256]
           OR (i.[SubmittedCandidateJson] IS NULL AND d.[SubmittedCandidateJson] IS NOT NULL)
           OR (i.[SubmittedCandidateJson] IS NOT NULL AND d.[SubmittedCandidateJson] IS NULL)
           OR i.[SubmittedCandidateJson] <> d.[SubmittedCandidateJson]
           OR ISNULL(i.[ExecutionOptionsJson], N'') <> ISNULL(d.[ExecutionOptionsJson], N'')
           OR ISNULL(i.[ExecutionOptionsIdentitySha256], '') <> ISNULL(d.[ExecutionOptionsIdentitySha256], '')
           OR ISNULL(i.[ProvisionalCentralDerivativeJobId], '00000000-0000-0000-0000-000000000000') <>
              ISNULL(d.[ProvisionalCentralDerivativeJobId], '00000000-0000-0000-0000-000000000000')
           OR i.[CreatedAtUtc] <> d.[CreatedAtUtc]
           OR (i.[CommittedAtUtc] IS NOT NULL AND i.[OutcomeRecordedAtUtc] IS NULL))
    BEGIN
        THROW 51000, 'Committed transient validation job identity is immutable.', 1;
    END
END
GO

-- TR_CentralTransientValidationOutcomeVersions_Immutable ON CentralTransientValidationOutcomeVersions
CREATE TRIGGER [TR_CentralTransientValidationOutcomeVersions_Immutable]
ON [CentralTransientValidationOutcomeVersions]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    THROW 51000, 'Transient validation outcome versions are immutable.', 1;
END
GO

-- TR_CuratedPublicPlacementDecisions_Immutable ON CuratedPublicPlacementDecisions
CREATE TRIGGER [TR_CuratedPublicPlacementDecisions_Immutable]
ON [CuratedPublicPlacementDecisions]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    THROW 51000, 'Curated public placement decision history is immutable.', 1;
END
GO

-- TR_LogicalCameraInstallations_Transitions ON LogicalCameraInstallations
CREATE TRIGGER [TR_LogicalCameraInstallations_Transitions]
ON [LogicalCameraInstallations]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted AS d LEFT JOIN inserted AS i ON i.[Id] = d.[Id] WHERE i.[Id] IS NULL)
       OR NOT UPDATE([RetiredAtUtc])
       OR UPDATE([Id]) OR UPDATE([LogicalCameraId]) OR UPDATE([RegistrationId])
       OR UPDATE([InstallationPublicId]) OR UPDATE([AssignedAtUtc]) OR UPDATE([ReplacesInstallationId])
       OR UPDATE([AssignedByUserId]) OR UPDATE([AssignmentReasonCode])
       OR EXISTS (
           SELECT 1 FROM deleted AS d INNER JOIN inserted AS i ON i.[Id] = d.[Id]
           WHERE d.[RetiredAtUtc] IS NOT NULL OR i.[RetiredAtUtc] IS NULL
              OR i.[RetiredByUserId] IS NULL OR i.[RetirementReasonCode] IS NULL)
    BEGIN
        THROW 51000, 'Logical camera installations allow only one terminal retirement.', 1;
    END;
END
GO

-- TR_ObservatoryInvitationDispositions_Immutable ON ObservatoryInvitationDispositions
CREATE TRIGGER [TR_ObservatoryInvitationDispositions_Immutable]
ON [ObservatoryInvitationDispositions]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    THROW 51000, 'Observatory invitation disposition history is immutable.', 1;
END
GO

-- TR_ObservatoryInvitations_Immutable ON ObservatoryInvitations
CREATE TRIGGER [TR_ObservatoryInvitations_Immutable]
ON [ObservatoryInvitations]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    THROW 51000, 'Observatory invitation history is immutable.', 1;
END
GO

-- TR_ObservatoryLocationDisclosureVersions_Transitions ON ObservatoryLocationDisclosureVersions
CREATE TRIGGER [TR_ObservatoryLocationDisclosureVersions_Transitions]
ON [ObservatoryLocationDisclosureVersions]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted AS d LEFT JOIN inserted AS i ON i.[Id] = d.[Id] WHERE i.[Id] IS NULL)
       OR NOT UPDATE([SupersededAtUtc])
       OR UPDATE([Id]) OR UPDATE([ObservatoryId]) OR UPDATE([Version]) OR UPDATE([DisclosureLevel])
       OR UPDATE([RegionCode]) OR UPDATE([RegionLabel]) OR UPDATE([PublicLatitudeDegrees])
       OR UPDATE([PublicLongitudeDegrees]) OR UPDATE([PublicPrecisionMeters])
       OR UPDATE([SourceObservatoryLocationVersionId]) OR UPDATE([EffectiveFromUtc])
       OR UPDATE([ActorUserId]) OR UPDATE([ReasonCode]) OR UPDATE([CanonicalSha256])
       OR EXISTS (
           SELECT 1 FROM deleted AS d INNER JOIN inserted AS i ON i.[Id] = d.[Id]
           WHERE d.[SupersededAtUtc] IS NOT NULL OR i.[SupersededAtUtc] IS NULL)
    BEGIN
        THROW 51000, 'Location disclosure versions allow only one terminal supersession.', 1;
    END;
END
GO

-- TR_ObservatoryMembershipAudits_Immutable ON ObservatoryMembershipAudits
CREATE TRIGGER [TR_ObservatoryMembershipAudits_Immutable]
ON [ObservatoryMembershipAudits]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    THROW 51000, 'Observatory membership audit evidence is immutable.', 1;
END
GO

-- TR_ObservatoryPublicationProfileVersions_Transitions ON ObservatoryPublicationProfileVersions
CREATE TRIGGER [TR_ObservatoryPublicationProfileVersions_Transitions]
ON [ObservatoryPublicationProfileVersions]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted AS d LEFT JOIN inserted AS i ON i.[Id] = d.[Id] WHERE i.[Id] IS NULL)
       OR NOT UPDATE([SupersededAtUtc])
       OR UPDATE([Id]) OR UPDATE([ObservatoryId]) OR UPDATE([Version]) OR UPDATE([PublicSlug])
       OR UPDATE([PublicDisplayName]) OR UPDATE([PublicDescription]) OR UPDATE([ProfileVisibility])
       OR UPDATE([PublishEnvironmentalSummary]) OR UPDATE([AllowAutomaticVerifiedEventInclusion])
       OR UPDATE([EffectiveFromUtc]) OR UPDATE([ActorUserId]) OR UPDATE([ReasonCode])
       OR UPDATE([CanonicalSha256])
       OR EXISTS (
           SELECT 1 FROM deleted AS d INNER JOIN inserted AS i ON i.[Id] = d.[Id]
           WHERE d.[SupersededAtUtc] IS NOT NULL OR i.[SupersededAtUtc] IS NULL)
    BEGIN
        THROW 51000, 'Publication profile versions allow only one terminal supersession.', 1;
    END;
END
GO

-- TR_PublicRecordPublicationDecisions_Immutable ON PublicRecordPublicationDecisions
CREATE TRIGGER [TR_PublicRecordPublicationDecisions_Immutable]
ON [PublicRecordPublicationDecisions]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    THROW 51000, 'Public record publication decision history is immutable.', 1;
END
GO