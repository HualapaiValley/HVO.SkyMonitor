# CI Runner Capacity Runbook

This runbook owns the shared x64 self-hosted runner pool and the ready-PR
capacity reserve introduced by issue #849. It records the measured inventory,
the observed and modeled before/after results, and the rollout and rollback
procedure. `docs/design/ci-deployment-target-architecture.md` section 3.5 holds
the design decision; this file holds the numbers and the operations.

## 1. Inventory (2026-10-15)

| Item | Observed |
| --- | --- |
| Pool | `gh-runner-01` .. `gh-runner-06`, organization runner IDs 16-21, one org runner group shared with HVO.AgentControl and HVO.WebSite |
| Host | LAN KVM virtual machines, one runner service each (`actions.runner.HualapaiValley.<name>.service`), one job at a time per runner |
| CPU / memory | 4 vCPU, 3.7 GiB RAM and 8 GiB swap per runner |
| Disk | `/` 96 GiB (22-37% used); Docker root on a separate 196 GiB volume (38-61% used) |
| Labels before #849 | identical on all six: `self-hosted, Linux, X64, hvo-skymonitor, ubuntu-24.04, dotnet, docker, hvo-website, hvo-agentcontrol` |
| Other self-hosted | `github-runner-pi-01` (ARM64, `hvo-skymonitor-arm64`) and `m5-mac-01` (macOS ARM64); neither matches an x64 selector |
| Trust | Public repository; fork pull requests from first-time contributors need approval. `ci.yml` runs only for `main`/`release/**` targets, pushes and dispatch; `development-v1.yml` runs the v1 profile. This change does not alter which events reach a self-hosted runner |

Pool consumers:

| Workflow | Selector | Notes |
| --- | --- | --- |
| SkyMonitor `development-v1.yml` Build and Unit (ready PRs, v1 pushes) | `[self-hosted, linux, x64, hvo-skymonitor]` | about 316s at p50; must be able to use every runner |
| SkyMonitor `ci.yml` (14 self-hosted jobs: promotion PRs, `main` pushes, dispatch) | `[self-hosted, linux, x64, hvo-skymonitor, hvo-qualification]` after #849 | one run fans out to seven or more concurrent jobs for 20-23 minutes |
| HVO.AgentControl | `hvo-agentcontrol` | low volume; can use any of the six runners |
| HVO.WebSite | GitHub-hosted (`ubuntu-latest`) on 2026-10-15 | the `hvo-website` label is currently unused |

## 2. Baseline (observed)

Collected with the read-only tool in this repository and the commands in
section 5. Waits are `started_at - created_at` in seconds for jobs that ran on
the pool; reused jobs that a failed-jobs re-run lists again are counted once.

Historical window, 2026-09-19 to 2026-10-03 (UTC days):

| Class | n | p50 | p90 | p99 | max | >60s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| ready-pr (v1 Build and Unit) | 129 | 2 | 157 | 727 | 1038 | 17 |
| v1-push | 92 | 2 | 3 | 9 | 9 | 0 |
| qualification-pull_request | 425 | 2 | 148 | 469 | 519 | 115 |
| qualification-push | 36 | 2 | 58 | 139 | 139 | 3 |
| qualification-workflow_dispatch | 104 | 2 | 238 | 686 | 801 | 25 |
| HVO.AgentControl | 49 | 2 | 3 | 115 | 115 | 1 |

Current window, 2026-10-04 to 2026-10-14, after #1142 split the v1 profile from
full qualification:

| Class | n | p50 | p90 | p99 | max | >60s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| ready-pr | 80 | 2 | 3 | 75 | 75 | 1 |
| v1-push | 35 | 2 | 3 | 29 | 29 | 0 |
| qualification-pull_request | 16 | 2 | 81 | 137 | 137 | 2 |
| qualification-push | 9 | 3 | 74 | 74 | 74 | 1 |
| HVO.AgentControl | 3 | 2 | 6 | 6 | 6 | 0 |

Observed qualification makespan in the current window: 3 runs, mean 1336s.

Reading the baseline:

- The historical ready-PR tail came from qualification-heavy days before #1142
  and from ready PRs queuing behind one another. The finalization lock and the
  short v1 profile have since removed most of it.
- What remains is structural. A qualification run puts seven or more jobs on a
  six-runner pool for about 22 minutes, so a ready PR, or the v1 push that a
  merge produces, that arrives in that window waits for a runner. Only three
  qualification runs fell inside the current window, so it seldom happened in
  the sample, but every promotion produces such a window.

## 3. Trial: reserve one general runner (modeled)

