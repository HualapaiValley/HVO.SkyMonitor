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
    local root="$1" pointer target resolved
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
    resolved="$(cd "$root" 2>/dev/null && cd "$target" 2>/dev/null && pwd)" || resolved=''
    if [[ -z "$resolved" || "$resolved" != "$root"/* ]]; then
        printf 'The "current" pointer in %s must resolve to a directory inside the same root; it points at %s.\n' \
            "$root" "$target" >&2
        return 2
    fi
    if [[ "$(find "$root" -type f -name '*.sqlite' -print | wc -l)" != 1 ]]; then
        printf 'The catalog root %s must contain exactly one catalog database.\n' "$root" >&2
        return 2
    fi
}
