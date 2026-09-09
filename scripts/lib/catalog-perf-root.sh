#!/usr/bin/env bash
# Shared shape validation for HVO_CATALOG_PERF_ROOT, the installed production catalog root
# that the opt-in CameraAgent acceptance campaigns bind into their containers.
#
# WHY THIS FILE EXISTS, AND WHY THE CHECK IS A SHAPE CHECK RATHER THAN AN EXISTENCE CHECK
#
# Two variables point into the same installed catalog tree and want opposite levels of it:
#
#   * HVO_CATALOG_PERF_ROOT is the installation ROOT. It holds `versions/` and the `current`
#     pointer, and the container resolves that pointer at startup through
#     CatalogSnapshotResolver.ReadPointer.
#   * The Deployment CLI `--catalog-bundle` option is the catalog VERSION directory, one
#     level further in. It refuses `current` outright, because CatalogInstaller rejects a
#     bundle path that is a link.
#
# Both levels exist, both are readable directories, and each contract read on its own makes
# the other level look correct. Every campaign runner used to accept the root as "a readable
# directory", which the version directory also is, so the wrong level was admitted silently.
# The container then died during startup with "Catalog current pointer is missing", and the
# runner surfaced that five minutes later as a login-endpoint timeout with the container
# already torn down. See issue #735; the cost was a lost campaign run during the #731
# diagnosis.
#
# The correction is to demand the shape the container will demand, at the point where the
# path is still attributable to the operator who typed it, and to name the likely mistake in
# the refusal. This deliberately checks only the structure the mismatch turns on -- the
# pointer, its target shape, and that the target resolves. Package identity, schema versions,
# database hash and row identity remain the campaign's own fail-closed gates; duplicating
# them here would be a second contract to keep in step.

# Refuse a catalog installation root that will not satisfy the container, naming both the
# expected level and the version-directory mistake. Callers exit 2 on failure, matching the
# usage refusals they already emit.
#
#   require_catalog_installation_root PATH [VARIABLE_NAME]
require_catalog_installation_root() {
    local root="$1"
    local variable="${2:-HVO_CATALOG_PERF_ROOT}"
    local pointer="$root/current"
    local target

    if [[ ! -d "$root" ]]; then
        _catalog_perf_root_refuse "$variable" "$root" 'it does not exist or is not a directory'
        return 1
    fi

    # -e follows the link, so a dangling pointer reaches the -L branch and is reported as a
    # broken pointer rather than as a missing one.
    if [[ ! -e "$pointer" && ! -L "$pointer" ]]; then
        _catalog_perf_root_refuse "$variable" "$root" "it has no 'current' entry"
        return 1
    fi
    if [[ ! -L "$pointer" ]]; then
        _catalog_perf_root_refuse "$variable" "$root" "its 'current' entry is not a symbolic link"
        return 1
    fi

    target="$(readlink "$pointer")"
    if [[ "$target" != versions/?* || "$target" == */*/* ]]; then
        _catalog_perf_root_refuse "$variable" "$root" \
            "its 'current' pointer targets '$target' rather than 'versions/PACKAGE_VERSION'"
        return 1
    fi
    if [[ ! -d "$root/$target" ]]; then
        _catalog_perf_root_refuse "$variable" "$root" \
            "its 'current' pointer targets '$target', which is not an installed version directory"
        return 1
    fi
}

# Print the refusal. Kept separate so every branch names the expectation and the mistake in
# the same words, and so the one branch that can identify the mistake positively -- a path
# that is itself a catalog version directory -- can add the root to use instead.
_catalog_perf_root_refuse() {
    local variable="$1" root="$2" reason="$3"
    local parent suggestion=''

    parent="${root%/}"
    parent="${parent%/*}"
    if [[ "${parent##*/}" == versions && -d "${parent%/*}" ]]; then
        suggestion="${parent%/*}"
    elif [[ -f "$root/manifest.json" && -f "$root/hyg_v42.sqlite" ]]; then
        suggestion='the directory that contains versions/ and current'
    fi

    printf '%s is not an installed production catalog root: %s\n' "$variable" "$root" >&2
    printf '  Refused because %s.\n' "$reason" >&2
    printf "  %s must be the installation ROOT, which holds 'versions/' and the 'current'\\n" "$variable" >&2
    printf "  pointer that the CameraAgent container resolves at startup.\\n" >&2
    printf '  The usual mistake is passing the catalog VERSION directory instead. That level\n' >&2
    printf "  belongs to the Deployment CLI --catalog-bundle option, which refuses 'current';\\n" >&2
    printf '  %s takes the level above it.\n' "$variable" >&2
    if [[ -n "$suggestion" ]]; then
        printf '  This path looks like a version directory. Try: %s\n' "$suggestion" >&2
    fi
}
