# Reduced CI Validation

This note retains the production proof for the path-aware pull request matrix
introduced by issue #163 and PR #164.

## Contract

Pull requests containing only added or modified paths from the reviewed
documentation/developer-environment allowlist run Change Classification,
Quality, and Required CI. Deployment Contracts, Build, Unit Tests, Integration
Tests, Architecture & Publish, Migrations, and Coverage are expected to report
`skipped`; Required CI rejects any other result combination for those inputs.
Catalog Contracts also skips in reduced mode but is not currently aggregated by
Required CI. Deployment selection is independent: ordinary full-mode application
pull requests skip Deployment Contracts, deployment-relevant pull requests run
them, and main/release pushes always run the complete matrix including deployment.
The workflow applies to pull requests targeting `main` or `release/**`, including
the `release/deploy-331` release strategy.

The exact allowlist, exclusions, trust boundary, and local contract-test command
are maintained in [`docs/runbooks/ci-pipeline.md`](../runbooks/ci-pipeline.md).

## Baseline

Full pull request run
[`29673206708`](https://github.com/RoySalisbury/HVO.SkyMonitor/actions/runs/29673206708)
completed in 7 minutes 18 seconds and consumed approximately 30.4 aggregate
runner-minutes across the required jobs.

Implementation run
[`29674151156`](https://github.com/RoySalisbury/HVO.SkyMonitor/actions/runs/29674151156)
selected full mode, completed in approximately 7 minutes, and consumed about
29.0 aggregate runner-minutes. This confirms no material full-matrix regression.

## Reduced Proof

The proof branch changed only this allowlisted documentation path. Initial run
[`29674399230`](https://github.com/RoySalisbury/HVO.SkyMonitor/actions/runs/29674399230)
at `c61f017` selected reduced mode and produced the required result matrix:

- Change Classification, Quality, and Required CI succeeded.
- This historical proof predates the split of deployment contracts from Quality
  and their independent pull-request relevance plan.
- Build, Unit Tests, Integration Tests, Architecture & Publish, Migrations, and
  Coverage were skipped.
- Required CI accepted only that explicit reduced pull request combination.

The run completed in 5 minutes 2 seconds and used approximately 5.1 aggregate
runner-minutes. Against the 30.4 runner-minute baseline, this saves about 25.3
runner-minutes, or 83.2%. Wall time decreased by 2 minutes 16 seconds, or 31.1%.

Review-correction run
[`29674552014`](https://github.com/RoySalisbury/HVO.SkyMonitor/actions/runs/29674552014)
at `39b6512` repeated the same result matrix, completed in 4 minutes 59
seconds, and used approximately 5.0 aggregate runner-minutes. The independent
replacement confirms that reduced selection and Required CI acceptance remain
stable after updating the retained evidence.

The proof pull request's final current-head Required CI run validates this
completed record before merge; its immutable check history remains attached to
the pull request.

## Deployment Contract Sharding Proof

Run `32669782600` was the cleanup baseline: Quality took 12:23, including about
4:05 for the closed Phase 14 component/source importer path. Issue #444 removed
that historical work from protected CI, retained the fast acceptance inventory
contract in full Quality, and moved current campaign contracts to Deployment
Contracts.

Earlier pre-change Quality jobs took approximately 24-25 minutes, with the
deployment suite accounting for approximately 19-20 minutes. The first
four-shard implementation measured 22:51 serial and 19:38 parallel; its
`existing-services` shard took 19:38 and did not satisfy the target. The refined
nine-shard candidate measured 22:48 serial and a 10:08 median parallel time on
the same 8-core, 31-GiB host, a 55.6% wall-time reduction. Three complete
parallel runs passed in 10:07.64, 10:07.82, and 10:11.06 with no leaked child or
listener process. User plus system CPU increased from 1,424.44 to 1,513.80
seconds (6.3%) because independently reproducible lifecycle setup is repeated;
maximum reported resident set stayed within measurement noise at approximately
202 MiB. Pull-request deployment relevance reduces unnecessary runner use but
was not included in that execution-time comparison.
