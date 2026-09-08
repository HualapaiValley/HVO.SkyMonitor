# Declarative cross-record predicates for issue #535 final-head acceptance evidence.
#
# This file owns domain and cross-record reasoning only. Raw JSON well-formedness,
# filesystem and PE identity, hashing, canonicalisation, CI import and publication
# belong to the calling script and, later, to the compiled helper; a predicate here
# may assume its input parsed but must never assume a supplied verdict is true.
#
# Every `passed`, `ready`, `citable` or `acceptanceReady` value that appears in an
# input is treated as a claim to recompute, never as a result to read. That rule is
# the reason this file exists: issue #720 is about evidence that reports a result it
# did not observe.

def problem($id; $detail): { id: $id, detail: $detail };

# --- source identity -------------------------------------------------------
# A revision is admissible only when it is exactly the bound head. The literal
# "working-tree" is rejected by name because at least one writer emits it as a
# fallback when its revision variable is unset, which makes an evidence file that
# names no revision byte-indistinguishable from one produced at a real head.
def revision_problems($bound):
    [ .artifacts[]?
      | select(.sourceRevision != null)
      | select(.sourceRevision != $bound)
      | problem("revision-not-bound-head";
          "\(.path): revision \(.sourceRevision) is not the bound head \($bound)") ]
  + [ .artifacts[]?
      | select(.sourceRevision == "working-tree")
      | problem("revision-working-tree-literal";
          "\(.path): revision is the literal working-tree, which names no commit") ];

# --- claimability ceiling --------------------------------------------------
# EvidenceSourceIdentity derives three states and none of them says "claimable";
# the strongest is clean-source-attributed-review-required. A writer that reports
# anything above that ceiling has invented a state, and the validator refuses it
# rather than inheriting it.
def derived_claimability_states:
    ["dirty-development-not-claimable",
     "clean-unrequested-not-claimable",
     "clean-source-attributed-review-required"];

def claimability_problems:
    derived_claimability_states as $states
    | [ .artifacts[]?
        | select(.claimability != null)
        | select((.claimability | IN($states[])) | not)
        | problem("claimability-above-ceiling";
            "\(.path): claimability \(.claimability) is not one of the three derived states") ];

# Final evidence additionally requires the strongest of the three.
def claimability_final_problems:
    [ .artifacts[]?
      | select(.claimability != null)
      | select(.claimability != "clean-source-attributed-review-required")
      | problem("claimability-not-final";
          "\(.path): claimability \(.claimability) is not clean-source-attributed-review-required") ];

# --- freshness -------------------------------------------------------------
# A modification-time ordering establishes ordering under honest conditions and
# nothing under careless or adversarial ones: a copied tree, a restored artifact
# and a touched file all satisfy it. It is therefore never sufficient on its own,
# and an artifact that offers only that relation as its freshness evidence is
# rejected. Byte hashes and the tree-plus-clean fingerprint carry freshness.
def freshness_problems:
    [ .artifacts[]?
      | select((.sha256 // "") == "" or (.sourceTree // "") == "")
      | problem("freshness-without-content-binding";
          "\(.path): has no sha256 or no sourceTree, so nothing binds it to content") ];

# --- admissibility ---------------------------------------------------------
# Presence is not a pass. An artifact must carry an explicit recomputed predicate.
def admissibility_problems:
    [ .artifacts[]?
      | select(.admissible != true)
      | problem("artifact-not-admissible";
          "\(.path): admissible is not true") ];

# --- head alignment --------------------------------------------------------
# The reviewed head, the head the commands executed against, candidate B's source,
# the protected-CI head and the aggregate's own source must all be the same commit.
# Any inequality means the evidence describes more than one tree while claiming to
# describe one, which no individual record can detect from inside itself.
def heads_problems:
    (.heads // null) as $h
    | if $h == null then []
      else
        ([$h | to_entries[] | .value] | unique) as $distinct
        | if ($distinct | length) <= 1 then []
          else
            [ $h | to_entries[]
              | problem("heads-not-aligned";
                  "heads.\(.key) is \(.value); every head in this section must be the same commit") ]
          end
      end;

# Final evidence must be produced from a clean tree. A dirty tree means the source
# fingerprint describes something no commit contains.
def source_clean_problems:
    if (.source.clean // null) == true then []
    else [ problem("source-not-clean";
             "source.clean is \(.source.clean // "absent"); final evidence requires a clean tree") ]
    end;

def all_problems($bound):
    revision_problems($bound) + claimability_problems + freshness_problems
    + admissibility_problems + heads_problems;

def evaluate($bound; $mode):
    (all_problems($bound)
      + (if $mode == "final" then claimability_final_problems + source_clean_problems else [] end)) as $problems
    | {
        schemaVersion: "issue-535-final-head-validation-v1",
        mode: $mode,
        boundHead: $bound,
        artifactCount: (.artifacts | length),
        problems: $problems,
        localReady: (($problems | length) == 0),
        acceptanceReady: ((($problems | length) == 0) and ($mode == "final"))
      };
