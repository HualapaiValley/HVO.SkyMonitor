# CameraAgent final-head release-readiness evidence

Issue #535 asks whether standalone CameraAgent is software complete. That question
is answered today by reading several summaries written by several scripts, and no
artifact says they all passed on one reviewed commit. This runbook covers the
machinery that replaces that reading with a predicate.

## What is here now, and what is not

Built and usable:

- `scripts/issue-535-final-head-aggregate.jq` — domain and cross-record predicates.
- `scripts/validate:cameraagent-final-head-535` — the pure validator.
- `scripts/test:cameraagent-final-head-535` — the deterministic fixture gate.
- `tests/fixtures/issue-535/final-head/` — the fixtures that gate exercises.

Not built, and deliberately so: the real aggregate generation. It is blocked by
#719 and must be produced once, on the unchanged final head, after every #535
blocker closes. The validator exists so that the generation step has something to
run against, not so that a generation can be produced early.

## Running it

```bash
./scripts/validate:cameraagent-final-head-535 local --evidence EVIDENCE.json --bound-head <40-char-sha>
./scripts/validate:cameraagent-final-head-535 final --evidence EVIDENCE.json --bound-head <40-char-sha>
./scripts/test:cameraagent-final-head-535
```

The validator is pure. It reads, recomputes and writes one canonical JSON result to
standard output, and mutates nothing — no staging, no publication, no generation.
Exit status is the contract: zero only when the requested mode's readiness holds.

`local` requires every local predicate and yields `localReady`. `final` additionally
requires the strongest claimability state and is the only thing that may yield
`acceptanceReady`. **A recorder may never set `acceptanceReady`**; it is derived in
the validator and nowhere else.

The two modes diverge in four places, each with a fixture that passes one and fails
the other: a claimability state below the ceiling, a dirty tree, a nonzero command
receipt, and a field declared as free text. Two modes that never diverge are one
check under two names, so each divergence is kept honest by a fixture rather than by
the description.

## Rules that are easy to get backwards

### A supplied verdict is a claim, not a result

Every `passed`, `ready`, `citable` or `acceptanceReady` value appearing in an input
is recomputed. None is read as an answer. This is the whole point of the issue: the
failure being guarded against is evidence that reports a result it did not observe,
and trusting a writer's own verdict would reproduce exactly that.

Presence is not a pass either. An artifact that exists but carries no explicit
recomputed predicate is refused.

### A modification time is not freshness evidence

An artifact may carry the relation `AssemblyWrittenUtc >= LatestSourceWriteUtc`.
That relation establishes ordering under honest conditions and **nothing at all**
under careless or adversarial ones: a copied tree satisfies it, a restored artifact
satisfies it, and a `touch` satisfies it. Modification times are not content
addressed and are trivially forged.

**Freshness is carried by the byte hashes and by the tree-plus-clean fingerprint.**
The validator therefore refuses an artifact whose only binding is temporal — one
with no `sha256` or no `sourceTree` fails with `freshness-without-content-binding`,
regardless of how favourable its timestamps look. Do not add a predicate that
accepts the mtime relation as sufficient, and do not read a passing freshness check
as meaning the artifact matches its source.

### Fingerprint equality is not cleanliness

The source section carries a start and an end fingerprint, and they must match: a
tree that changed during a run invalidates everything produced across the change,
because the recorded revision describes one tree while the run spanned two.

**Equality does not imply the tree was clean.** A tree that was dirty from the
beginning has an unchanged fingerprint, so a stability check alone is satisfied
while the run executed modified code and stamped every artefact with the committed
revision. This is not hypothetical: a campaign was nearly run that way, and the
existing worktree guard would not have caught it, because it fingerprints at start
and compares later.

Cleanliness is therefore asserted separately, and the fixture
`source-dirty-but-consistent` exists to keep it that way — matching fingerprints,
dirty tree, refused as final evidence. Do not collapse the two checks on the
reasoning that equal fingerprints prove a stable tree. They do. Stability is not
the property in question.

