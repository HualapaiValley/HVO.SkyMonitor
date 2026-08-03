def stats:
    sort as $values |
    {minimum: $values[0], median: $values[2], maximum: $values[4],
     maximumToMinimumRatio: (if $values[0] > 0 then $values[4] / $values[0] else null end)};
def within_blocked_budget($baseline; $candidate):
    $candidate <= ($baseline + ([($baseline * 0.10), 5] | max));
($lanes[0]) as $lane |
($controls[0]) as $control |
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
        sampleUnit: "median of five per-trial nearest-rank p95 values",
        budget: "blocked <= unblocked + max(10%, 5 ms)",
        ingress: {
            unblockedMedianMilliseconds: ($lane | map(.blockedComparison.unblocked.acceptP95Milliseconds) | sort | .[2]),
            blockedMedianMilliseconds: ($lane | map(.blockedComparison.blocked.acceptP95Milliseconds) | sort | .[2])
        },
        standardAck: {
            unblockedMedianMilliseconds: ($lane | map(.blockedComparison.unblocked.standardAckP95Milliseconds) | sort | .[2]),
            blockedMedianMilliseconds: ($lane | map(.blockedComparison.blocked.standardAckP95Milliseconds) | sort | .[2])
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
        w3MMigrationMilliseconds: ($lane | map(.w3M.migrationMilliseconds) | stats),
        w3MCpuMilliseconds: ($lane | map(.w3M.cpuMilliseconds) | stats),
        w3MAllocatedBytes: ($lane | map(.w3M.allocatedBytes) | stats),
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
        unblockedCommitAllocatedBytes: ($lane | map(.blockedComparison.unblocked.commitAllocatedBytes) | stats),
        blockedCommitAllocatedBytes: ($lane | map(.blockedComparison.blocked.commitAllocatedBytes) | stats),
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
            .blockedComparison.blocked.requiredDrainCapturesPerSecond >= 1 and
            .blockedComparison.blocked.optionalRecoveryCapturesPerSecond >= 1 and
            .blockedComparison.unblocked.rssMedianGrowthBytes <= 67108864 and
            .blockedComparison.blocked.rssMedianGrowthBytes <= 67108864] | all) and
            ([$control[].workloads[].productionScenarios[] |
                .finalAcquisitionBacklog == 0 and
                .postReleaseOptionalBacklog == 0 and
                .postReleaseCentralBacklog == 0] | all)),
        aggregateBlockedIngressWithinBudget:
            (($lane | map(.blockedComparison.unblocked.acceptP95Milliseconds) | sort | .[2]) as $unblocked |
             ($lane | map(.blockedComparison.blocked.acceptP95Milliseconds) | sort | .[2]) as $blocked |
             within_blocked_budget($unblocked; $blocked)),
        aggregateBlockedAckWithinBudget:
            (($lane | map(.blockedComparison.unblocked.standardAckP95Milliseconds) | sort | .[2]) as $unblocked |
             ($lane | map(.blockedComparison.blocked.standardAckP95Milliseconds) | sort | .[2]) as $blocked |
             within_blocked_budget($unblocked; $blocked)),
        rssGrowthWithinDeclaredAbsoluteBudget: ([$lane[] |
            .blockedComparison.unblocked.rssMedianGrowthBytes <= 67108864 and
            .blockedComparison.blocked.rssMedianGrowthBytes <= 67108864] | all),
        candidateOnlyTrialRanges: "Reported as min/median/max and maximumToMinimumRatio; no equivalent historical baseline exists, so range ratios are diagnostic rather than regression gates."
    }
} |
.pairedLatencyDisposition.ingress.passed = .gates.aggregateBlockedIngressWithinBudget |
.pairedLatencyDisposition.standardAck.passed = .gates.aggregateBlockedAckWithinBudget |
.result = {passed: (.gates.perTrialCorrectness and .gates.aggregateBlockedIngressWithinBudget and
    .gates.aggregateBlockedAckWithinBudget and .gates.rssGrowthWithinDeclaredAbsoluteBudget)}
