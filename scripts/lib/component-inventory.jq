# Component-inventory gate shared by the release script and pinned against the release tool.
#
# This must accept and reject exactly what ValidateComponentInventory in
# tools/HVO.SkyMonitor.Deployment.ReleaseTool/Program.cs accepts and rejects. The script applies it before
# the immutable registry push and the tool applies it again before signing, so a looser rule here lets a bad
# inventory consume a published version that adoption can never complete. ComponentInventoryParityTests runs a
# table of documents through both sides and fails when they disagree, so the equality is a gate rather than a
# comment.
#
# Parameters: $imageId — the "sha256:..." image the platform archive produced.
#             $floor   — the minimum component count, equal to MinimumInventoryComponents in the release tool.
#
# Every member read is coerced with `s` and every container is type-checked, so a malformed document fails as
# this rule rather than as a raw jq type error.

def s: if type == "string" then . else "" end;
def entries: if type == "array" then .[] else empty end;
def image_claims: [ .packages[] | .annotations | entries | .comment | s
                    | select(startswith("ImageID: ")) | ltrimstr("ImageID: ") ];
def is_subject: ([ .annotations | entries | .comment | s | select(startswith("ImageID: ")) ] | length) > 0;
def components: [ .packages[]
                  | select(is_subject | not)
                  | select(((.name | s) | gsub("^\\s+|\\s+$"; "")) != "")
                  | select(((.versionInfo | s) | gsub("^\\s+|\\s+$"; "")) != "") ];
def files_have_sha1:
  if (has("files") | not) then true
  elif (.files | type) != "array" then false
  else ([ .files[]
          | select((type) != "object"
                   or ((.checksums | type) != "array")
                   or ([ .checksums[]
                         | select((type) == "object")
                         | select((.algorithm | s) == "SHA1")
                         | select((.checksumValue | s) | test("^[0-9a-fA-F]{40}$")) ] | length == 0)) ] | length) == 0
  end;

(type == "object")
and ((.spdxVersion | s) == "SPDX-2.3")
and ((.SPDXID | s) == "SPDXRef-DOCUMENT")
and ((.packages | type) == "array")
and (([ .packages[] | select(type != "object") ] | length) == 0)
and (image_claims == [$imageId])
and ((components | length) >= $floor)
and files_have_sha1