### A revision must be the bound head, and `working-tree` is rejected by name

An artifact's revision must equal the bound head exactly. The literal string
`working-tree` is additionally rejected under its own identifier, because at least
one writer emits it as a fallback when its revision variable is unset. That default
makes an evidence file naming no commit byte-indistinguishable from one produced at
a real head, so it is refused explicitly rather than tolerated as a legacy spelling.

The bound head must be a full forty-character lowercase commit SHA. A branch name or
an abbreviation is refused, so a bound head cannot silently become a moving target.

### Sanitisation is done by construction, not by scanning

An earlier version of this contract scanned artifact text for credential-like words
and URI authorities. It was measured on 2026-09-08 against the categories
`AGENTS.md` names, and the result is recorded here rather than paraphrased, because
the sentence it replaces flattered the code:

| Category the rule names | refused | of | |
| --- | --- | --- | --- |
| credentials, keyword present | 4 | 4 | covered |
| bearer or session material with no keyword | 0 | 3 | **not detected** |
| service authorities with a scheme | 2 | 2 | covered |
| service authorities as bare `host:port` | 0 | 2 | **not detected** |
| absolute paths, POSIX, Windows and UNC | 0 | 4 | **not detected by the scan** |
| raw payload and log dumps | 0 | 2 | **not detected** |

Six of seventeen strings that had to be refused were refused, against one false
alarm in six clean strings — a documentation filename containing the word `secret`.
The sharp case: `Pwd=P@ssw0rd` inside a connection string passed a predicate named
for secrets, because it matches the *word* `password` and that spelling is not that
word.

Tuning the patterns was rejected as the remedy. A detector at that rate whose output
is a green is worse than no detector, because the green reads as "scanned and clean"
when it means "scanned for two of five categories and found nothing in those".

**So this layer no longer tries to recognise a secret.** Every field an evidence
record may carry is named in `evidence_schema` with the shape its value must have. A
field that is not named is refused as `evidence-field-unknown`; a value that does not
have its field's shape is refused as `evidence-field-shape-invalid`; a top-level
section that is not named is refused as `evidence-section-unknown`. A value
constrained to a hash, a commit, a bounded token or a bounded single-line path has no
room for a connection string, a URI, a PEM block or a JWT, and nothing has to be
recognised for that to hold.

Where a field must carry free text — an argument vector, a note, a failure reason —
it is declared `free-text` and **refused in `final` mode rather than scanned**, as
`evidence-free-text-in-final`. Local mode accepts it, because the staging record of a
failed run is worth keeping; final evidence carries the hash of that text instead of
the text. The fixtures `free-text-note` and `free-text-note-credential` differ only
in what the note says — one ordinary prose, one carrying a synthetic connection-string
credential and a service authority — and they must produce identical results. If they
ever diverge, detection has crept back in.

Three boundaries, because each is easy to overclaim:

- The schema governs what may appear and in what shape. It does **not** govern what
  must appear; required-field rules stay in the domain predicates, so one condition
  produces one problem rather than two.
- `path` is the one structurally free field, and its shape only bounds length and
  forbids a line break. Relativity and traversal are refused by `path_problems` —
  that is where the absolute-path category is actually covered, and the measurement
  above tested the deleted scan in isolation and so did not credit it.
- A bounded token cannot exclude every possible secret, because some secrets are
  short alphanumeric strings. It excludes every secret needing a separator, an
  underscore, a scheme, a path or more than forty characters, and the domain
  predicates then constrain most such fields to a handful of literals. The bound is
  forty because the longest enumerated value the schema carries is thirty-nine.

### Each replay profile is checked against #719, not against the other profile

The replay predicate once compared the two profiles' ordered node lists to each
other. That is a differential check standing in for an absolute one, and it passes
precisely when the failure is systematic rather than local. Measured on 2026-09-08,
it admitted all four of these as `acceptanceReady`, with no problems at all: both
profiles carrying an empty list, both carrying one fabricated node, both reversed
identically, and both omitting the `nodes` key entirely.

