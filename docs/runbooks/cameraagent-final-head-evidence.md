# CameraAgent final-head release-readiness evidence

Issue #535 asks whether standalone CameraAgent is software complete. That question
is answered today by reading several summaries written by several scripts, and no
artifact says they all passed on one reviewed commit. This runbook covers the
machinery that replaces that reading with a predicate.

## What is here now, and what is not

Built and usable:

- `scripts/issue-535-final-head-aggregate.jq` — domain and cross-record predicates.
- `scripts/validate:cameraagent-final-head-535` — the pure validator.
- `scripts/record:cameraagent-final-head-535` — the recorder that publishes a
  validated generation, and verifies one that was published earlier.
- `scripts/test:cameraagent-final-head-535` — the deterministic validator gate.
- `scripts/test:record-cameraagent-final-head-535` — the deterministic recorder gate.
- `tests/fixtures/issue-535/final-head/` — the fixtures both gates exercise.

Not built, and deliberately so: the real aggregate content. The machinery that
records a generation is complete and gated, but the campaign it will record must be
produced once, on the unchanged final head. PR #806 / issue #719 delivered the
required InProcess and LocalRunner W6 replay-profile producer. Issue #535 now owns
the one-time campaign, final validation, and generation publication. The tooling
exists so that the generation step has something to run against and somewhere to
put the result, not so that a real generation can be produced early.

## Running it

```bash
./scripts/validate:cameraagent-final-head-535 local --evidence EVIDENCE.json --bound-head <40-char-sha>
./scripts/validate:cameraagent-final-head-535 final --evidence EVIDENCE.json --bound-head <40-char-sha>
./scripts/test:cameraagent-final-head-535
./scripts/test:record-cameraagent-final-head-535
```

Publishing and re-reading a generation:

```bash
./scripts/record:cameraagent-final-head-535 publish --evidence EVIDENCE.json \
    --bound-head <40-char-sha> --campaign <id> [--mode local|final]
./scripts/record:cameraagent-final-head-535 latest --bound-head <40-char-sha> --campaign <id>
./scripts/record:cameraagent-final-head-535 verify --bound-head <40-char-sha> --campaign <id>
./scripts/record:cameraagent-final-head-535 clean  --bound-head <40-char-sha> --campaign <id>
```

Both gates run in CI, in the Quality job's static and lightweight contract checks.

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

## What publication guarantees

The validator owns the verdict; the recorder owns mutation and owns nothing else.
`publish` reserves the campaign, then runs the validator before staging a generation.
A validator refusal can leave the campaign directory and its lock file, but creates
no generation and does not advance the pointer. The verdict is copied into the index
as the validator produced it, and `verify` recomputes it rather than trusting it.

The recorder targets **Linux with Bash, jq, GNU coreutils and util-linux `flock`**.
Use an owner-controlled local filesystem with working advisory locks and same-directory
atomic rename; this is not a Windows reparse-point or distributed-filesystem contract.
New files/directories use `umask 077`; existing roots and evidence must remain under
trusted ownership. Path checks reject links already present, not hostile concurrent
replacement of parent directories or evidence by an independent filesystem writer.

`publish` and `clean` take the same nonblocking campaign reservation. An overlapping
mutation refuses with `Campaign is busy`, rather than allocating the same generation
or deleting a live publisher's staging. The `.record.lock` file is persistent and
must **not** be unlinked: another inode would permit a second independent lock.
Process exit releases the reservation; abandoned staging can then be cleaned. Readers
do not take that lock and may refuse if publication changes state during their read;
retry a read after the publisher completes.

Three properties carry the layout, and each has a gate case that must refuse rather
than a happy path that happens to pass.

**An uncommitted generation is ignored.** A run in progress writes into
`.incomplete-<n>-<pid>`, and a generation becomes visible only through an atomic
rename into its digit-named directory. Numbering counts only digit-named directories
holding both the index and the commit envelope, so an interrupted run cannot consume
a generation number, cannot be read as a result, and is removed by `clean` while its
committed neighbours survive.

**A committed generation is immutable.** Publishing into an occupied generation
number is refused rather than retried, because reaching one means discovery and the
filesystem disagree. Rename uses no-target-directory and no-clobber semantics and
checks that staging actually moved: neither directory nesting nor a silently skipped
move can produce a success receipt. Failure leaves incomplete staging for `clean` and
does not advance the pointer.

