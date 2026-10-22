#!/usr/bin/env bash
# install.sh: checks this machine, then downloads the release's hvo-sky-install for it, checks the download and starts
# it (install contract section 5; docs/runbooks/deployment-installer.md#installsh).
#
#   curl -fsSL https://github.com/HualapaiValley/HVO.SkyMonitor/releases/latest/download/install.sh | bash -s -- [options] [installer arguments]
#
# Each release's copy installs that release: the release tool (create-release --install-script) writes its version into
# release_version below, and this copy, the repository's, refuses to run. It needs Linux on 64-bit ARM or x86-64, bash,
# curl, tar and sha256sum.
#
# It downloads the release's small files (the installer, SHA256SUMS, release.json and the catalog) into a private
# folder, checks each, and hands cameraagent install and upgrade the release (--release) and the catalog
# (--catalog-bundle); Docker then pulls CameraAgent's image from ghcr.io by the digest release.json names. With --from
# DIR, everything comes from a folder holding the release's files, the offline image archive included.
#
# The whole script is one function, called on its last line, so a download cut short runs nothing: cut short in the
# function, bash finds it unfinished; cut short in the last line, the call is not made, or is made without the marker
# that line ends its arguments with, and refused. Its standard input is the download, so it reads nothing from it: the
# installer's questions are on the terminal (/dev/tty).

