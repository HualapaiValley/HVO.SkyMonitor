# GPS time-source preparation

Refs #1710. Roy authorized this fakes-only preparation slice on November 12,
2026, at 3:09 PM PST through Kepler. It does not complete the parent issue's
production reader, settings persistence, UI, or physical validation acceptance.

`GpsTimeReader` accepts only a caller-owned in-process `Stream`. No production
registration, device adapter, settings keys, UI enablement, or host clock writes
are added. Scripts supply checksummed GP/GN RMC and GGA sentences and invoke
`ObserveSimulatedPps` to model edge receipt. The injected `TimeProvider` supplies
receipt timestamps and monotonic freshness. The reader never opens a path.

Each read has a 4096-byte budget and a two-second deadline, including waiting
for another read. Streams must honor asynchronous read cancellation. Caller
cancellation propagates; an internal deadline ends a round normally. Each line
is limited to 82 characters including its optional CR, excluding LF. LF ends a
physical line; one CR is allowed only immediately before that LF. Non-ASCII,
DEL, other control characters, invalid checksums, unsupported sentences, and
malformed supported data are ignored. Overlong or invalid lines are discarded
through the next LF. Partial lines are not buffered across rounds: their tails
remain discarded through LF when the same source continues, even after a byte
budget, timeout, cancellation, EOF, or an intervening read from another source.
Framing state uses weak stream identities, so it does not retain caller-owned
streams. Continuations assume forward reads; use a fresh stream for a new script.

RMC supplies dated UTC and A/V validity. When a mode field is present, only
measured modes A (autonomous), D (differential), P (precise), R (fixed RTK),
and F (float RTK) allow a diagnostic fix. N (invalid), E (estimated), M (manual),
S (simulated), empty and unsupported modes report NoFix, even with status A;
a legacy sentence without a mode field still uses A/V validity. GGA supplies fix quality (0..8) and
satellite count (0..99), with its own freshness; a fresh quality of zero
suppresses an otherwise valid RMC offset until recovery or expiry; it cannot supply a UTC date.
The fake workload explicitly interprets two-digit years as 2000..2099, accepts
up to seven fractional-second digits, and rejects leap-second timestamps.
Fresh evidence expires at five seconds. Invalid traffic, new GGA data, and PPS
edges cannot refresh dated RMC evidence. Expected stream failures (IOException,
TimeoutException, UnauthorizedAccessException, ObjectDisposedException, or an
OperationCanceledException without a requested caller/deadline cancellation)
clear all evidence and report Unavailable. Caller cancellation propagates,
including when the internal deadline also expires; the reader's own deadline
ends the round normally and retains completed evidence. Unexpected programming
exceptions propagate. A subsequent valid sentence permits recovery. EOF simply ends a
scripted round; it is not interpreted as a real-device unplug indication.

`ReportInto` returns an immutable `ClockSyncSnapshot` copy with `Gps` diagnostics.
It preserves network selection and kernel evidence. `ArrivalOffset` is NMEA UTC
advanced by its monotonic age minus the current host wall clock, including
unknown transport latency. It reconstructs receipt time in the current host-clock
frame, so a wall-clock step changes the offset immediately without extending
freshness; a later GGA receipt cannot replace the RMC anchor. It is hidden
when dated fix evidence is absent, invalid, stale, or unavailable. Simulated PPS
reports its last receipt and freshness independently; it never establishes a
UTC association. `CanVerifyClock` is always false, so none of this slice's
GPS diagnostics can establish a healthy clock offline or override existing
clock-health semantics. Stale UTC/receipt timestamps are retained as history,
with explicit stale state; they are not current offset evidence.

Three questions remain open before a production GPS/PPS implementation:

1. Which PPS source and which edge are supported?
2. Which NMEA UTC second does an edge mark?
3. What measurement uncertainty is acceptable for known-good clock evidence?

Physical serial/PPS integration, receiver qualification, container device access,
configuration persistence, and operator UI activation remain separate work under
the parent issue. There is no new persistence, migration, background service,
log, metric, health registration, or production deployment in this slice.

## Bounded residual safety regressions

The follow-up authorized on November 13, 2026 addresses the carried L2/L3/L4
fake-reader cases in [#1710's ledger](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1710#issuecomment-6843149322).
These changes confer no production clock authority. The 2000..2099 parser
window describes decoding, not plausibility: a two-digit year and status A
alone cannot establish that receiver UTC is current. Fake callers supply an
immutable `GpsUtcWindow` with inclusive earliest/latest UTC bounds; reversed
bounds are rejected. Missing bounds or dated UTC outside them reports NoFix,
suppresses the offset, and revokes any earlier fix. GGA/PPS cannot restore that
fix. The bounds are script inputs, never derived from the injected host wall
clock. Unit fixtures declare 2026..2027 and test 2099 plus both 1024-week shifts
and tick-precise accepting/rejecting boundaries. These dates are fixture data,
not a new production tolerance. Selecting an independent
UTC reference and its acceptable window for production remains a required
parent-issue decision; host time is not silently trusted as that authority.
