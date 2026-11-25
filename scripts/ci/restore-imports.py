"""Capture and replay the reviewed Development v1 restore graph."""

import hashlib
import json
import os
from pathlib import Path
import posixpath
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
from urllib.parse import urlsplit
import xml.etree.ElementTree as ET

CERTIFICATE = "scripts/ci:restore-import-certificate.json"
SOLUTION = "HVO.SkyMonitor.v9.slnx"
COVERAGE_PROJECT = "scripts/coverage/HVO.SkyMonitor.CoverageGate.csproj"
HEX = re.compile(r"[0-9a-f]{64}")
IDENTITY = re.compile(r"[a-z0-9_.-]+/[a-z0-9_.+-]+")
ANCESTOR_INPUTS = {"nuget.config", "global.json", "directory.build.props", "directory.build.targets",
                   "directory.packages.props", "directory.build.rsp", "msbuild.rsp"}


class ProofError(Exception):
    pass


def require(condition, reason):
    if not condition:
        raise ProofError(reason)


def unique_object(pairs):
    result = dict(pairs)
    require(len(result) == len(pairs), "duplicate certificate or restore data keys")
    return result


def parse(data):
    return json.loads(data, object_pairs_hook=unique_object)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode()


def digest(data):
    return hashlib.sha256(data).hexdigest()


def hash_file(path):
    require(stat.S_ISREG(path.lstat().st_mode), "non-regular SDK or package file")
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def file_set_hash(directory, excluded=()):
    require(directory.is_dir() and not directory.is_symlink(), "SDK or package directory unavailable")
    files = {}
    for base, subdirs, names in os.walk(directory, followlinks=False, onerror=raise_walk_error):
        parent = Path(base)
        require(all(not (parent / name).is_symlink() for name in subdirs), "linked SDK or package directory")
        for name in names:
            path = parent / name
            relative = path.relative_to(directory).as_posix()
            if relative not in excluded:
                files[relative] = hash_file(path)
    require(files, "empty SDK or package payload")
    return digest(canonical(files))


def raise_walk_error(error):
    raise error


GLOBAL_JSON_KEYS = {"sdk"}
GLOBAL_JSON_SDK_KEYS = {"version", "rollForward", "allowPrerelease"}


def decode_utf8_json(data, reason):
    require(
        isinstance(data, (bytes, bytearray)) and
        not data.startswith((b"\x00\x00\xfe\xff", b"\xff\xfe\x00\x00", b"\xff\xfe", b"\xfe\xff")) and
        b"\x00" not in data,
        reason,
    )
    try:
        return data.decode("utf-8-sig")
    except UnicodeDecodeError as error:
        raise ProofError(reason) from error


def read_sdk_pin(data):
    text = decode_utf8_json(data, "global.json is not UTF-8")
    try:
        settings = parse(text)
    except ValueError as error:
        raise ProofError("unknown SDK resolver configuration") from error
    require(type(settings) is dict and set(settings) == GLOBAL_JSON_KEYS, "unknown SDK resolver configuration")
    version_pin = settings["sdk"]
    require(type(version_pin) is dict and set(version_pin) == GLOBAL_JSON_SDK_KEYS,
            "unknown SDK resolver configuration")
    version = version_pin["version"]
    require(type(version) is str and re.fullmatch(r"10\.0\.[0-9]+", version) and
            version_pin["rollForward"] == "disable" and
            version_pin["allowPrerelease"] is False, "SDK pin unavailable or unsupported")
    return version


def git_regular_blob(commit, path, missing_reason):
    entry = subprocess.check_output(["git", "ls-tree", commit, "--", path])
    require(entry.startswith((b"100644 blob ", b"100755 blob ")), missing_reason)
    return subprocess.check_output(["git", "cat-file", "blob", f"{commit}:{path}"])


RESERVED_FOLDER_NAMES = frozenset({
    "CON", "PRN", "AUX", "NUL", "CLOCK$",
    *(f"COM{i}" for i in range(1, 10)),
    *(f"LPT{i}" for i in range(1, 10)),
})


