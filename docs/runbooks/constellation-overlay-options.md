# Scene overlay options

CameraAgent's **Current Sky → Edit scene visibility, sizes and constellation style**
opens a draft of the active processing graph at its **Scene overlay** section
(`?section=scene-overlay#scene-overlay`). Existing constellation deep links
(`?section=constellations#constellations`) still open the same panel.
You can also open **Operations → Pipeline → Named graphs**, select a revision,
and choose **Draft a new revision from this one**.

Graphs using the separate legacy `Annotation` step have an
[existing Annotation styling panel](legacy-annotation-options.md), with its own
exact-version effectiveness limits. Opening either panel does not convert a graph.

1. In the `ScenePresentationLayer` node, adjust the existing visibility and scalar
   controls below. The constellation colour picker also accepts `#RRGGBB`.
2. Give the draft a new revision label, then select **Preview plan**. Errors show
   stable `processing.options.*` codes and the affected option. Correct them and
   preview again. Editing after a preview clears it; preview again before saving.
3. Select **Save immutable draft**, then **Open the saved draft**. In **Named
   graphs**, **Validate** that revision, then **Activate** it and confirm. Saving
   and previewing do not change live capture; activation selects the new revision
   for subsequent captures. A capture already accepted keeps its selected graph.
4. To undo the change, use **Roll back** on the previous retained revision in
   **Named graphs** and confirm. Future captures use that revision's original
   options. Existing captures and presentation products retain their original
   output and identities throughout.

Opening and previewing an unchanged graph, including returning controls to
their original values, leaves **Save immutable draft** disabled and creates no
revision. A successful save also disables that button until you edit again.

Only the edited JSON members change. Unknown option fields, original member
spelling and step aliases are retained. Defaults are never written merely by
opening the editor. This panel covers the currently shipped scene producer;
unsupported options stay opaque. It does not upgrade a graph, step alias or
rendering version. Future version changes require an explicit whole-draft
upgrade; retained graphs are never silently converted.

If the active graph has no supported scene overlay node, the deep link explains
that absence. Add `ScenePresentationLayer` with its `ProjectedScene` input in the
ordered graph, then preview the full dependency graph before saving. An
unavailable registry or revision is reported without changing live capture.

These are capture presentation settings. Current Sky's layer visibility controls
remain viewing choices, and previously retained captures do not acquire a new
style when you activate a graph.

## Existing scalar contract

The shared descriptors in `HVO.SkyMonitor.Processing` resolve the registered alias
`ScenePresentationLayer` (case insensitive) at these exact versions:

- `projected-scene-presentation-v12-plain-circle-markers` (retained contract)
- `projected-scene-presentation-v13-plain-circle-bounded-expected-labels` (current runtime)

Both describe the same scalar fields. Resolving a retained descriptor never makes
the current runtime execute an older producer under its old identity. Both hosts
use the same shared contract; no new producer or recipe version is introduced.

| Option member | Default | Existing bound / effect |
| --- | --- | --- |
| `drawMarkers` | `true` | Show point markers and existing solar-system outlines |
| `markerRadius` | `6` | Whole pixels, 0–32; zero retains the renderer's point-marker semantics |
| `drawLabels` | `true` | Show eligible object labels |
| `labelScale` | `2` | Whole size multiplier, 1–8 |
| `maximumLabelCharacters` | `24` | Whole character budget, 0–64; zero hides object labels |
| `maximumLabelMagnitude` | `2.5` | Finite magnitude, −30–30; selects named stellar markers and labels |
| `drawConstellationLines` | `true` | Show the retained constellation selection |
| `constellationLineRed/Green/Blue` | `96/160/255` | Each channel 0–255; one colour control, `#60A0FF` |
| `constellationLineThickness` | `2` | Whole pixels, 1–8 |
| `drawImageCircle` | `true` | Show the image circle when supplied by the projection |
| `drawCardinalDirections` | `true` | Show available N/E/S/W landmarks |
| `cardinalScale` | `2` | Whole size multiplier, 1–8 |

Availability, measured/expected qualification, decluttering and existing projection
geometry still determine which primitives can appear. A magnitude limit does not
change the catalog query or the scene itself; solar-system bodies keep their
existing selection rules. Expected-position labels retain their complete
`(expected)` marker, or are suppressed if the character budget cannot hold it.
Mandatory catalog/prediction captions and dashed distinctions keep their existing
rules.

For the separate physical predicted-scene query, use the
[Projected scene query panel](projected-scene-query-options.md). VirtualSky's
authoritative rendered-scene query remains in its module settings.

`constellationIds`, nullable legacy `markerValue`, `labelValue`, `imageCircleValue`
and `cardinalValue`, diagnostic policy and output variants remain preserved JSON
without editors. Collection/nullable editor design, new fonts/glyphs, producer
upgrades, general NGC/M/IC configuration and satellite styles remain separate
work. This is a bounded partial delivery of #1386; Frame Facts has its own panel.
