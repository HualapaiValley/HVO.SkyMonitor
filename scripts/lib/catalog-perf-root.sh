#!/usr/bin/env bash
# Shape validation for HVO_CATALOG_PERF_ROOT, shared by the acceptance campaigns that mount it.
#
# The installed catalog tree carries two levels that both exist, are both readable directories, and both look
# plausible to someone reading either contract on its own:
#
#   .../catalogs/hyg-v42-production                 <- the root; holds "current" beside "versions/"
#   .../catalogs/hyg-v42-production/versions/...    <- one version directory
#
# The campaigns mount the root, because the container resolves a "current" pointer that lives in it. The
# Deployment CLI installer bundle variable takes the version directory and refuses "current" outright. Passing
# the version directory here used to satisfy a -d test and then fail five minutes later inside the container,
# as an unhandled "Catalog current pointer is missing" that surfaced as a login-endpoint timeout.

# Fails with an exit-2 diagnostic unless the path is the installed catalog root the container can resolve.
# Deliberately shape-only: the reviewed-database identity check belongs to the campaign that pins a package
# version, and a campaign that only mounts the tree must not be made to pin one.
assert_catalog_perf_root_shape() {
    local root pointer target resolved databases
    # Resolve to a physical absolute path before anything compares against it. Every check below asks whether one
    # path is contained in another, and two separate ways of getting that wrong both turn on the difference between
    # the path as typed and the path on disk: a correct root given relatively or with a trailing slash is not a
    # textual prefix of the absolute path its pointer resolves to, and a version directory that is itself a symlink
    # out of the tree is contained in the root logically while living somewhere else physically.
    #
    # The `|| root=''` matters and is not defensive noise. All three callers run under `set -euo pipefail`, and a
    # bare assignment whose command substitution fails carries that failure. It is only harmless today because each
    # call site is written `assert_catalog_perf_root_shape ... || exit 2`, and errexit is suspended inside a function
    # invoked on the left of `||`. A future caller invoking this bare would die on the assignment instead of reading
    # the refusal below, so do not depend on the call-site form.
    root="$(cd "$1" 2>/dev/null && pwd -P)" || root=''
    if [[ -z "$root" ]]; then
        # A backstop rather than the usual path. Each runner already refuses a non-existent root by name before
        # sourcing this file, so what reaches here is a directory that exists and cannot be entered, an empty
        # argument, or a root deleted between that check and this call.
        printf 'HVO_CATALOG_PERF_ROOT must name a directory this process can enter: %s\n' "$1" >&2
        return 2
    fi
    pointer="$root/current"
    if [[ ! -L "$pointer" ]]; then
        printf 'HVO_CATALOG_PERF_ROOT must be the installed catalog root holding the "current" pointer, not a version directory: %s\n' "$root" >&2
        if [[ "$root" == */versions/* ]]; then
            printf 'That path is a version directory. The root this variable takes is %s.\n' "${root%/versions/*}" >&2
        fi
        printf 'The Deployment CLI installer bundle variable takes the version directory; this one takes the root above it.\n' >&2
        return 2
    fi
    target="$(readlink "$pointer")"
    # Two checks on the target, because they catch different escapes and neither subsumes the other. This one is
    # textual: the pointer must name a single directory under versions/, which is the only thing CatalogInstaller
    # ever writes there. It refuses a pointer into a nested path or into a sibling of versions/, both of which
    # resolve inside the root and so pass the check below. Adopted from the independent implementation on #822,
    # which caught these two and not the one below; the cross-probe recording that is in the PR ledger.
    if [[ "$target" != versions/?* || "$target" == */*/* ]]; then
        printf 'The "current" pointer in %s must target versions/PACKAGE_VERSION; it targets %s.\n' \
            "$root" "$target" >&2
        return 2
    fi
    resolved="$(cd "$root" 2>/dev/null && cd "$target" 2>/dev/null && pwd -P)" || resolved=''
    if [[ -z "$resolved" || "$resolved" != "$root"/* ]]; then
        printf 'The "current" pointer in %s must resolve to a directory inside the same root; it points at %s.\n' \
            "$root" "$target" >&2
        return 2
    fi
    # Scoped to the version "current" resolves to, not to the whole root. A root that has taken an upgrade
    # legitimately holds more than one version: the installer repoints "current" and deletes nothing, collection is a
    # separate step that refuses to remove a referenced version, and rollback needs the outgoing version still on disk
    # to have anything to roll back to. Counting across the root rejected every such root, and every root holding a
    # ".gc-" tombstone, which is the ordinary state of an installation that has been upgraded once. What this check is
    # for is that the directory the container opens is unambiguous, and that is the resolved version alone.
    # Counted into a variable and compared numerically. BSD wc pads its output to a fixed width, so comparing the
    # substitution to the string 1 is false on macOS for every root, correct or not, and true nowhere; GNU wc does
    # not pad, which is why that reads as working on the Linux runner. Arithmetic evaluation ignores the padding.
    databases="$(find "$resolved" -type f -name '*.sqlite' -print | wc -l)"
    if (( databases != 1 )); then
        printf 'The catalog version %s that "current" resolves to must contain exactly one catalog database.\n' \
            "$resolved" >&2
        return 2
    fi
}
