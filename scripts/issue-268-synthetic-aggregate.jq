def change_percent($baseline; $after):
  if $baseline == 0 then null else (($after / $baseline) - 1) * 100 end;

def valid_measurement:
  type == "number" and . >= 0;

def nearest_rank($values; $percentile):
  $values[((($values | length) * $percentile | ceil) - 1)];

def latency_valid($expectedSamples):
  (.Samples == $expectedSamples) and
  (.OrderedSamplesMilliseconds | length) == $expectedSamples and
  all(.OrderedSamplesMilliseconds[]; valid_measurement) and
  (.OrderedSamplesMilliseconds == (.OrderedSamplesMilliseconds | sort)) and
  (.MinimumMilliseconds == .OrderedSamplesMilliseconds[0]) and
  (.MedianMilliseconds == nearest_rank(.OrderedSamplesMilliseconds; 0.50)) and
  (.MaximumMilliseconds == .OrderedSamplesMilliseconds[-1]) and
  (if $expectedSamples >= 30 then
     .P95Milliseconds == nearest_rank(.OrderedSamplesMilliseconds; 0.95) and
     .P99Milliseconds == nearest_rank(.OrderedSamplesMilliseconds; 0.99)
   else
     .P95Milliseconds == null and .P99Milliseconds == null
   end) and
  (.OrderedSamplesSha256 | test("^[0-9A-F]{64}$"));

def high_metric($name; $baseline; $after; $limit):
  {
    name: $name,
    baseline: $baseline,
    after: $after,
    changePercent: change_percent($baseline; $after),
    worseLimitPercent: ($limit * 100),
    passed: (($baseline | valid_measurement) and ($after | valid_measurement) and
      ($after <= $baseline * (1 + $limit)))
  };

def low_metric($name; $baseline; $after; $limit):
  {
    name: $name,
    baseline: $baseline,
    after: $after,
    changePercent: change_percent($baseline; $after),
    worseLimitPercent: ($limit * 100),
    passed: (($baseline | valid_measurement) and $baseline > 0 and
      ($after | valid_measurement) and ($after >= $baseline * (1 - $limit)))
  };

def apply_regression_disposition:
  . as $metric |
  ({
    "c1-release-median-ms": {direction: "maximum", limit: 125},
    "c1-release-p95-ms": {direction: "maximum", limit: 230},
    "c1-release-p99-ms": {direction: "maximum", limit: 260},
    "c1-release-maximum-ms": {direction: "maximum", limit: 260},
    "c1-releases-per-second": {direction: "minimum", limit: 7},
    "c1-cpu-ms": {direction: "maximum", limit: 3600},
    "c1-allocated-bytes": {direction: "maximum", limit: 282000000},
    "c4-release-median-ms": {direction: "maximum", limit: 125},
    "c4-allocated-bytes": {direction: "maximum", limit: 245000000}
  }[$metric.name]) as $rule |
  if $metric.passed then
    $metric + {accepted: true, disposition: "comparative-budget-passed"}
  elif $rule == null then
    $metric + {accepted: false, disposition: "unaccepted-comparative-regression"}
  elif (($metric.baseline | valid_measurement) and ($metric.after | valid_measurement) and
        (($rule.direction == "maximum" and $metric.after <= $rule.limit) or
         ($rule.direction == "minimum" and $metric.after >= $rule.limit))) then
    $metric + {
      accepted: true,
      disposition: "operator-accepted-bounded-durable-correctness-cost",
      acceptedAbsoluteDirection: $rule.direction,
      acceptedAbsoluteLimit: $rule.limit,
      authority: "https://github.com/RoySalisbury/HVO.SkyMonitor/issues/268#issuecomment-5170885290"
    }
  else
    $metric + {
      accepted: false,
      disposition: "outside-operator-accepted-absolute-bound",
      acceptedAbsoluteDirection: $rule.direction,
      acceptedAbsoluteLimit: $rule.limit,
      authority: "https://github.com/RoySalisbury/HVO.SkyMonitor/issues/268#issuecomment-5170885290"
    }
  end;

