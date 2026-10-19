# Orbital prediction qualification (#529)

This directory records component qualification; it does not
claim physical accuracy, optical visibility or five-second integrated cadence.
The approved [decision checkpoint](../../planning/orbital-data-decision-527.md)
and [shared contract](../../contracts/orbital-elements-v1.md) govern this work.

## Preimplementation numerical gates

The following gates are recorded before implementing their numerical stages.
Reference inputs and expectations are frozen independently of the HVO engine.
Do not relax a tolerance after a failure without recording its cause and a
reviewed change to the reference convention or acceptance.

| Stage | Gate |
| --- | --- |
| SGP4 reference suite | WGS72 / AFSPC mode a; position-vector residual at most 1e-6 km and velocity-vector residual at most 1e-8 km/s; expected invalid cases fail explicitly |
| GP initializer | Direct nonzero DOT/DDOT coefficient comparisons with the shared fixture; no extra factors two or six; modern identifier text preserved |
| Appendix C TEME/ITRF literal input | Preserve the independent source's represented UT1 input and per-component limits of 5e-8 km position / 7e-8 km/s velocity; test split UTC conversion separately |
| Exact-convention Earth-fixed/observer reference | Position/range residual at most 1e-6 km; velocity/range-rate at most 1e-8 km/s; direction separation at most 1e-8 radians away from undefined azimuth; compare vectors at zenith |
| Existing projector/readout arithmetic | At most 1e-8 pixels against the same declared native/readout transform; apply the remaining transform once |
| Exposure sampling | Honor each request's chord tolerance, at most 0.25 pixels, and time step, at most one second; record the estimator/enclosure convention and withhold complete status for unresolved intervals |
| Well-conditioned future-pass crossings | Independent reference bracket at most 0.001 seconds; HVO bracket at most 0.01 seconds and event difference at most 0.02 seconds |
| Near-tangent/flat event | Separate altitude/bracket evidence and deterministic peak policy; no unsupported uniform timestamp-accuracy claim |

The old Seattle ISS POC uses mode i and half-second event refinement. Its image
and coordinate evidence is retained as exploratory context, and is not relabeled
as the new mode-a event oracle. The canonical propagation pack is
`docs/validation/orbital-reference-v1/`; selected independent coordinate/pass
fixtures need their own source, license, hashes, model conventions and coverage.

Production computation uses retained local immutable inputs. Resolved EOP/leap
content must match request facts, actual coverage and interpolation status;
unsupported leap crossings and missing brackets fail explicitly. No per-frame
network or storage resolution is introduced. An unqualified solar/shadow model
leaves illumination unknown. Pixel injection requires explicit synthetic
brightness; private simulation truth is unavailable to the ordinary detector.

The issue owns the full Tier C candidate gate and independent review. Component
resource evidence records cold/warm CPU, allocations, working set, elapsed time,
evaluation counts and partial outcomes. Existing W1/W2 geometry and the supplied
population workload guide scale. Integrated contention and complete
frame-available-to-publication acceptance remain #1395.

## Evidence index

- [SGP4 qualification and source pins](sgp4-qualification.md)
- [Resolved time and independent coordinate/pass references](coordinate-reference.md)
- [Numerical exposure sampling](exposure-sampling.md) and [future-pass search](pass-search.md)
- [Additional high-eccentricity/resonant pass qualification](additional-pass-qualification.md)
- [Independent dense calibration/held-out checks](sampling-reference.md)
- [Canonical camera reproduction, source context and labeled samples](virtual-camera.md)
- [Bounded full-population component resource baseline](component-resources.md)

The public producer is `OrbitalPredictionEngine`, constructed with already
materialized `OrbitalTimeData`. `OrbitalPredictionModel.Create` binds the actual
resolved hashes and declared conventions. A host resolves/pins its snapshot
outside frame processing, constructs a bounded shared query and calls the same
producer; no host-specific propagation or automatic registration is introduced.
Final candidate, independent review and target-branch CI evidence are recorded
on #529 and its PR. Software integration is separate from physical qualification.

Clock uncertainty publishes a maximum bound only when the capture host reports
both synchronization and a maximum error. Other clock facts, including an
estimate without a synchronized maximum, remain in timing provenance but yield
an unknown bound. Explicit nominal-zero-EOP diagnostic tracks retain their
degraded approximation in the orbit-uncertainty reason; sampling accuracy never
fills the unknown physical orbit uncertainty.
