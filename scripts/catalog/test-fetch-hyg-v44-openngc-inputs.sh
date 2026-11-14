#!/usr/bin/env bash
set -euo pipefail
umask 022

# Offline test of fetch-hyg-v44-openngc-inputs.sh (Refs #1774): an input that does not match its pin fails the
# fetch, and a failed fetch leaves nothing a caller could read. It takes the directory a successful fetch wrote and
# replays those verified bytes, intact or damaged one way at a time, through stand-ins for curl and git. It performs
# no network I/O and supplies no expected hash of its own; the fetch script's pins decide every case.
if [[ $# -ne 1 || "$1" == -* ]]; then
    printf 'Usage: %s VERIFIED_INPUTS_DIRECTORY\n' "$0" >&2
    exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
# shellcheck source=scripts/catalog/hyg-v44-openngc-common.sh
source "$SCRIPT_DIR/hyg-v44-openngc-common.sh"
hyg_require_commands cat chmod cmp cp dd diff find grep head ln mkdir mktemp mv realpath rm

VERIFIED="$(realpath -e -- "$1")"
readonly VERIFIED
[[ -f "$VERIFIED/hyg_v44.csv.gz" && -d "$VERIFIED/OpenNGC" ]] ||
    hyg_fail "not the output of fetch-hyg-v44-openngc-inputs.sh: $VERIFIED"

work="$(mktemp -d "${TMPDIR:-/tmp}/hvo-fetch-inputs-test.XXXXXX")"
cleanup() {
    chmod -R u+w "$work"
    rm -rf -- "$work"
}
trap cleanup EXIT

cases=0
fail() {
    printf 'Catalog input fetch test failed: %s\n' "$*" >&2
    [[ ! -s "$work/stderr" ]] || { printf -- '--- stderr\n' >&2; cat "$work/stderr" >&2; }
    exit 1
}

# Stand-ins that serve whatever the case prepared. Each records its arguments, so the control case can also prove
# the script asked for the pinned URL and the pinned commit, over HTTPS only.
mkdir "$work/bin"
cat > "$work/bin/curl" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$HVO_FAKE_CURL_LOG"
output=""
while [[ $# -gt 0 ]]; do
    [[ "$1" != --output ]] || output="$2"
    shift
done
[[ "$HVO_FAKE_CURL_STATUS" == 0 ]] || exit "$HVO_FAKE_CURL_STATUS"
cat -- "$HVO_FAKE_HYG" > "$output"
SH
cat > "$work/bin/git" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$HVO_FAKE_GIT_LOG"
directory=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        -c) shift 2 ;;
        -C) directory="$2"; shift 2 ;;
        init) mkdir -p -- "${@: -1}"; exit 0 ;;
        fetch) exit "$HVO_FAKE_GIT_FETCH_STATUS" ;;
        rev-parse) printf '%s\n' "$HVO_FAKE_GIT_COMMIT"; exit 0 ;;
        checkout) cp -R -- "$HVO_FAKE_OPENNGC/." "$directory/"; exit 0 ;;
        *) printf 'stand-in git was given an unexpected argument: %s\n' "$1" >&2; exit 97 ;;
    esac
done
SH
chmod 0755 "$work/bin/curl" "$work/bin/git"
export HVO_FAKE_CURL_LOG="$work/curl.log" HVO_FAKE_GIT_LOG="$work/git.log"

# Starts a case from the verified bytes: a fresh, writable copy of each input for the case to damage.
case_number=0
begin_case() {
    case_number=$((case_number + 1))
    case_root="$work/case-$case_number"
    mkdir "$case_root"
    cp -- "$VERIFIED/hyg_v44.csv.gz" "$case_root/hyg_v44.csv.gz"
    cp -R -- "$VERIFIED/OpenNGC" "$case_root/OpenNGC"
    chmod -R u+w "$case_root"
    export HVO_FAKE_HYG="$case_root/hyg_v44.csv.gz" HVO_FAKE_OPENNGC="$case_root/OpenNGC"
    export HVO_FAKE_CURL_STATUS=0 HVO_FAKE_GIT_FETCH_STATUS=0 HVO_FAKE_GIT_COMMIT="$OPENNGC_COMMIT"
    : > "$HVO_FAKE_CURL_LOG"
    : > "$HVO_FAKE_GIT_LOG"
    fetch_output="$case_root/published"
}

fetch_status=0
run_fetch() {
    set +e
    PATH="$work/bin:$PATH" "$SCRIPT_DIR/fetch-hyg-v44-openngc-inputs.sh" "$fetch_output" > "$work/stdout" 2> "$work/stderr"
    fetch_status=$?
    set -e
}