def protocol_counts:
  {
    efSqlCommands: .EfSqlCommands,
    externalSqlDeadlockRetries: .ExternalSqlDeadlockRetries,
    primarySqlDeadlockRetries: .PrimarySqlDeadlockRetries,
    recoverySqlDeadlockRetries: .RecoverySqlDeadlockRetries,
    inspectionSqlDeadlockRetries: .InspectionSqlDeadlockRetries,
    releaseAttempts: .ReleaseAttempts,
    acceptedReleaseResponses: .AcceptedReleaseResponses,
    recoveryProcessorAttempts: .RecoveryProcessorAttempts,
    recoveryProcessorCompletions: .RecoveryProcessorCompletions,
    primaryEfSqlCommands: .PrimaryEfSqlCommands,
    recoveryEfSqlCommands: .RecoveryEfSqlCommands,
    transactionStartAttempts: .TransactionStartAttempts,
    transactionsStarted: .TransactionsStarted,
    transactionsCommitted: .TransactionsCommitted,
    transactionsRolledBack: .TransactionsRolledBack,
    transactionsFailed: .TransactionsFailed,
    primaryTransactionStartAttempts: .PrimaryTransactions.StartAttempts,
    primaryTransactionsStarted: .PrimaryTransactions.SuccessfullyStarted,
    primaryTransactionsCommitted: .PrimaryTransactions.Committed,
    primaryTransactionsRolledBack: .PrimaryTransactions.RolledBack,
    primaryTransactionsFailed: .PrimaryTransactions.Failed,
    recoveryTransactionStartAttempts: .RecoveryTransactions.StartAttempts,
    recoveryTransactionsStarted: .RecoveryTransactions.SuccessfullyStarted,
    recoveryTransactionsCommitted: .RecoveryTransactions.Committed,
    recoveryTransactionsRolledBack: .RecoveryTransactions.RolledBack,
    recoveryTransactionsFailed: .RecoveryTransactions.Failed,
    committedTransactionDurationSamples: .CommittedTransactionDuration.Samples,
    minioRequests: .MinioRequests,
    minioDeletes: .MinioDeletes,
    minioBucketHeadRequests: .MinioBucketHeadRequests,
    minioObjectHeadRequests: .MinioObjectHeadRequests,
    minioOtherMethodRequests: .MinioOtherMethodRequests,
    minioUniqueDeleteTargets: .MinioUniqueDeleteTargets,
    minioDuplicateDeleteRequests: .MinioDuplicateDeleteRequests,
    minioDeleteResponses: .MinioDeleteResponses,
    minioDeleteExceptions: .MinioDeleteExceptions,
    minioRequestEntityBytes: .MinioRequestEntityBytes,
    minioResponseEntityBytes: .MinioResponseEntityBytes,
    minioUnknownRequestEntityLengths: .MinioUnknownRequestEntityLengths,
    minioUnknownResponseEntityLengths: .MinioUnknownResponseEntityLengths,
    minioDeleteDurationSamples: .MinioDeleteDuration.Samples,
    getApplicationLockStarts: .ApplicationLockCommands.GetApplicationLockStarts,
    getApplicationLockCompletions: .ApplicationLockCommands.GetApplicationLockCompletions,
    getApplicationLockErrors: .ApplicationLockCommands.GetApplicationLockErrors,
    releaseApplicationLockStarts: .ApplicationLockCommands.ReleaseApplicationLockStarts,
    releaseApplicationLockCompletions: .ApplicationLockCommands.ReleaseApplicationLockCompletions,
    releaseApplicationLockErrors: .ApplicationLockCommands.ReleaseApplicationLockErrors,
    applicationLockTotalStarts: .ApplicationLockCommands.TotalStarts,
    applicationLockTotalCompletions: .ApplicationLockCommands.TotalCompletions,
    applicationLockTotalErrors: .ApplicationLockCommands.TotalErrors
  };

