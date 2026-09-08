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
            # One condition, one problem. Emitting a problem per head reports the
            # same misalignment five times and makes a count of problems useless.
            [ problem("heads-not-aligned";
                "heads name \($distinct | length) different commits, and must name one: "
                + ([$h | to_entries[] | "\(.key)=\(.value)"] | join(", "))) ]
          end
      end;

# Final evidence must be produced from a clean tree. A dirty tree means the source
# fingerprint describes something no commit contains.
# A tree that changed during the run invalidates every artefact produced across the
# change, because the recorded revision describes one tree and the run spanned two.
def source_stability_problems:
    if (.source.startFingerprint // null) == null or (.source.endFingerprint // null) == null then
        [ problem("source-fingerprint-absent";
            "source has no start or end fingerprint, so nothing shows the tree held still") ]
    elif .source.startFingerprint != .source.endFingerprint then
        [ problem("source-fingerprint-changed";
            "source fingerprint moved from \(.source.startFingerprint) to \(.source.endFingerprint) during the run") ]
    else [] end;

# Cleanliness is asserted separately from stability, and that separation is the
# point rather than an accident. A consistently dirty tree has an unchanged
# fingerprint, so equality is satisfied while the run executed modified code and
# stamped every artefact with the committed revision. Equality proves the tree held
# still; it never proves the tree matched the commit. Do not collapse these two.
def source_clean_problems:
    if (.source.clean // null) == true then []
    else [ problem("source-not-clean";
             "source.clean is \(.source.clean // "absent"); final evidence requires a clean tree") ]
    end;

# --- command receipts ------------------------------------------------------
# A nonzero receipt is kept in staging, because the record of a failed run is
# itself evidence, but it can never support a final claim. A receipt without a
# content hash binds to nothing and is refused in either mode.
def command_problems:
    [ .commands[]?
      | select((.receiptSha256 // "") == "")
      | problem("command-receipt-unbound";
          "command \(.commandId): receipt has no sha256, so nothing binds the recorded run to its output") ];

def command_final_problems:
    [ .commands[]?
      | select(.exitCode != 0)
      | problem("command-exit-nonzero";
          "command \(.commandId): exit \(.exitCode); a failed run is retained but cannot support final evidence") ];

# --- test assemblies -------------------------------------------------------
# EvidenceSourceIdentity already refuses a non-Release binary and one whose
# informational version does not carry the head. This builds on that binding
# rather than duplicating it: the same two facts are required of every assembly
# an aggregate cites, because an aggregate may cite an assembly no writer checked.
def assembly_problems($bound):
    [ .testAssemblies[]?
      | select(.configuration != "Release")
      | problem("assembly-not-release";
          "\(.path): configuration is \(.configuration); evidence binaries are Release") ]
  + [ .testAssemblies[]?
      | select((.informationalVersion // "") | test($bound) | not)
      | problem("assembly-not-at-bound-head";
          "\(.path): informational version \(.informationalVersion) does not carry the bound head") ];

# --- paths ------------------------------------------------------------------
# Evidence paths are relative to the campaign root and stay inside it. An absolute
# path leaks the producing host's layout into a record meant to be portable, and a
# traversal segment escapes the root entirely. Both are refused before any content
# is considered, because a path this validator cannot reason about is a path whose
# content it cannot vouch for.
#
# Symlink, hard-link, ownership and mode checks are deliberately absent here: they
# are filesystem facts, not JSON facts, and belong to the compiled helper. Asserting
# them in jq would look like coverage without being any.
def path_problems:
    [ .artifacts[]?
      | select((.path // "") | test("^(/|[A-Za-z]:[\\\\/]|\\\\\\\\)"))
      | problem("path-not-relative";
          "\(.path): evidence paths are relative to the campaign root") ]
  + [ .artifacts[]?
      | select((.path // "") | test("(^|/)\\.\\.(/|$)"))
      | problem("path-escapes-root";
          "\(.path): contains a traversal segment and leaves the campaign root") ]
  + ( [ .artifacts[]?.path ] as $paths
      | ($paths | length) as $n
      | (($paths | unique) | length) as $u
      | if $n != $u then
          [ problem("path-duplicated";
              "\($n - $u) artifact path(s) appear more than once; each path identifies one artifact") ]
        else [] end );

# --- sanitisation -----------------------------------------------------------
# Evidence is published, so it must not carry credentials or the addresses of the
# services that produced it. These patterns are deliberately coarse: a false alarm
# costs someone a rename, and a miss publishes a secret.
def sanitisation_problems:
    ( [ .artifacts[]? | tostring ] ) as $texts
    | [ $texts[]
        | select(test("(?i)(authorization|bearer|password|secret|api[-_]?key|private key|BEGIN [A-Z ]*PRIVATE KEY)"))
        | problem("possible-secret-in-evidence";
            "an artifact record contains credential-like text and must be sanitised before publication") ]
    + [ $texts[]
        | select(test("[a-z][a-z0-9+.-]*://[^/\\s\"]+"))
        | problem("service-authority-in-evidence";
            "an artifact record contains a URI authority, which names the producing host") ];

def all_problems($bound):
    revision_problems($bound) + claimability_problems + freshness_problems
    + admissibility_problems + heads_problems + command_problems
    + assembly_problems($bound) + source_stability_problems
    + path_problems + sanitisation_problems;

def evaluate($bound; $mode):
    (all_problems($bound)
      + (if $mode == "final" then claimability_final_problems + source_clean_problems + command_final_problems else [] end)) as $problems
    | {
        schemaVersion: "issue-535-final-head-validation-v1",
        mode: $mode,
        boundHead: $bound,
        artifactCount: (.artifacts | length),
        problems: $problems,
        localReady: (($problems | length) == 0),
        acceptanceReady: ((($problems | length) == 0) and ($mode == "final"))
      };