The last is the reason it changed. `[null] | unique | length` is `1`, so **missing
evidence scored as matching evidence** — the fail-open shape this issue exists to
remove, sitting inside a predicate written to remove it.

`canonical_replay_nodes` now holds #719's ordered fourteen nodes as a literal, and
each profile is checked against it. The list is kept here as a literal on purpose: a
reader checks it against the issue by eye, and if #719 changes its node set this
literal must change with it, which the fixture gate makes loud. An absolute check
subsumes the differential one — two lists that each equal the canonical list equal
each other — so `replay-nodes-not-identical` was **deleted** rather than kept
alongside. Two checks where one is strictly stronger is how the weaker one is later
read as the guarantee.


## The claimability ceiling is inherited, not invented

`EvidenceSourceIdentity` derives exactly three states, and **none of them says
claimable**:

| State | Meaning |
| --- | --- |
| `dirty-development-not-claimable` | the working tree was not clean |
| `clean-unrequested-not-claimable` | clean, but no revision was requested to cross-check |
| `clean-source-attributed-review-required` | clean and attributed — the strongest state that exists |

The ceiling is *review-required*. A run cannot self-certify as final evidence no
matter how clean it is, which is precisely #720's requirement that `acceptanceReady`
be derived only by the validator, already implemented where provenance is done
properly. The validator adopts that ceiling rather than reinventing it: any fourth
state is refused as `claimability-above-ceiling`, and `final` mode additionally
requires the strongest of the three.

If you are tempted to add a state above the ceiling, the thing you actually want is
a validator run.

## Why the gate asserts identifiers rather than exit status

`scripts/test:cameraagent-final-head-535` asserts, for every case, the exact set of
problem identifiers the validator must produce — not merely that it failed. A
validator that fails for the wrong reason is a validator that will pass for the
wrong reason later, and an exit code cannot tell those two apart.

The gate also asserts **refusals**: a missing evidence set, an empty one, an
abbreviated head and a branch name as a head must each be refused rather than
reported as an absence of problems. Reading nothing and reporting no problems is
the defect this issue exists to remove; the validator is not exempt from it.

The gate's own property has been shown by construction rather than by reading.
Removing the free-text refusal, neutralising the canonical node check, and dropping
the unknown-field refusal each turn the gate red and name the case that detects
them. A gate whose assertions have never been observed to fail is a gate nobody has
shown to work.

## What this gate covers, and what it does not

Eight classes are covered: source and review identity, command receipts, test
assembly identity, the #719 replay profiles, #197 dual-agent admissibility,
central traffic, paths, and the evidence schema that replaces sanitisation
scanning.

**Two are not, and the gate is deliberately short rather than apparently
complete.** A gate that covers eight classes and says so is more useful than one
that covers eight and reads as though it covers ten.

*Filesystem and PE facts* — symlink, hard link, ownership, mode, byte length,
MVID — are not JSON facts. Asserting them here would look like coverage without
being any, so they belong to the compiled helper.

*The CI import* is not implemented in this layer, and this is where a deferral
would quietly become a pass. "Not implemented yet" reads as success to everything
downstream, which is the same defect as evidence reporting what it did not
observe. So a record carrying imported CI slots is **refused**, not skipped: it
fails with `ci-slots-unverifiable` and says why. A predicate here could check the
shape of a supplied CI conclusion while recomputing nothing about its provenance,
and a shape-only check living in a file named for provenance is exactly how a
later reader mistakes one for the other.

When the helper can query a real run and attempt, that refusal is replaced by
predicates that recompute. Until then the absence is loud rather than invisible.

## Extending it

Add the fixture before the predicate. A predicate added without a fixture that fails
without it is a predicate nobody has shown to work, and the gate will pass either
way — which is the shape being guarded against.
