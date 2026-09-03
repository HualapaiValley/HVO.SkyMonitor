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

-- TR_CentralArtifactProcessingEvidence_GraphContractImmutable ON CentralArtifactProcessingEvidence
CREATE TRIGGER [TR_CentralArtifactProcessingEvidence_GraphContractImmutable]
ON [CentralArtifactProcessingEvidence]
AFTER INSERT, UPDATE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        INNER JOIN inserted AS i ON i.[CentralArtifactId] = d.[CentralArtifactId]
        WHERE ISNULL(i.[GraphProductContractIdentitySha256], '') <>
              ISNULL(d.[GraphProductContractIdentitySha256], '')
           OR ISNULL(i.[ProductKind], N'') <> ISNULL(d.[ProductKind], N'')
           OR ISNULL(i.[RecipeOperationKind], N'') <> ISNULL(d.[RecipeOperationKind], N'')
           OR ISNULL(i.[ProductSchemaVersion], N'') <> ISNULL(d.[ProductSchemaVersion], N'')
           OR ISNULL(i.[ProductMediaType], N'') <> ISNULL(d.[ProductMediaType], N''))
        THROW 51000, 'Derivative graph product-contract evidence is immutable.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN [CentralDerivativeJobs] AS job WITH (UPDLOCK, HOLDLOCK)
            ON job.[Id] = i.[CentralDerivativeJobId]
        INNER JOIN [CentralArtifacts] AS artifact ON artifact.[Id] = i.[CentralArtifactId]
        WHERE job.[GraphExecutionId] IS NOT NULL
          AND (i.[GraphProductContractIdentitySha256] IS NULL
                OR job.[InputSetIdentitySha256] IS NULL
                OR i.[RecipeOperationKind] IS NULL
                OR i.[ProductKind] IS NULL
               OR i.[ProductMediaType] IS NULL
               OR i.[ProductMediaType] <> artifact.[MediaType]
               OR i.[DevicePublicId] <> artifact.[DevicePublicId]
               OR i.[RequestedRecipeIdentitySha256] <> job.[RequestedRecipeIdentitySha256]
               OR i.[RecipeIdentitySha256] <> job.[ExpectedRecipeIdentitySha256]
               OR NOT EXISTS (
                   SELECT 1
                   FROM [CentralDerivativeJobOutputs] AS output
                   WHERE output.[CentralDerivativeJobId] = job.[Id]
                     AND output.[ContractIdentitySha256] = i.[GraphProductContractIdentitySha256]
                     AND output.[Role] = artifact.[Role]
                     AND output.[Variant] = ISNULL(artifact.[Variant], N'') COLLATE Latin1_General_100_BIN2
                     AND JSON_VALUE(output.[ContractJson], '$.role') = output.[Role]
                     AND JSON_VALUE(output.[ContractJson], '$.variant') COLLATE Latin1_General_100_BIN2 = output.[Variant]
                      AND JSON_VALUE(output.[ContractJson], '$.productKind') = output.[ProductKind]
                      AND output.[ProductKind] = i.[ProductKind]
                      AND (JSON_VALUE(output.[ContractJson], '$.recipe.operationKind') IS NULL
                           OR JSON_VALUE(output.[ContractJson], '$.recipe.operationKind') = i.[RecipeOperationKind])
                     AND (JSON_VALUE(output.[ContractJson], '$.mediaType') IS NULL
                          OR JSON_VALUE(output.[ContractJson], '$.mediaType') = i.[ProductMediaType])
                     AND ISNULL(JSON_VALUE(output.[ContractJson], '$.schemaVersion'), N'') =
                         ISNULL(i.[ProductSchemaVersion], N'')
                     AND JSON_QUERY(output.[ContractJson], '$.algorithms') = i.[AlgorithmsJson])))
        THROW 51000, 'Derivative graph product-contract evidence is inconsistent.', 1;
END
GO

-- TR_CentralDerivativeJobCanonicalInputs_GraphImmutable ON CentralDerivativeJobCanonicalInputs
CREATE TRIGGER [TR_CentralDerivativeJobCanonicalInputs_GraphImmutable]
ON [CentralDerivativeJobCanonicalInputs]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        INNER JOIN [CentralDerivativeJobs] AS job WITH (UPDLOCK, HOLDLOCK)
            ON job.[Id] = d.[CentralDerivativeJobId]
        WHERE job.[GraphExecutionId] IS NOT NULL)
        THROW 51000, 'Selected derivative graph canonical inputs are immutable.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        LEFT JOIN deleted AS d ON d.[Id] = i.[Id]
        INNER JOIN [CentralDerivativeJobs] AS job WITH (UPDLOCK, HOLDLOCK)
            ON job.[Id] = i.[CentralDerivativeJobId]
        INNER JOIN [CentralProcessingGraphExecutions] AS execution WITH (UPDLOCK, HOLDLOCK)
            ON execution.[Id] = job.[GraphExecutionId]
        INNER JOIN [CentralDerivativeJobInputRequirements] AS requirement
            ON requirement.[CentralDerivativeJobId] = i.[CentralDerivativeJobId]
           AND requirement.[Id] = i.[CentralDerivativeJobInputRequirementId]
        WHERE job.[GraphExecutionId] IS NOT NULL
          AND (d.[Id] IS NULL AND
                   (job.[InputSetIdentitySha256] IS NOT NULL OR execution.[ExpandedAtUtc] IS NULL)
               OR requirement.[SourceKind] = N'Artifact'
               OR requirement.[Ordinal] <> i.[Ordinal]
               OR EXISTS (
                   SELECT 1
                   FROM [CentralDerivativeJobInputs] AS artifact_input
                   WHERE artifact_input.[CentralDerivativeJobId] = i.[CentralDerivativeJobId]
                     AND artifact_input.[CentralDerivativeJobInputRequirementId] =
                         i.[CentralDerivativeJobInputRequirementId])))
        THROW 51000, 'Selected derivative graph canonical input binding is inconsistent or frozen.', 1;
