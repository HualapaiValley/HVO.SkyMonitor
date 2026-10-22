# Constellation overlay options

CameraAgent's **Current Sky → Edit constellation colour and thickness** opens a
draft of the active processing graph at its **Constellation overlay** section.
You can also open **Operations → Pipeline → Named graphs**, select a revision,
and choose **Draft a new revision from this one**.

1. In the `ScenePresentationLayer` node, choose a colour with the picker or enter
   `#RRGGBB`. Set line thickness to a whole number from **1 to 8 pixels**.
   Unconfigured values remain the existing blue `#60A0FF` and 2 pixels.
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

Opening and previewing an unchanged graph, including returning both controls to
their original values, leaves **Save immutable draft** disabled and creates no
revision. A successful save also disables that button until you edit again.

Only the edited JSON members change. Unknown option fields, original member
spelling and step aliases are retained. Defaults are never written merely by
opening the editor. This panel covers the currently shipped scene producer;
other steps keep opaque options. It does not upgrade a graph, step alias or
rendering version. Future version changes require an explicit whole-draft
upgrade; retained graphs are never silently converted.

If the active graph has no supported scene overlay node, the deep link explains
that absence. Add `ScenePresentationLayer` with its `ProjectedScene` input in the
ordered graph, then preview the full dependency graph before saving. An
unavailable registry or revision is reported without changing live capture.

These are capture presentation settings. Current Sky's layer visibility controls
remain viewing choices, and previously retained captures do not acquire a new
colour when you activate a graph. Font, glyph, template, deep-sky and other filter
editors are outside this slice.