def ordinal_ignore_case(text):
    out = []
    for c in text:
        code = ord(c)
        if code in (0x0131, 0x017F):
            out.append(c)
        elif 0x10D70 <= code <= 0x10D85:
            out.append(chr(code - 0x20))
        else:
            upper = c.upper()
            if len(upper) == 1:
                out.append(upper)
            else:
                title = c.title()
                out.append(title if len(title) == 1 else c)
    return "".join(out)


def is_valid_folder_name(folder_name):
    if (len(folder_name) < 3 or
            not folder_name.startswith("/") or
            not folder_name.endswith("/") or
            any(c in folder_name for c in '\\:*?"<>|') or
            any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in folder_name)):
        return False
    for segment in folder_name[1:-1].split("/"):
        if not segment.strip() or segment in (".", ".."):
            return False
        stem = segment.rsplit(".", 1)[0] if "." in segment else segment
        if stem.isascii() and stem.upper() in RESERVED_FOLDER_NAMES:
            return False
    return True


def solution_projects(solution_blob):
    text = solution_blob.decode("utf-8-sig")
    require(not re.search(r"&#|%[0-9a-fA-F]{2}", text), "escaped build input requires manual review")
    # Match the source audit before ElementTree normalizes literal attribute whitespace.
    attributes = re.finditer(
        r"""<!--.*?-->|<!\[CDATA\[.*?\]\]>|<\?.*?\?>|"""
        r"""<(?:Folder\s+Name|Project\s+Path)\s*=\s*(["'])(.*?)\1""",
        text, re.DOTALL,
    )
    require(not any(re.search(r"[\t\r\n]", match[2] or "") for match in attributes),
            "solution attribute contains control whitespace")
    solution_root = ET.fromstring(text)
    require(solution_root.tag == "Solution" and not solution_root.attrib, "unknown solution format")
    expected_projects = set()
    seen_folders = set()

    def add_project(node):
        require(len(node) == 0 and set(node.attrib) == {"Path"}, "unknown solution entry")
        value = node.attrib["Path"].replace("\\", "/")
        require(value and not value.startswith("/") and not any(c in value for c in ("$", "*", "?", ";")),
                "unresolved solution project")
        target = posixpath.normpath(value)
        require(target != ".." and not target.startswith("../") and target not in expected_projects,
                "solution project outside committed inventory")
        expected_projects.add(target)

    for child in solution_root:
        if child.tag == "Project":
            add_project(child)
        elif child.tag == "Folder":
            folder_name = child.attrib.get("Name", "")
            folder_key = ordinal_ignore_case(folder_name)
            require(
                set(child.attrib) == {"Name"} and
                is_valid_folder_name(folder_name) and
                folder_key not in seen_folders,
                "unknown solution entry",
            )
            seen_folders.add(folder_key)
            for entry in child:
                require(entry.tag == "Project", "unknown solution entry")
                add_project(entry)
        else:
            raise ProofError("unknown solution entry")
    require(expected_projects, "solution has no projects")
    return expected_projects


def committed_expected_projects(commit):
    solution_blob = git_regular_blob(commit, SOLUTION, "solution is unavailable or non-regular")
    expected_projects = solution_projects(solution_blob)
    coverage_entry = subprocess.check_output(["git", "ls-tree", commit, "--", COVERAGE_PROJECT])
    if coverage_entry:
        require(coverage_entry.startswith((b"100644 blob ", b"100755 blob ")), "non-regular build input")
        expected_projects.add(COVERAGE_PROJECT)
    return expected_projects