GitHub Actions cannot exclude a label or give one workflow priority. A
`runs-on` list must match every label it names. The reserve is therefore
expressed positively:

- `gh-runner-01` .. `gh-runner-05` carry the additional label `hvo-qualification`;
- every self-hosted `ci.yml` job requires it;
- `development-v1.yml` does not, so v1 jobs can still run on all six runners;
- `gh-runner-06` stays general, so qualification can never occupy it.

The replay below is a model, not a measurement. It replays the observed job
durations on the explicit six-runner pool through a FIFO queue, with seeded
random runner choice and in-run delay propagation. It then injects a 316s ready
PR every 30s through each observed qualification run, in three arrival orders:

- `alone`: the ready PR arrives on its own;
- `pr-then-push`: a v1 push arrives at the same moment, behind it;
- `push-then-pr`: the v1 push arrives just ahead of it, as when the previous
  merge's push is still dispatching.

Each order is reported separately. Seeds 0-2.

Current window (405 probes per row):

| Reserve | Arrival order | Injected ready-PR wait mean / p99 / max | >60s | Isolated qualification makespan (mean) |
| ---: | --- | ---: | ---: | ---: |
| 0 | alone, pr-then-push | 3.2s / 80s / 87s | 9 | 1288s |
| 0 | push-then-pr | 40.0s / 300s / 317s | 84 | 1288s |
| **1** | **alone, pr-then-push** | **0s / 0s / 0s** | **0** | **1337s (+3.8%)** |
| **1** | **push-then-pr** | **40.0s / 300s / 316s** | **84** | **1337s (+3.8%)** |
| 2 | all three | 0s / 0s / 0s | 0 | 1482s (+15%) |

Historical window (10158 probes per row):

| Reserve | Arrival order | Injected ready-PR wait mean / p99 / max | >60s | Isolated qualification makespan (mean) |
| ---: | --- | ---: | ---: | ---: |
| 0 | alone, pr-then-push | 49.1s / 601s / 958s | 2067 | 1290s |
| 0 | push-then-pr | 94.6s / 723s / 1065s | 3330 | 1290s |
| 1 | alone, pr-then-push | 16.6s / 590s / 958s | 420 | 1325s (+2.7%) |
| 1 | push-then-pr | 92.1s / 735s / 1167s | 3492 | 1325s (+2.7%) |
| 2 | alone, pr-then-push | 11.4s / 583s / 958s | 223 | 1469s (+14%) |
| 2 | push-then-pr | 39.0s / 735s / 1167s | 1273 | 1469s (+14%) |

Decision: trial one reserved runner.

- In the current regime it removes modeled qualification contention for a ready
  PR that arrives on its own or ahead of a concurrent push, at about 4%
  qualification makespan.
- It does not help a ready PR that arrives just behind a v1 push. The push takes
  `gh-runner-06` first, and the ready PR waits for the push (316s) or for a
  qualification job, exactly as without a reserve.
- Two reserved runners remove that case too in the current regime, but cost
  about 15% qualification makespan, four times the cost of one.

Two reserved runners are therefore deferred, not rejected: the live acceptance
in section 4 decides between one and two. With one reserve, the historical
`push-then-pr` order is slightly worse than with none, because qualification runs
longer and overlaps more probes.

In the historical regime, one reserved runner cuts injected waits over 60s by
80% for a lone ready PR, but the tail stays near ten minutes. That tail comes
from ready PRs and dispatches queuing behind one another during bursts. A
runner reserve cannot remove it; the finalization lock and the short v1 profile
address it instead.

What the reserve does not guarantee:

- It does not make `gh-runner-06` exclusive. A v1 push, a second ready PR or an
  HVO.AgentControl job can occupy it, and the `push-then-pr` rows above show the
  resulting wait.
- It does not protect against an outage of `gh-runner-06`. `verify-labels`
  warns when no general runner is online.
- It does not change the job count, timeouts, cancellation, exact-SHA checkout,
  required checks or the trust boundary of any workflow. Only the qualification
  runner selector changes.

## 4. Rollout and rollback

Order matters: a job whose labels match no runner queues indefinitely, up to
GitHub's 24-hour queue limit. A promotion PR runs the `ci.yml` from its merge
ref, which is `development/v1`. Therefore:

1. Confirm that no qualification run is queued or in progress, and that the
   runners are idle or only finishing work. Labels can be added while a runner
   is busy, and no service restart is needed.
