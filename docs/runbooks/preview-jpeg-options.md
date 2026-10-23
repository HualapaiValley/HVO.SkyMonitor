# Preview and JPEG scalar settings

Open **Operations → Pipeline graphs → New draft from revision**. The `Preview`,
`CalibratedPreview`, `CombinedPreview` and `JpegEncoding` nodes expose their
existing scalar settings. Each panel shows the active recipe implementation.

Preview panels expose enablement, the retained artifact recipe label and the
output variant. The label defaults to `mono16-asinh-v2`; it is a label, not a
transfer selector. Output variants default to `default`, `calibrated-preview`
and `combined-preview` respectively. Changing a route requires separately
updating any variant-specific dependent inputs.

When `FixedTransfer` is absent or null, the existing `encoded-preview-v1`
recipe exposes black percentile (default 0.5, range 0–0.999999), white percentile
(default 0.9999, range 0.000001–1) and asinh strength (default 4, range 0.01–1000).
Percentiles are fractions, and black must be less than white. These controls
produce packed output; JPEG encoding is a separate node. The stretch applies to
Mono16/BayerRggb16 input. Existing Mono8/Rgb24 input keeps its packed-copy
behavior.

When `FixedTransfer` is present, the existing `fixed-preview-v1` recipe stays
active. Its nested levels and gamma are preserved exactly. Percentile/asinh
controls are omitted because they do not affect that mode. There is no mode
switch or nested transfer editor in this slice.

`JpegEncoding` uses the existing `packed-jpeg-v1` recipe. JPEG quality defaults
to 80 and accepts integers 1–100. Its output variant defaults to
`annotated-final-jpeg`. Nullable `MaximumDimension` and its absent/null/value
semantics remain untouched; this panel does not change resize settings.
All three shared recipes remain semantic version 1.0.0.

Unknown members, explicit defaults, numeric spelling and source formatting are
preserved unless that scalar is edited. Returning an absent value to its default
does not materialize it. An unchanged draft cannot be saved as a new revision.
Use **Preview plan → Save immutable draft → Validate → Activate**. Saving alone
does not change live capture. **Rollback** restores an eligible retained
revision. Old graph definitions and retained products remain reconstructable.

This is the bounded #1567 delivery, Refs #1386. New encoders, display transfers,
precision/retention policy, automatic upgrades and physical qualification are
outside this slice; #1386 stays open.
