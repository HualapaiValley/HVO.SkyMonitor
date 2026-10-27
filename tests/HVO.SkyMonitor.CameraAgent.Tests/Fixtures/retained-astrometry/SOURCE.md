# Retained viewer input v1

`accepted-solution.json` is the byte-identical compact solution from the canonical
accepted durable producer for issue #1644. `calibration.json` contains only the
exact projection, calibration version and readout identity extracted from that
solution's retained context. No status, quality, fit, source identity or budget
was changed. The catalog and raw pixels are deliberately omitted: component
consumers need retained input, not another numerical solve.

Producer: `DurableCaptureProcessingTests.RetainedAstrometry_ViewerPublishesTypedDiagnosticsAndFrozenExportWithoutChangingBase("accepted")`,
with `HVO_ASTROMETRY_VIEWER_FIXTURE_ROOT` selecting an empty destination. See
[the canonical generator and browser evidence](../../../../docs/validation/issue-1644-retained-viewer.md).
Generation uses native millisecond timing and raw paths before producing the
scene, measurements and solution. Original canonical artifacts remain under
`/tmp/hvo-skymonitor-1644-canonical-viewer.hLz32w/accepted` on the evidence host.

Solution source: `derived/2026/08/25/Metadata/2026-08-25_00-00-01.100Z-0091238ee0e881129fea60631ad0f753.json`.
Context source: `derived/2026/08/25/Metadata/2026-08-25_00-00-01.100Z-5f5f598b5cc48d10b67b08aca6e3848f.json`.

The loader pins the solution checksum, runs `MeasuredAstrometryJson.Parse`, and
constructs `AstrometricMapping`, which validates the native assessment identity,
quality gates and exact calibration identity. The UI DTO derives capture, source,
exposure and readout dimensions from those inputs. Existing UI tests exercise
calculator round trips and capture/stage/authorization changes; recipe, numerical,
durability and browser suites retain actual-producer coverage.

SHA-256:

- `accepted-solution.json`: `947DC629346E531E252FC89E251182BBC6B44FD569E4A49B97664E2970DA6AB1`
- `calibration.json`: `BE02D193E2FECDB6E161B428D2CCFD2B2EC456C2F9D238B782633E6FBAE5781A`
