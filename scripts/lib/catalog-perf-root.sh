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
    local root pointer target resolved databases link_component
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
    # Guard the argument before it reaches cd, not after. `cd ""` succeeds in bash and stays put, so resolving an
    # empty argument yields the working directory rather than the empty string: the backstop below is never reached
    # and a caller whose cwd happens to be a correct root gets success for a root it never named. Called with no
    # argument at all, an unguarded "$1" dies on nounset before any diagnostic prints, since nounset is fatal
    # whether or not errexit is suspended.
    if [[ -z "${1:-}" ]]; then
        printf 'HVO_CATALOG_PERF_ROOT must name a directory this process can enter, and was empty or unset.\n' >&2
        return 2
    fi
    root="$(cd "$1" 2>/dev/null && pwd -P)" || root=''
    if [[ -z "$root" ]]; then
        # A backstop rather than the usual path. Each runner already refuses a non-existent root by name before
        # sourcing this file, so what reaches here is a directory that exists and cannot be entered, or a root
        # deleted between that check and this call. The empty argument is handled above and does not reach here.
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
    # The segment must start with a letter or digit, which is what the container's version validator requires. A
    # bare "?" here admits "versions/.", whose single dot satisfies the pattern, is not a link, resolves to
    # versions/ inside the root, and counts one database on a single-version root. The container refuses it twice,
    # in the contained-path check and again in version validation, so this admitted a root the container rejects.
    # The first character is not the whole rule. IsValidVersion also constrains every remaining character to an
    # ASCII alphanumeric or . _ + - so a name like "1.0 0" passes a first-character-only pattern, is not a link,
    # resolves inside the root and counts one database. The third clause tests the segment after versions/ for any
    # character outside that set. The prefix is stripped first because the slash itself is outside the set.
    if [[ "$target" != versions/[A-Za-z0-9]* || "$target" == */*/* || "${target#versions/}" == *[!A-Za-z0-9._+-]* ]]; then
        printf 'The "current" pointer in %s must target versions/PACKAGE_VERSION; it targets %s.\n' \
            "$root" "$target" >&2
        return 2
    fi
    # The container applies a stronger rule than containment, and this precondition has to match it rather than
    # approximate it. ReadPointer calls EnsureDirectoryIsNotLink on the versions directory and again on the
    # snapshot directory, so no component of the resolved path may be a link, whatever it points at. Containment
    # alone admits a link whose target stays inside the root: it resolves under the root, both count scopes pass,
    # and nothing below fires, so this function returns success on a root the container then refuses. That is the
    # original failure mode intact, an unhandled pointer error surfacing minutes later at an unrelated endpoint,
    # which is exactly what this file exists to refuse first. Two shapes reach it, a linked version directory and
    # a linked versions/, and a superset check against the container contract found them and nothing else.
    #
    # The "current" pointer is deliberately not checked here. It is a link by design and the container reads it
    # as one; it is validated above as a pointer, not as a path component.
    for link_component in "$root/versions" "$root/$target"; do
        if [[ -L "$link_component" ]]; then
            printf 'No directory on the resolved catalog path may be a symbolic link, and %s is one. The container refuses this root whatever the link targets, including a target that stays inside the root.\n' \
                "$link_component" >&2
            return 2
        fi
    done
    resolved="$(cd "$root" 2>/dev/null && cd "$target" 2>/dev/null && pwd -P)" || resolved=''
    if [[ -z "$resolved" || "$resolved" != "$root"/* ]]; then
        printf 'The "current" pointer in %s must resolve to a directory inside the same root; it points at %s.\n' \
            "$root" "$target" >&2
        return 2
    fi
    # Scoped to the version "current" resolves to, not to the whole root. A root that has taken an upgrade
    # legitimately holds more than one version, and it does not get there by the pointer moving. The installer does
    # not repoint "current": EnsureLegacyCurrentPointer returns early when the link already exists and is the only
    # place in src that creates it, so selection is a configuration write. A second install adds a version directory
    # and leaves the pointer where it was, collection is a separate step that refuses to remove a referenced version,
    # and rollback needs the outgoing version still on disk to have anything to roll back to. Counting across the root rejected every such root, and every root holding a
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
