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
- `scripts/import:cameraagent-final-head-ci-535` — the exact-run protected-CI importer.
- `scripts/test:cameraagent-final-head-ci-import-535` — the deterministic importer gate.
- `scripts/assemble:cameraagent-final-head-535` — the assembler that derives the
  aggregate from its producers (issue #961).
- `scripts/test:assemble-cameraagent-final-head-535` — the deterministic assembler gate.
- `scripts/lib/assembly-identity.fsx` — the metadata-only reader the assembler uses
  for a test assembly's MVID, configuration and informational version.
- `tests/fixtures/issue-535/final-head/` — the fixtures the validator and recorder
  gates exercise.

The real aggregate content is produced by the assembler from one campaign, on the
unchanged final head, and never written by hand. PR #806 / issue #719 delivered the
required InProcess and LocalRunner W6 replay-profile producer. Issue #535 owns the
one-time campaign, final validation, and generation publication.

## Running it

```bash
./scripts/validate:cameraagent-final-head-535 local --evidence EVIDENCE.json --bound-head <40-char-sha>
./scripts/validate:cameraagent-final-head-535 final --evidence EVIDENCE.json --bound-head <40-char-sha>
./scripts/test:cameraagent-final-head-535
./scripts/test:record-cameraagent-final-head-535
./scripts/test:cameraagent-final-head-ci-import-535
./scripts/test:assemble-cameraagent-final-head-535
```

Assembling the aggregate from its producers:

```bash
./scripts/assemble:cameraagent-final-head-535 \
    --bound-head <40-char-sha> \
    --campaign-root <issue-211 run root holding run-manifest.json> \
    --dual-agent TestResults/issue-197/dual-agent/five-trial-summary.json \
    --components TestResults/issue-535/<head>/<campaign>/components/components.json \
    --ci TestResults/issue-535/<head>/<campaign>/ci/ci.json \
    --command-log <commandId TAB exitCode TAB receiptPath TAB argv, one per line> \
    --output TestResults/issue-535/<head>/<campaign>/evidence.json \
    --mode final
```

The assembler reads HEAD, the tree and cleanliness from git and refuses unless
HEAD is the bound head and the tree is clean. It re-hashes every retained output
the campaign's run manifest names, refuses the campaign unless its own terminal
summary records every gate as passed on the clean bound head's fingerprint, and
derives each aggregate section from one named source: `artifacts[]` from the run
manifest, `replayProfiles[]` from the two #719 evidence records and their retained
OTLP metrics, `centralTrafficAttempts` from the deny-sink summary plus every
trial's recorded attempts (every term must be a recorded integer; a missing field
is a refusal, not a zero), `dualAgent` from the #197 `five-trial-summary.json`
(`issue-197-five-trial-summary-v2`, final mode, five trials, clean at the bound
head), `testAssemblies[]` from the Release acceptance assembly's metadata, and
`source` from the manifest's dirty-state digest. The `components` and `ci`
projections are carried verbatim and their support directories copied beside the
output. It validates the result in the requested mode before writing; a refusal
writes nothing, and the validator's document says why. Replay node lists are
carried in the order the producer recorded them; the assembler never sorts a
record into shape, and its gate proves that a definition-order record reaches
the validator's refusal rather than being reordered on the way.

Two inputs are operator assertions and are recorded as such. The command log's
ids, exit codes and argv are typed by the operator; only each receipt's digest is
derived, and argv is hashed rather than carried because final evidence refuses
free text. `heads.reviewed` and `heads.candidateB` have no producer; they are the
operator's binding and are checked only for alignment with the bound head
(`heads.execution` is the run manifest's revision and `heads.protectedCi` is
checked against `ci.run.headSha` by the validator).

The acceptance assembly the campaign executed is bound by digest when the
campaign retained it: `scripts/test:cameraagent-standalone-211` writes
`test-assembly.sha256` into the run root (bound by the run manifest), and the
assembler requires the assembly it reads to have that digest. Without that file
the binding is weaker and is said so in the assembler: a clean tree at the bound
head and a Release build carrying that head is the same source, not provably the
same bytes.

The three replay counters have no producer field. Each is derived from what the
campaign retained, and each is named in the assembler for what it observes rather
than for the property the field is named after:

- `fallbacks` is the #719 record's `attemptCount - 1` (durable) plus the peak of
  the sampled replay retry-wait and terminal backlog gauges, so a retry that
  completed between telemetry samples is still counted through `attemptCount`.
- `publishedOutputs` is the peak delivery-outbox backlog observed. The durable
  fact is `published_flag` on the execution's outputs, which no producer projects
  yet; under the campaign's standalone configuration nothing is ever enqueued for
  delivery, so a zero here says "no delivery backlog was observed", not "the replay
  was proven unpublished".