END
GO

-- TR_CentralDerivativeJobDependencies_Immutable ON CentralDerivativeJobDependencies
CREATE TRIGGER [TR_CentralDerivativeJobDependencies_Immutable]
ON [CentralDerivativeJobDependencies]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51000, 'Derivative graph dependencies are immutable.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN [CentralProcessingGraphExecutions] AS execution WITH (UPDLOCK, HOLDLOCK)
            ON execution.[Id] = i.[ExecutionId]
        WHERE execution.[ExpandedAtUtc] IS NOT NULL)
        THROW 51000, 'Derivative graph dependencies cannot be added after expansion is sealed.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN [CentralDerivativeJobs] AS consumer ON consumer.[Id] = i.[ConsumerJobId]
        LEFT JOIN [CentralDerivativeJobs] AS producer_job ON producer_job.[Id] = i.[ProducerJobId]
        LEFT JOIN [CentralProcessingGraphExecutionSources] AS producer_source ON producer_source.[Id] = i.[ProducerSourceId]
        WHERE consumer.[GraphExecutionId] IS NULL
           OR consumer.[GraphExecutionId] <> i.[ExecutionId]
           OR i.[ProducerJobId] = i.[ConsumerJobId]
           OR i.[ProducerJobId] IS NOT NULL
              AND (producer_job.[GraphExecutionId] IS NULL OR producer_job.[GraphExecutionId] <> i.[ExecutionId])
           OR i.[ProducerSourceId] IS NOT NULL
              AND (producer_source.[ExecutionId] <> i.[ExecutionId]
                   OR producer_source.[OutputOrdinal] <> i.[ProducerOutputOrdinal]))
        THROW 51000, 'Derivative graph dependencies must remain within one execution.', 1;
END
GO

