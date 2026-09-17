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
#      consequence here is fail-closed for the VERDICT: a phantom non-Passed element refuses a
#      run that would otherwise be accepted, and a phantom Passed element cannot turn a refusal
#      into an acceptance, because every real element is still matched and tested. It is not
#      free, though, and the earlier wording that a Passed phantom "changes nothing" was wrong:
#      it inflates the reported result count, which is the line a human reads. So the escaping
#      argument is not load-bearing for the verdict, and the count is advisory rather than
#      authoritative whenever a file contains CDATA. It IS load-bearing for anything that reads a
#      singleton element such as <Counters>, where a phantom match changes which value is
#      read. trx_assert_counters_executed_and_passed therefore requires exactly one such
#      element, and that requirement is not defensive tidiness: it is the only thing standing
#      between a CDATA-quoted counters tag and a silently substituted total WHEN THE REAL
#      ELEMENT IS PRESENT. When it is absent the count alone is not enough, because the phantom
#      is then the only match and so satisfies exactly-one by itself: it is not substituted for
#      the reading, it IS the reading. So the assertion also compares that count against the
#      count taken with CDATA stripped, and refuses when they disagree. The
#      cdata-phantom-counters and counters-phantom-only fixtures pin the two cases separately;
#      neither alone would have caught the other.
#   2. XML requires escaping "<" and "&" inside an attribute value but permits ">". A raw ">"
#      in a testName truncates [^>]* before outcome=, and the truncated element is then
#      refused for having no readable outcome. Fail-closed, and deliberate: an element this
#      cannot parse is not evidence that it passed.
#
# THE TWO ASSERTIONS, AND WHY THEY ARE SEPARATE
#
# trx_assert_executed_and_passed reads the per-result outcomes. trx_assert_counters_executed_and_passed
# reads the run's own totals. They are separate because they promise different things and most
# callers want only the first: the per-result check is what distinguishes a skipped run from a
# real one, while the counters check cross-examines that verdict against the totals the runner
# recorded for itself. Callers that want both call both, which is what
# scripts/test:cameraagent-standalone-211 does.
#
# The counters assertion arrived here under issue #803. It previously lived as a second copy
# inside that runner, asserted by nothing, in a runner no workflow invokes. Two copies of one
# contract do not drift loudly; they drift silently, and the ungated copy reads as
# authoritative as the gated one. There is now one implementation, and scripts/test:trx-evidence-contract
# covers it in both the refusing and the accepting direction.

# Print every <UnitTestResult> start-tag in the file, one per line.
trx_result_elements() {
    grep -o '<UnitTestResult [^>]*' "$1" || true
}

# Print every <Counters> start-tag in the file, one per line, including any that a CDATA section
# merely quotes. Separating this from the assertion is what lets the assertion compare the raw
# reading against the CDATA-stripped one; see trx_assert_counters_executed_and_passed.
trx_counters_elements() {
    grep -o '<Counters [^>]*' "$1" || true
}

