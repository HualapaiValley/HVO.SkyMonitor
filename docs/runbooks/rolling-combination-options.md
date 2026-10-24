# Same-gain rolling combination

Open the Processing graph editor and draft the active graph. An existing
`RollingCombination` node using the same-gain rolling mean has controls for
enabled state, window size (1–100), trailing or centered windows, and optional
positive age and integration limits in milliseconds. Fractional milliseconds
are supported. Blank limits mean unlimited; **Remove limit** explicitly clears
an existing limit. Missing, explicit null and unchanged numbers keep their source
JSON spelling when saved without an effective change.

Trailing windows produce during warm-up. Centered windows require all positions
from `-(size / 2)` through `size - (size / 2) - 1`, including future captures.
An even window therefore has one more position before its center than after it.
The current live path rejects centered windows; use the existing archived replay
workflow with the required retained inputs. Incomplete centered windows fail
readiness rather than inventing inputs. Graph validation checks window counts,
kind and positions against the node options.

Age cuts count backwards from the newest selected frame and include the exact
boundary. Integration cuts include frames while their summed exposure stays
within the limit; the newest frame is always included even if it alone exceeds
the limit. These cuts can reduce a ready centered window's combined source set.
Gain and existing compatibility boundaries still reset the window.

Preview and save a draft, validate it, then explicitly activate it. Saving never
activates a graph. Roll back to the retained revision to restore its settings;
old revisions retain their original options and output identities.

OutputVariant, Recipe and normalization bindings are preserved. Mixed-gain nodes
keep their existing opaque options and have no same-gain controls. This panel
does not select recipes or plans, acquire masters, change stacking algorithms,
or change retention and scheduling. #1386 remains open for remaining editors.
