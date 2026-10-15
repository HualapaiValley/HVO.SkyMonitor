# Raw-ingress initialization readiness (#1253)

A validated journal with missing committed evidence must expose audited recovery
operations while refusing capture and recovered live-execution binding. Strict
initialization still throws for this condition. Healthy initialization returns
ready; invalid journals and other initialization failures still propagate.

The controlled witness on `7e8e3cb0eef4b6deb72b5275d80cc229d0c73048` reproduced
an operations result of `true` while the payload remained absent, health was
`Unhealthy`, and one retained journal record was `missing_evidence`. A concurrent
strict initializer reset the shared flags after operations initialization but
before its final readiness decision. Actual recovered binding then threw through
strict initialization. The clean RED run recorded one expected failure and two
passing cost cases, without compiler/analyzer diagnostics. Earlier diagnostic
runs retained two test analyzer errors; they are not warning-clean evidence.

This establishes the controlled causal schedule. It does not establish which
schedule occurred in the archived #1236 or #1005 failures. #1005's trail query
first calls strict initialization, then directly reads and authenticates the
journal; the witness covers that first call without importing its unmerged code.
Its owner retains responsibility for overlay integration and acceptance.

`InitializeCoreAsync` now makes the readiness decision and classifies failure
under the initialization gate. Operations returns the captured result instead
of reading mutable flags later. The recovery fast path also holds that gate.
Each new validation attempt clears cached validation before root/lifecycle work,
so an early failure cannot reuse earlier recovery readiness. No interface,
journal schema, recovery policy, filesystem publication, or distribution change
is required.

Two optional no-op scheduling callbacks on the existing internal fault injector
pause after initialization state reset and before the operations result is
returned. They inject no exception or state and leave the publication fault
inventory intact. The dedicated test uses task signals and bounded waits, real
journal reconciliation, retained original bytes, and actual registered binding.
It checks unhealthy state, no readiness/binding/acknowledgement, and retained data.
Additional tests cover repeated/concurrent callers, cancellation and healthy
retry, audited restoration, invalid journals, and failed root revalidation.
The existing registered-host paused recovery cases remain unchanged.

Focused reproduction and regression commands (Release, SDK 10.0.401):

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release --filter 'FullyQualifiedName~MissingEvidenceReadinessCannotBeChangedByConcurrentStrictInitialization|FullyQualifiedName~RawCaptureIngressTests'
dotnet test tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj \
  --configuration Release --filter 'TestCategory=Integration'
```

The initial corrected focused selection passed 113/113 with zero skips. The
cost fixture records cold initialization plus 100 batches of 100 steady calls
on healthy and missing-evidence journals. Before/after both allocated zero
measured steady-state bytes and performed no additional journal checkpoints,
transactions, file flushes, directory syncs, or reconciliation. Missing-evidence
10,000-call elapsed time changed from 0.3132 ms to 0.9589 ms: approximately
65 ns per call for serialization, accepted to guarantee the readiness contract.
Healthy elapsed was 5.8118/6.122 ms; median batch was 0.0099/0.0069 ms. These
single-process measurements include JIT/scheduling noise and establish no
startup speed improvement. Retained backlog stayed one capture/four bytes;
health stayed `Accepting` or `Unhealthy` as appropriate. No frame copy or
repository-wide performance campaign is justified by this flag/lock change.

Original RED, clean RED, baseline/after TRX, logs, patches, source identity, and
hash manifests are retained at
`home-dev-01:/home/roys/review-evidence/issue-1253-20261015` and linked from the
issue ledger. Full candidate and review results belong to the append-only
issue/PR ledger. Tier C local validation applies; protected CI targets only
`Development v1 / Preflight` and `Development v1 / Build and Unit`.
