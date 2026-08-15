#!/usr/bin/env bash
set -euo pipefail
umask 022

readonly SQLITE_VERSION=3.45.1
readonly SQLITE_ARCHIVE=sqlite-autoconf-3450100.tar.gz
readonly SQLITE_SOURCE_URL="https://www.sqlite.org/2024/$SQLITE_ARCHIVE"
readonly SQLITE_SOURCE_LENGTH=3232682
readonly SQLITE_SOURCE_SHA256=cd9c27841b7a5932c9897651e20b86c701dd740556989b01ca596fcfa3d49a0a
readonly INSTALL_ROOT=/usr/local/lib/hvo/sqlite-$SQLITE_VERSION
readonly COMMAND_LINK=/usr/local/bin/sqlite3

usage() {
    cat >&2 <<USAGE
Usage: $0 (--source ARCHIVE | --fetch)

Installs the pinned SQLite $SQLITE_VERSION CLI without replacing the operating
system SQLite library. --source performs no network I/O. --fetch explicitly
downloads the checksum-pinned source archive over HTTPS.
USAGE
}

fail() {
    printf 'sqlite installer error: %s\n' "$*" >&2
    exit 1
}

source_file=""
fetch=false
while [[ $# -gt 0 ]]; do
    case "$1" in
        --source)
            [[ $# -ge 2 ]] || { usage; exit 2; }
            source_file="$2"
            shift 2
            ;;
        --fetch)
            fetch=true
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

[[ -n "$source_file" && "$fetch" == false || -z "$source_file" && "$fetch" == true ]] || { usage; exit 2; }
[[ $EUID -eq 0 ]] || fail "run as root to install beneath /usr/local"
for required in cc curl install make mktemp nproc readlink realpath sha256sum tar wc; do
    command -v "$required" >/dev/null || fail "missing required command: $required"
done

work="$(mktemp -d "${TMPDIR:-/tmp}/hvo-sqlite-install.XXXXXX")"
install_staging=""
cleanup() {
    local status=$?
    trap - EXIT
    rm -rf -- "$work"
    [[ -z "$install_staging" ]] || rm -rf -- "$install_staging"
    exit "$status"
}
trap cleanup EXIT

archive="$work/$SQLITE_ARCHIVE"
if [[ "$fetch" == true ]]; then
    curl --fail --location --proto '=https' --tlsv1.2 "$SQLITE_SOURCE_URL" --output "$archive"
else
    source_file="$(realpath "$source_file")"
    [[ -f "$source_file" && ! -L "$source_file" ]] || fail "source is not a regular nonsymlink file: $source_file"
    install -m 0600 "$source_file" "$archive"
fi

[[ "$(wc -c < "$archive")" == "$SQLITE_SOURCE_LENGTH" ]] || fail "source archive length mismatch"
archive_hash="$(sha256sum "$archive")"
[[ "${archive_hash%% *}" == "$SQLITE_SOURCE_SHA256" ]] || fail "source archive SHA-256 mismatch"

mkdir "$work/source" "$work/prefix"
tar -xzf "$archive" -C "$work/source" --strip-components=1
(
    cd "$work/source"
    ./configure \
      --prefix="$work/prefix" \
      --disable-shared \
      --enable-static \
      --enable-static-shell \
      --disable-readline \
      --disable-editline \
      --disable-dynamic-extensions \
      CFLAGS='-O2'
    make -j"$(nproc)" sqlite3
)

built="$work/source/sqlite3"
[[ -x "$built" ]] || fail "build did not produce sqlite3"
actual_version="$($built --version)"
[[ "${actual_version%% *}" == "$SQLITE_VERSION" ]] || fail "built CLI version mismatch"
[[ "$($built ':memory:' "SELECT json_extract('{\"ready\":true}', '$.ready');")" == 1 ]] || fail "built CLI lacks required JSON support"

if [[ -e "$INSTALL_ROOT" || -L "$INSTALL_ROOT" ]]; then
    [[ -d "$INSTALL_ROOT" && ! -L "$INSTALL_ROOT" && -x "$INSTALL_ROOT/bin/sqlite3" && -f "$INSTALL_ROOT/source.sha256" && ! -L "$INSTALL_ROOT/source.sha256" ]] || \
      fail "existing install root is not a valid physical installation: $INSTALL_ROOT"
    installed_version="$($INSTALL_ROOT/bin/sqlite3 --version)"
    [[ "${installed_version%% *}" == "$SQLITE_VERSION" ]] || fail "existing install has the wrong version"
    [[ "$(<"$INSTALL_ROOT/source.sha256")" == "$SQLITE_SOURCE_SHA256  $SQLITE_ARCHIVE" ]] || \
      fail "existing install has the wrong source identity"
    [[ "$($INSTALL_ROOT/bin/sqlite3 ':memory:' "SELECT json_extract('{\"ready\":true}', '$.ready');")" == 1 ]] || \
      fail "existing install lacks required JSON support"
else
    install_parent="$(dirname "$INSTALL_ROOT")"
    install -d -m 0755 "$install_parent"
    install_staging="$(mktemp -d "$install_parent/.sqlite-$SQLITE_VERSION.XXXXXX")"
    install -d -m 0755 "$install_staging/bin"
    install -m 0755 "$built" "$install_staging/bin/sqlite3"
    printf '%s  %s\n' "$SQLITE_SOURCE_SHA256" "$SQLITE_ARCHIVE" > "$install_staging/source.sha256"
    chmod 0644 "$install_staging/source.sha256"
    mv -- "$install_staging" "$INSTALL_ROOT"
    install_staging=""
fi

if [[ -e "$COMMAND_LINK" && ! -L "$COMMAND_LINK" ]]; then
    fail "refusing to replace non-symlink command: $COMMAND_LINK"
fi
install -d -m 0755 "$(dirname "$COMMAND_LINK")"
ln -sfn "$INSTALL_ROOT/bin/sqlite3" "$COMMAND_LINK"
[[ "$(readlink -f "$COMMAND_LINK")" == "$INSTALL_ROOT/bin/sqlite3" ]] || fail "command link verification failed"
actual_version="$($COMMAND_LINK --version)"
[[ "${actual_version%% *}" == "$SQLITE_VERSION" ]] || fail "installed CLI version verification failed"

printf 'SQLite %s CLI installed at %s.\n' "$SQLITE_VERSION" "$INSTALL_ROOT/bin/sqlite3"