def validate_certificate(commit, source_hash):
    blob = subprocess.check_output(["git", "cat-file", "blob", f"{commit}:{CERTIFICATE}"])
    manifest = parse(blob)
    require(type(manifest) is dict and
            set(manifest) == {"version", "sourceSha256", "sdk", "packages", "projects"} and
            type(manifest["version"]) is int and manifest["version"] == 1, "invalid restore import certificate schema")
    require(manifest["sourceSha256"] == source_hash, "restore import certificate source inputs differ")
    sdk = manifest["sdk"]
    require(type(sdk) is dict and set(sdk) == {"version", "platform", "sha256"} and
            type(sdk["version"]) is str and re.fullmatch(r"10\.0\.[0-9]+", sdk["version"]) and
            sdk["platform"] == "linux-x64" and
            type(sdk["sha256"]) is str and HEX.fullmatch(sdk["sha256"]), "invalid SDK certificate entry")
    committed_sdk_version = read_sdk_pin(
        git_regular_blob(commit, "global.json", "committed global.json SDK pin unavailable"))
    require(sdk["version"] == committed_sdk_version,
            "restore import certificate SDK version differs from global.json")
    require(type(manifest["packages"]) is dict and type(manifest["projects"]) is dict and
            manifest["projects"], "invalid restore import certificate records")
    for identity, package in manifest["packages"].items():
        require(IDENTITY.fullmatch(identity) and type(package) is dict and
                set(package) == {"source", "archiveSha256", "payloadSha256"} and
                type(package["source"]) is str and package["source"] and
                type(package["archiveSha256"]) is str and HEX.fullmatch(package["archiveSha256"]) and
                type(package["payloadSha256"]) is str and HEX.fullmatch(package["payloadSha256"]),
                "invalid package certificate entry")
    for path, project in manifest["projects"].items():
        require("/" in path and path.endswith(".csproj") and type(project) is dict and
                set(project) == {"graphSha256", "sources", "props", "targets"} and
                type(project["graphSha256"]) is str and HEX.fullmatch(project["graphSha256"]) and
                type(project["sources"]) is list and all(type(source) is str and source for source in project["sources"]),
                "invalid project certificate entry")
        for kind in ("props", "targets"):
            imports = project[kind]
            require(type(imports) is dict and set(imports) == {"sha256", "imports"} and
                    type(imports["sha256"]) is str and HEX.fullmatch(imports["sha256"]) and
                    type(imports["imports"]) is list and all(type(value) is str and value for value in imports["imports"]),
                    "invalid generated import certificate entry")
    expected_projects = committed_expected_projects(commit)
    require(set(manifest["projects"]) == expected_projects,
            "restore import certificate project set differs from committed solution")
    return manifest


def extract_commit(commit, checkout):
    # Restore builds its graph from a fresh copy of the trusted commit only.
    # This prevents an ignored obj file or a PR checkout file from supplying evidence.
    with subprocess.Popen(["git", "archive", "--format=tar", commit], stdout=subprocess.PIPE) as archive:
        with archive.stdout, tarfile.open(fileobj=archive.stdout, mode="r|") as tree:
            tree.extractall(checkout, filter="data")
        require(archive.wait() == 0, "trusted commit snapshot unavailable")


def check_ancestors(checkout):
    for parent in checkout.parents:
        require(not any(path.name.lower() in ANCESTOR_INPUTS for path in parent.iterdir()),
                "an external ancestor can supply restore or build inputs")


def sdk_command(checkout, env):
    version = read_sdk_pin((checkout / "global.json").read_bytes())
    install_dir = env.get("DOTNET_INSTALL_DIR")
    candidate = str(Path(install_dir) / "dotnet") if install_dir else "dotnet"
    executable = shutil.which(candidate)
    require(executable, "dotnet executable unavailable")
    dotnet = Path(executable).resolve(strict=True)
    sdk_path = dotnet.parent / "sdk" / version
    sdk_version = (sdk_path / ".version").read_text().splitlines()
    require(sdk_version[1:3] == [version, "linux-x64"], "SDK version or platform differs")
    return dotnet, sdk_path, {"version": version, "platform": sdk_version[2], "sha256": file_set_hash(sdk_path)}


def source_name(source, checkout):
    if source.startswith("/"):
        relative = Path(source).resolve().relative_to(checkout)
        return f"repository:{relative.as_posix()}"
    url = urlsplit(source)
    require(url.scheme == "https" and url.hostname and not (url.username or url.password or url.query or url.fragment),
            "unknown package source")
    return source


def normalize(value, checkout, packages, sdk_path):
    # Normalize only the paths that vary by runner. Preserve spelling, import
    # order, predicates and all the remaining graph and generated XML bytes.
    for path, token in ((packages, "<packages>"), (checkout, "<source>"), (sdk_path, "<sdk>")):
        for delimiter in ('/', '"', '<'):
            normalized = token + delimiter
            if path == packages and delimiter != "/":
                normalized = token + "/" + delimiter
            value = value.replace(f"{path}{delimiter}", normalized)
    return value


