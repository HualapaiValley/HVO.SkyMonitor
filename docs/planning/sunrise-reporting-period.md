# Sunrise reporting periods

Issue #1135 implements the period accepted in #1134: the label October 12 names sunrise on October 12 through
sunrise on October 13 at the configured site. Capture civil date, report date, source period, admitted span, exclusions,
closure, settle allowance and publication time remain separate facts.

`SunriseReportingCalendar` binds an immutable deployment-location snapshot and the installed IANA time-zone rule
identity. It resolves each local civil date through the existing `CaptureScheduleTimeZone` contract and asks
Astronomy's `ISolarEventCalculator` for sunrise within that interval. It never uses a fixed sunrise or adds 24 hours.
Neighbouring periods use the same resolved event. Event and immutable resolved-period caches each retain at most
256 dates; repeated attribution reuses the period identity without rehashing its site and endpoints. A period persists
exact UTC `[start,end)` endpoints, both event
algorithm identities, the complete site snapshot, time-zone rules hash and canonical period identity. A retry consumes
that value without consulting the current site or wall clock. Changed configuration creates a different calendar.

Missing sunrise, skipped civil dates and a missing/invalid site have explicit unavailable states. They do not produce
substitute noon or midnight windows. A dark-night producer selects actual eligible sources inside a resolved full
period; neither daylight exclusion nor missing coverage changes its planned boundaries. A product can become final
only at or after end plus a separately configured, bounded settle allowance. This issue does not implement scheduling,
coverage pixels, source interpolation or a producer.

Raw ingress has already normalized retained capture timestamps to milliseconds. Solar endpoints retain the full
Astronomy precision. Indexed range queries therefore use the first stored millisecond at or after each endpoint,
including the exclusive end. This preserves exact half-open membership for every retained timestamp; truncating a
fractional solar endpoint would assign one stored millisecond to the wrong period. Original manifest bytes and
timestamps remain unchanged.

The supported historical interpretation remains explicitly versioned noon-to-noon. The existing `Create(zone)` and
UTC calendar factories keep that behavior, and old window metadata without a sunrise period remains legacy noon.
The default configured-site calendar/day views adopt sunrise with visible period labels and exact civil start/end
dates. A legacy interpretation must be explicitly selected in queries/links and visibly labelled. This is a read
interpretation of capture timestamps, not a rewrite of retained products, their report association, or their provenance.
Generation/catalog adoption is #993/#1138; task occurrences are #1136. No installed archive migration is performed.

Validation covers exact sunrise and one tick either side, morning before noon, midnight, adjacent changing sunrises,
month/year boundaries, DST folds/gaps, skipped dates, polar no-event cases, bounded cache/ranges, corrupted retained
identity, configuration changes, finality, source-query precision and explicit historical interpretation. Classifier-
selected local checks, a bounded cold/warm calendar measurement, independent deep review and development-v1
protected checks qualify the implementation. The accepted POC remains prototype evidence with its own source pin.
