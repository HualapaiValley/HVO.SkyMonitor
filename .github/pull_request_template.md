## Summary

- Closes #
- Roadmap initiative (`RM-###`) or maintenance rationale:
- Owning roadmap epic:
- Plain-language outcome and why this issue is next:
- Practical benefit and what this unlocks:

## Scope

- Implemented:
- Excluded or deferred:
- Compatibility/migration impact:

## Validation

- [ ] Risk tier (`A`, `B`, `C`, or `M`) and rationale are recorded below.
- [ ] Focused and tier-appropriate affected tests pass.
- [ ] Tier `C`/`M` complete local candidate gate passed, or `N/A` is justified.
- [ ] Corrections reran focused and affected gates without repeating unrelated
      long suites.
- [ ] Required Debug/Release builds and solution tests pass for the selected tier.
- [ ] Coverage output was reviewed; the current gate was not lowered and no
      uncorrected regression remains.
- [ ] Migrations, architecture, publish, format, package, fault, soak, external,
      or browser gates required by the issue pass.
- Commands and results:

## Output Evidence

- Checksums/numerical invariants:
- Provenance/recipe/profile/lineage:
- Durable filesystem/SQLite/SQL/MinIO state:

## Performance

- Applicability and any `N/A` rationale:
- Baseline/candidate revisions and dirty-state disposition:
- Exact command, workload/config/fixture/seed/count/rate/outage parameters:
- Environment, tools/counters, warm-up, repetitions, sampling, and raw-result location:
- Baseline and candidate I/O, CPU, allocations/working set, throughput, latency,
  and backlog evidence:
- Noise/tolerance method, regression disposition, and residual risk:
- Complexity justification: simpler alternative, measured/correctness benefit,
  and added ownership/invalidation/migration/recovery burden:

## Runtime Review

- Logs/errors/warnings:
- Metrics/cardinality:
- Traces and health transitions:
- Expected signal manifest, collection/assertion command, and retained artifact path:
- Secret, payload, path, or personal-data leakage review:

## Review And Merge Gate

- [ ] Every actionable review finding is corrected.
- [ ] Correction commits are pushed without force-push or unrequested amend.
- [ ] Replacement CI ran on the corrected current head.
- [ ] Every required current-head check is green; canceled, timed-out, flaky,
      missing, or stale pre-correction checks are not accepted.
- [ ] Review conversations are resolved only after correction evidence exists.
- [ ] For roadmap work, the owning epic will be updated after merge with
      validation, performance, and the exact next action.
- [ ] For roadmap work, the next ready issue will start automatically unless
      execution is explicitly paused or blocked.