- `liveRunnerDispatches` is zero once the runner's job meter is recorded only for
  the LocalRunner profile with no non-completed outcome. The adapter routes to the
  runner only for Replay-class executions, so the value follows from routing; the
  per-attempt execution route is durable but not projected into evidence (#799).

A nonzero value is carried, not hidden, so that the validator is what refuses it.

### Producing the real generation

The order that was proven end to end on `aa5ec674` (issue #961), all on one
clean bound head with `development/v1` not advancing between steps:

1. `scripts/test:cameraagent-standalone-211` — the campaign; note its run root.
2. `scripts/test:cameraagent-dual-standalone-smoke` with the default five trials
   (final mode; it runs the #171 baseline first). The host must be quiescent
   (`HVO_SMOKE_QUIESCENCE_WAIT_SECONDS` raises the wait).
3. `scripts/record:cameraagent-final-head-components-535` into
   `TestResults/issue-535/<head>/components`.
4. `gh workflow run ci.yml --ref <branch whose tip is the head>`, then
   `scripts/import:cameraagent-final-head-ci-535` into
   `TestResults/issue-535/<head>/ci/ci.json`.
5. `scripts/assemble:cameraagent-final-head-535 --mode final` into
   `TestResults/issue-535/<head>/<campaign>/evidence.json`.
6. `scripts/record:cameraagent-final-head-535 publish`, then `verify` and `latest`.

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

The order the literal carries is the **recorded execution order**: the #719
producer projects `processing_execution_nodes` by rowid, which is the order the
compiled plan scheduled the nodes, and both profiles on every campaign head have
recorded it. Until #961 the literal carried the W6 template's *definition* order,
in which `combined-preview` precedes `quality` and `cloud`; the plan schedules it
after them because it consumes their outputs. Same fourteen nodes, different order,
and no producer had ever emitted the literal's order — so the literal refused every
honest record, and the only ways past it were to reorder a record into shape or to
weaken the comparison to a sorted set. Both were rejected: the first is evidence
reporting an order it did not observe, and the second would stop detecting a real
reorder. The literal changed instead, and the definition order is kept as the
fixture `replay-nodes-definition-order` so the distinction stays a refusal.


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

The gate covers source and review identity, command receipts, test assembly
identity, the #719 replay profiles, #197 dual-agent admissibility, central
traffic, paths, schema-by-construction sanitisation, exact protected CI, and the
four independently required component lanes.

The validator also requires an imported exact-head CI projection in final mode.
Local mode may omit CI and remains useful for staging, but can never yield
`acceptanceReady`.

Filesystem facts are not JSON facts. The validator wrapper resolves every cited
CI/component support path relative to the evidence document, rejects missing,
linked, hard-linked, unsafe, length-mismatched or digest-mismatched files, and the
recorder copies those verified files into each immutable generation before
publication. The CI importer itself queries one numeric GitHub Actions run and exact
attempt, verifies the repository, workflow path and exact-head blob identity,
reads every exact-attempt job page, parses one bounded classifier record, downloads
each required artifact as an opaque ZIP, and checks its GitHub-reported byte length
and SHA-256 against the downloaded bytes. Every retained CI artifact also contains
one bounded producer record written by its actual Actions job; the importer checks
run, attempt, head, workflow path, job key and artifact name from that embedded
record rather than inferring the producer from the artifact name. It emits no URLs,
logs, tokens, runner names, absolute paths, or raw API payloads.

Use a fresh first-attempt workflow-dispatch run. GitHub's artifact API binds an
artifact to a run and head but does not report the rerun attempt, so the importer
refuses attempts greater than one rather than attributing an archive to an attempt
it cannot prove. The campaign invocation is:

```bash
./scripts/import:cameraagent-final-head-ci-535 \
  --repo HualapaiValley/HVO.SkyMonitor \
  --run <numeric-run-id> --attempt 1 --bound-head <40-char-sha> \
  --output TestResults/issue-535/<head>/<campaign>/ci.json
```

The projection contains the eighteen jobs aggregated by Required CI. For the
non-PR complete matrix, thirteen must succeed and the five component jobs must be
skipped because Unit, Integration and Coverage already own the complete solution
plan. It also binds the nine artifacts produced by Catalog Contracts, Quality,
Deployment Contracts, Unit, Integration, Architecture & Publish and Coverage.
The pure validator independently requires the exact job and artifact inventories,
recomputes their expected conclusions from the complete plan, and rejects absent,
unknown, duplicate, stale-head, wrong-run, failed, cancelled, skipped-required or
unbound records. A supplied top-level `conclusion: success` cannot override a failed
job.

The complete matrix deliberately skips the component jobs. #535 independently
requires `shared`, `cameraagent`, `combined`, and `delivery`; a separate LogicHost
component rerun is not part of the standalone CameraAgent claim. Produce the four
exact-head replacements on a clean bound head with the repository-owned runner:

```bash
./scripts/record:cameraagent-final-head-components-535 \
  --bound-head <40-char-sha> \
  --catalog-bundle <verified-hyg-v42-bundle> \
  --output-dir TestResults/issue-535/<head>/<campaign>/components
```

The runner executes each lane's existing component restore/build, exact Unit and
Integration selections, strict TRX result and counter checks, component coverage
enforcement, and required publish operation. It cleans shared result roots between
lanes, archives only the resulting evidence, and emits `components.json` after the
source remains clean and unchanged. The final aggregate cites those four archives;
the validator and immutable-generation recorder bind their actual bytes.

## Extending it

Add the fixture before the predicate. A predicate added without a fixture that fails
without it is a predicate nobody has shown to work, and the gate will pass either
way — which is the shape being guarded against.
