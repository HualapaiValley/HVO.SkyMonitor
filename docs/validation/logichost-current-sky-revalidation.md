# Current Sky revalidation lifetime evidence

Issue #1568 dispositions F1 from #1545 and PR #1544. F2 remains open in #1545;
this work does not choose an older-search reset/preserve policy.

## Decision

Retain a generation-keyed viewer lifetime, with the key on the outer detail
cascade rather than on the nested `ExactCaptureViewer` alone. This replaces the
detail/selection/viewer subtree together. It avoids notifying the retiring
viewer of the new selection before replacing it, while retaining the existing
image, layer, error and authorization reset.

`LoadAsync` clears detail before awaiting authentication or archive reads.
With delayed reads, that empty detail renders and removes the viewer. With
synchronously completed reads, the empty detail need not render at all. The
generation key therefore supplies a reset that the null assignment alone does
not guarantee. The viewer also deliberately preserves state when capture,
authentication task and selection references are unchanged. Its reference
checks are not a substitute for the host's per-revalidation lifetime boundary,
particularly if the same authorized selection snapshot is returned again.

A mutation run removing the generation key fails both synchronous same-capture
rows: a fresh snapshot retains the viewer instance, and a reused snapshot also
retains the failed-image state (`IsBaseImageUnavailable` stays true after
refresh). Both delayed rows still pass because their empty-detail render
removes the viewer. The mutation is reverted in the candidate.

## Reproduction and measurement

`CurrentSkyViewTests.Lifetime.cs` exercises the real nested viewer with
deterministic services and a counting JavaScript interop implementation. Each
case starts with an exact capture, independent capture/receipt times, and a
layered presentation. The measurements count presentation reads, viewer
instance replacement, module-handle imports/disposals and layer bindings.
They do not measure browser module evaluation, heap, CPU or network latency.

On the original nested-key implementation at `3e118ee8`, the new synchronous
fresh-selection and selected-base-change tests fail: initial display plus one
refresh performs **three** presentation reads, rather than two. The cascades
notify the old viewer and the new keyed viewer reads again. The delayed paths
and synchronous reused-selection path perform two reads.

With the key on the outer cascade, initial display plus one refresh performs
**two** selection reads, **two** detail reads, **two** presentation reads and no
capture-list read in all four same-capture combinations (delayed/synchronous,
fresh/reused selection). Both paths replace the viewer once, import two viewer
module handles in total and dispose the retiring handle once. Revalidation
does not reduce necessary viewer or module-handle recreation; it removes one
redundant presentation read from the synchronous fresh-selection path.

The same-capture test first fails the image, retries it, and fails it again
before refresh. Retry adds no archive or presentation reads and reuses the
module handle. Refresh restores the exact image, enabled default layer controls
and independent timestamps. Including that retry, initial display plus refresh
binds layer controls three times.

The other cases verify:

- A changed selected artifact rejects the old layered base, displays only the
  new exact Preview, and retries that Preview without additional service reads.
- Pending membership revalidation removes image, layers, timestamps, provenance
  and the viewer before authority completes. Credential revocation removes them
  without another archive read. Neither path imports another viewer module.
- A new authenticated actor gets a fresh viewer and presentation read. An old
  cancellation-ignoring selection cannot replace it. The deliberately
  cancellation-ignoring fixture records three selection/detail attempts, but
  only two presentation reads; production services receive the cancelled token.
- A late presentation result for a disposed viewer cannot restore layers or
  attach JavaScript after refresh or credential revocation.

Existing `CurrentSkyViewTests`, `ExactCaptureViewerTests` and
`CurrentSkyBrowserAcceptanceTests` retain the plain Preview, exact identity,
bounded continuation, metadata-only, keyboard, touch, responsive layout and
reconnect-denial contracts. This change does not alter controls, JavaScript,
timers, content endpoints or continuation behavior. Browser acceptance is a
separate Manual selection; unit interop counts do not claim browser execution.

## Validation scope

Tier B, `profile=development-v1`. Use `scripts/ci:classify` on the committed
review range to select affected local lanes, then the affected LogicHost Unit
selection, formatting and the four CI-control guards. The PR ledger records
the exact commands, classifier output, immutable head and outcomes. Protected
CI is Development v1 Preflight plus Build and Unit; this evidence does not
claim full qualification or promotion to `main`.
