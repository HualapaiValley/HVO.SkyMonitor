def stats:
    sort as $values |
    {minimum: $values[0], median: $values[2], maximum: $values[4],
     maximumToMinimumRatio: (if $values[0] > 0 then $values[4] / $values[0] else null end)};
($lanes[0]) as $lane |
($controls[0]) as $control |
($runtime[0]) as $signals |
{
    schema: "hvo-issue-257-five-trial-summary-v1",
    issue: 257,
    revision: $revision,
    trialCount: 5,
    trialOrder: ["control-UB", "BU-control", "control-UB", "BU-control", "control-UB"],
    baseline: {
        comparison: "N/A",
        reason: "Issue #95 used a different profile identity and raw-ingress schema."
    },
    pairedLatencyDisposition: {
        causalResult: "N/A: mixed order-stratified results from a 3/2 scenario ordering are insufficient for causal blocked-vs-unblocked attribution.",
        sampleUnit: "five paired per-trial nearest-rank p95 values, reported without a causal pass or improvement claim",
        ingress: {
            unblockedMedianMilliseconds: ($lane | map(.blockedComparison.unblocked.acceptP95Milliseconds) | sort | .[2]),
            blockedMedianMilliseconds: ($lane | map(.blockedComparison.blocked.acceptP95Milliseconds) | sort | .[2]),
            pairedBlockedMinusUnblockedMilliseconds: ($lane | map(.blockedComparison.blocked.acceptP95Milliseconds - .blockedComparison.unblocked.acceptP95Milliseconds) | stats),
            unblockedThenBlockedDeltasMilliseconds: ($lane | map(select(.blockedComparison.executionOrder[0] == "unblocked") | .blockedComparison.blocked.acceptP95Milliseconds - .blockedComparison.unblocked.acceptP95Milliseconds)),
            blockedThenUnblockedDeltasMilliseconds: ($lane | map(select(.blockedComparison.executionOrder[0] == "blocked") | .blockedComparison.blocked.acceptP95Milliseconds - .blockedComparison.unblocked.acceptP95Milliseconds))
        },
        standardAck: {
            unblockedMedianMilliseconds: ($lane | map(.blockedComparison.unblocked.standardAckP95Milliseconds) | sort | .[2]),
            blockedMedianMilliseconds: ($lane | map(.blockedComparison.blocked.standardAckP95Milliseconds) | sort | .[2]),
            pairedBlockedMinusUnblockedMilliseconds: ($lane | map(.blockedComparison.blocked.standardAckP95Milliseconds - .blockedComparison.unblocked.standardAckP95Milliseconds) | stats)
        }
    },
    identities: {
        profileSha256: ($lane | map(.workload.profileSha256) | unique | if length == 1 then .[0] else error("profile changed") end),
        rawIngressSchema: ($lane | map(.w3M.userVersion) | unique | if length == 1 then .[0] else error("schema changed") end),
        testAssemblySha256: ($lane | map(.provenance.testAssembly.sha256) | unique | if length == 1 then .[0] else error("test assembly changed") end),
        productionAssemblySha256: ($lane | map(.provenance.productionAssembly.sha256) | unique | if length == 1 then .[0] else error("production assembly changed") end)
    },
    metrics: {
        w2IngressMedianMilliseconds: ($lane | map(.w2.medianMilliseconds) | stats),
        w2IngressP95Milliseconds: ($lane | map(.w2.p95Milliseconds) | stats),
        w2IngressCapturesPerSecond: ($lane | map(.w2.throughputPerSecond) | stats),
        w2CpuMilliseconds: ($lane | map(.w2.cpuMilliseconds) | stats),
        w2AllocatedBytes: ($lane | map(.w2.allocatedBytes) | stats),
        w2RssDeltaBytes: ($lane | map(.w2.rssAfterBytes - .w2.rssBeforeBytes) | stats),
        w3MInitializationMilliseconds: ($lane | map(.w3M.initializationMilliseconds) | stats),
        w3MInitializationCpuMilliseconds: ($lane | map(.w3M.initializationCpuMilliseconds) | stats),
        w3MInitializationAllocatedBytes: ($lane | map(.w3M.initializationAllocatedBytes) | stats),
        w3MInitializationRssDeltaBytes: ($lane | map(.w3M.rssAfterInitializationBytes - .w3M.rssBeforeInitializationBytes) | stats),
        w3MCanonicalInsertionCpuMilliseconds: ($lane | map(.w3M.canonicalInsertionCpuMilliseconds) | stats),
        w3MCanonicalInsertionAllocatedBytes: ($lane | map(.w3M.canonicalInsertionAllocatedBytes) | stats),
        w3MCanonicalInsertionRssDeltaBytes: ($lane | map(.w3M.rssAfterCanonicalInsertionBytes - .w3M.rssBeforeCanonicalInsertionBytes) | stats),
        w3MRestartAllocatedBytes: ($lane | map(.w3M.restartAllocatedBytes) | stats),
        w3MRestartDiscoveryMedianMilliseconds: ($lane | map(.w3M.restartDiscoveryMedianMilliseconds) | stats),
        syntheticIndexedTraversalMilliseconds: ($lane | map(.w3M.syntheticIndexedTraversalMilliseconds) | stats),
        unblockedProductionClaimP95Milliseconds: ($lane | map(.blockedComparison.unblocked.standardClaimP95Milliseconds) | stats),
        blockedProductionClaimP95Milliseconds: ($lane | map(.blockedComparison.blocked.standardClaimP95Milliseconds) | stats),
        unblockedProductionClaimMaximumMilliseconds: ($lane | map(.blockedComparison.unblocked.standardClaimMaximumMilliseconds) | stats),
        blockedProductionClaimMaximumMilliseconds: ($lane | map(.blockedComparison.blocked.standardClaimMaximumMilliseconds) | stats),
        unblockedStandardAckP95Milliseconds: ($lane | map(.blockedComparison.unblocked.standardAckP95Milliseconds) | stats),
        blockedStandardAckP95Milliseconds: ($lane | map(.blockedComparison.blocked.standardAckP95Milliseconds) | stats),
        unblockedIngressP95Milliseconds: ($lane | map(.blockedComparison.unblocked.acceptP95Milliseconds) | stats),
        blockedIngressP95Milliseconds: ($lane | map(.blockedComparison.blocked.acceptP95Milliseconds) | stats),
        unblockedCommitCpuMilliseconds: ($lane | map(.blockedComparison.unblocked.commitCpuMilliseconds) | stats),
        blockedCommitCpuMilliseconds: ($lane | map(.blockedComparison.blocked.commitCpuMilliseconds) | stats),
        unblockedRssMedianGrowthBytes: ($lane | map(.blockedComparison.unblocked.rssMedianGrowthBytes) | stats),
        blockedRssMedianGrowthBytes: ($lane | map(.blockedComparison.blocked.rssMedianGrowthBytes) | stats),
        requiredDrainCapturesPerSecond: ($lane | map(.blockedComparison.blocked.requiredDrainCapturesPerSecond) | stats),
        optionalRecoveryCapturesPerSecond: ($lane | map(.blockedComparison.blocked.optionalRecoveryCapturesPerSecond) | stats),
        blockedRestartDiscoveryMedianMilliseconds: ($lane | map(.blockedComparison.blocked.restartDiscoveryMedianMilliseconds) | stats),
        w2SparseMeterWallP95Milliseconds: ($control | map(.workloads[] | select(.id == "W2") | .candidate.wallP95Milliseconds) | stats),
        w2SparseMeterCpuP95Milliseconds: ($control | map(.workloads[] | select(.id == "W2") | .candidate.cpuP95Milliseconds) | stats),
        w2SparseMeterAllocatedBytes: ($control | map(.workloads[] | select(.id == "W2") | .candidate.allocatedBytesTotal) | stats)
    },
    gates: {
        perTrialCorrectness: (([$lane[] |
            .blockedComparison.unblocked.finalPendingLaneRows == 0 and
            .blockedComparison.blocked.finalPendingLaneRows == 0 and
            .blockedComparison.unblocked.finalQuarantinedLaneRows == 0 and
            .blockedComparison.blocked.finalQuarantinedLaneRows == 0 and
            .blockedComparison.unblocked.finalRetentionHeldRows == 0 and
            .blockedComparison.blocked.finalRetentionHeldRows == 0 and
            .blockedComparison.unblocked.finalCompletedLaneRows == 300 and
            .blockedComparison.blocked.finalCompletedLaneRows == 300 and
            .blockedComparison.blocked.optionalPendingOrLeasedBeforeRelease == 100 and
            .blockedComparison.blocked.requiredCompletedRowsBeforeOptionalRelease == 200 and
            .blockedComparison.unblocked.gracefulStop == true and
            .blockedComparison.blocked.gracefulStop == true and
            .blockedComparison.blocked.requiredDrainCapturesPerSecond >= 1 and
            .blockedComparison.blocked.optionalRecoveryCapturesPerSecond >= 1 and
            .blockedComparison.unblocked.rssMedianGrowthBytes <= 67108864 and
            .blockedComparison.blocked.rssMedianGrowthBytes <= 67108864] | all) and
            ([$control[].workloads[].productionScenarios[] |
                .finalAcquisitionBacklog == 0 and
                .postReleaseOptionalBacklog == 0 and
                .postReleaseCentralBacklog == 0] | all)),
        runtimeSignals: ([$signals[] |
            .issue == 257 and .revision == $revision and
            .finalDurableState.blockedPendingRows == 0 and
            .finalDurableState.blockedQuarantinedRows == 0 and
            .finalDurableState.forcedShutdownObserved == false and
            ([.observedLogEvents[].eventId] | all(. < 2059 or . > 2062))] | all),
        rssGrowthWithinDeclaredAbsoluteBudget: ([$lane[] |
            .blockedComparison.unblocked.rssMedianGrowthBytes <= 67108864 and
            .blockedComparison.blocked.rssMedianGrowthBytes <= 67108864] | all),
        blockedLatencyCausalDisposition: "N/A: order-confounded; functional optional-lane isolation remains a hard gate.",
        candidateOnlyTrialRanges: "Reported as min/median/max and maximumToMinimumRatio; no equivalent historical baseline exists, so range ratios are diagnostic rather than regression gates."
    }
} |
.result = {passed: (.gates.perTrialCorrectness and .gates.runtimeSignals and .gates.rssGrowthWithinDeclaredAbsoluteBudget),
    scope: "functional isolation, correctness, durability, runtime signals, and RSS bounds; latency causality is N/A"}
