# Frame Facts template v1

`hvo-frame-facts-template-v1` is the shared, host-neutral grammar and evaluator
for **one corner** (#1550, refs #1387). This additive contract does not register
a producer, options, host binding or renderer. Slice #1551 supplies the retained
token catalogue, source/provenance mapping and presets. Later slices bind the
four corners and explicitly upgrade a saved draft.

The public API is in `HVO.SkyMonitor.Processing`: construct a
`FrameFactsTokenCatalog`, call `FrameFactsTemplate.Parse(source, catalog)`, then
`FrameFactsEvaluator.Evaluate(template, context, missingPolicy)`. The parsed
template and context are immutable and reusable across evaluations and hosts.

## Source grammar

| Form | Meaning |
| --- | --- |
| Literal text | Plain text; no interpretation by an HTML or script engine |
| `{{token.key}}` | Catalogue entry with its default format |
| `{{token.key\|format}}` | Catalogue entry with a named closed format |
| `**text**` | Bold |
| `*text*` | Italic |
| `__text__` | Underline |
| Backslash before `\`, `*`, `_`, `{`, `}`, `\|`, `<`, `>` | The escaped character, literally |
| Actual LF, CRLF or CR | One line break, normalized to LF |

Markers nest, must close in stack order and may span actual line breaks.
`***text***` combines bold and italic. A run of stars closes the innermost
active marker first when possible; otherwise two stars select bold and one
selects italic. Empty emphasis is permitted and contributes no characters.
Single underscores are literal. Unescaped braces must form a token. Angle
brackets must be escaped in source; HTML is not a syntax option. Tokens cannot
contain escapes, whitespace, expressions, conditionals or arbitrary format
strings. A backslash followed by `n` is invalid; use an actual newline.

Token keys are ordinal and case sensitive, with at least two dot-separated
segments. Each segment starts with an ASCII lowercase letter and continues with
ASCII letters or digits. Base keys have at most 96 UTF-16 code units. The suffix
`.status` is generated, never supplied as a base definition or context key.
`spaceWeather.*` is reserved and has no active tokens.

## Typed catalogue and frozen input (slice B boundary)

No token is globally registered by this slice. A catalogue copies at most 256
base definitions and generates one status companion per definition. Duplicate
definitions, supplied status definitions, malformed keys and reserved names
are rejected. Each definition's formats are fixed by its type:

| Kind | Default | Other formats | Output |
| --- | --- | --- | --- |
| Text | `text` | None | The retained string |
| WholeNumber | `integer` | None | Invariant signed Int64, no grouping |
| Number | `number` | `fixed0`, `fixed1`, `fixed2`, `fixed3` | Invariant `G17`, or fixed decimal places with .NET midpoint-to-even rounding |
| Timestamp | `iso8601` | `date`, `time` | `yyyy-MM-ddTHH:mm:ss.fffzzz`, `yyyy-MM-dd`, or `HH:mm:ss` |
| Generated status | `text` | None | `AVAILABLE`, `MISSING`, `STALE`, `CONFLICTING`, `UNSUPPORTED` |

The immutable `FrameFactsTokenContext` copies at most 256 ordinal base-key/fact
pairs. Duplicate keys are rejected. A fact is either an initialized typed
`FrameFactsValue` marked available, or an explicit unavailable state. Default
facts and absent keys are missing. Values allow text up to 4,096 UTF-16 code
units, finite doubles, signed Int64s and `DateTimeOffset`. Unknown input keys do
not activate tokens. Enumeration stops at the first entry over the count limit.

The binder in #1551 must select retained facts and decide freshness, conflicts,
units and provenance before creating the context. Stale and conflicting facts
carry no scalar value. No zero or measured value is invented. An available fact
whose type differs from the catalogue is treated as unsupported by both its
scalar token and its status companion. Input strings are never parsed as
template markup. Newlines, controls and invalid UTF-16 in an expanded string
fail evaluation instead of injecting more lines.

Timestamps retain their explicit offset. The evaluator does not consult the
clock, current culture, current time zone, providers, storage, rig configuration
or a live catalogue. Slice B must distinguish source, capture and processing
times, convert UTC explicitly, and freeze any desired local offset using the
chosen time zone before constructing the context. Format names never request
a host-local conversion.

## Availability and output

`Placeholder` (default) emits `UNAVAILABLE` for missing, stale, conflicting or
unsupported scalars. `DropLine` removes a line containing an unavailable scalar
**unless that same line contains any status token**. In that case the entire
line remains, including `UNAVAILABLE`, so the reason remains visible. Status
tokens themselves are always available and reflect their base fact's state.
Lines containing only literals, empty available text or status tokens remain.
Suppression is decided before expanding values on the removed line.

`FrameFactsEvaluationResult.Lines` contains immutable lines of plain text runs
with `FrameFactsTextStyle` flags. No markup or pixel geometry is emitted. A
renderer must draw these strings as text (and escape them if serializing into
XML/HTML). It must not interpret fact values as tags, markdown or tokens.
`Text` joins run text and lines using LF. An empty source produces zero lines;
explicit blank and trailing lines are preserved. Runs retain source boundaries;
adjacent equal-style runs need not be coalesced. Evaluation errors return no
partial lines.

## Bounds and errors

Source is limited to **4,096 UTF-16 code units**, including syntax and CRLF.
There may be at most **8 source/rendered lines**, including blank and trailing
lines, even if the missing policy would remove some of them. Per line, literal
content in the parsed source and total expanded output are each limited to
**64 Unicode scalar values**; delimiters and emphasis markers do not count.
Supplementary characters count once. There is no wrapping, clipping or silent
truncation. Controls, Unicode line/paragraph separators and malformed UTF-16
are invalid text; only source LF/CRLF/CR creates lines.

The parser scans bounded source without recursion. The evaluator scans bounded
parts and checks at most 65 scalars of an overflowing expansion before failing.
Number formatting has only bounded built-in numeric outputs. Neither token
expansion nor style nesting can recursively evaluate text. The typed context
and catalogue constructors reject invalid programmer input with argument
exceptions; source/evaluation validation returns a diagnostic instead.

Diagnostics have fixed codes and a zero-based UTF-16 `SourceOffset`. Parsing
reports the first error; expansion reports the start of its offending source
part (including a literal's first escape). Source-limit errors use offset 4096.

| Code suffix (all prefixed `frame-facts.`) | Meaning |
| --- | --- |
| `source-required`, `source-too-long` | Null source or source limit exceeded |
| `too-many-lines`, `line-too-long` | Source line count or literal content limit exceeded |
| `malformed-token`, `unknown-token` | Invalid delimiters/key or absent catalogue entry |
| `reserved-namespace`, `unknown-format` | Reserved space-weather key or format outside the entry's closed set |
| `unsupported-syntax`, `invalid-escape`, `invalid-character` | Angle bracket syntax, invalid escape or invalid source text |
| `mismatched-style`, `unclosed-style` | Crossed or unclosed emphasis markers |
| `invalid-expansion`, `expansion-too-long` | Unsafe expanded text or expanded line over 64 scalars |

Acceptance is hardware-free. Deterministic tests use synthetic catalogues and
frozen facts. Current graph identities, retained products, producer versions,
default corner content and rendered bytes are untouched by this contract.
