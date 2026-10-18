# shellcheck shell=bash
# Which release is the newest final release (install contract section 3, #1310), sourced by scripts/release:publish and
# scripts/release:tag-latest so publishing a release and moving the image's latest tag decide alike. It needs jq.
#
#   release_newest_final <releases>
#
# The version, X.Y.Z, of the highest published final release in <releases>, a JSON array of releases as
# github_api_read_releases (scripts/lib/github-api.sh) prints them: neither a draft nor a pre-release, with a tag
# vX.Y.Z. It prints nothing when there is none. Versions compare as numbers, so 1.10.0 is newer than 1.9.0.
#
#   release_is_newer <version> <newest>
#
# Whether the final version X.Y.Z <version> is newer than <newest>, the output of release_newest_final: true when
# <newest> is empty.

release_newest_final() {
    jq -r '[.[] | select(.draft == false and .prerelease == false) | .tag_name
            | select(test("\\Av(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\z"))
            | ltrimstr("v") | split(".") | map(tonumber)]
           | max | if . == null then "" else map(tostring) | join(".") end' <<<"$1"
}

release_is_newer() {
    [[ -z "$2" ]] && return 0
    jq -en --arg version "$1" --arg newest "$2" \
        '($version | split(".") | map(tonumber)) > ($newest | split(".") | map(tonumber))' >/dev/null
}
