# shellcheck shell=bash
# Strict GitHub API reads for the pre-release scripts (#1309), sourced by scripts/release:prerelease-gate,
# scripts/release:prerelease-check and scripts/release:publish-prerelease. It needs gh and jq.
#
#   github_api_read [--absent-ok] <what> <shape> <path> [gh api arguments...]
#
# Reads <path> with gh api --include and prints the body only when the read is certain: gh exited 0, GitHub answered
# 200, the body is exactly one JSON document, and the jq expression <shape> holds for all of it. With --absent-ok,
# GitHub's own 404 (gh failing, a 404 status line, and one JSON object with a string message) returns
# GITHUB_API_ABSENT and prints nothing. Anything else is not an answer: it prints "::error::cannot read <what>
# (<reason>)" with gh's own message and returns 1. That is another status, a 404 that is not GitHub's, a 200 that gh
# still failed (an answer cut short), no answer at all, an empty, invalid or repeated body, or a body of another shape.
#
#   github_api_read_list <what> <element-shape> <path> [gh api arguments...]
#
# Reads every page of a list, 100 entries a page, each page strictly as above: one array of at most 100 elements, each
# satisfying <element-shape>. It stops at the first short page and prints the entries as one array. It never relies on
# gh api --paginate, whose pages arrive as several JSON documents.
#
#   github_api_read_releases <repository>
#
# Every release of <repository> the token can see (drafts too, with a token that can write), as one array whose every
# element has a string tag_name and boolean draft and prerelease.

GITHUB_API_ABSENT=3

github_api_read() {
    local absent_ok=0
    if [[ "${1:-}" == --absent-ok ]]; then
        absent_ok=1
        shift
    fi
    if [[ $# -lt 3 ]]; then
        printf '::error::usage: github_api_read [--absent-ok] <what> <shape> <path> [gh api arguments...]\n' >&2
        return 1
    fi
    local what="$1" shape="$2" path="$3"
    shift 3
    local work exit_status=0 code="" documents="" reason="" result=1 errors
    work="$(mktemp -d)" || return 1
    # The exit status is kept: gh can fail after printing a complete-looking 200, and that is not an answer.
    gh api --include "$path" "$@" >"$work/answer" 2>"$work/errors" </dev/null || exit_status=$?
    # --include prints the status line, the headers and a blank line before the body.
    code="$(sed -n '1s/^HTTP\/[0-9.]* \([0-9]\{3\}\)\( .*\)\{0,1\}\r\{0,1\}$/\1/p' "$work/answer")"
    if [[ -n "$code" ]]; then
        sed '1,/^\r\{0,1\}$/d' "$work/answer" >"$work/body"
        # jq -s rejects an invalid body and counts the documents, so an empty or repeated body is not one answer.
        documents="$(jq -s 'length' "$work/body" 2>/dev/null)" || documents=""
    fi
    if [[ -z "$code" ]]; then
        reason="no answer from GitHub"
    elif [[ "$code" == 404 && "$exit_status" != 0 && "$documents" == 1 ]] &&
        jq -e 'type == "object" and (.message | type == "string")' "$work/body" >/dev/null 2>&1; then
        if [[ "$absent_ok" == 1 ]]; then
            result="$GITHUB_API_ABSENT"
        else
            reason=404
        fi
    elif [[ "$code" != 200 ]]; then
        reason="$code"
    elif [[ "$exit_status" != 0 ]]; then
        reason="200, but gh failed"
    elif [[ "$documents" != 1 ]]; then
        reason="200, but not one JSON document"
    elif ! jq -e "$shape" "$work/body" >/dev/null 2>&1; then
        reason="200, but not the expected answer"
    elif cat "$work/body"; then
        result=0
    else
        reason="the answer could not be passed on"
    fi
    if [[ -n "$reason" ]]; then
        errors="$(tr -s '\r\n' '  ' <"$work/errors" | sed 's/ *$//' | cut -c 1-300)"
        printf '::error::cannot read %s (%s)%s\n' "$what" "$reason" "${errors:+: $errors}" >&2
    fi
    rm -rf "$work"
    return "$result"
}

github_api_read_list() {
    if [[ $# -lt 3 ]]; then
        printf '::error::usage: github_api_read_list <what> <element-shape> <path> [gh api arguments...]\n' >&2
        return 1
    fi
    local what="$1" element="$2" path="$3"
    shift 3
    local work page=1 page_file count separator='?' result=1
    [[ "$path" != *\?* ]] || separator='&'
    work="$(mktemp -d)" || return 1
    while :; do
        page_file="$work/$(printf 'page.%04d' "$page")"
        github_api_read "$what" "type == \"array\" and length <= 100 and all(.[]; $element)" \
            "$path${separator}per_page=100&page=$page" "$@" >"$page_file" || break
        count="$(jq 'length' "$page_file")" || break
        if ((count < 100)); then
            if jq -s 'add // []' "$work"/page.*; then
                result=0
            else
                printf '::error::cannot read %s (its pages could not be joined)\n' "$what" >&2
            fi
            break
        fi
        if ((page >= 100)); then
            printf '::error::cannot read %s (more than 100 pages of 100)\n' "$what" >&2
            break
        fi
        page=$((page + 1))
    done
    rm -rf "$work"
    return "$result"
}

github_api_read_releases() {
    if [[ $# -ne 1 ]]; then
        printf '::error::usage: github_api_read_releases <repository>\n' >&2
        return 1
    fi
    github_api_read_list "the releases of $1" \
        'type == "object" and (.tag_name | type == "string") and (.draft | type == "boolean") and (.prerelease | type == "boolean")' \
        "repos/$1/releases"
}
