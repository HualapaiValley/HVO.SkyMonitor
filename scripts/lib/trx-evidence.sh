#!/usr/bin/env bash
# Shared TRX evidence assertions for the repository's opt-in test runners.
#
# WHY THIS FILE EXISTS, AND WHY THE ASSUMPTION IS WRITTEN HERE RATHER THAN INFERRED
#
# A skipped MSTest exits zero, so "the command succeeded" proves only that nothing failed.
# The runners that produce evidence therefore inspect the TRX themselves. Three of them grew
# their own copy of that inspection, and two copies carried an assumption that never travelled
# with them: that a TRX puts one <UnitTestResult> element on each line.
#
# That assumption is a habit of a particular writer, not a property of the format, and it is
# wrong in both directions when it fails:
#
#   * A line-level invert reads a line holding a Passed result beside a NotExecuted one as
#     passing, because the line contains outcome="Passed". FAIL OPEN — the guard reports that
#     a run proved something when part of it never executed.
#   * A line-level count reports the number of lines, not the number of results.
#
# This repository already produces exactly that shape, at scripts/test:phase14-component, so
# the defect was latent rather than theoretical. See issue #769; the same defect was corrected
# separately in scripts/test:cameraagent-standalone-211 under #734.
#
# THE CORRECTION: ANCHOR TO THE ELEMENT, NEVER TO THE LINE
#
# `grep -o '<UnitTestResult [^>]*'` yields one match per element, so the invert applies to a
# result and the count counts results. Anchoring to the element start-tag is also what makes
# this immune to console output that merely looks like markup: "<" must be escaped as "&lt;"
# in XML character data, so a literal "<UnitTestResult " cannot appear in an StdOut, Message
# or ErrorInfo body.
#
# TWO LIMITS OF THAT ARGUMENT, RECORDED BECAUSE THEY ARE EASY TO REASON PAST
#
#   1. CDATA is the exception to the escaping rule: inside <![CDATA[ ... ]]> a raw "<" is
#      legal, so a CDATA-wrapped element start-tag does produce a literal match. The
#      consequence here is fail-closed — a phantom element either adds a Passed that changes
#      nothing, or adds a non-Passed that refuses a run — so the escaping argument is not
#      load-bearing for these assertions. It WOULD be load-bearing for anything that reads a
#      singleton element such as <Counters>, where a phantom match changes which value is
#      read. If a counters assertion is ever added here, it must require exactly one such
#      element and must not treat that requirement as defensive tidiness.
#   2. XML requires escaping "<" and "&" inside an attribute value but permits ">". A raw ">"
#      in a testName truncates [^>]* before outcome=, and the truncated element is then
#      refused for having no readable outcome. Fail-closed, and deliberate: an element this
#      cannot parse is not evidence that it passed.
#
# WHAT IS DELIBERATELY NOT HERE
#
# ResultSummary/Counters verification. The runners that source this file assert a non-empty
# selection and an all-Passed set; adding a counters assertion would change what they promise
# rather than fix what they get wrong, and issue #769 is scoped to the latter.
# scripts/test:cameraagent-standalone-211 does assert counters and keeps its own copy; folding
# it into this helper is a follow-up, not part of the correction.

# Print every <UnitTestResult> start-tag in the file, one per line.
trx_result_elements() {
    grep -o '<UnitTestResult [^>]*' "$1" || true
}

# Refuse unless the TRX records at least one result and every result is Passed.
# Prints the reason and the offending results to stderr. Returns 0 on success, 1 on refusal;
# the caller decides whether that is an exit or a `fail`.
trx_assert_executed_and_passed() {
    local trx="$1"
    local label="$2"
    local results unpassed reasons

    if [[ ! -s "$trx" ]]; then
        printf '%s: no results file was written to %s.\n' "$label" "$trx" >&2
        return 1
    fi

    results="$(trx_result_elements "$trx")"
    if [[ -z "$results" ]]; then
        printf '%s: the selection recorded no results, so the filter no longer matches the assembly.\n' "$label" >&2
        return 1
    fi

    # An element without a readable outcome is refused rather than skipped.
    unpassed="$(printf '%s\n' "$results" | grep -v 'outcome="Passed"' || true)"
    if [[ -n "$unpassed" ]]; then
        printf '%s: the evidence contains a result that is not Passed, so this run proved nothing.\n' "$label" >&2
        # awk rather than sed: BSD sed rejects a bare `t` branch, and this must run on the
        # operator's macOS host as well as the Linux runners.
        printf '%s\n' "$unpassed" | awk '{
            name = ""; outcome = ""
            if (match($0, /testName="[^"]*"/)) { name = substr($0, RSTART + 10, RLENGTH - 11) }
            if (match($0, /outcome="[^"]*"/)) { outcome = substr($0, RSTART + 9, RLENGTH - 10) }
            if (name != "" && outcome != "") { print "  " name ": " outcome }
            else { print "  unparseable result: " $0 }
        }' >&2
        printf '  A skipped or inconclusive result is not a reduced pass; it is no result at all.\n' >&2
        reasons="$(trx_recorded_reasons "$trx")" || reasons=''
        if [[ -n "${reasons//[[:space:]]/}" ]]; then
            printf 'Reasons recorded by the tests themselves:\n' >&2
            printf '%s\n' "$reasons" >&2
        fi
        return 1
    fi

    # $results is already one element per line, so counting its lines counts results. Counting
    # lines of the TRX itself is the defect this file exists to remove; do not reintroduce it.
    printf '%s: %s results, every one Passed.\n' "$label" "$(printf '%s\n' "$results" | wc -l | tr -d ' ')"
}

# Extract the <Message> bodies the tests recorded, for the diagnostic above.
#
# An Assert.Inconclusive message is one line but a failing assertion wraps across lines, so the
# element is closed explicitly instead of matching a line or a sed range. A sed range is wrong
# here: when the open and close tags share a line the end address is only sought on later
# lines, so each range would run to the next message and swallow the captured output between.
# Whole messages are deduplicated rather than lines: piping this through `sort -u` would
# alphabetise a wrapped message and splice unrelated ones through it, so an expected/actual
# pair could print out of order.
trx_recorded_reasons() {
    awk '
        /<Message>/ { sub(/.*<Message>/, ""); open = 1; message = "" }
        open {
            closing = /<\/Message>/
            if (closing) { sub(/<\/Message>.*/, ""); open = 0 }
            message = message (message == "" ? "" : "\n") "  " $0
            if (closing && !(message in seen)) { seen[message] = 1; print message }
        }' "$1"
}