# The same, with every <![CDATA[ ... ]]> span removed first, so only real markup survives.
#
# A CDATA span may open and close on one line or run across many, and more than one may share a
# line, so this consumes them as a stream rather than matching a line or a sed range. The output
# is deliberately only used for COUNTING against the raw reading: a file where the two disagree
# is a file whose totals cannot be read unambiguously, and this refuses it rather than choosing.
trx_counters_elements_outside_cdata() {
    awk '
        {
            line = $0
            out = ""
            while (length(line) > 0) {
                if (incdata) {
                    pos = index(line, "]]>")
                    if (pos == 0) { line = ""; break }
                    incdata = 0
                    line = substr(line, pos + 3)
                } else {
                    pos = index(line, "<![CDATA[")
                    if (pos == 0) { out = out line; line = ""; break }
                    out = out substr(line, 1, pos - 1)
                    incdata = 1
                    line = substr(line, pos + 9)
                }
            }
            print out
        }' "$1" | grep -o '<Counters [^>]*' || true
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
        # Capped: a campaign trial can record thousands of NotExecuted results, and a diagnostic
        # that floods the log buries the line naming the failure. The count below is uncapped and
        # reports the true total, so the cap costs no information about scale.
        printf '%s\n' "$unpassed" | awk '{
            name = ""; outcome = ""
            if (match($0, /testName="[^"]*"/)) { name = substr($0, RSTART + 10, RLENGTH - 11) }
            if (match($0, /outcome="[^"]*"/)) { outcome = substr($0, RSTART + 9, RLENGTH - 10) }
            if (name != "" && outcome != "") { print "  " name ": " outcome }
            else { print "  unparseable result: " $0 }
        }' | head -n 8 >&2
        printf '  %s results are not Passed.\n' "$(printf '%s\n' "$unpassed" | wc -l | tr -d ' ')" >&2
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

# Refuse unless the run's own ResultSummary/Counters totals show every selected test executing
# and passing. This cross-examines the per-result verdict against the totals the runner recorded
# for itself; it does not replace trx_assert_executed_and_passed, which reads the outcomes.
# Returns 0 on success, 1 on refusal; the caller decides whether that is an exit or a `fail`.
trx_assert_counters_executed_and_passed() {
    local trx="$1"
    local label="$2"
    local counters executed passed

    if [[ ! -s "$trx" ]]; then
        printf '%s: no results file was written to %s.\n' "$label" "$trx" >&2
        return 1
    fi

    # Read the counters out of the Counters element itself, not out of the first line that
    # happens to contain the attribute name. A genuine pass whose StdOut quotes ' executed="0"'
    # was refused by the line-based read, which is a fail-closed defect in a guard whose whole
    # purpose is to be trusted. The element anchor is load-bearing here in a way the line read
    # cannot approximate: a collapsed TRX puts StdOut on the SAME line as the real element, so a
    # whole-line read of a zero-executed run can pick up quoted non-zero totals from the output
    # beside it and report a skipped run as a pass. counters-collapsed-stdout-quotes.trx pins it.
    counters="$(trx_counters_elements "$trx")"
    # Exactly one, for the reason recorded in limit 1 at the top of this file: this reads a
    # singleton, so a CDATA-quoted phantom would change which value is read rather than merely
    # inflating an advisory count. Refusing an ambiguous file is the only safe reading.
    if [[ "$(printf '%s\n' "$counters" | grep -c '<Counters ')" != 1 ]]; then
        printf '%s: the evidence does not contain exactly one Counters element, so its totals cannot be read.\n' "$label" >&2
        return 1
    fi

    # Exactly one match is not yet exactly one ELEMENT: a file whose only match is quoted inside
    # CDATA satisfies the count above while containing no real totals at all, and would then be
    # read as the run's own. That is a fail-open the count cannot see, because the phantom is not
    # competing with a real element, it IS the reading. So the raw count must also agree with the
    # count taken after CDATA is stripped. Disagreement means the one match is quoted output.
    if [[ "$(trx_counters_elements_outside_cdata "$trx" | grep -c '<Counters ')" != 1 ]]; then
        printf '%s: the only Counters element is quoted inside CDATA, so the run recorded no totals of its own.\n' "$label" >&2
        return 1
    fi

    executed="$(printf '%s' "$counters" | sed -n 's/.*[[:space:]]executed="\([0-9]*\)".*/\1/p')"
    passed="$(printf '%s' "$counters" | sed -n 's/.*[[:space:]]passed="\([0-9]*\)".*/\1/p')"
    # Validate before arithmetic. Bash reads a leading-zero operand as octal, so executed="008"
    # is not merely wrong, it ABORTS the arithmetic; inside an `if` condition that abort is a
    # false result, the || chain falls through, and a skipped run is accepted. Values above 2^64
    # wrap silently instead. Both are fail-open, so an operand that is not a plain bounded
    # decimal is refused before it ever reaches (( )).
    #
    # A bare "0" is legal and must stay legal: it is what a skipped run records, and it has to
    # reach the executed==0 refusal below so that run is refused for the reason that is true of
    # it. Only a LEADING zero is rejected, which is why this is not simply [0-9]{1,9}.
    if [[ ! "$executed" =~ ^(0|[1-9][0-9]{0,8})$ || ! "$passed" =~ ^(0|[1-9][0-9]{0,8})$ ]]; then
        printf '%s: the TRX counters are not readable as plain decimal totals: executed=%s passed=%s\n' \
            "$label" "${executed:-<absent>}" "${passed:-<absent>}" >&2
        return 1
    fi
    if ((executed == 0)) || ((passed != executed)); then
        printf '%s: the TRX counters do not show every selected test executing and passing: executed=%s passed=%s\n' \
            "$label" "${executed:-<absent>}" "${passed:-<absent>}" >&2
        return 1
    fi

    printf '%s: counters verified, executed=%s passed=%s.\n' "$label" "$executed" "$passed"
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