# One byte changed in place: the length a pin records stays right and only the SHA-256 can catch it.
flip_first_byte() {
    local file="$1" replacement=X
    [[ "$(head -c 1 -- "$file")" != X ]] || replacement=Y
    printf '%s' "$replacement" | dd of="$file" bs=1 count=1 conv=notrunc status=none
}

expect_refused() {
    local label="$1" message="$2" leftover
    run_fetch
    [[ "$fetch_status" != 0 ]] || fail "$label: the fetch accepted an input that does not match its pin"
    grep -Fq -- "$message" "$work/stderr" || fail "$label: expected the refusal '$message'"
    [[ ! -e "$fetch_output" && ! -L "$fetch_output" ]] || fail "$label: a refused fetch published $fetch_output"
    leftover="$(find "$case_root" -mindepth 1 -maxdepth 1 -name '.hyg-v44-openngc-inputs.*')"
    [[ -z "$leftover" ]] || fail "$label: a refused fetch left staging behind: $leftover"
    cases=$((cases + 1))
}

# Control: the verified bytes are accepted, and what is published is exactly them. The checkout also carries files
# the composer never reads; none of them may be published.
begin_case
printf 'not a pinned input\n' > "$case_root/OpenNGC/README.md"
printf 'not a pinned input\n' > "$case_root/OpenNGC/database_files/extra.csv"
run_fetch
[[ "$fetch_status" == 0 ]] || fail "control: the fetch refused the verified inputs"
cmp -- "$VERIFIED/hyg_v44.csv.gz" "$fetch_output/hyg_v44.csv.gz" || fail "control: the published HYG input differs"
diff -r -- "$VERIFIED/OpenNGC" "$fetch_output/OpenNGC" > /dev/null ||
    fail "control: the published OpenNGC inputs are not exactly the pinned files"
[[ "$(find "$fetch_output" -mindepth 1 -maxdepth 1 | wc -l)" == 2 ]] || fail "control: the fetch published something else"
[[ -z "$(find "$case_root" -mindepth 1 -maxdepth 1 -name '.hyg-v44-openngc-inputs.*')" ]] || fail "control: staging was left behind"
grep -Fq -- "--proto =https --tlsv1.2 $HYG44_SOURCE_URL " "$HVO_FAKE_CURL_LOG" ||
    fail "control: the HYG input was not requested from its pinned URL over HTTPS only"
grep -Fq -- "-c protocol.allow=never -c protocol.https.allow=always" "$HVO_FAKE_GIT_LOG" ||
    fail "control: the OpenNGC fetch was not restricted to HTTPS"
grep -Eq -- " fetch --quiet --depth 1 $OPENNGC_PROJECT_URL $OPENNGC_COMMIT\$" "$HVO_FAKE_GIT_LOG" ||
    fail "control: OpenNGC was not requested from its pinned project at its pinned commit"
cases=$((cases + 1))

begin_case
flip_first_byte "$HVO_FAKE_HYG"
expect_refused "HYG input with one byte changed" "compressed HYG source SHA-256 does not match its manifest"

begin_case
head -c 1000 -- "$VERIFIED/hyg_v44.csv.gz" > "$HVO_FAKE_HYG"
expect_refused "truncated HYG input" "compressed HYG source byte length does not match its manifest"

begin_case
printf 'x' >> "$HVO_FAKE_HYG"
expect_refused "HYG input with a byte appended" "compressed HYG source byte length does not match its manifest"

begin_case
: > "$HVO_FAKE_HYG"
expect_refused "empty HYG input" "compressed HYG source byte length does not match its manifest"

begin_case
export HVO_FAKE_CURL_STATUS=22
run_fetch
[[ "$fetch_status" == 22 ]] || fail "failed HYG download: expected curl's status 22, received $fetch_status"
[[ ! -e "$fetch_output" ]] || fail "failed HYG download: the fetch published $fetch_output"
[[ ! -s "$HVO_FAKE_GIT_LOG" ]] || fail "failed HYG download: the fetch went on to OpenNGC"
cases=$((cases + 1))

begin_case
export HVO_FAKE_GIT_FETCH_STATUS=128
run_fetch
[[ "$fetch_status" == 128 ]] || fail "failed OpenNGC fetch: expected git's status 128, received $fetch_status"
[[ ! -e "$fetch_output" ]] || fail "failed OpenNGC fetch: the fetch published $fetch_output"
cases=$((cases + 1))