def count_delta($baseline; $after):
  reduce ($baseline | keys[]) as $key
    ({}; .[$key] = (($after[$key] // 0) - ($baseline[$key] // 0)));

def protocol_comparison($name; $baseline; $after; $releases):
  ($baseline | protocol_counts) as $before |
  ($after | protocol_counts) as $candidate |
  ($candidate.externalSqlDeadlockRetries) as $retries |
  ($candidate.primarySqlDeadlockRetries) as $primaryRetries |
  ($candidate.recoverySqlDeadlockRetries) as $recoveryRetries |
  ($candidate.inspectionSqlDeadlockRetries) as $inspectionRetries |
  ($candidate.releaseAttempts) as $releaseAttempts |
  ($candidate.recoveryProcessorAttempts) as $recoveryAttempts |
  ($candidate.recoveryProcessorCompletions) as $recoveryCompletions |
  ($releases * 8) as $minimumLocks |
  ($minimumLocks + ($retries * 8) + ($recoveryAttempts * 23)) as $maximumLocks |
  {
    name: $name,
    releases: $releases,
    baseline: $before,
    after: $candidate,
    delta: count_delta($before; $candidate),
    designAccounting: {
      expectedReleaseAttempts: ($releases + $retries),
      primaryEfSqlCommandRange: {minimum: $releaseAttempts, maximum: ($releaseAttempts * 170)},
      primaryTransactionStartRange: {minimum: $releases, maximum: ($releaseAttempts * 20)},
      recoveryEfSqlCommandRange: {minimum: $recoveryAttempts, maximum: ($recoveryAttempts * 170)},
      recoveryTransactionStartRange: {minimum: 0, maximum: ($recoveryAttempts * 20)},
      expectedApplicationLockStarts: {minimum: $minimumLocks, maximum: $maximumLocks},
      commandScope: "Exact observed primary ReleaseAsync and recovery ProcessNextAsync counts are partitioned. Transaction terminals reconcile per partition; attempts and commands are bounded by recorded release/retry/recovery operations rather than DELETE timing. HTTP requests partition by method and target class; DELETEs partition by unique/duplicate target and response/transport outcome without serializing keys. Unreleased application locks are bounded to unsuccessful recorded lock attempts.",
      passed: (
        ((["MinioBucketHeadRequests", "MinioObjectHeadRequests", "MinioOtherMethodRequests",
          "MinioUniqueDeleteTargets", "MinioDuplicateDeleteRequests", "MinioDeleteResponses",
          "MinioDeleteExceptions"] - ($after | keys) | length) == 0) and
        ($candidate | to_entries | all(.[]; (.value | type == "number" and . >= 0 and . == floor))) and
        ($retries <= ($releases * 9)) and
        ($retries == ($primaryRetries + $recoveryRetries + $inspectionRetries)) and
        ($releaseAttempts == ($releases + $retries)) and
        ($candidate.acceptedReleaseResponses <= $releases) and
        ($recoveryAttempts >= $candidate.acceptedReleaseResponses) and
        ($recoveryAttempts <= ($candidate.acceptedReleaseResponses * 100)) and
        ($recoveryCompletions <= $recoveryAttempts) and
        ($candidate.primaryEfSqlCommands >= $releaseAttempts) and
        ($candidate.primaryEfSqlCommands <= ($releaseAttempts * 170)) and
        ($candidate.primaryTransactionStartAttempts == $candidate.primaryTransactionsStarted) and
        ($candidate.primaryTransactionStartAttempts ==
          ($candidate.primaryTransactionsCommitted + $candidate.primaryTransactionsRolledBack +
            $candidate.primaryTransactionsFailed)) and
        ($candidate.primaryTransactionStartAttempts >= $releases) and
        ($candidate.primaryTransactionStartAttempts <= ($releaseAttempts * 20)) and
        ($candidate.primaryTransactionsRolledBack <= $candidate.acceptedReleaseResponses) and
        ($candidate.primaryTransactionsFailed == $primaryRetries) and
        ($candidate.recoveryEfSqlCommands >= $recoveryAttempts) and
        ($candidate.recoveryEfSqlCommands <= ($recoveryAttempts * 170)) and
        ($candidate.recoveryTransactionStartAttempts == $candidate.recoveryTransactionsStarted) and
        ($candidate.recoveryTransactionStartAttempts ==
          ($candidate.recoveryTransactionsCommitted + $candidate.recoveryTransactionsRolledBack +
            $candidate.recoveryTransactionsFailed)) and
        ($candidate.recoveryTransactionStartAttempts <= ($recoveryAttempts * 20)) and
        ($candidate.recoveryTransactionsFailed == $recoveryRetries) and
        ($candidate.efSqlCommands ==
          ($candidate.primaryEfSqlCommands + $candidate.recoveryEfSqlCommands)) and
        ($candidate.transactionStartAttempts ==
          ($candidate.primaryTransactionStartAttempts + $candidate.recoveryTransactionStartAttempts)) and
        ($candidate.transactionStartAttempts == $candidate.transactionsStarted) and
        ($candidate.transactionStartAttempts ==
          ($candidate.transactionsCommitted + $candidate.transactionsRolledBack + $candidate.transactionsFailed)) and
        ($candidate.transactionsCommitted ==
          ($candidate.primaryTransactionsCommitted + $candidate.recoveryTransactionsCommitted)) and
        ($candidate.transactionsRolledBack ==
          ($candidate.primaryTransactionsRolledBack + $candidate.recoveryTransactionsRolledBack)) and
        ($candidate.transactionsFailed ==
          ($candidate.primaryTransactionsFailed + $candidate.recoveryTransactionsFailed)) and
        ($candidate.committedTransactionDurationSamples == $candidate.transactionsCommitted) and
        ($candidate.minioRequests == ($candidate.minioDeletes + $candidate.minioBucketHeadRequests +
          $candidate.minioObjectHeadRequests + $candidate.minioOtherMethodRequests)) and
        ($candidate.minioOtherMethodRequests == 0) and
        ($candidate.minioUniqueDeleteTargets == ($releases * 5)) and
        ($candidate.minioDeletes ==
          ($candidate.minioUniqueDeleteTargets + $candidate.minioDuplicateDeleteRequests)) and
        ($candidate.minioBucketHeadRequests == $candidate.minioObjectHeadRequests) and
        ($candidate.minioDeletes ==
          ($candidate.minioDeleteResponses + $candidate.minioDeleteExceptions)) and
        ($candidate.minioDeleteExceptions == 0) and
        ($candidate.minioRequestEntityBytes == 0) and
        ($candidate.minioResponseEntityBytes == 0) and
        ($candidate.minioUnknownRequestEntityLengths == 0) and
        ($candidate.minioUnknownResponseEntityLengths == 0) and
        ($candidate.minioDeleteDurationSamples == $candidate.minioDeletes) and
        ($candidate.getApplicationLockStarts >= $minimumLocks) and
        ($candidate.getApplicationLockStarts <= $maximumLocks) and
        ($candidate.getApplicationLockStarts == $candidate.getApplicationLockCompletions) and
        ($candidate.releaseApplicationLockStarts <= $candidate.getApplicationLockStarts) and
        (($candidate.getApplicationLockStarts - $candidate.releaseApplicationLockStarts) <=
          ($candidate.acceptedReleaseResponses + ($recoveryAttempts * 16))) and
        ($candidate.releaseApplicationLockStarts == $candidate.releaseApplicationLockCompletions) and
        ($candidate.getApplicationLockErrors == 0) and
        ($candidate.releaseApplicationLockErrors == 0) and
        ($candidate.applicationLockTotalStarts ==
          ($candidate.getApplicationLockStarts + $candidate.releaseApplicationLockStarts)) and
        ($candidate.applicationLockTotalStarts == $candidate.applicationLockTotalCompletions) and
        ($candidate.applicationLockTotalErrors == 0)
      )
    }
  };

($baseline[0]) as $b |
($after[0]) as $a |
($w0[0].Evidence) as $fault |
($w3m[0].Evidence) as $plan |
($lanes[0]) as $lane |
($a.SteadyState | map(.Concurrency) == [1, 4]) as $steadyIdentities |
($a.DelayedDelete | map(.DeleteDelayMilliseconds) == [0, 250, 2000]) as $delayIdentities |
([range(0; 2) as $index |
  ($b.SteadyState[$index]) as $before |
  ($a.SteadyState[$index]) as $candidate |
  high_metric("c\($candidate.Concurrency)-release-median-ms";
    $before.ReleaseLatency.MedianMilliseconds; $candidate.ReleaseLatency.MedianMilliseconds; 0.20),
  high_metric("c\($candidate.Concurrency)-release-p95-ms";
    $before.ReleaseLatency.P95Milliseconds; $candidate.ReleaseLatency.P95Milliseconds; 0.20),
  high_metric("c\($candidate.Concurrency)-release-p99-ms";
    $before.ReleaseLatency.P99Milliseconds; $candidate.ReleaseLatency.P99Milliseconds; 0.50),
  high_metric("c\($candidate.Concurrency)-release-maximum-ms";
    $before.ReleaseLatency.MaximumMilliseconds; $candidate.ReleaseLatency.MaximumMilliseconds; 0.50),
  low_metric("c\($candidate.Concurrency)-releases-per-second";
    $before.ReleasesPerSecond; $candidate.ReleasesPerSecond; 0.20),
  high_metric("c\($candidate.Concurrency)-cpu-ms";
    $before.Resources.ProcessCpuMilliseconds; $candidate.Resources.ProcessCpuMilliseconds; 0.20),
  high_metric("c\($candidate.Concurrency)-allocated-bytes";
    $before.Resources.AllocatedBytesDelta; $candidate.Resources.AllocatedBytesDelta; 0.20),
  high_metric("c\($candidate.Concurrency)-rss-growth-bytes";
    ($before.Resources.RssPeakBytes - $before.Resources.RssStartBytes);
    ($candidate.Resources.RssPeakBytes - $candidate.Resources.RssStartBytes); 0.20)
] | map(apply_regression_disposition)) as $comparisons |
([range(0; 2) as $index |
  protocol_comparison(
    "steady-c\($a.SteadyState[$index].Concurrency)";
    $b.SteadyState[$index].Protocol;
    $a.SteadyState[$index].Protocol;
    $a.SteadyState[$index].MeasuredReleases)
] + [range(0; 3) as $index |
  ($a.DelayedDelete[$index].DeleteDelayMilliseconds) as $delay |
  protocol_comparison(
    "delay-\($delay)-natural";
    $b.DelayedDelete[$index].NaturalProtocol;
    $a.DelayedDelete[$index].NaturalProtocol;
    $a.DelayedDelete[$index].Releases),
  protocol_comparison(
    "delay-\($delay)-contention";
    $b.DelayedDelete[$index].ContentionProtocol;
    $a.DelayedDelete[$index].ContentionProtocol;
    1)
]) as $protocolComparisons |
([$a.DelayedDelete[] |
  select(.DeleteDelayMilliseconds >= 250) |
  {
    delayMilliseconds: .DeleteDelayMilliseconds,
    naturalTransactionOverlapMilliseconds: .NaturalSql.TransactionOverlapMilliseconds,
    contentionTransactionOverlapMilliseconds: .ContentionSql.TransactionOverlapMilliseconds,
    naturalObjectLockWindowMilliseconds: .NaturalSql.ObjectLockWindowMilliseconds,
    contentionObjectLockWindowMilliseconds: .ContentionSql.ObjectLockWindowMilliseconds,
    exactWriterOutcome: .ExactRowWriter.Outcome,
    exactWriterElapsedMilliseconds: .ExactRowWriter.ElapsedMilliseconds,
    exactAttributedBlockerSamples: .ContentionSql.ExactAttributedBlockerSamples,
    passed: (
      (.NaturalSql.TransactionOverlapMilliseconds == 0) and
      (.ContentionSql.TransactionOverlapMilliseconds == 0) and
      (.NaturalSql.ObjectLockWindowMilliseconds | valid_measurement) and
      (.ContentionSql.ObjectLockWindowMilliseconds | valid_measurement) and
      (.NaturalSql.ObjectLockWindowMilliseconds >= .DeleteDelayMilliseconds - 100) and
      (.ContentionSql.ObjectLockWindowMilliseconds >= .DeleteDelayMilliseconds - 100) and
      (.ContentionSql.ExactAttributedBlockerSamples == 0) and
      (.ExactRowWriter.Outcome == "completed") and
      (.ExactRowWriter.ElapsedMilliseconds | valid_measurement) and
      (.ExactRowWriter.ElapsedMilliseconds < 1000)
    )
  }
]) as $delays |
{
  schema: "hvo-issue-268-synthetic-summary-v1",
  issue: 268,
  revision: $revision,
  authenticatedInputs: {
    baselineEvidenceSha256: $baselineEvidenceSha256,
    afterEvidenceSha256: $afterEvidenceSha256,
    afterW0Sha256: $afterW0Sha256,
    afterW3mSha256: $afterW3mSha256,
    retainedLanesSummarySha256: $lanesSummarySha256
  },
  issue250: {
    baseline: {
      sourceHead: $b.Source.Head,
      manifestSha256: "0390D4A70CDF18A5F1714EAD17D5D1790B88A9BA6D7388D1C067B29913746F14",
      authenticatedHistoricalWorkloadSha256: $b.CompatibilityWorkloadSha256,
      semanticWorkloadSha256: $a.BaselineBinding.BaselineSemanticWorkloadSha256,
      protocolSha256: $b.ProtocolSha256,
      compatibilityProtocolSha256: $b.CompatibilityProtocolSha256,
      environmentSha256: $b.EnvironmentSha256
    },
    after: {
      sourceHead: $a.Source.Head,
      productionRevision: $a.ProductionRevision,
      baselineBinding: $a.BaselineBinding,
      workloadSha256: $a.CompatibilityWorkloadSha256,
      protocolSha256: $a.ProtocolSha256,
      compatibilityProtocolSha256: $a.CompatibilityProtocolSha256,
      environmentSha256: $a.EnvironmentSha256
    },
    comparisons: $comparisons,
    protocolComparisons: $protocolComparisons,
    delayedDelete: $delays,
    correctness: $a.Correctness,
    faultRuntime: {
      objectAlreadyAbsentCompletedIdempotently: $fault.ObjectAlreadyAbsentCompletedIdempotently,
      retryableFailurePersisted: $fault.BeforeDeleteFailureLeftRetryablePendingParentAndItem,
      falseParentCompletionObserved: $fault.FalseParentCompletionObserved,
      freshProcessorRecovered: $fault.FreshProcessorProcessNextRecoveredAndCompleted,
      health: $fault.Health
    },
    w3m: {
      logicalReads: $plan.LogicalReads,
      baselineLogicalReads: $baselineW3mLogicalReads,
      baselineComparison: "N/A: baseline selects a parent only; candidate also seeks the first pending item to enforce durable lease/retry eligibility.",
      candidateAbsoluteLogicalReadLimit: 32,
      disposition: "operator-accepted-changed-semantics-candidate-baseline",
      authority: "https://github.com/RoySalisbury/HVO.SkyMonitor/issues/268#issuecomment-5170885290",
      indexUsed: $plan.IndexUsed,
      indexes: $plan.Indexes,
      operators: $plan.Operators,
      boundedSelection: $plan.BoundedSelection,
      metadataOnly: $plan.MetadataOnly,
      selectedOldest: $plan.ActualProcessorSelectedOldest,
      completedOldest: $plan.ActualProcessorCompletedOldest,
      finalPendingParents: $plan.FinalPendingParents,
      finalPendingItems: $plan.FinalPendingItems,
      sentinelVerified: ($plan.SentinelPreReleaseVerificationSucceeded and $plan.SentinelPostReleaseAbsent),
      logicalReadsPassed: ($plan.LogicalReads <= 32)
    }
  },
  issue257: {
    revision: $lane.revision,
    profileSha256: $lane.identities.profileSha256,
    rawIngressSchema: $lane.identities.rawIngressSchema,
    baselineComparison: $lane.baseline.comparison,
    blockedLatencyDisposition: $lane.gates.blockedLatencyCausalDisposition,
    passed: $lane.result.passed
  },
  gates: {
    authenticatedInputs: (
      ($baseline | length) == 1 and ($after | length) == 1 and
      ($w0 | length) == 1 and ($w3m | length) == 1 and ($lanes | length) == 1 and
      ($baselineEvidenceSha256 | test("^[0-9A-F]{64}$")) and
      ($afterEvidenceSha256 | test("^[0-9A-F]{64}$")) and
      ($afterW0Sha256 | test("^[0-9A-F]{64}$")) and
      ($afterW3mSha256 | test("^[0-9A-F]{64}$")) and
      ($lanesSummarySha256 | test("^[0-9A-F]{64}$"))
    ),
    cardinality: (
      $steadyIdentities and $delayIdentities and
      ($b.SteadyState | map(.Concurrency) == [1, 4]) and
      ($b.DelayedDelete | map(.DeleteDelayMilliseconds) == [0, 250, 2000]) and
      all($a.SteadyState[]; .Warmups == 5 and .MeasuredReleases == 30 and
        (.ReleaseLatency | latency_valid(30)) and (.DeleteItemLatency | latency_valid(150)) and
        (.Protocol as $protocol |
          ($protocol.CommittedTransactionDuration | latency_valid($protocol.TransactionsCommitted)) and
          ($protocol.MinioDeleteDuration | latency_valid($protocol.MinioDeletes))) and
        .Correctness.Releases == 30) and
      all($a.DelayedDelete[]; .Releases == 4 and (.ReleaseLatency | latency_valid(4)) and
        (.NaturalProtocol as $natural |
          ($natural.CommittedTransactionDuration | latency_valid($natural.TransactionsCommitted)) and
          ($natural.MinioDeleteDuration | latency_valid($natural.MinioDeletes))) and
        (.ContentionProtocol as $contention |
          ($contention.CommittedTransactionDuration | latency_valid($contention.TransactionsCommitted)) and
          ($contention.MinioDeleteDuration | latency_valid($contention.MinioDeletes))) and
        .NaturalCorrectness.Releases == 4 and .ContentionCorrectness.Releases == 1) and
      ($delays | map(.delayMilliseconds) == [250, 2000]) and
      ($comparisons | length == 16) and ($protocolComparisons | length == 8)
    ),
    identities: (
      ($b.Schema == "hvo-issue-250-central-transient-payload-release-evidence-v3") and
      ($a.Schema == "hvo-issue-250-central-transient-payload-release-evidence-v4") and
      ($a.Issue == 250) and ($a.Phase == "after") and
      ($a.Source.Head == $revision) and
      ($a.ProductionRevision == $revision) and
      ($a.Source.Dirty == false) and
      ($a.BaselineBinding.Status == "reviewed-baseline-manifest-validated") and
      ($a.BaselineBinding.BaselineManifestSha256 == "0390D4A70CDF18A5F1714EAD17D5D1790B88A9BA6D7388D1C067B29913746F14") and
      ($a.BaselineBinding.BaselineSemanticWorkloadSha256 == "F02F56D059143F6B1AC37AEAD5D027F438AE74BCB876AC7666E575320A04D02B") and
      ($a.CompatibilityWorkloadSha256 == $a.BaselineBinding.BaselineSemanticWorkloadSha256) and
      ($a.BaselineBinding.CurrentSemanticWorkloadSha256 == $a.BaselineBinding.BaselineSemanticWorkloadSha256) and
      ($a.CompatibilityProtocolSha256 == $b.CompatibilityProtocolSha256) and
      ($a.ProtocolSha256 == "CFA0132DE0CFAA0B205D5A4A3669BF8E1FD8042E25E8A21B7B70025C82ED00FA") and
      ($a.EnvironmentSha256 == $b.EnvironmentSha256) and
      ($b.Environment.GcDynamicAdaptationMode == 0) and
      ($a.Environment.GcDynamicAdaptationMode == 0) and
      ($a.SchemaCapabilities.CandidateHarnessImplemented == true) and
      ($a.SchemaCapabilities.RequiredAfterFaultHooks == [
        "reservation-committed-before-delete",
        "process-termination-restart",
        "delete-completed-before-finalize",
        "stale-rowversion-generation",
        "late-hold",
        "final-item-before-parent-completion",
        "bounded-retry-no-duplicate-harm"
      ])
    ),
    issue250Correctness: (
      all($a.SteadyState[].Protocol, $a.DelayedDelete[].NaturalProtocol;
        .AcceptedReleaseResponses == 0 and .RecoveryProcessorAttempts == 0 and
        .RecoveryProcessorCompletions == 0 and .MinioDuplicateDeleteRequests == 0 and
        .MinioBucketHeadRequests == 0 and .MinioObjectHeadRequests == 0) and
      ($a.Correctness | keys | sort) == ([
        "BaselineHeldNormalization",
        "ExactPreReleaseLengthAndSha256ForAllSeven",
        "ExactReplay",
        "FiveAbsentTwoPreservedPerRelease",
        "ImmutableEventReviewAuditStable",
        "NoUnresolvedReleaseBacklog",
        "ParentItemOutcomesAndOrdinalsExact",
        "SqlSourceAndIntentStatesExact",
        "W3MProcessor"
      ] | sort) and
      all($a.Correctness | to_entries[] | select(.value | type == "boolean"); .value) and
      ($a.Correctness | to_entries | map(select(.value | type == "boolean")) | length == 7)
    ),
    protocolAccounting: all($protocolComparisons[]; .designAccounting.passed),
    delayedDelete: (($delays | length) == 2 and all($delays[]; .passed)),
    faultRuntime: (
      $fault.ObjectAlreadyAbsentCompletedIdempotently and
      $fault.BeforeDeleteFailureLeftRetryablePendingParentAndItem and
      ($fault.FalseParentCompletionObserved == false) and
      $fault.FreshProcessorProcessNextRecoveredAndCompleted and
      ($fault.FutureOnlyBoundaries == "Candidate fault stages and schema are authenticated by preflight. This measured W0 exercises absent-object idempotency, retryable DELETE failure, prior-ordinal restart, recovery, and health transitions; remaining issue #250 fault stages stay in the merged functional suite.") and
      ($fault.Health.FreshPending.Status == "Healthy") and
      ($fault.Health.StalePending.Status == "Degraded") and
      ($fault.Health.Drained.Status == "Healthy")
    ),
    w3m: (
      ($plan.LogicalReads | valid_measurement and . == floor) and
      ($baselineW3mLogicalReads | valid_measurement and . == floor) and
      $plan.IndexUsed and $plan.BoundedSelection and $plan.MetadataOnly and
      (($plan.Indexes | sort) == [
        "IX_CentralTransientPayloadReleases_State_CreatedUtc_ReleaseId",
        "PK_CentralTransientPayloadReleaseItems"
      ]) and
      ($plan.Operators | index("Index Seek/Index Seek") != null) and
      ($plan.Operators | index("Clustered Index Seek/Clustered Index Seek") != null) and
      (($plan.CandidateItemDueWorkPlan | fromjson) as $duePlan |
        $duePlan.ExpectedLoadNextIndex == "PK_CentralTransientPayloadReleaseItems" and
        ($duePlan.ClaimPlanIndexes | sort) == [
          "IX_CentralTransientPayloadReleases_State_CreatedUtc_ReleaseId",
          "PK_CentralTransientPayloadReleaseItems"
        ]) and
      $plan.ActualProcessorSelectedOldest and $plan.ActualProcessorCompletedOldest and
      ($plan.FinalPendingParents == 0) and ($plan.FinalPendingItems == 0) and
      $plan.SentinelPreReleaseVerificationSucceeded and $plan.SentinelPostReleaseAbsent and
      ($plan.LogicalReads <= 32)
    ),
    regressions: (($comparisons | length) == 16 and all($comparisons[]; .accepted)),
    issue257: (
      ($lane.revision == "4cb0cdcef7f1d9df2e0923ee9b993454e0252843") and
      ($lane.identities.profileSha256 == "227FB3C0484AB5BBAFC4CA3674EA0D68B473315EBDD63F547CB2851D0A00F499") and
      ($lane.identities.rawIngressSchema == 10) and
      ($lane.baseline.comparison == "N/A") and $lane.result.passed
    )
  }
} |
.result = {
  passed: all(.gates[]; . == true),
  scope: "Authenticated issue #250 baseline/after comparison plus retained issue #257 current-profile lane evidence."
}