def generated_imports(path, checkout, packages, sdk_path, graph_packages):
    data = path.read_bytes().decode("utf-8")
    prefix = "$(NuGetPackageRoot)"
    xml = ET.fromstring(data)
    require(xml.tag == "{http://schemas.microsoft.com/developer/msbuild/2003}Project", "unknown generated import format")
    imports = []
    for node in xml.iter():
        if node.tag.rsplit("}", 1)[-1].lower() != "import":
            continue
        require(node.tag == "{http://schemas.microsoft.com/developer/msbuild/2003}Import" and
                set(node.attrib) <= {"Project", "Condition"}, "unknown generated MSBuild import")
        target = node.attrib["Project"]
        relative = target.removeprefix(prefix).removeprefix("/")
        parts = relative.split("/")
        require(target.startswith(prefix) and len(parts) >= 3 and
                all(part not in ("", ".", "..") for part in parts) and
                re.fullmatch(r"[a-zA-Z0-9_.+/-]+", relative) and
                "/".join(parts[:2]) in graph_packages and (packages / relative).is_file(),
                "generated import is outside the restored package graph")
        imports.append(relative)
    # NuGet writes the separator in either the root property value or the
    # import expression, depending on the package-cache configuration.
    data = data.replace(prefix + "/", prefix)
    return {"sha256": digest(normalize(data, checkout, packages, sdk_path).encode()), "imports": imports}


def resolved_identities(assets):
    identities = {identity.lower() for identity, entry in assets["libraries"].items() if entry["type"] == "package"}
    for framework in assets["project"]["frameworks"].values():
        for request in framework.get("downloadDependencies", []):
            version = re.fullmatch(r"\[([0-9a-zA-Z.+-]+)(?:, \1)?\]", request["version"])
            require(version, "resolved download dependency is not exact")
            identities.add(f"{request['name'].lower()}/{version[1].lower()}")
    require(all(IDENTITY.fullmatch(identity) and not ({".", ".."} & set(identity.split("/"))) for identity in identities),
            "unknown resolved package identity")
    return identities


def package_record(packages, identity, sources, checkout):
    name, version = identity.split("/")
    directory = packages / identity
    archive = f"{name}.{version}.nupkg"
    meta = ".nupkg.metadata"
    require(not directory.parent.is_symlink() and not directory.is_symlink() and
            stat.S_ISREG((directory / meta).lstat().st_mode), "linked package directory or metadata")
    source = source_name(parse((directory / meta).read_bytes())["source"], checkout)
    require(source in sources, "resolved package came from an unexpected source")
    return {"source": source, "archiveSha256": hash_file(directory / archive),
            "payloadSha256": file_set_hash(directory, (archive, meta))}