`verify` binds index and envelope schema, campaign, full head and generation to the
requested namespace, including the envelope's index filename. Generation numbers are
canonical positive integers of at most fifteen digits; aliases such as `01` are not
accepted. It recomputes index bytes and digest against the envelope, evidence bytes
and digest against the index, and re-runs the validator against that evidence using
the requested bound head and recorded mode. The fresh verdict must equal the recorded
one. Copying a valid generation into a different generation/head/campaign namespace
fails even though its internal hashes still agree.

**The pointer moves last.** `latest-generation.json` is rewritten only after the
committed generation has been re-verified in place, so a reader following the pointer
never reaches a generation that was not complete when the pointer named it. `latest`
does not trust the pointer either: it binds schema, campaign, head, generation and
relative generation path to the request, re-verifies the generation and compares both
index byte count and digest. Missing or mismatched state is refused.

Recorded evidence paths are canonical and repository-relative. The recorder rejects
symlinks in every path component **before** canonicalization, hard-linked files
(link count other than one), nonregular evidence files, parent traversal and control
characters. The same checks protect later verification, index/envelope/pointer files
and output paths; a genuine regular-file copy remains valid. Evidence outside the
repository is refused rather than recorded by absolute path, so a generation never
carries a private path from the machine that produced it. Campaign identifiers must be a single
safe path segment. With `--recorded-utc` pinned the index is a pure function of its
inputs, which is what lets two generations be diffed and an unchanged one be shown to
be unchanged.

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
credential and a service authority — and the gate requires their outputs to be
**byte-identical**, not merely that each matches its own expectation. Those are
different guarantees, and only the stronger one catches recognition creeping back:
a refusal that quotes the text it refused still produces the same identifier and the
same exit status for both fixtures, so every fixed expectation passes while the two
are plainly being treated differently — and the quoted credential is then in the
validator's own output.

Three boundaries, because each is easy to overclaim:

- The schema governs what may appear and in what shape. It does **not** govern what
  must appear; required-field rules stay in the domain predicates, so one condition
  produces one problem rather than two.
- `path` carries a grammar, not a length bound. It once carried `^.{1,200}$`, which
  is a length check wearing a shape's name: any text under two hundred characters
  satisfied it, so a connection string sitting in `path` was admitted while the
  identical string in `note` was refused as free text. That made the central claim
  above — free text is confined to declared free-text fields — false as implemented.
  The grammar now admits an optional root, drive or UNC prefix followed by segments
  of `[A-Za-z0-9._-]`, and nothing else; a space, a `=`, a `;` or a `://` fails the
  shape. A leading `/`, a drive prefix and a `..` segment are admitted **on purpose**
  so that `path_problems` keeps reporting each as one problem rather than two, the
  same reason the revision shape admits the `working-tree` literal. Relativity and
  traversal remain `path_problems`' job — that is where the absolute-path category
  is actually covered, and the measurement above tested the deleted scan in isolation
  and so did not credit it. Evidence paths therefore may not contain spaces.
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
literal must change with it, which the fixture gate makes loud. **#719's acceptance
criteria are authoritative and the literal is a copy of them.** A stale copy fails
closed but still gives a wrong answer, refusing evidence that is correct, so when the
two disagree the issue wins and the literal is what changes. An absolute check
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

The gate's own property has been shown by construction rather than by reading. Five
mutations, each reverted: removing the free-text refusal, neutralising the canonical
node check, dropping the unknown-field refusal, and restoring the old `^.{1,200}$`
path bound each turn the gate red and name the case that detects them. The fifth —
making the free-text refusal quote the text it refused — is caught **only** by the
content-independence assertion, because every fixed expectation still passes. A gate
whose assertions have never been observed to fail is a gate nobody has shown to
work.

## What this gate covers, and what it does not

Eight classes are covered: source and review identity, command receipts, test
assembly identity, the #719 replay profiles, #197 dual-agent admissibility,
central traffic, paths, and the evidence schema that replaces sanitisation
scanning.

**Two are not, and the gate is deliberately short rather than apparently
complete.** A gate that covers eight classes and says so is more useful than one
that covers eight and reads as though it covers ten.

*Filesystem and PE facts* — symlink, hard link, ownership, mode, byte length,
MVID — are not JSON facts. The pure validator does not infer them from claims in a
JSON document. The separate recorder now checks its own input evidence file and
publication files for actual byte lengths, links and regular-file status as described
above. That does not verify every nested artifact named inside the evidence set, or
PE identity, ownership and mode provenance; those remain the compiled helper's job.

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