begin_case
export HVO_FAKE_GIT_COMMIT=0000000000000000000000000000000000000001
expect_refused "OpenNGC at a commit other than the pin" \
    "OpenNGC fetch returned commit 0000000000000000000000000000000000000001, expected $OPENNGC_COMMIT"

begin_case
flip_first_byte "$HVO_FAKE_OPENNGC/$OPENNGC_NGC_FILE"
expect_refused "NGC.csv with one byte changed" "OpenNGC NGC.csv SHA-256 does not match its manifest"

begin_case
printf 'x' >> "$HVO_FAKE_OPENNGC/$OPENNGC_NGC_FILE"
expect_refused "NGC.csv with a byte appended" "OpenNGC NGC.csv byte length does not match its manifest"

begin_case
flip_first_byte "$HVO_FAKE_OPENNGC/$OPENNGC_ADDENDUM_FILE"
expect_refused "addendum.csv with one byte changed" "OpenNGC addendum.csv SHA-256 does not match its manifest"

begin_case
rm -- "$HVO_FAKE_OPENNGC/$OPENNGC_ADDENDUM_FILE"
expect_refused "missing addendum.csv" "OpenNGC input is missing or is not a regular file: $OPENNGC_ADDENDUM_FILE"

outlines=("$VERIFIED/OpenNGC/$OPENNGC_OUTLINE_DIRECTORY"/*)
[[ -f "${outlines[0]}" ]] || hyg_fail "the verified inputs hold no OpenNGC outline: $VERIFIED"
outline="${outlines[0]##*/}"

begin_case
flip_first_byte "$HVO_FAKE_OPENNGC/$OPENNGC_OUTLINE_DIRECTORY/$outline"
expect_refused "outline with one byte changed" "OpenNGC outline files do not match the committed listing"

begin_case
rm -- "$HVO_FAKE_OPENNGC/$OPENNGC_OUTLINE_DIRECTORY/$outline"
expect_refused "missing outline" "OpenNGC outline directory does not hold exactly the listed regular files"

begin_case
printf 'not a pinned outline\n' > "$HVO_FAKE_OPENNGC/$OPENNGC_OUTLINE_DIRECTORY/Unlisted_lv1.txt"
expect_refused "unlisted outline" "OpenNGC outline directory does not hold exactly the listed regular files"

begin_case
mv -- "$HVO_FAKE_OPENNGC/$OPENNGC_OUTLINE_DIRECTORY/$outline" "$case_root/outline-target"
ln -s "$case_root/outline-target" "$HVO_FAKE_OPENNGC/$OPENNGC_OUTLINE_DIRECTORY/$outline"
expect_refused "outline replaced by a symbolic link to the same bytes" \
    "OpenNGC outline directory does not hold exactly the listed regular files"

begin_case
rm -rf -- "$HVO_FAKE_OPENNGC/outlines"
expect_refused "missing outline directory" "OpenNGC source directory is missing or is not a directory: outlines"

# An existing output is never reused or replaced, and nothing is fetched to find that out.
begin_case
mkdir "$fetch_output"
printf 'earlier\n' > "$fetch_output/keep"
run_fetch
[[ "$fetch_status" != 0 ]] || fail "existing output: the fetch reused an existing directory"
grep -Fq -- "output directory already exists: $fetch_output" "$work/stderr" || fail "existing output: expected the refusal"
[[ "$(cat -- "$fetch_output/keep")" == earlier && "$(find "$fetch_output" -mindepth 1 | wc -l)" == 1 ]] ||
    fail "existing output: the existing directory was changed"
[[ ! -s "$HVO_FAKE_CURL_LOG" && ! -s "$HVO_FAKE_GIT_LOG" ]] || fail "existing output: the fetch ran before refusing"
cases=$((cases + 1))

for arguments in "" "one two" "--help"; do
    set +e
    # shellcheck disable=SC2086 # Word splitting supplies zero, two or one option-like argument.
    PATH="$work/bin:$PATH" "$SCRIPT_DIR/fetch-hyg-v44-openngc-inputs.sh" $arguments > /dev/null 2> "$work/stderr"
    fetch_status=$?
    set -e
    [[ "$fetch_status" == 2 ]] || fail "arguments '$arguments': expected usage status 2, received $fetch_status"
    grep -Fq -- 'OUTPUT_DIRECTORY' "$work/stderr" || fail "arguments '$arguments': expected the usage line"
    cases=$((cases + 1))
done

printf 'Catalog input fetch test passed: %s cases.\n' "$cases"
