# Reduced CI Validation

This note retains the production proof for the path-aware pull request matrix
introduced by issue #163 and PR #164.

## Contract

Pull requests containing only added or modified paths from the reviewed
documentation/developer-environment allowlist run Change Classification,
Quality, and Required CI. Deployment Contracts, Build, Unit Tests, Integration
Tests, Architecture & Publish, Migrations, and Coverage are expected to report
`skipped`. Required CI rejects any other result combination. Deployment
selection is independent: ordinary full-mode application pull requests skip
Deployment Contracts, deployment-relevant pull requests run them, and
main/release pushes always run the complete matrix including deployment.
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
