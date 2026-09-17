# Heterogeneous Elastic Fleet Allocation Model

Decision date: 2026-09-17
Issues: #854 discovery for #613

## Status

This document defines the selected allocation model and implementation slices.
The executable oracle is test-only. It does not change production autoscaling,
enable a provider, or complete #613.

## Inputs

One allocation sample freezes these facts:

- Claimable queued jobs: stable ID, recipe/version identity, total input bytes,
  observatory entitlement scope, and `AvailableSince` age.
- Running registrations: stable runner/instance ID, exact eligible recipe set,
  maximum transfer bytes, registered concurrency, and occupied slots from
  unexpired leases/accepted claims.
- Starting instances: count and the probed template capabilities they will
  register with.
- Remaining entitlement concurrency for the backlogged observatories after all
  active leases, regardless of worker type.
- Template concurrency, expected cold start, queue deadline, instance limit,
  warm minimum, and daily instance-minute state.
- Terminal cleanup demand remains separate. It needs at most one compatible
  instance and keeps its existing exemption from pool, entitlement, and deadline
  policy.

An unavailable slot is reserved rather than reassigned by recipe. All slots in
one registration advertise the same capability set; therefore subtracting the
registration's occupied count before matching preserves the remaining claimable
capability without pretending that a busy slot is free.

## Selected Model

1. Expand each running registration into its available slots:
   `max(0, MaxConcurrency - OccupiedSlots)`.
2. Connect a queued job to a slot only when the registration advertises the
   exact recipe and its transfer limit admits the job's complete inputs.
3. Compute a maximum-cardinality bipartite matching. Process jobs with the fewest
   compatible slots first, then oldest age and stable ID. Earlier matched jobs
   remain matched: an augmenting path may move them between slots but succeeds
   only when every displaced earlier job is reassigned. Canonically sort slots by
   registration ID. This yields a maximum-cardinality result with a stable
   lexicographic secondary objective over job priority, independent of registration
   enumeration order.
4. Matched jobs are covered by existing registered capacity. Unmatched jobs are
   the executable shortfall.
5. Apply remaining entitlement headroom independently for each unmatched job's
   observatory scope, without crediting matched capacity that cannot claim those
   jobs or allowing an exhausted observatory to consume another observatory's
   headroom. The resulting provisionable set determines required template
   instances by ceiling division over template concurrency, minus starting
   template capacity when production adopts it.
6. Apply the cold-start deadline to the oldest job in the provisionable unmatched
   set. Old work already covered by existing registrations does not reject a
   cold start for younger uncovered work.
7. Preserve existing instance-limit, incompatible-replacement, warm-minimum,
   daily-budget, cleanup, and retirement decisions around this allocation result.

## Counterexamples Closed By The Model

| #613 finding | Oracle evidence |
| --- | --- |
| Greedy assignment consumes constrained capacity and strands later work | A three-job A/B fixture requires an augmenting-path reassignment to fill all slots. |
| Busy B-only runner is credited with queued B capacity | Occupied slots are removed before matching; a fully occupied registration covers zero queued jobs. |
| Partial entitlement headroom disappears behind aggregate compatible capacity | Headroom is enforced per observatory; an older unmatched job from an exhausted observatory cannot displace a younger eligible job from another observatory. |
| Old covered work rejects a cold start for young uncovered work | Deadline age is the oldest provisionable unmatched job, not queue-wide oldest age. |

The transfer-limit fixture separately proves that recipe compatibility is not
enough and that a larger job is reassigned to a registration that admits its
complete input bytes. The oracle also runs a deterministic representative case with 64 jobs, 16
heterogeneous registrations, eight recipes, four slots per registration, mixed
occupancy, transfer limits, and bounded entitlement.

## Complexity Bound

Let `J` be claimable queued jobs and `S` available registered slots after
occupancy reservation. Compatibility construction is `O(J*S)`. The bounded
augmenting-path oracle is polynomial; the straightforward implementation is
`O(J^2*S)` in the worst case and stores `O(J+S)` matching state. The oracle's
compatibility counter includes adjacency ordering and augmenting-path searches.
Current claim
and candidate bounds make this acceptable for the proof model. Production should
precompute adjacency and record job, slot, edge-visit, and elapsed metrics. It
must keep an explicit candidate cap and fail visibly rather than silently
truncating the shortfall.

## Production Implementation Slices

These slices preserve one invariant per merge and remain children/follow-ups of
#613. They are proposals until the owning issues are accepted.

1. **Capability and occupancy matching.** Extract a host-neutral internal
   allocation input/result inside LogicHost, read current occupied slots and
   exact registration capabilities, replace the greedy allocator with maximum
   matching, and add the A+B/A-only plus busy-B integration cases. No entitlement
   or deadline behavior changes in this slice.
2. **Scoped unmatched-work policy.** Apply per-observatory remaining entitlement
   to the unmatched job identities before constructing policy input. Carry the
   resulting provisionable shortfall count and oldest provisionable age together
   into `ElasticScalingInput`; they are one atomic policy fact. Add focused policy
   tests for mixed exhausted/eligible observatories and covered-old /
   uncovered-young work.
3. **Integrated fleet evidence and signals.** Run all four scripted-provider
   scenarios together, add bounded complexity/elapsed signals and reason-code
   documentation, update the contract pins, and verify instance-limit,
   incompatible replacement, warm capacity, and cleanup interactions.

The first two slices interact through the allocation result contract and should
be reviewed as a coordinated series. The third consumes both and is the parent
#613 convergence gate. Do not parallel-edit the same allocator implementation.

## Exclusions

- No CameraAgent change or live-work eligibility change.
- No cloud provider or external scheduler.
- No production allocator edit in #854.
- No optimization that weakens exact recipe, transfer, entitlement, lease, or
  deadline semantics.