hvo_sky_install_sh() {
  if (($# == 0)) || [ "${!#}" != "end of install.sh" ]; then
    printf 'install.sh: the download was cut short, and nothing was done: download it again.\n' >&2
    exit 1
  fi
  set -- "${@:1:$(($# - 1))}"
  set -euo pipefail
  # Interrupted, it stops with the install contract's 130; the download folder's trap still removes what it made.
  trap 'exit 130' INT TERM
  # The installer runs with the umask it would have without this script; this script's own files are private.
  local caller_umask
  caller_umask=$(umask)
  umask 077

  local release_version=''  # The release tool writes the release's version here (create-release --install-script).
  local repository="HualapaiValley/HVO.SkyMonitor"
  local image=skymonitor-cameraagent
  # scripts/test:install-sh puts the files this script reads (/etc/os-release and /proc/device-tree/model) in a folder of
  # its own. Nothing else changes with it.
  local root="${HVO_SKY_INSTALL_TEST_ROOT:-}"
  # Where the release's files are downloaded from. For tests only, HVO_SKY_INSTALL_TEST_RELEASE_URL names another
  # address, so a test can serve a locally built release: it must be an absolute https:// URL with a host, it is handed
  # to curl with --url (never as an option) and still fetched over HTTPS alone, and it is announced whenever it is set.
  local base_url="https://github.com/${repository}/releases/download/v${release_version}"

  # The free space each place needs, in KiB: the download (the installer and the release's small files), the catalog
  # once it is unpacked, and Docker's images (CameraAgent's and the one it replaces). The installer measures exactly what
  # it needs before it changes anything.
  local need_download_kib=$((300 * 1024)) need_catalog_kib=$((50 * 1024)) need_docker_kib=$((3 * 1024 * 1024))
  # How far this machine's clock may be from github.com's: further, and TLS, the attestation and the certificates
  # CameraAgent makes go wrong.
  local clock_tolerance_seconds=300
  # CameraAgent's container is limited to at least 2 CPUs and 4 GiB (install contract section 7). Docker refuses a CPU
  # limit above the CPUs it has; a memory limit above the machine's memory still applies, but cannot hold.
  local least_cpus=2 least_memory_bytes=$((4 * 1024 * 1024 * 1024))

  local failures=0

  # ---------------------------------------------------------------------------------------------------------------------
  # Output: this script's own is on standard error, so standard output is the installer's alone (its --json result, for
  # one). Only --help prints on standard output.

  say() { printf '%s\n' "$*" >&2; }
  ok() { printf '  ok    %s\n' "$*" >&2; }
  note() { printf '  note  %s\n' "$*" >&2; }
  failed() {
    printf '  FAIL  %s\n' "$*" >&2
    failures=$((failures + 1))
  }
  # The install contract's exit codes: 1 when a step failed and running this again continues (a download), 2 for a
  # command line that is not valid, 3 when it refused and nothing was changed (a check, or a file that is not the
  # release's), and 130 when it was interrupted.
  die() {
    printf 'install.sh: %s\n' "$*" >&2
    exit 1
  }
  refuse() {
    printf 'install.sh: %s\n' "$*" >&2
    exit 3
  }
  usage_error() {
    printf 'install.sh: %s\n' "$*" >&2
    printf 'install.sh --help lists its options.\n' >&2
    exit 2
  }

  usage() {
    local which="${release_version:-"(the repository's copy, which installs nothing)"}"
    cat <<EOF
install.sh for HVO SkyMonitor ${which}

Checks this machine, downloads hvo-sky-install ${release_version:+${release_version} }for it with the release's small
files, checks them against the release's SHA256SUMS (and its attestation, when gh is signed in) and the installer's
--version, and starts it as you.

  curl -fsSL https://github.com/${repository}/releases/download/v${release_version:-<version>}/install.sh | bash -s -- [options] [--] [installer arguments]

Options, before the installer's arguments:
  --from DIR   Take hvo-sky-install, SHA256SUMS, the catalog and CameraAgent's image (the release's offline image
               archive for this machine) from DIR, a folder holding the release's files, not from GitHub and ghcr.io.
               cameraagent install and cameraagent upgrade are given --release DIR and --no-download, so nothing is
               pulled.
  --check      Check this machine, then stop: download nothing and change nothing.
  -h, --help   This help. (--help after the installer's arguments is the installer's.)

The first argument that is not one of these, and everything after it (or after --), goes to hvo-sky-install:
cameraagent install, cameraagent upgrade, status, --help and the rest; with none, it runs cameraagent install, which
asks on the terminal for what it needs. Without --from, cameraagent install and
cameraagent upgrade are given the release's release.json and SHA256SUMS (--release, a private folder) and
--image-source registry: Docker pulls CameraAgent's image from ghcr.io by the digest release.json names. cameraagent
install is also given the release's catalog (--catalog-bundle), unpacked in ${XDG_CACHE_HOME:-~/.cache}/hvo-sky/catalogs
so that running it again names the same catalog. Whatever the installer is already given (--release, --image-ref,
--image-archive, --catalog-bundle or --config) is not given again. Run this as the user that runs Docker, not as root:
hvo-sky-install asks sudo itself for the steps that need root.

Exit codes: the installer's, when it ran; otherwise 0 when --check passed, 1 when a download failed (run this again),
2 for a command line that is not valid, 3 when a check or the verification refused and nothing was changed, and 130
when it was interrupted.
EOF
  }

  # ---------------------------------------------------------------------------------------------------------------------
  # Helpers

  have() { command -v "$1" >/dev/null 2>&1; }

  # True when there is a terminal for the installer to ask on: not from cron, CI or ssh without one.
  have_terminal() { { : </dev/tty; } 2>/dev/null; }

  # The value of a name in /etc/os-release, without its quotes. The file is read, never run.
  os_release() {
    local file="${root}/etc/os-release" line value=""
    [ -r "${file}" ] || return 0
    while IFS= read -r line || [ -n "${line}" ]; do
      case "${line}" in
        "$1="*)
          value=${line#*=}
          value=${value#[\"\']}
          value=${value%[\"\']}
          ;;
      esac
    done <"${file}"
    printf '%s' "${value}"
  }

  # and_list <a, b, c>: "a, b and c".
  and_list() {
    case "$1" in
      *", "*) printf '%s and %s' "${1%, *}" "${1##*, }" ;;
      *) printf '%s' "$1" ;;
    esac
  }

  # Human-readable KiB.
  size_of() {
    if (($1 >= 1024 * 1024)); then
      printf '%d.%d GB' $(($1 / 1024 / 1024)) $(($1 * 10 / 1024 / 1024 % 10))
    else
      printf '%d MB' $(($1 / 1024))
    fi
  }

  # trusted_folder <folder>: prints <folder>'s physical path (every link in it followed) when that folder, and every
  # folder above it up to /, is a real folder (not a link) that is root's or yours and that no one else can write to
  # unless it is sticky (as /tmp is: there no one else can rename or remove what root or you own). No one else can then
  # rename the folder this script makes in it, or put another in its place, between the check of the installer it
  # downloads there and the installer's start. Fails, printing nothing, otherwise. (-O is bash's own test that you own a
  # file.)
  trusted_folder() {
    local physical folder
    physical=$(cd -- "$1" 2>/dev/null && pwd -P) || return 1
    folder=${physical}
    while :; do
      if [ ! -d "${folder}" ] || [ -L "${folder}" ]; then
        return 1
      fi
      if [ ! -O "${folder}" ] && [ -z "$(find "${folder}" -maxdepth 0 -user 0 2>/dev/null)" ]; then
        return 1
      fi
      [ -n "$(find "${folder}" -maxdepth 0 \( -perm -1000 -o \( ! -perm -0020 ! -perm -0002 \) \) 2>/dev/null)" ] || return 1
      [ "${folder}" != / ] || break
      folder=$(dirname "${folder}")
    done
    printf '%s\n' "${physical}"
  }

  # own_folder <folder>: <folder> is still the folder this script made: a real folder (not a link), yours, mode 0700.
  own_folder() {
    [ -d "$1" ] && [ ! -L "$1" ] && [ -O "$1" ] && [ -n "$(find "$1" -maxdepth 0 -perm 0700 2>/dev/null)" ]
  }

  # trusted_file <file>: a real file (not a link) that is root's or yours and that no one else can write to. In a folder
  # trusted_folder admits, only you and root can then change it, or put another in its place, after it is checked.
  trusted_file() {
    [ -f "$1" ] && [ ! -L "$1" ] || return 1
    [ -O "$1" ] || [ -n "$(find "$1" -maxdepth 0 -user 0 2>/dev/null)" ] || return 1
    [ -n "$(find "$1" -maxdepth 0 ! -perm -0020 ! -perm -0002 2>/dev/null)" ]
  }

  # The --from folder's problem when it is not a folder only you and root can change: the installer reads release.json,
  # SHA256SUMS and the image archive from it after this script has checked them, so no one else may change them between.
  local exposed_from="is not a folder only you and root can change: it is writable by its group or by others (sticky or not), or it or a folder above it is someone else's, or a folder above it is writable by others and not sticky, so someone else could change the release's files after they are checked and before the installer reads them. Put the release's files in a folder of your own that no one else can write to (chmod -R go-w DIR), then run this again."

  # trusted_release_folder: --from's folder, already resolved to its physical path when the command line was read, is
  # still that path (no folder in it has become a link), is one trusted_folder admits, and is itself writable by no one
  # but its owner: the sticky exception is for the folders above it, such as /tmp, never for the release's own folder,
  # where a sticky bit would still let others add files.
  trusted_release_folder() {
    local physical
    physical=$(trusted_folder "${from}") && [ "${physical}" = "${from}" ] &&
      [ -n "$(find "${from}" -maxdepth 0 ! -perm -0020 ! -perm -0002 2>/dev/null)" ]
  }

  # check_space <path> <KiB needed> <what it is for>: the free space on the file system that holds the path (or the
  # nearest folder above it that is there).
  check_space() {
    local path=$1 need=$2 what=$3 free
    while [ ! -d "${path}" ] && [ "${path}" != / ]; do
      path=$(dirname "${path}")
    done
    free=$(df -Pk "${path}" 2>/dev/null | awk 'NR == 2 { print $4 }' || true)
    if ! [[ ${free} =~ ^[0-9]+$ ]]; then
      note "df could not tell the free space on ${path}: ${what} need $(size_of "${need}")"
    elif ((free < need)); then
      failed "${path} has $(size_of "${free}") free, and ${what} need $(size_of "${need}"): free some space, then run this again."
    else
      ok "$(size_of "${free}") free on ${path} for ${what}"
    fi
  }

  # What a curl exit code means.
  curl_problem() {
    case "$1" in
      5) printf "the proxy's name did not resolve" ;;
      6) printf 'its name did not resolve (DNS)' ;;
      7) printf 'the connection was refused, or the network is unreachable' ;;
      22) printf 'it answered with an HTTP error' ;;
      28) printf 'it did not answer in time' ;;
      35 | 51 | 53 | 54 | 58 | 59 | 60 | 66 | 77 | 80 | 82 | 83 | 90 | 91)
        printf 'TLS failed (curl exit %s): a clock that is wrong, or a proxy that intercepts HTTPS, does this' "$1" ;;
      *) printf 'curl exit %s' "$1" ;;
    esac
  }

  # fetch <url> <file>: a download over HTTPS only, retried a few times. The URL is curl's --url, never an option.
  fetch() {
    curl -fsSL --proto '=https' --tlsv1.2 --retry 3 --connect-timeout 15 --max-time 600 -o "$2" --url "$1"
  }

  # probe <url> <curl options...>: one request, HTTPS only, with a short timeout.
  probe() {
    local url=$1
    shift
    curl -sS --proto '=https' --tlsv1.2 --connect-timeout 15 --max-time 30 "$@" --url "${url}"
  }

  # The file's SHA-256, from its contents on standard input: given a name with a backslash or a newline in it, sha256sum
  # escapes the name, and puts a backslash before the hash.
  sha256_of() {
    sha256sum <"$1" | awk '{ print $1 }'
  }

  # verify_attestation <file> <name>: the file's build-provenance attestation names this repository's release workflow
  # (prerelease.yml for an integration pre-release, release.yml otherwise) at the release's tag, on a GitHub-hosted
  # runner, as docs/runbooks/prereleases.md checks it by hand.
  verify_attestation() {
    local workflow=release.yml verification
    case "${release_version}" in
      *-dev.*) workflow=prerelease.yml ;;
    esac
    if verification=$(gh attestation verify "$1" --repo "${repository}" \
      --cert-identity "https://github.com/${repository}/.github/workflows/${workflow}@refs/tags/v${release_version}" \
      --source-ref "refs/tags/v${release_version}" --deny-self-hosted-runners 2>&1 </dev/null); then
      ${quick} || ok "$2's attestation says ${repository}'s ${workflow} built it for v${release_version}"
    else
      printf '%s\n' "${verification}" | tail -n 5 | sed 's/^/        /' >&2
      refuse "$2's attestation did not verify, so it may not be the release's: nothing was installed."
    fi
  }

  # catalog_asset <SHA256SUMS>: the names of the release's catalog archives, <catalog version>.bundle.tar.gz, as
  # SHA256SUMS lists them (a release has one).
  catalog_asset() {
    awk '{ name = $2; sub(/^\*/, "", name); if (name ~ /^[A-Za-z0-9][A-Za-z0-9._-]*\.bundle\.tar\.gz$/) print name }' "$1"
  }

  # catalog_root: where the release's catalog is unpacked, ${XDG_CACHE_HOME:-~/.cache}/hvo-sky/catalogs. It is kept
  # there, not in the download folder, because the installer binds an install to the catalog folder it was given: a
  # second run, or a --resume, must name the same one.
  catalog_root() {
    local cache=${XDG_CACHE_HOME:-}
    [[ ${cache} == /* ]] || cache="${HOME}/.cache"
    printf '%s/hvo-sky/catalogs' "${cache%/}"
  }

  # The download: the installer for this machine and the release's files the hand-over needs, each checked against the
  # release's SHA256SUMS, their attestations (with gh) and the installer's --version; then the hand-over. It never
  # returns.
  fetch_and_start() {
    local release_page="https://github.com/${repository}/releases/tag/v${release_version}"
    local asset="hvo-sky-install-${rid}" work installer sums status=0 parent catalog="" catalog_dir="" file arch=arm64
    [ "${rid}" != linux-x64 ] || arch=amd64
    # A folder (--from) is used by the physical path it was resolved to once, when the command line was read, and only
    # when only you and root can change it, so the files the installer reads there are the ones checked here.
    if [ -n "${from}" ]; then
      trusted_release_folder || refuse "${from} ${exposed_from} Nothing was installed."
    fi
    # The download goes in a new folder of this script's own, made by its physical path in a TMPDIR only you and root
    # can change (trusted_folder), so the installer that runs is the one checked. Only that folder is removed, and only
    # while it is still the one made.
    parent=$(trusted_folder "${TMPDIR:-/tmp}") \
      || refuse "TMPDIR (${TMPDIR:-/tmp}) is not a folder only you and root can change: it is not there, or it or a folder above it is someone else's, or writable by others and not sticky, so someone else could change the installer after it is checked and before it runs. Nothing was made in it. Set TMPDIR to a folder that is, such as /tmp, then run this again."
    work=$(mktemp -d "${parent%/}/hvo-sky-install.XXXXXXXX") || die "Could not make a folder in ${parent} for the download."
    own_folder "${work}" || refuse "The folder made for the download, ${work}, is not a folder of yours with mode 0700: nothing was installed."
    local quoted_work
    printf -v quoted_work '%q' "${work}"
    # shellcheck disable=SC2064 # The folder is named now, so the trap needs no variable when it runs.
    trap "if own_folder ${quoted_work}; then rm -rf -- ${quoted_work}/hvo-sky-install ${quoted_work}/release; rmdir -- ${quoted_work} 2>/dev/null || true; fi" EXIT
    mkdir -- "${work}/release" || die "Could not make a folder in ${work} for the release's files."
    installer="${work}/hvo-sky-install"
    sums="${work}/release/SHA256SUMS"

    # take <name> <destination>: one of the release's files, from the folder (--from) or downloaded.
    take() {
      local status=0
      if [ -n "${from}" ]; then
        [ -f "${from}/$1" ] || refuse "${from} has no $1: download it from ${release_page} into it."
        trusted_file "${from}/$1" \
          || refuse "${from}/$1 is a link, or someone else's, or writable by others, so someone else could change it after it is checked: nothing was installed."
        cp -- "${from}/$1" "$2" || die "$1 could not be copied from ${from}."
      else
        fetch "${base_url}/$1" "$2" || status=$?
        ((status == 0)) || die "$1 could not be downloaded from ${base_url}: $(curl_problem "${status}")."
      fi
    }

    # verify_listed <file> <name>: the file is the one SHA256SUMS lists, once, under its name. SHA256SUMS is sha256sum's:
    # a hash, two spaces (or a space and *, for binary) and the file's name.
    verify_listed() {
      local expected actual
      expected=$(awk -v name="$2" '$2 == name || $2 == "*" name { print $1 }' "${sums}")
      [[ ${expected} =~ ^[0-9a-f]{64}$ ]] \
        || refuse "SHA256SUMS does not list $2 once, so it cannot be checked: nothing was installed."
      actual=$(sha256_of "$1")
      [ "${actual}" = "${expected}" ] \
        || refuse "$2's SHA-256 is ${actual}, and SHA256SUMS says ${expected}: it is not the release's, and nothing was installed. Run this again; if it happens again, tell the project (https://github.com/${repository}/issues)."
      ${quick} || ok "$2's SHA-256 is the one SHA256SUMS lists"
    }

    # What the hand-over needs besides the installer, unless the installer is already given it. cameraagent install and
    # upgrade get the release (--release): from a folder, that folder; otherwise its release.json and SHA256SUMS, and
    # Docker pulls the image by the digest release.json names, so its offline archive is never downloaded. cameraagent
    # install also gets the release's catalog.
    local hand_release=false hand_catalog=false
    if ! ${quick} && ${takes_release} && ! ${names_image}; then
      hand_release=true
    fi
    if ! ${quick} && ${install_command} && ! ${names_catalog}; then
      hand_catalog=true
    fi

    if [ -n "${from}" ]; then
      ${quick} || say "Taking hvo-sky-install ${release_version} for ${rid} from ${from}"
    else
      ${quick} || say "Downloading hvo-sky-install ${release_version} for ${rid} from ${base_url}"
    fi
    take SHA256SUMS "${sums}"
    take "${asset}" "${installer}"
    verify_listed "${installer}" "${asset}"
    if ${hand_release} && [ -z "${from}" ]; then
      cp -- "${installer}" "${work}/release/${asset}"
      take "hvo-sky-${rid}" "${work}/release/hvo-sky-${rid}"
      verify_listed "${work}/release/hvo-sky-${rid}" "hvo-sky-${rid}"
      take release.json "${work}/release/release.json"
      verify_listed "${work}/release/release.json" release.json
    fi
    if ${hand_catalog}; then
      catalog=$(catalog_asset "${sums}")
      case "${catalog}" in
        "" | *$'\n'*)
          refuse "SHA256SUMS does not list one catalog (<catalog version>.bundle.tar.gz), so the catalog cannot be given to the installer: nothing was installed. Give it --catalog-bundle yourself, or use the release's own SHA256SUMS."
          ;;
      esac
      take "${catalog}" "${work}/release/${catalog}"
      verify_listed "${work}/release/${catalog}" "${catalog}"
    fi

    # The attestations say the release's workflow built SHA256SUMS, which every other file is checked against, and the
    # installer itself. gh fetches them when it is signed in. They are checked before the installer is started at all,
    # for its help and version too.
    if ! have gh; then
      note "the attestations were not checked: gh (GitHub's CLI, https://cli.github.com) is not installed"
    elif ! gh auth status >/dev/null 2>&1 </dev/null; then
      note "the attestations were not checked: gh is not signed in (gh auth login), or did not reach GitHub (gh auth status)"
    else
      verify_attestation "${sums}" SHA256SUMS
      verify_attestation "${installer}" "${asset}"
    fi

    chmod 0755 "${installer}"
    local built
    status=0
    built=$("${installer}" --version 2>&1 </dev/null) || status=$?
    ((status != 126)) \
      || refuse "Programs cannot run from ${TMPDIR:-/tmp} (it is mounted noexec): set TMPDIR to a folder they can run from, then run this again. Nothing was installed."
    ((status == 0)) \
      || refuse "${asset} --version failed (exit ${status}, saying '${built}'), so it is not this release's installer working here: nothing was installed."
    case "${built}" in
      "${release_version}+"*) [[ ${built#"${release_version}+"} =~ ^[0-9a-f]{40}$ ]] ;;
      *) false ;;
    esac || refuse "${asset} says its version is '${built}', and this is release ${release_version}: nothing was installed."
    ${quick} || ok "${asset} says it is ${built}"

    if ${hand_catalog}; then
      supply_catalog "${work}/release/${catalog}" "${catalog}"
    fi

    # From a folder, the installer reads release.json and the image archive there itself: only you and root may be able
    # to change them.
    if ${hand_release} && [ -n "${from}" ]; then
      for file in release.json "${image}-${release_version}-linux-${arch}.oci.tar"; do
        [ ! -e "${from}/${file}" ] || trusted_file "${from}/${file}" \
          || refuse "${from}/${file} is a link, or someone else's, or writable by others, so someone else could change it before the installer reads it: nothing was installed."
      done
    fi

    # The hand-over. The installer reads the terminal, since this script's standard input is the download: with none
    # (cron, CI) it reads nothing, and asks nothing.
    local -a arguments=(${passed[@]+"${passed[@]}"})
    if ${hand_release} && [ -n "${from}" ]; then
      arguments+=(--release "${from}")
    elif ${hand_release}; then
      arguments+=(--release "${work}/release")
      [ -n "${image_source}" ] || arguments+=(--image-source registry)
    fi
    if [ -n "${from}" ] && ${takes_release} && ! ${quick} && ! ${no_download}; then
      arguments+=(--no-download)
    fi
    if ${hand_catalog}; then
      arguments+=(--catalog-bundle "${catalog_dir}")
    fi
    local input=/dev/null
    ! have_terminal || input=/dev/tty
    if ! ${quick}; then
      say ""
      say "Starting hvo-sky-install ${release_version}"
      say ""
    fi
    umask "${caller_umask}"
    status=0
    "${installer}" ${arguments[@]+"${arguments[@]}"} <"${input}" || status=$?
    exit "${status}"
  }

  # supply_catalog <archive> <name>: unpacks the checked catalog archive into catalog_root, replacing what an earlier run
  # unpacked there, and sets catalog_dir to the catalog's folder. The archive must hold only files in its one folder,
  # <catalog version>.bundle; the installer checks the catalog itself.
  supply_catalog() {
    local archive=$1 name=$2 base=${2%.tar.gz} wanted folder staging entry
    wanted=$(catalog_root)
    mkdir -p -- "${wanted}" || die "Could not make ${wanted} for the catalog."
    folder=$(trusted_folder "${wanted}") \
      || refuse "${wanted} is not a folder only you and root can change, so the catalog cannot be unpacked there: set XDG_CACHE_HOME to a folder that is, or give the installer --catalog-bundle yourself. Nothing was installed."
    while IFS= read -r entry; do
      case "${entry}" in
        "${base}/"?*) ;;
        *) refuse "${name} holds ${entry}, which is not a file in its folder ${base}: it is not a catalog the installer takes, and nothing was installed." ;;
      esac
      case "/${entry}/" in
        */../* | */./*) refuse "${name} holds ${entry}: it is not a catalog the installer takes, and nothing was installed." ;;
      esac
    done < <(tar -tzf "${archive}" 2>/dev/null || printf '%s\n' "(an archive tar cannot read)")
    while IFS= read -r entry; do
      [ "${entry:0:1}" = - ] \
        || refuse "${name} holds something other than files (${entry}): it is not a catalog the installer takes, and nothing was installed."
    done < <(tar -tvzf "${archive}" 2>/dev/null || printf '%s\n' "?")
    staging=$(mktemp -d "${folder}/.${base}.XXXXXXXX") || die "Could not make a folder in ${folder} for the catalog."
    if ! tar -xzf "${archive}" -C "${staging}" --no-same-owner --no-same-permissions 2>/dev/null || [ ! -d "${staging}/${base}" ]; then
      chmod -R u+w -- "${staging}" 2>/dev/null || true
      rm -rf -- "${staging}"
      die "${name} could not be unpacked into ${folder}."
    fi
    if [ -e "${folder}/${base}" ] || [ -L "${folder}/${base}" ]; then
      chmod -R u+w -- "${folder}/${base}" 2>/dev/null || true
      rm -rf -- "${folder:?}/${base}"
    fi
    if ! mv -- "${staging}/${base}" "${folder}/${base}" || ! rmdir -- "${staging}"; then
      die "${name} could not be put in ${folder}."
    fi
    catalog_dir="${folder}/${base}"
    ok "the catalog ${base} is unpacked in ${catalog_dir}"
  }

  # ---------------------------------------------------------------------------------------------------------------------
  # The test-only release URL, checked and announced before anything else.

  if [ -n "${HVO_SKY_INSTALL_TEST_RELEASE_URL+set}" ]; then
    local https_url='^https://[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?(:[0-9]{1,5})?(/[^[:space:]]*)?$'
    [[ ${HVO_SKY_INSTALL_TEST_RELEASE_URL} =~ ${https_url} ]] \
      || usage_error "HVO_SKY_INSTALL_TEST_RELEASE_URL is '${HVO_SKY_INSTALL_TEST_RELEASE_URL}', not an absolute https:// URL with a host. It is for tests only: unset it."
    base_url=${HVO_SKY_INSTALL_TEST_RELEASE_URL%/}
    printf 'install.sh: HVO_SKY_INSTALL_TEST_RELEASE_URL is set, for tests only: the release'"'"'s files come from %s, not from GitHub.\n' \
      "${base_url}" >&2
  fi

  # ---------------------------------------------------------------------------------------------------------------------
  # The command line: this script's options, then the installer's arguments.

  local from="" check_only=false
  while (($# > 0)); do
    case "$1" in
      --from)
        { (($# >= 2)) && [ -n "$2" ]; } || usage_error "$1 needs a value."
        from=$2
        shift 2
        ;;
      --from=?*)
        from=${1#--from=}
        shift
        ;;
      --check)
        check_only=true
        shift
        ;;
      -h | --help)
        usage
        exit 0
        ;;
      --)
        shift
        break
        ;;
      *) break ;;
    esac
  done
  local -a passed=("$@")
  # No installer arguments is an install, as the install contract has it: the installer is given cameraagent install, with
  # an install's checks, downloads and hand-over. (--check alone still stops after the checks.)
  ((${#passed[@]} > 0)) || passed=(cameraagent install)

  if [ -z "${release_version}" ]; then
    refuse "This is the repository's copy of install.sh, which names no release: use a release's copy.
  curl -fsSL https://github.com/${repository}/releases/latest/download/install.sh | bash"
  fi

  # What the installer is asked to do. Its command is its first word or two. An install (options with no command are
  # checked as one, as the install contract has it) and an upgrade start CameraAgent from the release, so they need
  # Docker and its limits;
  # cameraagent install and cameraagent upgrade read the release (--release DIR), and cameraagent install the catalog
  # (--catalog-bundle). Any other command checks what it needs itself, so a machine that lost Docker's limits can still
  # be looked at, rolled back or uninstalled. Help and the version only print. --config names the whole request, so
  # nothing is added to it.
  local words="" deploys="" takes_release=false install_command=false quick=false names_image=false names_catalog=false
  local no_download=false image_source="" index=0 argument
  if ((${#passed[@]} > 0)) && [[ ${passed[0]} != -* ]]; then
    words=${passed[0]}
    if ((${#passed[@]} > 1)) && [[ ${passed[1]} != -* ]]; then
      words="${words} ${passed[1]}"
    fi
  fi
  case "${words}" in
    "") deploys=install ;;
    "cameraagent install") deploys=install takes_release=true install_command=true ;;
    "cameraagent upgrade") deploys=upgrade takes_release=true ;;
    help) quick=true ;;
  esac
  while ((index < ${#passed[@]})); do
    argument=${passed[index]}
    case "${argument}" in
      --help | -h) quick=true ;;
      --version) [ -n "${words}" ] || quick=true ;;
      --release | --release=* | --image-ref | --image-ref=* | --image-archive | --image-archive=*) names_image=true ;;
      --catalog-bundle | --catalog-bundle=*) names_catalog=true ;;
      --config | --config=*) names_image=true names_catalog=true ;;
      --image-source)
        image_source=${passed[index + 1]:-}
        index=$((index + 1))
        ;;
      --image-source=*) image_source=${argument#--image-source=} ;;
      --no-download) no_download=true ;;
    esac
    index=$((index + 1))
  done
  if [ -n "${from}" ]; then
    [ -d "${from}" ] || usage_error "--from ${from}: there is no such folder."
    # Resolved once, here, to its physical path: every check, the attestation, the catalog and the hand-over use this path
    # alone, so a link in --from that is pointed elsewhere later changes nothing.
    from=$(CDPATH='' cd -- "${from}" && pwd -P) || usage_error "--from ${from}: the folder cannot be opened."
  elif ${takes_release} && ! ${names_image} && [ "${image_source}" = archive ]; then
    usage_error "Without --from, CameraAgent's image is pulled from ghcr.io by its digest, and its offline archive is not downloaded: leave out --image-source archive, or install from a folder holding the release's files (--from DIR)."
  fi

  # ---------------------------------------------------------------------------------------------------------------------
  # The platform: Linux on 64-bit ARM or x86-64, with glibc.

  local rid="" processor pi_model="" description kernel
  kernel=$(uname -s)
  [ "${kernel}" = Linux ] \
    || refuse "This machine runs ${kernel}: SkyMonitor's installer and CameraAgent run on Linux (64-bit ARM or x86-64)."
  processor=$(uname -m)
  case "${processor}" in
    aarch64 | arm64) rid=linux-arm64 ;;
    x86_64 | amd64) rid=linux-x64 ;;
    arm* | aarch32)
      refuse "This machine runs 32-bit ARM (${processor}), and SkyMonitor's programs are 64-bit: install the 64-bit Raspberry Pi OS (or another 64-bit Linux), then run this again."
      ;;
    *) refuse "This machine's processor is ${processor}: SkyMonitor's programs run on 64-bit ARM (aarch64) and x86-64." ;;
  esac
  # The 32-bit Raspberry Pi OS runs on a 64-bit kernel on a Pi 4 or 5: uname says aarch64, and nothing 64-bit runs.
  local bits
  bits=$(getconf LONG_BIT 2>/dev/null || printf 64)
  [ "${bits}" = 64 ] \
    || refuse "This machine has a 64-bit kernel and a ${bits}-bit system, and SkyMonitor's programs are 64-bit: install the 64-bit Raspberry Pi OS (or another 64-bit Linux), then run this again."
  # musl's ldd says so on its standard error, and exits 1.
  if have ldd; then
    case "$(ldd --version 2>&1 || true)" in
      *musl* | *MUSL*)
        refuse "This Linux has musl (as Alpine does), and SkyMonitor's programs need glibc (as Debian, Raspberry Pi OS and Ubuntu have)."
        ;;
    esac
  fi
  if [ -r "${root}/proc/device-tree/model" ]; then
    pi_model=$(tr -d '\0' <"${root}/proc/device-tree/model")
    case "${pi_model}" in
      "Raspberry Pi"*) ;;
      *) pi_model="" ;;
    esac
  fi
  description=$(os_release PRETTY_NAME)
  description="Linux on ${processor}${description:+, ${description}}${pi_model:+, ${pi_model}}"

  # hvo-sky-install runs as the user that runs Docker, and refuses root: it asks sudo itself for what needs root. Nothing
  # this script downloads is started as root, not even for its help or version.
  [ "$(id -u)" != 0 ] \
    || refuse "hvo-sky-install runs as the user that runs CameraAgent's Docker containers, not as root: run this as that user, without sudo (it asks sudo itself for the steps that need root). Nothing was changed."

  # The installer's help or version: nothing to check on this machine, and the installer is verified as fully as for an
  # install before it runs.
  if ${quick} && ! ${check_only}; then
    fetch_and_start
  fi

  say "HVO SkyMonitor ${release_version}: install.sh"
  say ""
  say "Checking this machine"
  ok "${description} (${rid})"

  # ---------------------------------------------------------------------------------------------------------------------
  # The commands this script and the installer use

  local tool missing=""
  for tool in curl tar sha256sum; do
    have "${tool}" || missing="${missing} ${tool}"
  done
  if [ -n "${missing}" ]; then
    failed "These commands are missing:${missing}. Install them (sudo apt-get install${missing/sha256sum/coreutils}), then run this again."
  else
    ok "curl, tar and sha256sum"
  fi
  have curl || refuse "Without curl nothing more can be checked: install it, then run this again."

  # ---------------------------------------------------------------------------------------------------------------------
  # Free space, the network and the clock

  check_space "${TMPDIR:-/tmp}" "${need_download_kib}" "the installer's download (TMPDIR)"
  # The catalog is unpacked where a second run finds it again (catalog_root): only you and root may change the folder.
  if [ "${deploys}" = install ] && ! ${names_catalog}; then
    local catalogs nearest
    catalogs=$(catalog_root)
    nearest=${catalogs}
    while [ ! -e "${nearest}" ] && [ "${nearest}" != / ]; do
      nearest=$(dirname "${nearest}")
    done
    if trusted_folder "${nearest}" >/dev/null; then
      check_space "${nearest}" "${need_catalog_kib}" "the catalog (${catalogs})"
    else
      failed "${nearest} is not a folder only you and root can change, and the catalog is unpacked in ${catalogs}: set XDG_CACHE_HOME to a folder that is, or give the installer --catalog-bundle yourself, then run this again."
    fi
  fi

  local headers status=0 remote_date remote_seconds skew
  headers=$(probe https://github.com -I 2>/dev/null) || status=$?
  if ((status != 0)); then
    if [ -n "${from}" ]; then
      note "github.com did not answer ($(curl_problem "${status}")): the release's files come from ${from}"
    else
      failed "github.com did not answer: $(curl_problem "${status}"). The installer and the release come from there."
    fi
  else
    ok "github.com answers"
    # The last Date is github.com's: through a proxy, its reply to CONNECT comes first. awk reads to the end: with
    # pipefail, a printf cut off by an awk that stopped reading would end the script.
    remote_date=$(printf '%s\n' "${headers}" | tr -d '\r' | awk 'tolower($1) == "date:" { sub(/^[^:]*:[ \t]*/, ""); date = $0 } END { if (date != "") print date }')
    remote_seconds=""
    [ -z "${remote_date}" ] || remote_seconds=$(LC_ALL=C date -u -d "${remote_date}" +%s 2>/dev/null || true)
    if [[ ${remote_seconds} =~ ^[0-9]+$ ]]; then
      skew=$(($(date -u +%s) - remote_seconds))
      ((skew >= 0)) || skew=$((-skew))
      if ((skew > clock_tolerance_seconds)); then
        failed "This machine's clock is $( ((skew < 600)) && printf '%d seconds' "${skew}" || printf '%d minutes' $(((skew + 30) / 60))) out (it says $(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S') GMT, and github.com ${remote_date}): set it, and synchronise it (sudo timedatectl set-ntp true), then run this again."
      else
        ok "the clock is within ${skew} s of github.com's"
      fi
    else
      note "github.com sent no date to compare this machine's clock with"
    fi
  fi
  if have timedatectl; then
    case "$(timedatectl show --property=NTPSynchronized --value 2>/dev/null || true)" in
      yes) ok "the clock is synchronised" ;;
      no) note "the clock is not synchronised: turn synchronisation on (sudo timedatectl set-ntp true), so that it stays right" ;;
    esac
  fi

  # The release's files. From a folder (--from), the installer, SHA256SUMS, release.json and, for an install or an
  # upgrade, the offline image archive for this machine, which the installer loads: ghcr.io is never asked. Otherwise
  # Docker pulls CameraAgent's image from ghcr.io.
  local arch=arm64 file
  [ "${rid}" != linux-x64 ] || arch=amd64
  if [ -n "${from}" ] && ! trusted_release_folder; then
    failed "${from} ${exposed_from}"
  elif [ -n "${from}" ]; then
    local -a files=("hvo-sky-install-${rid}" SHA256SUMS release.json)
    local absent=0 catalog="" exposed=""
    if [ -n "${deploys}" ] && ! ${names_image}; then
      files+=("${image}-${release_version}-linux-${arch}.oci.tar")
    fi
    # An install's catalog is the one archive SHA256SUMS lists as <catalog version>.bundle.tar.gz.
    if [ "${deploys}" = install ] && ! ${names_catalog} && [ -f "${from}/SHA256SUMS" ]; then
      catalog=$(catalog_asset "${from}/SHA256SUMS")
      case "${catalog}" in
        "" | *$'\n'*) failed "${from}/SHA256SUMS does not list one catalog (<catalog version>.bundle.tar.gz): use the release's own SHA256SUMS, or give the installer --catalog-bundle yourself." ;;
        *) files+=("${catalog}") ;;
      esac
    fi
    missing=""
    for file in "${files[@]}"; do
      if [ ! -f "${from}/${file}" ]; then
        missing="${missing:+${missing}, }${file}"
        absent=$((absent + 1))
      elif ! trusted_file "${from}/${file}"; then
        exposed="${exposed:+${exposed}, }${file}"
      fi
    done
    if [ -n "${exposed}" ]; then
      failed "In ${from}, $(and_list "${exposed}") $(printf '%s' "${exposed}" | grep -q ', ' && printf 'are' || printf 'is') a link, or someone else's, or writable by others, so someone else could change it after it is checked: make them yours and writable by you alone (chmod go-w), then run this again."
    fi
    if [ -n "${missing}" ]; then
      failed "${from} does not have $(and_list "${missing}"): download $( ((absent == 1)) && printf 'it' || printf 'them') from https://github.com/${repository}/releases/tag/v${release_version} into ${from}, then run this again."
    elif [ -n "${deploys}" ] && ! ${names_image}; then
      ok "${from} has the release's files, and ${image}-${release_version}-linux-${arch}.oci.tar: the installer loads CameraAgent's image from it, and pulls nothing from ghcr.io"
    else
      ok "${from} has the release's files"
    fi
  elif [ -n "${deploys}" ]; then
    local code
    status=0
    code=$(probe https://ghcr.io/v2/ -o /dev/null -w '%{http_code}' 2>/dev/null) || status=$?
    if ((status != 0)); then
      failed "ghcr.io did not answer: $(curl_problem "${status}"). Docker pulls CameraAgent's image from there (or install from a folder: --from DIR)."
    else
      ok "ghcr.io answers (HTTP ${code})"
    fi
  fi

  # ---------------------------------------------------------------------------------------------------------------------
  # Docker, for an install or an upgrade: its engine answers you, it is 20.10 or later with Compose v2, and it enforces
  # the memory, CPU and process limits CameraAgent's container runs with (install contract section 7). A Docker that
  # cannot enforce a limit drops it with only a warning, and CameraAgent would run unbounded: the installer refuses it
  # too. Raspberry Pi OS boots with cgroup_disable=memory, for one.

  check_docker() {
    local version major minor compose info memory cpu pids cpus memory_total docker_root limits="" remedy
    if ! have docker; then
      failed "Docker is not installed, and CameraAgent runs in it: install Docker Engine 20.10 or later with its Compose plugin (https://docs.docker.com/engine/install/), then run this again."
      return
    fi
    if ! version=$(docker version --format '{{.Server.Version}}' 2>&1 </dev/null); then
      case "${version}" in
        *"permission denied"*)
          failed "Docker's engine does not let you in ($(printf '%s' "${version}" | tail -n 1)), and hvo-sky-install runs CameraAgent as you: add yourself to the docker group (sudo usermod -aG docker $(id -un)), sign in again, then run this again."
          ;;
        *)
          failed "Docker is installed, and its engine did not answer ($(printf '%s' "${version}" | tail -n 1)): start it (sudo systemctl enable --now docker), then run this again."
          ;;
      esac
      return
    fi
    if ! [[ ${version} =~ ^([0-9]+)\.([0-9]+) ]]; then
      failed "Docker's engine says its version is '${version}', which this script cannot read: the installer needs 20.10 or later."
      return
    fi
    major=$((10#${BASH_REMATCH[1]}))
    minor=$((10#${BASH_REMATCH[2]}))
    if ((major < 20 || (major == 20 && minor < 10))); then
      failed "Docker ${version} is too old, and the installer needs Docker Engine 20.10 or later: upgrade it (https://docs.docker.com/engine/install/), then run this again."
      return
    fi
    compose=$(docker compose version --short 2>/dev/null </dev/null || true)
    compose=${compose#v}
    if [[ ${compose} =~ ^([2-9]|[1-9][0-9]+)\. ]]; then
      ok "Docker ${version}, with Compose ${compose}"
    else
      failed "Docker ${version} has no Compose v2, and the installer runs CameraAgent with it: install it (sudo apt-get install docker-compose-plugin), then run this again."
    fi

    if ! info=$(docker info --format '{{.MemoryLimit}},{{.CPUCfsQuota}},{{.PidsLimit}},{{.NCPU}},{{.MemTotal}},{{.DockerRootDir}}' 2>/dev/null </dev/null); then
      failed "Docker did not say which limits it enforces (docker info), and CameraAgent's container runs with memory, CPU and process limits: check that docker info works, then run this again."
      return
    fi
    IFS=, read -r memory cpu pids cpus memory_total docker_root <<<"${info}"
    [ "${memory}" = true ] || limits="memory"
    [ "${cpu}" = true ] || limits="${limits:+${limits}, }CPU"
    [ "${pids}" = true ] || limits="${limits:+${limits}, }process"
    if [ -n "${limits}" ]; then
      if [ -n "${pi_model}" ] && [ "${memory}" != true ]; then
        remedy="add a space and cgroup_enable=memory to the end of the single line in /boot/firmware/cmdline.txt, and reboot"
      else
        remedy="give the Docker daemon the memory, cpu and pids cgroup controllers"
      fi
      failed "Docker does not enforce $(and_list "${limits}") limits (docker info: MemoryLimit ${memory:-?}, CPUCfsQuota ${cpu:-?}, PidsLimit ${pids:-?}), and CameraAgent's container runs with memory, CPU and process limits: ${remedy}; docker info must then say true for all three. Run this again after that."
    else
      ok "Docker enforces memory, CPU and process limits"
    fi
    if [[ ${cpus} =~ ^[0-9]+$ ]] && ((cpus < least_cpus)); then
      failed "Docker has ${cpus} CPU, and CameraAgent's container is limited to at least ${least_cpus}, which Docker refuses on this machine: run CameraAgent on a machine with at least ${least_cpus} CPUs."
    fi
    if [[ ${memory_total} =~ ^[0-9]+$ ]] && ((memory_total < least_memory_bytes)); then
      note "This machine has $(size_of $((memory_total / 1024))) of memory, less than CameraAgent's 4 GiB limit: the limit is still set, and cannot keep CameraAgent from using all of it"
    fi
    check_space "${docker_root:-/var/lib/docker}" "${need_docker_kib}" "Docker's images"
    # Online, the installer reads CameraAgent's image from the registry with docker buildx imagetools (0.8 or later)
    # before anything is pulled.
    if [ -z "${from}" ] && ! ${names_image} && [ "${image_source}" != archive ]; then
      local buildx
      buildx=$(docker buildx version 2>/dev/null </dev/null | awk 'NR == 1' || true)
      if [[ ${buildx} =~ v([0-9]+)\.([0-9]+) ]] && ((10#${BASH_REMATCH[1]} > 0 || 10#${BASH_REMATCH[2]} >= 8)); then
        ok "docker buildx ${BASH_REMATCH[1]}.${BASH_REMATCH[2]}, which reads CameraAgent's image from the registry before it is pulled"
      else
        failed "docker buildx 0.8 or later is not installed${buildx:+ (docker buildx version: ${buildx})}, and the installer reads CameraAgent's image from the registry with it before pulling: install it (sudo apt-get install docker-buildx-plugin), or install from a folder (--from DIR), then run this again."
      fi
    fi
  }

  if [ -n "${deploys}" ]; then
    check_docker
  fi

  say ""
  if ((failures > 0)); then
    refuse "$( ((failures == 1)) && printf 'A check' || printf '%d checks' "${failures}") failed, and nothing was downloaded or installed."
  fi
  if ${check_only}; then
    say "This machine is ready for hvo-sky-install ${release_version}."
    exit 0
  fi

  fetch_and_start
}

hvo_sky_install_sh "$@" "end of install.sh" </dev/null