2. Add the label to the five qualification runners. This is safe before the
   workflow requires it.

   ```bash
   for id in 16 17 18 19 20; do   # gh-runner-01 .. gh-runner-05
     gh api -X POST "orgs/HualapaiValley/actions/runners/$id/labels" -f 'labels[]=hvo-qualification'
   done
   ./scripts/ci:runner-capacity verify-labels --org HualapaiValley \
     --qualification gh-runner-01,gh-runner-02,gh-runner-03,gh-runner-04,gh-runner-05 \
     --general gh-runner-06
   ```

   Confirm the runner IDs with
   `gh api orgs/HualapaiValley/actions/runners --jq '.runners[] | [.id,.name] | @tsv'`
   first. IDs change when a runner is re-registered.
3. Merge the workflow change into `development/v1`. It reaches `main` with the
   next promotion.
4. After the next promotion window, collect that window and compare it with
   sections 2 and 3, using the commands in section 5. Use the same `--pool` even
   if a runner ran nothing. Keep the change when both hold:
   - every ready-PR or v1-push job that arrived during the qualification run,
     while no other general job held `gh-runner-06`, started within 60s;
   - the qualification makespan stays within the modeled +5%.

   Attribute every longer wait from the collected input: either another v1 or
   AgentControl job held `gh-runner-06`, which is the modeled `push-then-pr`
   case, or something else, which is a defect. Then record the result on #849.
   - Repeated `push-then-pr` waits are the evidence for two reserved runners:
     delete the label from `gh-runner-05` and rerun `verify-labels` with
     `gh-runner-05,gh-runner-06` as general.
   - A makespan regression beyond +5%, or an unattributed wait, means roll back.

Re-registering a runner (`config.sh`) replaces its labels. Re-register
`gh-runner-01` .. `gh-runner-05` with `--labels ...,hvo-qualification`, then run
`verify-labels`.

Rollback (reverse order):

1. Revert the `hvo-qualification` selector in `ci.yml` on every branch that
   carries it (`development/v1`, `main` if promoted, and any `release/**` cut
   from them), and wait for queued qualification runs to drain or cancel them.
2. Only then remove the label:

   ```bash
   for id in 16 17 18 19 20; do
     gh api -X DELETE "orgs/HualapaiValley/actions/runners/$id/labels/hvo-qualification"
   done
   ```

If the label disappears first by accident, for example through a
re-registration, qualification jobs queue without failing. Restoring the label
with step 2 of the rollout releases them.

## 5. Reproducing the numbers

`scripts/ci:runner-capacity` is read-only against GitHub (`gh api` GETs only):

```bash
./scripts/ci:runner-capacity collect --since 2026-10-04 --until 2026-10-14 \
  --repo HualapaiValley/HVO.SkyMonitor --repo HualapaiValley/HVO.AgentControl \
  --output capacity-input.json
pool=gh-runner-01,gh-runner-02,gh-runner-03,gh-runner-04,gh-runner-05,gh-runner-06
./scripts/ci:runner-capacity report --input capacity-input.json --pool "$pool" --summary
./scripts/ci:runner-capacity report --input capacity-input.json --pool "$pool" > capacity-report.json
```

`collect` splits the window by UTC day and fails rather than writing a partial
input when a listing is incomplete, repeats an object, or is at the 1000-run
cap.

`report` takes the pool inventory explicitly through `--pool`. An idle runner
still serves the replay, and the reserve is the last `N` names in sorted
order, so `gh-runner-06` for one reserved runner. A pool job on a runner that
`--pool` omits fails the report. The report is deterministic for a given input,
and `--reserve`, `--seeds`, `--probe-seconds` and `--probe-step` select the
model. `scripts/test:ci-runner-capacity` checks the
tool against a hand-computed fixture and an offline `gh` substitute, and runs in
the full-qualification Quality job.

The inputs and reports behind sections 2 and 3 are retained in the #849
evidence archive with SHA-256 manifests.

## 6. Options considered

| Option | Disposition |
| --- | --- |
| Reserve one general runner with `hvo-qualification` | Adopted as the trial (section 3) |
| Reserve two runners | Deferred to the live acceptance (section 4). It also covers a ready PR arriving just behind a v1 push, at +15% qualification makespan against +3.8% for one |
| Add runner VMs | Not measured. It depends on KVM host CPU, memory and I/O headroom, which this issue did not inventory, so it is out of scope for a reversible trial |
| Runner-local or LAN content-addressed build store (#852 follow-up) | Recorded as an option, not adopted. #852 measured 527 MB packed per build and 398-709s per GitHub artifact hop against a 97s rebuild. A fleet-local store could remove the transfer, but it would add a cache-trust boundary shared by mutually untrusting jobs (poisoning, digest binding and eviction) and needs disk on the Docker volumes (38-61% used). It does not address the queue wait this issue measures |
| Workflow priority or label exclusion | Not available in GitHub Actions |
