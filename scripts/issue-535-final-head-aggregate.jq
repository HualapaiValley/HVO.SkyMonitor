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

# --- evidence schema: sanitisation by construction ---------------------------
# This section replaces a scan. The scan matched artifact text against
# credential-like words and a URI-authority pattern, and it was measured on
# 2026-09-08 (PR #759, comment 5589048029): six of seventeen strings the AGENTS.md
# sanitisation rule requires be refused were refused, against one false alarm in
# six clean strings. Bare session material, bare host:port authorities and raw
# payload or log dumps were not detected at all, and `Pwd=P@ssw0rd` inside a
# connection string passed a predicate named for secrets, because it matches the
# word `password` and that spelling is not that word.
#
# Tuning the patterns was rejected as the remedy. Nine successive rounds on #756
# each closed one spelling and left the next, and a detector at that rate whose
# output is a green is worse than no detector: the green reads as "scanned and
# clean" when it means "scanned for two of five categories and found nothing".
#
# So this layer no longer tries to recognise a secret. Every field an evidence
# record may carry is named below with the shape its value must have. A field that
# is not named is refused, and a value that does not have its shape is refused. A
# field constrained to a hash, a commit, a bounded token or a bounded single-line
# path has no room for a connection string, a URI, a PEM block or a JWT, and
# nothing has to be recognised for that to hold.
#
# Three boundaries, stated because each is easy to overclaim:
#
#   * The schema governs what may appear and in what shape. It does not govern
#     what must appear. Required-field rules stay in the domain predicates above,
#     so that one condition produces one problem instead of two.
#   * `path` is the one field whose value is structurally free, and its shape only
#     bounds length and forbids a line break. Relativity and traversal are refused
#     by `path_problems`, which is where the AGENTS.md absolute-path category is
#     actually covered; the measurement above tested the deleted scan alone and so
#     did not credit it.
#   * A bounded token cannot exclude every possible secret, because some secrets
#     are short alphanumeric strings. It excludes every secret that needs a
#     separator, an underscore, a scheme, a path or more than forty characters to
#     express itself, and the domain predicates then constrain most such fields to
#     a handful of literals.
#
# Where a field must carry free text — an argument vector, a note, a failure
# reason — it is declared `free-text` and REFUSED in final mode rather than
# scanned. Local mode accepts it, because the staging record of a failed run is
# worth keeping. Final evidence carries the hash of that text instead of the text.
# That is a fourth real divergence between the modes rather than a nominal one.
#
# The token bound is forty characters because the longest enumerated value the
# schema carries, the claimability state clean-source-attributed-review-required,
# is thirty-nine. It is not tuned against any particular secret format.
def evidence_schema:
    { "artifacts": { "many": true, "fields": {
          "admissible": "bool", "claimability": "token", "note": "free-text",
          "path": "path", "sha256": "sha256", "sourceRevision": "revision",
          "sourceTree": "commit" } },
      "commands": { "many": true, "fields": {
          "argv": "array:free-text", "argvSha256": "sha256", "commandId": "token",
          "exitCode": "uint", "receiptSha256": "sha256" } },
      "testAssemblies": { "many": true, "fields": {
          "configuration": "token", "informationalVersion": "version",
          "mvid": "guid", "path": "path", "sha256": "sha256" } },
      "replayProfiles": { "many": true, "fields": {
          "fallbacks": "uint", "liveRunnerDispatches": "uint", "nodes": "array:token",
          "phase": "uint", "publishedOutputs": "uint", "runner": "token",
          "schemaVersion": "token" } },
      "heads": { "many": false, "fields": {
          "aggregate": "commit", "candidateB": "commit", "execution": "commit",
          "protectedCi": "commit", "reviewed": "commit" } },
      "source": { "many": false, "fields": {
          "clean": "bool", "endFingerprint": "fingerprint",
          "startFingerprint": "fingerprint" } },
      "dualAgent": { "many": false, "fields": {
          "citable": "bool", "evidenceMode": "token" } },
      "centralTrafficAttempts": { "scalar": "uint" },
      # Handled by ci_slot_problems, which refuses the section outright. Naming it
      # here keeps that refusal the only problem such a record produces.
      "ci": { "opaque": true } };

