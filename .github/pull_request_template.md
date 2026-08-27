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

- Initial merge-base SHA:
- Initial reviewed head SHA:
- Latest reviewed head SHA:
- Review path (`GitHub`, `@codex review`, or independent local):

| Round | Reviewed range | Review path | Finding disposition and evidence |
| --- | --- | --- | --- |
| Initial | `<merge-base>..<head>` |  |  |

- [ ] Initial review covered the complete PR diff through the initial reviewed head.
- [ ] Every finding is corrected, evidenced non-actionable, agreed deferred to a
      linked issue, or identified as an unresolved merge blocker.
- [ ] Every correction delta since the previous reviewed head was rereviewed and
      the preceding findings were verified; unchanged code was not repeatedly
      reopened without concrete interaction evidence.
- [ ] Every review round was appended to the ledger without replacing earlier
      reviewed ranges.
- [ ] Correction commits are pushed without force-push or unrequested amend.
- [ ] The PR remained draft during review correction and was marked ready only
      after review convergence.
- [ ] Every planned post-ready head change returned the PR to draft before the
      change and received bounded delta review.
- [ ] Classifier-selected protected CI ran on the final reviewed current head.
- [ ] Every required current-head check is green; canceled, timed-out, flaky,
      missing, or stale pre-correction checks are not accepted.
- [ ] Review conversations are resolved only after correction evidence and
      correction-delta review exist.
- [ ] For roadmap work, the owning epic will be updated after merge with
      validation, performance, and the exact next action.
- [ ] For roadmap work, the next ready issue will start automatically unless
      execution is explicitly paused or blocked.
