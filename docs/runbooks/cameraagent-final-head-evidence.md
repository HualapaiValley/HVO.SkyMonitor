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

## Three rules that are easy to get backwards

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

## Extending it

Add the fixture before the predicate. A predicate added without a fixture that fails
without it is a predicate nobody has shown to work, and the gate will pass either
way — which is the shape being guarded against.
