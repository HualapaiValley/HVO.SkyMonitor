# Reduced CI Validation

This note retains the production proof for the path-aware pull request matrix
introduced by issue #163 and PR #164.

## Contract

Pull requests containing only added or modified paths from the reviewed
documentation/developer-environment allowlist run Change Classification,
Quality, and Required CI. Build, Unit Tests, Integration Tests, Architecture &
Publish, Migrations, and Coverage are expected to report `skipped`. Required CI
rejects any other result combination. Pushes and changes outside the allowlist
always run the complete matrix.

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

The proof branch changes only this allowlisted documentation path. Record the
initial reduced run and final current-head replacement run here before merging
the proof pull request.
