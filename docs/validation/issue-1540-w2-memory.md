**#1540 W2 memory protocol (declared before any measurement)**

- **Arms.**
  - Baseline: `development/v1` at `c1971fbf2e640ea82473f7e2dae3aca2a457c251`, the exact merge base of the synced branch.
  - Candidate before attribution/fix: base-sync merge `26ae1b9bd258d70f93ee254bdb59772f6700fd4e` on `agent/1540-1187-slice-a-re-sync-the-preserved-branc`.
  - Each arm is a detached worktree at its commit, with `HVO.SkyMonitor.CameraAgent.Tests` built in Release with `-warnaserror` on SDK 10.0.402.
- **Harness.** The unchanged #1170 `Issue1170ComposedPipelineEvidenceTests.ComposedShippedGraph`, with these settings:
  - steady scenario, workload `w2-night`, runtime profile `production-defaults`;
  - 5 warmup and 30 measured operations;
  - one fresh process per trial, the `hyg-v42-production` snapshot, and no collectors attached.
  - It runs through `dotnet test` directly, as the #1187 predeclaration required, using a committed driver: `docs/validation/issue-1540-w2-memory.sh`.
- **Design.**
  - Six interleaved pairs, ordered B-C, C-B, B-C and so on: 6 trials per arm, on one host, under a single hold of the host's heavy-step lock.
  - Each trial waits for a one-minute load average below 1.0. The wait is capped at 10 minutes and recorded per trial, along with the tenants before and after.
- **Primary metric and rule.** The metric is `memory.peakWorkingSetBytes` (VmHWM). The #1170 rule applies: a regression is `median(C) − median(B) > max(5 % of median(B), 32 MiB)`.
  - I report the median, minimum, maximum, spread and paired C−B deltas.
  - I also report final RssAnon and RssFile, LOH and POH size, GC pause, gen0, gen1 and gen2 counts, allocated bytes per operation, and the operation at which VmHWM was set.
- **Attribution.** A separate attribution pack, labelled as such, covers two more pairs. It never feeds the rule. It records:
  - `dotnet-counters` System.Runtime at 1 s;
  - `/proc/<pid>/status` and `smaps_rollup` every 250 ms, and full `smaps` every 5 s;
  - a GC-keyword (verbose, including stacks) EventPipe trace with AllocationTick, stopped by the harness's `MeasurementComplete` event;
  - a gcdump of the live heap after the last operation.

  From these I split the peak into GC heap (gen0/1/2, LOH, POH, fragmentation), other anonymous native memory (allocator arenas, SkiaSharp, SQLite page cache) and file-backed mappings, and name the code path that accounts for any difference.
- **Raw evidence.** Every pack keeps its raw data, `index.json` and `SHA256SUMS` on the measuring host, and the PR records the digests.
- **Decisions.**
  - If the measure pack violates the rule, I attribute the difference, fix it, and re-run the same protocol on the fix.
  - If it doesn't, F3 does not reproduce on the synced branch. The attribution pack then explains the residual difference and the original +13.56 % median, whose maximum was only +0.54 %. Any growth must be shown to be bounded and proportional before I call it accepted.

The historical +13.56% result remains a distinct observation. Its missing raw heap history cannot be reconstructed by this campaign; any explanation must distinguish directly observed current behavior from a hypothesis about that old run.

Run `python3 -I docs/validation/test-issue-1540-w2-memory.py` to check the acceptance guard. The analyzer exits 1 for an RSS regression and rejects incomplete pairs, fewer than five measurement trials per arm, failed trials, wrong binary revisions, incomplete operations and collectors in a measurement pack. Attribution packs never supply acceptance results. In `peakOrdinal`, -1 means the peak was already reached at the first operation boundary, and -2 means it occurred after the last operation boundary; operation-boundary probes do not locate an intra-operation peak.
