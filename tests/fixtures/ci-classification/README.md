# CI push-trigger guard fixtures

`scripts/test:ci-classification` carries a small Python guard that decides whether
`.github/workflows/ci.yml` really triggers on a push to a branch the classifier
treats as protected. The guard runs against that one real file. Running it against
one input that is expected to pass proves the guard is satisfiable, not that it
discriminates: a guard that accepted everything would pass that check too.

This directory is what separates those two. It holds a corpus of near misses,
each one the real workflow with exactly one property changed, together with the
answer the guard is required to give for each.

## Layout

| Path | What it is |
| --- | --- |
| `generate` | The executable that produces everything else. It is the definition of the corpus. |
| `cases/*.yml` | The generated fixtures, committed so the count below is reproducible. |
| `expectations.tsv` | `name`, `ACCEPT` or `REFUSE`, and the reason, one line per case. Also generated. |

Nothing here is edited by hand. To change the corpus, change `generate` and run it:

```bash
tests/fixtures/ci-classification/generate
```

`generate --check` regenerates into memory and compares against what is committed,
including files in `cases/` that the generator does not produce. `scripts/test:ci-classification`
runs that check before the sweep, so a workflow that moves without the corpus being
refreshed fails with instructions instead of leaving a corpus whose case names no
longer describe their contents.

## Why the cases are derived rather than written

Each fixture is the repository's real `ci.yml` with one mutation applied. A corpus
of standalone miniature workflows would test the guard against files nobody ships,
and the guard's whole job is to decide about this file. Derivation also means a case
cannot quietly stop being a near miss when the workflow changes, because the
generator asserts the shape it mutates and exits with the line it actually found.

## The expectations do not move to meet the guard

`expectations.tsv` states what the correct answer is, in one place, with the reason
beside it. When a case and the guard disagree, one of them is a defect, and the
sweep prints which case, what was expected, what happened, and the guard's own
stated reason. The fix goes into whichever one is wrong. It never goes into the
expectation column to make the sweep green.

Two cases carry their history in the reason column because their expectation was
reversed during review: `unknown-on-key` and `misspelled-push-beside-real-push`
both name an undefined event beside a correct `push:` trigger. GitHub refuses to
load such a workflow at all, so no run is produced, and the guard must refuse them.
The earlier round had them accepted on the argument that the real trigger survives.
It does not.

## Current size

59 cases, 14 accepted and 45 refused. The guard agrees with all of them; the sweep
prints the count it checked, so the number in the output is measured rather than
asserted.

## Supersedes

Section 5 of the CMD-REVIEW-366 reconstruction note on pull request 824,
`issuecomment-5609325282`, described these probe shapes in prose for a rebuilder,
because at the time they existed only in a session-scoped scratch directory. That
section is superseded by this directory. The shapes are executable here, and prose
describing them elsewhere is a second definition that nobody will update. Read
`generate`, not the note.