-- TR_CentralDerivativeJobInputRequirements_GraphBindingImmutable ON CentralDerivativeJobInputRequirements
CREATE TRIGGER [TR_CentralDerivativeJobInputRequirements_GraphBindingImmutable]
ON [CentralDerivativeJobInputRequirements]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        LEFT JOIN inserted AS i ON i.[Id] = d.[Id]
        LEFT JOIN [CentralDerivativeJobs] AS old_job ON old_job.[Id] = d.[CentralDerivativeJobId]
        LEFT JOIN [CentralDerivativeJobs] AS new_job ON new_job.[Id] = i.[CentralDerivativeJobId]
        WHERE (old_job.[GraphExecutionId] IS NOT NULL OR new_job.[GraphExecutionId] IS NOT NULL
               OR d.[GraphDependencyId] IS NOT NULL OR i.[GraphDependencyId] IS NOT NULL)
          AND (i.[Id] IS NULL
               OR i.[CentralDerivativeJobId] <> d.[CentralDerivativeJobId]
                   OR i.[Ordinal] <> d.[Ordinal]
                   OR i.[BindingName] <> d.[BindingName]
                   OR ISNULL(i.[GraphDependencyId], '00000000-0000-0000-0000-000000000000') <>
                   ISNULL(d.[GraphDependencyId], '00000000-0000-0000-0000-000000000000')
                   OR ISNULL(i.[GraphInputOrdinal], -1) <> ISNULL(d.[GraphInputOrdinal], -1)
                   OR ISNULL(i.[GraphInputBindingKind], N'') <> ISNULL(d.[GraphInputBindingKind], N'')
                   OR i.[SourceKind] <> d.[SourceKind]
                   OR ISNULL(i.[SequenceOffset], -2147483648) <> ISNULL(d.[SequenceOffset], -2147483648)
                   OR i.[IsRequired] <> d.[IsRequired]
                   OR i.[SelectorJson] <> d.[SelectorJson]
                   OR i.[CompatibilityMode] <> d.[CompatibilityMode]
                   OR i.[ExpectedAgentId] <> d.[ExpectedAgentId]
                    OR ISNULL(i.[ExpectedRigId], N'') <> ISNULL(d.[ExpectedRigId], N'')
                    OR ISNULL(i.[ExpectedCaptureSequence], -9223372036854775808) <>
                       ISNULL(d.[ExpectedCaptureSequence], -9223372036854775808)))
        THROW 51000, 'Derivative graph input requirement identity is immutable.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        LEFT JOIN deleted AS d ON d.[Id] = i.[Id]
        INNER JOIN [CentralDerivativeJobs] AS job ON job.[Id] = i.[CentralDerivativeJobId]
        INNER JOIN [CentralProcessingGraphExecutions] AS execution WITH (UPDLOCK, HOLDLOCK)
            ON execution.[Id] = job.[GraphExecutionId]
        WHERE d.[Id] IS NULL
          AND execution.[ExpandedAtUtc] IS NOT NULL)
        THROW 51000, 'Derivative graph input requirements cannot be added after expansion is sealed.', 1;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        INNER JOIN inserted AS i ON i.[Id] = d.[Id]
        INNER JOIN [CentralDerivativeJobs] AS job ON job.[Id] = d.[CentralDerivativeJobId]
        WHERE (job.[GraphExecutionId] IS NOT NULL OR d.[GraphDependencyId] IS NOT NULL)
          AND d.[ResolutionState] <> N'Waiting'
          AND (ISNULL(i.[ExpectedCentralArtifactId], '00000000-0000-0000-0000-000000000000') <>
               ISNULL(d.[ExpectedCentralArtifactId], '00000000-0000-0000-0000-000000000000')
               OR i.[ResolutionState] <> d.[ResolutionState]
               OR ISNULL(i.[ResolutionReasonCode], N'') <> ISNULL(d.[ResolutionReasonCode], N'')
               OR ISNULL(i.[ResolvedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00')) <>
                   ISNULL(d.[ResolvedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00'))))
        THROW 51000, 'Derivative graph input resolution is immutable after settlement.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN [CentralDerivativeJobs] AS job ON job.[Id] = i.[CentralDerivativeJobId]
        WHERE job.[GraphExecutionId] IS NOT NULL
          AND NOT (
              i.[ResolutionState] = N'Waiting'
              AND i.[ExpectedCentralArtifactId] IS NULL
              AND i.[ResolvedAtUtc] IS NULL
              OR i.[ResolutionState] = N'Resolved'
              AND i.[ResolvedAtUtc] IS NOT NULL
              AND (i.[SourceKind] = N'Artifact' AND i.[ExpectedCentralArtifactId] IS NOT NULL
                   OR i.[SourceKind] <> N'Artifact' AND i.[ExpectedCentralArtifactId] IS NULL)
              OR i.[ResolutionState] IN (N'Missing', N'Incompatible')
              AND i.[ExpectedCentralArtifactId] IS NULL
              AND i.[ResolvedAtUtc] IS NOT NULL
              AND LEN(i.[ResolutionReasonCode]) > 0))
        THROW 51000, 'Derivative graph input resolution evidence is inconsistent.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN [CentralDerivativeJobDependencies] AS dependency
            ON dependency.[Id] = i.[GraphDependencyId]
           AND dependency.[ConsumerJobId] = i.[CentralDerivativeJobId]
        WHERE ISNULL(i.[GraphInputOrdinal], -1) <> ISNULL(dependency.[ConsumerInputOrdinal], -1)
           OR ISNULL(i.[GraphInputBindingKind], N'') <> ISNULL(dependency.[ConsumerBindingKind], N'')
           OR i.[BindingName] COLLATE Latin1_General_100_BIN2 <> dependency.[ConsumerBindingName])
        THROW 51000, 'Derivative graph input binding evidence is inconsistent.', 1;
END
GO

-- TR_CentralDerivativeJobInputs_GraphImmutable ON CentralDerivativeJobInputs
CREATE TRIGGER [TR_CentralDerivativeJobInputs_GraphImmutable]
ON [CentralDerivativeJobInputs]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        INNER JOIN [CentralDerivativeJobs] AS job WITH (UPDLOCK, HOLDLOCK)
            ON job.[Id] = d.[CentralDerivativeJobId]
        WHERE job.[GraphExecutionId] IS NOT NULL)
        THROW 51000, 'Selected derivative graph artifact inputs are immutable.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        LEFT JOIN deleted AS d ON d.[Id] = i.[Id]
        INNER JOIN [CentralDerivativeJobs] AS job WITH (UPDLOCK, HOLDLOCK)
            ON job.[Id] = i.[CentralDerivativeJobId]
        INNER JOIN [CentralProcessingGraphExecutions] AS execution WITH (UPDLOCK, HOLDLOCK)
            ON execution.[Id] = job.[GraphExecutionId]
        INNER JOIN [CentralDerivativeJobInputRequirements] AS requirement
            ON requirement.[CentralDerivativeJobId] = i.[CentralDerivativeJobId]
           AND requirement.[Id] = i.[CentralDerivativeJobInputRequirementId]
        WHERE job.[GraphExecutionId] IS NOT NULL
          AND (d.[Id] IS NULL AND
                   (job.[InputSetIdentitySha256] IS NOT NULL OR execution.[ExpandedAtUtc] IS NULL)
               OR requirement.[SourceKind] <> N'Artifact'
               OR requirement.[Ordinal] <> i.[Ordinal]
               OR EXISTS (
                   SELECT 1
                   FROM [CentralDerivativeJobCanonicalInputs] AS canonical_input
                   WHERE canonical_input.[CentralDerivativeJobId] = i.[CentralDerivativeJobId]
                     AND canonical_input.[CentralDerivativeJobInputRequirementId] =
                         i.[CentralDerivativeJobInputRequirementId])))
        THROW 51000, 'Selected derivative graph artifact input binding is inconsistent or frozen.', 1;
END
GO

-- TR_CentralDerivativeJobOutputs_BindOnce ON CentralDerivativeJobOutputs
CREATE TRIGGER [TR_CentralDerivativeJobOutputs_BindOnce]
ON [CentralDerivativeJobOutputs]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted AS d LEFT JOIN inserted AS i ON i.[Id] = d.[Id] WHERE i.[Id] IS NULL)
        THROW 51000, 'Derivative graph output contracts are immutable.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        LEFT JOIN deleted AS d ON d.[Id] = i.[Id]
        WHERE d.[Id] IS NULL
          AND (i.[ResultCentralArtifactId] IS NOT NULL
               OR i.[ResultOutputIdentitySha256] IS NOT NULL
               OR i.[BoundAtUtc] IS NOT NULL))
        THROW 51000, 'Derivative graph output slots must be created unbound.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN [CentralDerivativeJobs] AS job ON job.[Id] = i.[CentralDerivativeJobId]
        LEFT JOIN [CentralProcessingGraphExecutions] AS execution WITH (UPDLOCK, HOLDLOCK)
            ON execution.[Id] = job.[GraphExecutionId]
        LEFT JOIN deleted AS d ON d.[Id] = i.[Id]
        WHERE job.[GraphExecutionId] IS NULL
           OR d.[Id] IS NULL AND execution.[ExpandedAtUtc] IS NOT NULL)
        THROW 51000, 'Derivative graph outputs require graph-owned jobs.', 1;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        INNER JOIN inserted AS i ON i.[Id] = d.[Id]
        WHERE i.[CentralDerivativeJobId] <> d.[CentralDerivativeJobId]
           OR i.[Ordinal] <> d.[Ordinal]
           OR i.[Role] <> d.[Role]
           OR i.[Variant] <> d.[Variant]
           OR i.[ProductKind] <> d.[ProductKind]
           OR i.[ContractJson] <> d.[ContractJson]
           OR i.[ContractIdentitySha256] <> d.[ContractIdentitySha256]
           OR d.[ResultCentralArtifactId] IS NOT NULL
           OR d.[ResultOutputIdentitySha256] IS NOT NULL
           OR d.[BoundAtUtc] IS NOT NULL
           OR i.[ResultCentralArtifactId] IS NULL
           OR i.[ResultOutputIdentitySha256] IS NULL
           OR i.[BoundAtUtc] IS NULL)
        THROW 51000, 'Derivative graph output slots may bind exactly once.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN deleted AS d ON d.[Id] = i.[Id]
        WHERE d.[ResultCentralArtifactId] IS NULL
          AND NOT EXISTS (
              SELECT 1
              FROM [CentralDerivativeJobs] AS job
              INNER JOIN [CentralProcessingGraphExecutions] AS execution
                  ON execution.[Id] = job.[GraphExecutionId]
              INNER JOIN [CentralArtifacts] AS anchor
                  ON anchor.[Id] = execution.[AnchorSourceCentralArtifactId]
              INNER JOIN [CentralArtifacts] AS source ON source.[Id] = job.[SourceCentralArtifactId]
              INNER JOIN [CentralArtifacts] AS result ON result.[Id] = i.[ResultCentralArtifactId]
               INNER JOIN [CentralArtifactProcessingEvidence] AS evidence
                   ON evidence.[CentralArtifactId] = result.[Id]
               INNER JOIN [CentralDerivativeJobs] AS evidence_job
                   ON evidence_job.[Id] = evidence.[CentralDerivativeJobId]
               LEFT JOIN [CentralArtifactRecipes] AS result_recipe
                   ON result_recipe.[CentralArtifactId] = result.[Id]
               WHERE job.[Id] = i.[CentralDerivativeJobId]
                AND execution.[ExpandedAtUtc] IS NOT NULL
                AND source.[CentralFrameId] = anchor.[CentralFrameId]
                AND result.[CentralFrameId] = anchor.[CentralFrameId]
                AND result.[DevicePublicId] = anchor.[DevicePublicId]
                AND result.[Role] = i.[Role]
                AND ISNULL(result.[Variant], N'') COLLATE Latin1_General_100_BIN2 = i.[Variant]
                AND result.[RecipeVersion] = job.[TargetRecipeVersion]
                AND evidence.[DevicePublicId] = result.[DevicePublicId]
                AND evidence.[RequestedRecipeIdentitySha256] = job.[RequestedRecipeIdentitySha256]
                AND evidence.[RecipeIdentitySha256] = job.[ExpectedRecipeIdentitySha256]
                AND evidence.[OutputIdentitySha256] = i.[ResultOutputIdentitySha256]
                -- Graph-tagged evidence must name this exact slot. Deterministic evidence produced by a legacy
                -- (non-graph) job carries no contract identity and is adopted only through the durable fields
                -- required alongside: frozen input set, requested/expected recipe identities, output identity,
                -- operation kind, product kind, schema, media type, recipe descriptor, and algorithms.
                AND (evidence.[GraphProductContractIdentitySha256] = i.[ContractIdentitySha256]
                     OR evidence.[GraphProductContractIdentitySha256] IS NULL
                        AND evidence_job.[GraphExecutionId] IS NULL)
                AND (JSON_VALUE(i.[ContractJson], '$.recipe.operationKind') IS NULL
                     OR evidence.[RecipeOperationKind] = JSON_VALUE(i.[ContractJson], '$.recipe.operationKind'))
                AND evidence.[ProductKind] = i.[ProductKind]
                AND evidence.[ProductMediaType] = result.[MediaType]
                AND evidence_job.[InputSetIdentitySha256] = job.[InputSetIdentitySha256]
                AND JSON_VALUE(i.[ContractJson], '$.role') = i.[Role]
                AND JSON_VALUE(i.[ContractJson], '$.variant') COLLATE Latin1_General_100_BIN2 = i.[Variant]
                AND JSON_VALUE(i.[ContractJson], '$.productKind') = i.[ProductKind]
                AND (JSON_VALUE(i.[ContractJson], '$.mediaType') IS NULL
                     OR JSON_VALUE(i.[ContractJson], '$.mediaType') = evidence.[ProductMediaType])
                AND ISNULL(JSON_VALUE(i.[ContractJson], '$.schemaVersion'), N'') =
                    ISNULL(evidence.[ProductSchemaVersion], N'')
                AND (JSON_VALUE(i.[ContractJson], '$.recipe.name') IS NULL
                     OR result_recipe.[Name] = JSON_VALUE(i.[ContractJson], '$.recipe.name')
                        AND result_recipe.[SemanticVersion] = JSON_VALUE(i.[ContractJson], '$.recipe.semanticVersion')
                        AND result_recipe.[ImplementationVersion] =
                            JSON_VALUE(i.[ContractJson], '$.recipe.implementationVersion'))
                AND JSON_QUERY(i.[ContractJson], '$.algorithms') = evidence.[AlgorithmsJson]))
        THROW 51000, 'Derivative graph output binding evidence is inconsistent.', 1;