def collect_restore(checkout, expected):
    env = dict(os.environ, MSBUILDDISABLENODEREUSE="1", DOTNET_CLI_USE_MSBUILD_SERVER="0",
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    require(not any(key.lower().startswith(("dotnet_msbuild_sdk_resolver_", "dotnetsdk_workload_manifest_roots",
                                           "msbuildsdkspath", "msbuildexepath", "msbuildextensionspath",
                                           "msbuilduserextensionspath", "custombefore", "customafter",
                                           "directorybuildpropspath", "directorybuildtargetspath"))
                    for key in env), "SDK or build import environment overrides are present")
    dotnet, sdk_path, sdk = sdk_command(checkout, env)
    require(expected is None or sdk == expected["sdk"], "installed SDK differs from the restore import certificate")
    version = subprocess.check_output([dotnet, "--version"], cwd=checkout, env=env, timeout=15).decode().strip()
    require(version == sdk["version"], "executing SDK differs from the committed SDK pin")
    restore_targets = [SOLUTION]
    expected_projects = solution_projects((checkout / SOLUTION).read_bytes())
    if (checkout / COVERAGE_PROJECT).is_file():
        restore_targets.append(COVERAGE_PROJECT)
        expected_projects.add(COVERAGE_PROJECT)
    require(not any(checkout.rglob("project.assets.json")) and not any(checkout.rglob("*.nuget.g.*")),
            "trusted commit contains pre-existing restore outputs")
    for target in restore_targets:
        result = subprocess.run([dotnet, "restore", target, "--disable-build-servers", "--verbosity", "quiet"],
                                cwd=checkout, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=90)
        require(result.returncode == 0, "trusted restore did not succeed")
    projects = {}
    packages = {}
    generated_files = set()
    for assets_path in sorted(checkout.rglob("project.assets.json")):
        assets = parse(assets_path.read_bytes())
        require(assets["version"] == 4 and not assets.get("logs"), "unexpected restore assets or diagnostics")
        details = assets["project"]["restore"]
        project = Path(details["projectPath"]).relative_to(checkout)
        path = project.as_posix()
        require(path in expected_projects and path not in projects and
                assets_path.parent == checkout / project.parent / "obj",
                "restore produced an unexpected project or output path")
        package_folders = list(assets["packageFolders"])
        require(len(package_folders) == 1 and package_folders[0] == details["packagesPath"],
                "unexpected package fallback folders")
        package_root = Path(package_folders[0])
        require(package_root.is_absolute() and package_root.resolve(strict=True) == package_root,
                "package root is missing or linked")
        sources = sorted(source_name(source, checkout) for source in details["sources"])
        reviewed_project = expected["projects"].get(path) if expected is not None else None
        require(expected is None or (reviewed_project and reviewed_project["sources"] == sources),
                f"restored package sources differ for {project.name}")
        details.pop("configFilePaths")
        graph_packages = resolved_identities(assets)
        imports = {}
        for kind in ("props", "targets"):
            generated = assets_path.with_name(f"{project.name}.nuget.g.{kind}")
            generated_files.add(generated)
            imports[kind] = generated_imports(generated, checkout, package_root, sdk_path, graph_packages)
        for identity in sorted(graph_packages):
            key = (package_root, identity)
            if key not in packages:
                require(expected is None or identity in expected["packages"], "resolved an unexpected package")
                packages[key] = package_record(package_root, identity, sources, checkout)
                require(expected is None or packages[key] == expected["packages"][identity],
                        "resolved package content or source differs")
            require(packages[key]["source"] in sources, "project resolved an unexpected package source")
        projects[path] = {"graphSha256": digest(normalize(canonical(assets).decode(), checkout, package_root, sdk_path).encode()),
                          "sources": sources, **imports}
        require(expected is None or reviewed_project == projects[path],
                f"restored graph or generated imports differ for {project.name}")
    require(projects.keys() == expected_projects and set(checkout.rglob("*.nuget.g.*")) == generated_files,
            "missing or extra restore projects or imports")
    by_identity = {identity: entry for (_, identity), entry in packages.items()}
    require(len(by_identity) == len(packages), "package identity resolved from multiple folders")
    return {"sdk": sdk, "packages": by_identity, "projects": projects}


def main():
    command, commit, source_hash = sys.argv[1:]
    require(HEX.fullmatch(source_hash), "invalid restore source digest")
    expected = None if command == "capture" else validate_certificate(commit, source_hash)
    if command == "check-source":
        return
    require(command in ("capture", "verify"), "unknown restore import operation")
    root = Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip()).resolve()
    check_ancestors(root)
    with tempfile.TemporaryDirectory(prefix="hvo-r-") as path:
        checkout = Path(path).resolve() / "src"
        checkout.mkdir()
        check_ancestors(checkout)
        extract_commit(commit, checkout)
        observed = {"version": 1, "sourceSha256": source_hash, **collect_restore(checkout, expected)}
    if command == "verify":
        require(observed["packages"] == expected["packages"], "resolved package content or sources differ")
    else:
        print(json.dumps(observed, indent=2, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except ProofError as error:
        sys.exit(f"docs inventory audit: {error}")
    except (OSError, ValueError, TypeError, KeyError, IndexError, AttributeError,
            ET.ParseError, tarfile.TarError, subprocess.SubprocessError):
        sys.exit("docs inventory audit: restore import proof could not be completed")