def shape_test($shape):
    . as $v
    | if   $shape == "bool" then ($v | type) == "boolean"
      elif $shape == "uint" then ($v | type) == "number" and $v >= 0 and ($v | floor) == $v
      elif ($v | type) != "string" then false
      elif $shape == "sha256"      then $v | test("^[0-9a-f]{64}$")
      elif $shape == "commit"      then $v | test("^[0-9a-f]{40}$")
      # The literal is admitted by the shape so that revision_problems can keep
      # rejecting it by name; a shape rejection there would report one condition twice.
      elif $shape == "revision"    then $v | test("^([0-9a-f]{40}|working-tree)$")
      elif $shape == "fingerprint" then $v | test("^[0-9A-F]{64}$")
      elif $shape == "guid"        then $v | test("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
      elif $shape == "version"     then $v | test("^[0-9]+\\.[0-9]+\\.[0-9]+\\+[0-9a-f]{40}$")
      elif $shape == "token"       then $v | test("^[A-Za-z0-9][A-Za-z0-9.-]{0,39}$")
      # `.` does not match a line break, so this also refuses an embedded log dump.
      elif $shape == "path"        then $v | test("^.{1,200}$")
      elif $shape == "free-text"   then true
      else false end;

def value_ok($shape):
    if ($shape | startswith("array:")) then
        (type == "array") and ([ .[] | shape_test($shape[6:]) ] | all)
    else shape_test($shape) end;

def schema_records:
    evidence_schema as $schema
    | [ to_entries[]
        | .key as $section
        | select($schema | has($section))
        | .value as $value
        | $schema[$section] as $spec
        | select((($spec.opaque // false) | not) and (($spec.scalar // null) == null))
        | if ($spec.many // false) then
              (if ($value | type) == "array"
               then ($value | to_entries[]
                     | { section: $section, label: "\($section)[\(.key)]",
                         spec: $spec, record: .value })
               else empty end)
          else
              (if ($value | type) == "object"
               then { section: $section, label: $section, spec: $spec, record: $value }
               else empty end)
          end ]
    | map(select((.record | type) == "object"));

def schema_problems:
    evidence_schema as $schema
    | [ keys_unsorted[] as $section
        | select(($schema | has($section)) | not)
        | problem("evidence-section-unknown";
            "top-level section \($section) is not named by the evidence schema, so nothing here can vouch for it") ]
    + [ to_entries[]
        | .key as $section
        | select($schema | has($section))
        | .value as $value
        | $schema[$section] as $spec
        | select(($spec.opaque // false) | not)
        | if ($spec.scalar // null) != null then
              (if ($value | value_ok($spec.scalar)) then empty
               else problem("evidence-field-shape-invalid";
                   "\($section): value does not have shape \($spec.scalar)") end)
          elif ($spec.many // false) then
              (if ($value | type) == "array" then empty
               else problem("evidence-section-shape-invalid";
                   "\($section) must be an array of records") end)
          else
              (if ($value | type) == "object" then empty
               else problem("evidence-section-shape-invalid";
                   "\($section) must be a record") end)
          end ]
    + [ schema_records[]
        | .label as $label
        | .spec as $spec
        | .record
        | to_entries[]
        | .key as $field
        | if (($spec.fields | has($field)) | not) then
              problem("evidence-field-unknown";
                  "\($label): field \($field) is not named by the evidence schema")
          elif (.value | value_ok($spec.fields[$field]) | not) then
              problem("evidence-field-shape-invalid";
                  "\($label): field \($field) does not have shape \($spec.fields[$field])")
          else empty end ];

# Free text is refused in final mode rather than scanned. The text itself is never
# examined, so no spelling of a secret can defeat this.
def free_text_final_problems:
    [ schema_records[]
      | .label as $label
      | .spec as $spec
      | .record
      | to_entries[]
      | .key as $field
      | select($spec.fields | has($field))
      | select($spec.fields[$field] | test("free-text"))
      | problem("evidence-free-text-in-final";
          "\($label): field \($field) is declared free text; final evidence carries its hash, not its text") ];

# --- #719 replay profiles ---------------------------------------------------
# #720 requires exactly two profiles and no extras, independently of what #719
# claims to have emitted. "Exactly two" is asserted rather than "at least two",
# because an extra profile is how a second run's evidence would enter unnoticed.
# The ordered fourteen nodes #719 names in its acceptance criteria. Keeping the
# list here as a literal is the point: a reader checks it against the issue by eye,
# and the validator checks each profile against it rather than against the other
# profile. If #719 changes its node set this literal must change with it, and the
# fixture gate fails loudly when it has not.
def canonical_replay_nodes:
    ["projected-scene", "calibration", "calibrated-preview", "rolling",
     "combined-preview", "quality", "cloud", "scene-presentation",
     "cloud-presentation", "environment-presentation", "overlay-manifest",
     "presentation-materializer", "storage", "telemetry"];

def replay_problems:
    (.replayProfiles // []) as $p
    | ([$p[] | "\(.runner)/\(.phase)"] | sort) as $seen
    | (if $seen != ["InProcess/1", "LocalRunner/2"] then
        [ problem("replay-profiles-not-exact";
            "replay profiles are [\($seen | join(", "))]; exactly InProcess/1 and LocalRunner/2 are required") ]
      else [] end)
    # Each profile is checked against the canonical list, not against the other
    # profile. The differential check this replaces admitted, with zero problems and
    # acceptanceReady true (PR #759, comment 5589072603): both profiles empty, both
    # carrying one fabricated node, both reversed identically, and both omitting the
    # key entirely. The last is why this changed — `[null] | unique | length` is 1, so
    # missing evidence scored as matching evidence, which is the fail-open shape this
    # issue exists to remove.
    #
    # An absolute check subsumes the differential one, because two lists that each
    # equal the canonical list equal each other. The weaker check is therefore deleted
    # rather than kept alongside: two checks where one is strictly stronger is how the
    # weaker one is later read as the guarantee.
    + [ $p[]
        | select(has("nodes") | not)
        | problem("replay-nodes-absent";
            "\(.runner)/\(.phase): carries no node list, so nothing records which nodes executed") ]
    + [ $p[]
        | select(has("nodes"))
        | select(.nodes != canonical_replay_nodes)
        | problem("replay-nodes-not-canonical";
            "\(.runner)/\(.phase): nodes are "
            + (if (.nodes | type) == "array" then "[\(.nodes | join(", "))]" else (.nodes | tostring) end)
            + "; #719 requires exactly [\(canonical_replay_nodes | join(", "))]") ]
    + [ $p[] | select((.publishedOutputs // 0) != 0)
        | problem("replay-output-published";
            "\(.runner)/\(.phase): \(.publishedOutputs) replay output(s) published; replay must publish nothing") ]
    + [ $p[] | select((.liveRunnerDispatches // 0) != 0)
        | problem("replay-live-dispatch";
            "\(.runner)/\(.phase): \(.liveRunnerDispatches) live runner dispatch(es); archived replay must not dispatch live work") ]
    + [ $p[] | select((.fallbacks // 0) != 0)
        | problem("replay-fallback-taken";
            "\(.runner)/\(.phase): \(.fallbacks) fallback(s) taken") ];

# --- #197 dual-agent admissibility ------------------------------------------
# Only a final, citable summary counts. A reduced diagnostic summary is a real
# record of a real run and is still not evidence of the thing #535 claims, so it
# is refused by name rather than filtered out quietly.
def dual_agent_problems:
    (.dualAgent // null) as $d
    | if $d == null then []
      elif ($d.evidenceMode != "final") or ($d.citable != true) then
        [ problem("dual-agent-not-citable";
            "dual-agent summary is evidenceMode=\($d.evidenceMode) citable=\($d.citable); only final and citable counts") ]
      else [] end;

# --- central traffic --------------------------------------------------------
# A standalone campaign that reached the central host did not demonstrate
# standalone operation, whatever else it demonstrated.
def central_traffic_problems:
    if (.centralTrafficAttempts // 0) != 0 then
        [ problem("central-traffic-observed";
            "\(.centralTrafficAttempts) central traffic attempt(s); standalone evidence requires zero") ]
    else [] end;

# --- imported CI slots ------------------------------------------------------
# The CI import is not implemented in this layer, and a deferral is not a neutral
# state: "not implemented yet" reads as a pass to everything downstream, which is
# the same defect as a green reporting something unverified, one costume further on.
#
# So a record carrying imported CI slots is REFUSED rather than validated around.
# A predicate here could check the shape of a supplied CI conclusion and recompute
# nothing about where it came from, and a shape-only check living in a file named
# for provenance is exactly how a later reader mistakes one for the other.
#
# When the compiled helper can query a real run and attempt, this refusal is
# replaced by predicates that recompute. Until then the absence is loud.
def ci_slot_problems:
    if (.ci // null) != null then
        [ problem("ci-slots-unverifiable";
            "this record carries imported CI slots, and nothing in this layer can check their provenance; "
            + "the CI import belongs to the compiled helper and until it exists such a record is refused rather than "
            + "validated around") ]
    else [] end;

def all_problems($bound):
    revision_problems($bound) + claimability_problems + freshness_problems
    + admissibility_problems + heads_problems + command_problems
    + assembly_problems($bound) + source_stability_problems
    + path_problems + schema_problems + replay_problems
    + dual_agent_problems + central_traffic_problems + ci_slot_problems;

def evaluate($bound; $mode):
    (all_problems($bound)
      + (if $mode == "final" then claimability_final_problems + source_clean_problems + command_final_problems
          + free_text_final_problems else [] end)) as $problems
    | {
        schemaVersion: "issue-535-final-head-validation-v1",
        mode: $mode,
        boundHead: $bound,
        artifactCount: (.artifacts | length),
        problems: $problems,
        localReady: (($problems | length) == 0),
        acceptanceReady: ((($problems | length) == 0) and ($mode == "final"))
      };