END
GO

-- TR_CentralDerivativeJobs_GraphIdentityImmutable ON CentralDerivativeJobs
CREATE TRIGGER [TR_CentralDerivativeJobs_GraphIdentityImmutable]
ON [CentralDerivativeJobs]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        LEFT JOIN deleted AS d ON d.[Id] = i.[Id]
        INNER JOIN [CentralProcessingGraphExecutions] AS execution WITH (UPDLOCK, HOLDLOCK)
            ON execution.[Id] = i.[GraphExecutionId]
        WHERE d.[Id] IS NULL AND execution.[ExpandedAtUtc] IS NOT NULL)
        THROW 51000, 'Derivative graph jobs cannot be added after expansion is sealed.', 1;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        LEFT JOIN inserted AS i ON i.[Id] = d.[Id]
        WHERE (d.[GraphExecutionId] IS NOT NULL OR i.[GraphExecutionId] IS NOT NULL)
          AND (i.[Id] IS NULL
               OR i.[SourceCentralArtifactId] <> d.[SourceCentralArtifactId]
               OR i.[TargetRole] <> d.[TargetRole]
               OR i.[TargetRecipeVersion] <> d.[TargetRecipeVersion]
               OR i.[TargetVariant] <> d.[TargetVariant]
               OR i.[RecipeName] <> d.[RecipeName]
               OR i.[RecipeOptionsJson] <> d.[RecipeOptionsJson]
               OR i.[InputSelectorJson] <> d.[InputSelectorJson]
               OR i.[RequestedRecipeIdentitySha256] <> d.[RequestedRecipeIdentitySha256]
               OR i.[ExpectedRecipeIdentitySha256] <> d.[ExpectedRecipeIdentitySha256]
               OR i.[RequestIdentitySha256] <> d.[RequestIdentitySha256]
               OR ISNULL(i.[TraceParent], '') <> ISNULL(d.[TraceParent], '')
               OR ISNULL(i.[TraceState], '') <> ISNULL(d.[TraceState], '')
               OR ISNULL(i.[GraphExecutionId], '00000000-0000-0000-0000-000000000000') <>
                   ISNULL(d.[GraphExecutionId], '00000000-0000-0000-0000-000000000000')
               OR ISNULL(i.[GraphNodeId], N'') <> ISNULL(d.[GraphNodeId], N'')
               OR ISNULL(i.[GraphNodeOrdinal], -1) <> ISNULL(d.[GraphNodeOrdinal], -1)
               OR ISNULL(i.[SharedNodePlanIdentitySha256], '') <> ISNULL(d.[SharedNodePlanIdentitySha256], '')
               OR ISNULL(i.[FrozenNodePlanJson], N'') <> ISNULL(d.[FrozenNodePlanJson], N'')
               OR ISNULL(i.[GraphFailurePolicy], N'') <> ISNULL(d.[GraphFailurePolicy], N'')
               OR ISNULL(i.[WaitKind], N'') <> ISNULL(d.[WaitKind], N'')
               OR ISNULL(i.[ResolutionDeadlineUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00')) <>
                  ISNULL(d.[ResolutionDeadlineUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00'))
               OR ISNULL(i.[ResolutionStartedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00')) <>
                  ISNULL(d.[ResolutionStartedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00'))
                OR ISNULL(i.[MissingInputOutcome], N'') <> ISNULL(d.[MissingInputOutcome], N'')
                OR ISNULL(i.[MinimumInputCount], -1) <> ISNULL(d.[MinimumInputCount], -1)
                OR ISNULL(i.[PredecessorJobId], '00000000-0000-0000-0000-000000000000') <>
                   ISNULL(d.[PredecessorJobId], '00000000-0000-0000-0000-000000000000')
                OR i.[CreatedAtUtc] <> d.[CreatedAtUtc]))
        THROW 51000, 'Derivative graph executable identity is immutable.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN deleted AS d ON d.[Id] = i.[Id]
        WHERE d.[GraphExecutionId] IS NOT NULL
          AND (ISNULL(i.[InputSetIdentitySha256], '') <> ISNULL(d.[InputSetIdentitySha256], '')
               AND NOT (d.[InputSetIdentitySha256] IS NULL AND i.[InputSetIdentitySha256] IS NOT NULL)
               OR ISNULL(i.[ResolutionCompletedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00')) <>
                  ISNULL(d.[ResolutionCompletedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00'))
               AND NOT (d.[ResolutionCompletedAtUtc] IS NULL AND i.[ResolutionCompletedAtUtc] IS NOT NULL)))
        THROW 51000, 'Derivative graph resolution identity may be recorded exactly once.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN deleted AS d ON d.[Id] = i.[Id]
        INNER JOIN [CentralProcessingGraphExecutions] AS execution ON execution.[Id] = d.[GraphExecutionId]
        WHERE execution.[ExpandedAtUtc] IS NULL)
        THROW 51000, 'Derivative graph jobs cannot advance before expansion is sealed.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN [CentralProcessingGraphExecutions] AS execution ON execution.[Id] = i.[GraphExecutionId]
        WHERE execution.[Status] IN
                (N'Completed', N'CompletedWithOptionalFailures', N'Failed', N'Canceled', N'Superseded')
          AND i.[Status] NOT IN
                (N'Completed', N'TerminalFailure', N'Canceled', N'Skipped', N'Quarantined', N'Superseded'))
        THROW 51000, 'Terminal processing graph executions require terminal node outcomes.', 1;
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

-- TR_CentralProcessingGraphExecutions_IdentityImmutable ON CentralProcessingGraphExecutions
CREATE TRIGGER [TR_CentralProcessingGraphExecutions_IdentityImmutable]
ON [CentralProcessingGraphExecutions]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        LEFT JOIN inserted AS i ON i.[Id] = d.[Id]
        WHERE i.[Id] IS NULL
           OR i.[ExecutionClass] <> d.[ExecutionClass]
           OR i.[RequestIdentitySha256] <> d.[RequestIdentitySha256]
           OR i.[RevisionId] <> d.[RevisionId]
           OR ISNULL(i.[AssignmentId], '00000000-0000-0000-0000-000000000000') <>
              ISNULL(d.[AssignmentId], '00000000-0000-0000-0000-000000000000')
           OR i.[DefinitionIdentitySha256] <> d.[DefinitionIdentitySha256]
           OR i.[FrozenDefinitionJson] <> d.[FrozenDefinitionJson]
           OR i.[CentralPlanIdentitySha256] <> d.[CentralPlanIdentitySha256]
           OR i.[FrozenCentralPlanJson] <> d.[FrozenCentralPlanJson]
           OR i.[ExpectedSourceCount] <> d.[ExpectedSourceCount]
           OR i.[ExpectedNodeCount] <> d.[ExpectedNodeCount]
           OR i.[ExpectedDependencyCount] <> d.[ExpectedDependencyCount]
           OR i.[ExpectedOutputCount] <> d.[ExpectedOutputCount]
           OR i.[ObservatoryId] <> d.[ObservatoryId]
           OR i.[LogicalCameraId] <> d.[LogicalCameraId]
           OR i.[LogicalCameraInstallationId] <> d.[LogicalCameraInstallationId]
           OR i.[InstallationPublicId] <> d.[InstallationPublicId]
           OR i.[AnchorSourceCentralArtifactId] <> d.[AnchorSourceCentralArtifactId]
           OR i.[AnchorSourceArtifactId] <> d.[AnchorSourceArtifactId]
           OR i.[AnchorSourceChecksumSha256] <> d.[AnchorSourceChecksumSha256]
           OR i.[Trigger] <> d.[Trigger]
           OR i.[ActorId] <> d.[ActorId]
           OR i.[IdempotencyKey] <> d.[IdempotencyKey]
           OR i.[ReasonCode] <> d.[ReasonCode]
           OR ISNULL(i.[PredecessorExecutionId], '00000000-0000-0000-0000-000000000000') <>
              ISNULL(d.[PredecessorExecutionId], '00000000-0000-0000-0000-000000000000')
           OR i.[CreatedAtUtc] <> d.[CreatedAtUtc])
        THROW 51000, 'Processing graph execution identity is immutable.', 1;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        INNER JOIN inserted AS i ON i.[Id] = d.[Id]
        WHERE ISNULL(i.[ExpandedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00')) <>
              ISNULL(d.[ExpandedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00'))
          AND NOT (d.[ExpandedAtUtc] IS NULL AND i.[ExpandedAtUtc] IS NOT NULL))
        THROW 51000, 'Processing graph expansion may be sealed exactly once.', 1;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        INNER JOIN inserted AS i ON i.[Id] = d.[Id]
        WHERE i.[UpdatedAtUtc] < d.[UpdatedAtUtc]
           OR ISNULL(i.[StartedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00')) <>
              ISNULL(d.[StartedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00'))
              AND NOT (d.[StartedAtUtc] IS NULL AND i.[StartedAtUtc] IS NOT NULL)
           OR ISNULL(i.[CancellationRequestedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00')) <>
              ISNULL(d.[CancellationRequestedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00'))
              AND NOT (d.[CancellationRequestedAtUtc] IS NULL AND i.[CancellationRequestedAtUtc] IS NOT NULL)
            OR ISNULL(i.[CompletedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00')) <>
               ISNULL(d.[CompletedAtUtc], CONVERT(datetimeoffset, '0001-01-01T00:00:00+00:00'))
               AND NOT (d.[CompletedAtUtc] IS NULL AND i.[CompletedAtUtc] IS NOT NULL))
        THROW 51000, 'Processing graph execution timestamps are append-only.', 1;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        INNER JOIN inserted AS i ON i.[Id] = d.[Id]
        WHERE i.[Status] <> d.[Status]
          AND NOT (
              d.[Status] = N'Pending' AND i.[Status] IN (N'Running', N'CancelRequested', N'Failed', N'Superseded')
              OR d.[Status] = N'Running' AND i.[Status] IN
                  (N'Completed', N'CompletedWithOptionalFailures', N'Failed', N'CancelRequested', N'Superseded')
              OR d.[Status] = N'CancelRequested' AND i.[Status] IN (N'Canceled', N'Failed', N'Superseded')))
        THROW 51000, 'Processing graph execution status transition is invalid.', 1;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        INNER JOIN inserted AS i ON i.[Id] = d.[Id]
        WHERE d.[ExpandedAtUtc] IS NULL AND i.[ExpandedAtUtc] IS NOT NULL
          AND ((SELECT COUNT_BIG(*) FROM [CentralProcessingGraphExecutionSources] AS source
                WHERE source.[ExecutionId] = i.[Id]) <> i.[ExpectedSourceCount]
               OR (SELECT COUNT_BIG(*) FROM [CentralDerivativeJobs] AS job
                   WHERE job.[GraphExecutionId] = i.[Id]) <> i.[ExpectedNodeCount]
               OR (SELECT COUNT_BIG(*) FROM [CentralDerivativeJobDependencies] AS dependency
                   WHERE dependency.[ExecutionId] = i.[Id]) <> i.[ExpectedDependencyCount]
               OR (SELECT COUNT_BIG(*)
                   FROM [CentralDerivativeJobOutputs] AS output
                   INNER JOIN [CentralDerivativeJobs] AS job ON job.[Id] = output.[CentralDerivativeJobId]
                    WHERE job.[GraphExecutionId] = i.[Id]) <> i.[ExpectedOutputCount]))
        THROW 51000, 'Processing graph expansion does not match its frozen counts.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        WHERE i.[Status] <> N'Pending' AND i.[ExpandedAtUtc] IS NULL)
        THROW 51000, 'Processing graph execution cannot advance before expansion is sealed.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        WHERE i.[Status] IN (N'Completed', N'CompletedWithOptionalFailures', N'Failed', N'Canceled', N'Superseded')
          AND EXISTS (
              SELECT 1
              FROM [CentralDerivativeJobs] AS job
              WHERE job.[GraphExecutionId] = i.[Id]
                AND job.[Status] NOT IN
                    (N'Completed', N'TerminalFailure', N'Canceled', N'Skipped', N'Quarantined', N'Superseded')))
        THROW 51000, 'Processing graph execution cannot terminate while a node is nonterminal.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        WHERE i.[Status] = N'Completed'
          AND EXISTS (
              SELECT 1
              FROM [CentralDerivativeJobs] AS job
              WHERE job.[GraphExecutionId] = i.[Id] AND job.[Status] NOT IN (N'Completed', N'Skipped')))
        THROW 51000, 'Completed processing graphs cannot hide node failure.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        WHERE i.[Status] = N'CompletedWithOptionalFailures'
          AND (EXISTS (
                  SELECT 1
                  FROM [CentralDerivativeJobs] AS job
                  WHERE job.[GraphExecutionId] = i.[Id]
                    AND job.[GraphFailurePolicy] = N'Required'
                    AND job.[Status] NOT IN (N'Completed', N'Skipped'))
               OR NOT EXISTS (
                  SELECT 1
                  FROM [CentralDerivativeJobs] AS job
                  WHERE job.[GraphExecutionId] = i.[Id]
                    AND job.[GraphFailurePolicy] = N'Optional'
                    AND job.[Status] IN (N'TerminalFailure', N'Canceled', N'Quarantined', N'Superseded'))))
        THROW 51000, 'Optional-failure completion does not match node outcomes.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN [LogicalCameras] AS camera ON camera.[Id] = i.[LogicalCameraId]
        INNER JOIN [LogicalCameraInstallations] AS installation ON installation.[Id] = i.[LogicalCameraInstallationId]
        INNER JOIN [CentralArtifacts] AS artifact ON artifact.[Id] = i.[AnchorSourceCentralArtifactId]
        INNER JOIN [CentralFrames] AS frame ON frame.[Id] = artifact.[CentralFrameId]
        WHERE camera.[ObservatoryId] <> i.[ObservatoryId]
           OR installation.[LogicalCameraId] <> i.[LogicalCameraId]
           OR installation.[InstallationPublicId] <> i.[InstallationPublicId]
           OR frame.[ObservatoryId] <> i.[ObservatoryId]
           OR frame.[LogicalCameraInstallationId] IS NULL
           OR frame.[LogicalCameraInstallationId] <> i.[LogicalCameraInstallationId]
           OR artifact.[ArtifactId] <> i.[AnchorSourceArtifactId]
           OR artifact.[ChecksumSha256] <> i.[AnchorSourceChecksumSha256])
        THROW 51000, 'Processing graph execution source topology is inconsistent.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN [CentralProcessingGraphRevisions] AS revision ON revision.[Id] = i.[RevisionId]
        WHERE revision.[DefinitionIdentitySha256] <> i.[DefinitionIdentitySha256]
           OR revision.[DefinitionJson] <> i.[FrozenDefinitionJson]
           OR revision.[CentralPlanIdentitySha256] IS NULL
           OR revision.[CentralPlanIdentitySha256] <> i.[CentralPlanIdentitySha256]
           OR revision.[PublishedAtUtc] IS NULL
           OR revision.[PublishedAtUtc] > i.[CreatedAtUtc])
        THROW 51000, 'Processing graph execution revision provenance is inconsistent.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN [CentralProcessingGraphAssignments] AS assignment ON assignment.[Id] = i.[AssignmentId]
        WHERE assignment.[RevisionId] <> i.[RevisionId]
           OR assignment.[TargetHost] <> N'Central'
           OR i.[CreatedAtUtc] < assignment.[EffectiveFromUtc]
           OR assignment.[EffectiveUntilUtc] IS NOT NULL AND i.[CreatedAtUtc] >= assignment.[EffectiveUntilUtc]
           OR assignment.[Scope] = N'Observatory' AND assignment.[ObservatoryId] <> i.[ObservatoryId]
           OR assignment.[Scope] = N'LogicalCamera'
              AND (assignment.[ObservatoryId] <> i.[ObservatoryId]
                   OR assignment.[LogicalCameraId] <> i.[LogicalCameraId]))
        THROW 51000, 'Processing graph execution assignment provenance is inconsistent.', 1;
END
GO

-- TR_CentralProcessingGraphExecutionSources_Immutable ON CentralProcessingGraphExecutionSources
CREATE TRIGGER [TR_CentralProcessingGraphExecutionSources_Immutable]
ON [CentralProcessingGraphExecutionSources]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51000, 'Processing graph execution sources are immutable.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN [CentralProcessingGraphExecutions] AS execution WITH (UPDLOCK, HOLDLOCK)
            ON execution.[Id] = i.[ExecutionId]
        WHERE execution.[ExpandedAtUtc] IS NOT NULL)
        THROW 51000, 'Processing graph execution sources cannot be added after expansion is sealed.', 1;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN [CentralArtifacts] AS artifact ON artifact.[Id] = i.[CentralArtifactId]
        INNER JOIN [CentralFrames] AS frame ON frame.[Id] = artifact.[CentralFrameId]
        INNER JOIN [CentralProcessingGraphExecutions] AS execution ON execution.[Id] = i.[ExecutionId]
        WHERE artifact.[ArtifactId] <> i.[ArtifactId]
           OR artifact.[ChecksumSha256] <> i.[ArtifactChecksumSha256]
           OR artifact.[ByteLength] <> i.[ArtifactByteLength]
           OR frame.[ObservatoryId] <> execution.[ObservatoryId]
           OR frame.[LogicalCameraInstallationId] IS NULL
           OR frame.[LogicalCameraInstallationId] <> execution.[LogicalCameraInstallationId])
        THROW 51000, 'Processing graph execution source evidence is inconsistent.', 1;
END
GO

-- TR_CentralProcessingGraphAssignments_Immutable ON CentralProcessingGraphAssignments
CREATE TRIGGER [TR_CentralProcessingGraphAssignments_Immutable]
ON [CentralProcessingGraphAssignments]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    THROW 51000, 'Processing graph assignments are immutable.', 1;
END
GO

-- TR_CentralProcessingGraphDeliveryFacts_Immutable ON CentralProcessingGraphDeliveryFacts
CREATE TRIGGER [TR_CentralProcessingGraphDeliveryFacts_Immutable]
ON [CentralProcessingGraphDeliveryFacts]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    THROW 51000, 'Processing graph delivery facts are immutable.', 1;
END
GO

-- TR_CentralProcessingGraphDeliveryProposals_Immutable ON CentralProcessingGraphDeliveryProposals
CREATE TRIGGER [TR_CentralProcessingGraphDeliveryProposals_Immutable]
ON [CentralProcessingGraphDeliveryProposals]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    THROW 51000, 'Processing graph delivery proposals are immutable.', 1;
END
GO

-- TR_CentralProcessingGraphRevisions_Transitions ON CentralProcessingGraphRevisions
CREATE TRIGGER [TR_CentralProcessingGraphRevisions_Transitions]
ON [CentralProcessingGraphRevisions]
AFTER UPDATE, DELETE
AS
BEGIN
    IF (ROWCOUNT_BIG() = 0) RETURN;
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted AS d
        LEFT JOIN inserted AS i ON i.[Id] = d.[Id]
        WHERE i.[Id] IS NULL
           OR i.[Name] <> d.[Name]
           OR i.[Revision] <> d.[Revision]
           OR i.[DefinitionJson] <> d.[DefinitionJson]
           OR i.[DefinitionIdentitySha256] <> d.[DefinitionIdentitySha256]
           OR i.[PortablePlanIdentitySha256] <> d.[PortablePlanIdentitySha256]
           OR ISNULL(i.[EdgePlanIdentitySha256], '') <> ISNULL(d.[EdgePlanIdentitySha256], '')
           OR ISNULL(i.[CentralPlanIdentitySha256], '') <> ISNULL(d.[CentralPlanIdentitySha256], '')
           OR i.[CreatedAtUtc] <> d.[CreatedAtUtc]
           OR i.[CreatedByUserId] <> d.[CreatedByUserId]
           OR NOT (
                d.[PublishedAtUtc] IS NULL
                AND d.[RetiredAtUtc] IS NULL
                AND i.[PublishedAtUtc] IS NOT NULL
                AND i.[PublishedByUserId] IS NOT NULL
                AND i.[RetiredAtUtc] IS NULL
                AND i.[RetiredByUserId] IS NULL
                AND i.[RetirementReasonCode] IS NULL
                OR d.[PublishedAtUtc] IS NOT NULL
                AND d.[RetiredAtUtc] IS NULL
                AND i.[PublishedAtUtc] = d.[PublishedAtUtc]
                AND i.[PublishedByUserId] = d.[PublishedByUserId]
                AND i.[RetiredAtUtc] IS NOT NULL
                AND i.[RetiredByUserId] IS NOT NULL
                AND i.[RetirementReasonCode] IS NOT NULL))
    BEGIN
        THROW 51000, 'Processing graph revisions allow only publish and terminal retire transitions.', 1;
    END
END
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
