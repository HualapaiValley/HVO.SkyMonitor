#!/usr/bin/env bash

if ! declare -F hyg_resolve_catalog_identity >/dev/null; then
    # shellcheck source=scripts/catalog/catalog-common.sh
    . "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/catalog/catalog-common.sh"
fi

phase14_source_fail() {
    deploy_fail source-import "$1" "$2"
}

phase14_source_safe_relative_path() {
    [[ "$1" =~ ^[A-Za-z0-9._/-]+$ && "$1" != /* && "$1" != *//* &&
       "$1" != ../* && "$1" != */../* && "$1" != */.. && "$1" != */./* ]]
}

phase14_source_safe_directory() {
    [[ -d "$1" && ! -L "$1" && "$(stat -c '%u:%a' -- "$1" 2>/dev/null)" == "$(id -u):700" ]]
}

phase14_source_no_symlink_path() {
    local root="$1" path="$2" canonical_root relative current component
    canonical_root="$(readlink -e -- "$root" 2>/dev/null)" || return 1
    [[ "$canonical_root" == "$root" || "$path" == "$root"/* ]] || return 1
    relative="${path#"$root"}"; relative="${relative#/}"; current="$root"
    IFS=/ read -r -a components <<< "$relative"
    for component in "${components[@]}"; do
        [[ -n "$component" ]] || continue
        current="$current/$component"
        [[ ! -L "$current" ]] || return 1
    done
    [[ "$(readlink -e -- "$path" 2>/dev/null)" == "$canonical_root${relative:+/$relative}" ]]
}

phase14_source_no_symlink_ancestors() {
    local path="$1" current='' component
    IFS=/ read -r -a components <<< "$path"
    for component in "${components[@]}"; do
        [[ -n "$component" ]] || continue
        current="$current/$component"
        [[ ! -L "$current" ]] || return 1
    done
}

phase14_source_safe_file() {
    local metadata
    [[ -f "$1" && ! -L "$1" ]] || return 1
    metadata="$(stat -c '%u:%h:%a:%s' -- "$1" 2>/dev/null)" || return 1
    [[ "$metadata" =~ ^$(id -u):1:(400|600):[1-9][0-9]*$ ]]
}

phase14_source_safe_catalog_file() {
    local metadata
    [[ -f "$1" && ! -L "$1" ]] || return 1
    metadata="$(stat -c '%u:%h:%a:%s' -- "$1" 2>/dev/null)" || return 1
    [[ "$metadata" =~ ^$(id -u):1:(400|444|600):[1-9][0-9]*$ ]]
}

phase14_source_hash() {
    local digest
    digest="$(sha256sum -- "$1")" || return 1
    printf '%s\n' "${digest%% *}"
}

phase14_source_method_id() {
    printf '%s' "$1" | sha256sum | cut -c 1-16
}

phase14_source_sanitize_trx() {
    local repo="$1" results="$2" fqn="$3" output="$4"
    dotnet run --project "$repo/scripts/acceptance-trx/HVO.SkyMonitor.AcceptanceTrx.csproj" \
      --configuration Release --no-restore -- "$results" "$fqn" "$output" >/dev/null
}

phase14_source_run_build() {
    local repo="$1" project="$2" path_map="$3"
    dotnet build "$repo/$project" --configuration Release --no-restore --no-incremental -warnaserror \
      -p:PathMap="$path_map"
}

phase14_source_resolve_target() {
    local repo="$1" project="$2"
    dotnet msbuild "$repo/$project" -property:Configuration=Release -getProperty:TargetPath
}

phase14_source_run_test() {
    local repo="$1" project="$2" fqn="$3" raw="$4"
    dotnet test "$repo/$project" --configuration Release --no-build --no-restore \
      --filter "FullyQualifiedName=$fqn" --results-directory "$raw" --logger 'trx;LogFileName=result.trx'
}

phase14_source_run_standard_test() (
    local repo="$1" project="$2" fqn="$3" raw="$4" evidence_root="$5"
    local auxiliary_evidence='' test_status=0
    umask 077
    if [[ -n "${DOTNET_TEST_FILTER+x}" ]]; then
        phase14_source_fail collection ambient-test-filter
        return 1
    fi
    unset HVO_DERIVATIVE_PERF_SMOKE HVO_DERIVATIVE_P5_DIAGNOSTIC_TRIAL \
      HVO_EVIDENCE_REVISION HVO_EVIDENCE_TRIAL HVO_ISSUE_107_DEPENDENCY \
      HVO_ISSUE_107_EVIDENCE_ROOT HVO_EVIDENCE_REPOSITORY_ROOT \
      HVO_RAW_INGRESS_CRASH_ROOT HVO_RAW_INGRESS_CRASH_POINT HVO_RAW_INGRESS_CRASH_HANG \
      HVO_ISSUE_246_RETENTION_EVIDENCE HVO_ISSUE_248_BASELINE_EVIDENCE HVO_ISSUE_248_SMOKE \
      HVO_ISSUE_248_CENSORED_SMOKE HVO_ISSUE_248_AGGREGATE_ONLY HVO_ISSUE_250_EVIDENCE \
      VSTEST_TESTCASE_FILTER
    export HVO_PHASE14_EVIDENCE_ALLOWED_ROOT="$PHASE14_COLLECTION_ALLOWED_ROOT" \
      HVO_PHASE14_EVIDENCE_ROOT="$evidence_root" HVO_PHASE14_SOURCE_REVISION="$PHASE14_PRODUCT_REVISION" \
      HVO_PHASE14_SOURCE_TREE="$PHASE14_PRODUCT_TREE"
    if [[ "$fqn" == HVO.SkyMonitor.IntegrationTests.LogicHostIngestPerformanceTests.NativeManifestV2Ingest_W1W2AndW4_RecordsPerformanceEvidence ]]; then
        auxiliary_evidence="$repo/TestResults/issue-170/$PHASE14_HARNESS_REVISION/trial-01/logichost-ingest-performance.json"
        if [[ -e "$auxiliary_evidence" || -L "$auxiliary_evidence" ]]; then
            phase14_source_fail collection auxiliary-evidence-preexisting
            return 1
        fi
        export HVO_EVIDENCE_REVISION="$PHASE14_HARNESS_REVISION" \
          HVO_EVIDENCE_PRODUCTION_REVISION="$PHASE14_HARNESS_REVISION" HVO_EVIDENCE_TRIAL=1 DOTNET_gcServer=1
    fi
    phase14_source_run_test "$repo" "$project" "$fqn" "$raw" || test_status=$?
    if [[ -n "$auxiliary_evidence" && ( -e "$auxiliary_evidence" || -L "$auxiliary_evidence" ) ]]; then
        local auxiliary_metadata
        auxiliary_metadata="$(stat -c '%u:%h:%a:%s' -- "$auxiliary_evidence" 2>/dev/null)" || return 1
        if [[ ! -f "$auxiliary_evidence" || -L "$auxiliary_evidence" ||
            ! "$auxiliary_metadata" =~ ^$(id -u):1:(600|644):[1-9][0-9]*$ ]] ||
            ! phase14_source_no_symlink_path "$repo" "$auxiliary_evidence"; then
            phase14_source_fail collection unsafe-auxiliary-evidence
            return 1
        fi
        chmod 600 -- "$auxiliary_evidence" || return 1
        phase14_source_safe_file "$auxiliary_evidence" || return 1
        rm -f -- "$auxiliary_evidence" || return 1
    fi
    ((test_status == 0))
)

phase14_source_run_issue211() {
    local repo="$1" catalog="$2" image="$3" private_results="$4" evidence_method_root="$5" path_map="$6" assembly_sha_file="$7"
    local retained="$private_results/issue-211" trx_root="$private_results/phase14-trx"
    install -d -m 700 "$retained" "$trx_root"
    HVO_CATALOG_PERF_ROOT="$catalog" HVO_OTEL_COLLECTOR_IMAGE="$image" HVO_ISSUE_211_TRIAL_COUNT=5 \
      HVO_PHASE14_BUILD_PATH_MAP="$path_map" \
      HVO_PHASE14_TEST_ASSEMBLY_SHA_FILE="$assembly_sha_file" \
      HVO_ISSUE_211_RESULTS_BASE="$retained" HVO_PHASE14_TRX_ROOT="$trx_root" \
      HVO_PHASE14_EVIDENCE_ALLOWED_ROOT="$PHASE14_COLLECTION_ALLOWED_ROOT" \
      HVO_PHASE14_EVIDENCE_ROOT="$evidence_method_root" HVO_PHASE14_SOURCE_REVISION="$PHASE14_PRODUCT_REVISION" \
      HVO_PHASE14_SOURCE_TREE="$PHASE14_PRODUCT_TREE" "$repo/scripts/test:cameraagent-standalone-211" >/dev/null
}

phase14_source_catalog_inputs() {
    local root="$1" expected_id="$2" expected_kind="$3" current resolved manifest database relative
    local actual_id actual_sha actual_length package_version path
    local -a payload_files
    current="$root/current"
    [[ -L "$current" ]] || return 1
    resolved="$(readlink -e -- "$current" 2>/dev/null)" || return 1
    [[ "$resolved" == "$root/versions/"* ]] || return 1
    phase14_source_no_symlink_path "$root/versions" "$resolved" || return 1
    manifest="$resolved/manifest.json"
    phase14_source_no_symlink_path "$root/versions" "$manifest" && phase14_source_safe_catalog_file "$manifest" || return 1
    hyg_validate_manifest_document "$manifest" || return 1
    package_version="$(hyg_json_value "$manifest" '$.package.version')" || return 1
    [[ "${resolved##*/}" == "$package_version" ]] || return 1
    [[ "$(hyg_json_value "$manifest" '$.package.kind')" == "$expected_kind" ]] || return 1
    if [[ "$expected_kind" == production ]]; then
        payload_files=("$HYG_MANIFEST_FILE" "$HYG_DATABASE_FILE" "$HYG_LICENSE_FILE" "$HYG_ATTRIBUTION_FILE")
    elif [[ "$expected_kind" == fixture ]]; then
        payload_files=("$HYG_MANIFEST_FILE" "$HYG_DATABASE_FILE")
    else
        return 1
    fi
    for path in "${payload_files[@]}"; do
        path="$resolved/$path"
        phase14_source_no_symlink_path "$root/versions" "$path" && phase14_source_safe_catalog_file "$path" || return 1
    done
    actual_id="$(hyg_resolve_catalog_identity "$resolved")" || return 1
    [[ "$actual_id" == "$expected_id" ]] || return 1
    relative="$(hyg_json_value "$manifest" '$.database.relativePath')" || return 1
    phase14_source_safe_relative_path "$relative" || return 1
    database="$resolved/$relative"
    phase14_source_no_symlink_path "$root/versions" "$database" && phase14_source_safe_catalog_file "$database" || return 1
    actual_sha="$(phase14_source_hash "$database")"; actual_length="$(stat -c %s -- "$database")"
    jq -cn --arg collectorImage "$PHASE14_COLLECTOR_IMAGE" --arg manifestSha256 "$(phase14_source_hash "$manifest")" \
      --arg databaseSha256 "$actual_sha" --argjson databaseByteLength "$actual_length" \
      '{collectorImage:$collectorImage,catalogManifestSha256:$manifestSha256,
        catalogDatabaseSha256:$databaseSha256,catalogDatabaseByteLength:$databaseByteLength}'
}

phase14_source_catalog_identity() {
    phase14_source_catalog_inputs "$1" "$HYG_CATALOG_ID" production
}

phase14_source_validate_contract() {
    local contract="$1" inventory="$2" canonical_hash
    canonical_hash="$(jq -S -c . "$inventory" | sha256sum)" || return 1
    canonical_hash="${canonical_hash%% *}"
    jq -e --arg hash "$canonical_hash" --slurpfile inventory "$inventory" '
      .schemaVersion == 2 and .contract == "phase-14-deferred-evidence" and
      .inventory.schemaVersion == 2 and .inventory.scenarioCount == 111 and
      .inventory.canonicalSha256 == $hash and ($inventory[0].scenarios | length) == 111 and
      .dispositionSummary["source-family-import"] == 103 and
      (.sourceFamilies | length) == 3 and ([.sourceFamilies[].scenarioCount] | add) == 103 and
      ([.sourceFamilies[].uniqueMethodCount] | add) == 25 and
      (.sourceEvidenceContract.requiredSourceRevision | test("^[0-9a-f]{40}$")) and
      (.sourceEvidenceContract.requiredSourceTree | test("^[0-9a-f]{40}$")) and
      (.sourceEvidenceContract.evidenceHarnessPolicy.requiredRevision == null or
        (.sourceEvidenceContract.evidenceHarnessPolicy.requiredRevision | test("^[0-9a-f]{40}$"))) and
      (.sourceEvidenceContract.evidenceHarnessPolicy.requiredTree == null or
        (.sourceEvidenceContract.evidenceHarnessPolicy.requiredTree | test("^[0-9a-f]{40}$"))) and
      (.sourceEvidenceContract.evidenceHarnessPolicy.allowedChangedPaths | length > 0 and length == (unique | length)) and
      .artifactContract.runtimeArtifactSchemaVersion == 1 and .artifactContract.maximumByteLength == 262144
    ' "$contract" >/dev/null 2>&1 || phase14_source_fail contract invalid
}

phase14_source_validate_repository() {
    local repo="$1" contract="$2" harness_revision="$3" product_revision product_tree head tree status path
    [[ "$harness_revision" =~ ^[0-9a-f]{40}$ ]] || { phase14_source_fail source invalid-harness-revision; return 1; }
    product_revision="$(jq -r '.sourceEvidenceContract.requiredSourceRevision' "$contract")"
    product_tree="$(jq -r '.sourceEvidenceContract.requiredSourceTree' "$contract")"
    head="$(git -C "$repo" rev-parse HEAD 2>/dev/null)" || { phase14_source_fail source head-unavailable; return 1; }
    tree="$(git -C "$repo" rev-parse 'HEAD^{tree}' 2>/dev/null)" || { phase14_source_fail source tree-unavailable; return 1; }
    status="$(git -C "$repo" status --porcelain --untracked-files=normal 2>/dev/null)" || return 1
    [[ "$head" == "$harness_revision" && -z "$status" ]] || { phase14_source_fail source harness-not-clean-head; return 1; }
    if jq -e '.sourceEvidenceContract.evidenceHarnessPolicy.requiredRevision != null or
      .sourceEvidenceContract.evidenceHarnessPolicy.requiredTree != null' "$contract" >/dev/null; then
        [[ "$(jq -r '.sourceEvidenceContract.evidenceHarnessPolicy.requiredRevision' "$contract")" == "$harness_revision" &&
           "$(jq -r '.sourceEvidenceContract.evidenceHarnessPolicy.requiredTree' "$contract")" == "$tree" ]] ||
          { phase14_source_fail source harness-contract-mismatch; return 1; }
    fi
    [[ "$(git -C "$repo" rev-parse "${product_revision}^{tree}" 2>/dev/null)" == "$product_tree" ]] ||
      { phase14_source_fail source product-tree-mismatch; return 1; }
    git -C "$repo" merge-base --is-ancestor "$product_revision" "$harness_revision" 2>/dev/null ||
      { phase14_source_fail source harness-not-descendant; return 1; }
    while IFS= read -r path; do
        [[ -n "$path" ]] || continue
        jq -e --arg path "$path" '.sourceEvidenceContract.evidenceHarnessPolicy.allowedChangedPaths | index($path) != null' \
          "$contract" >/dev/null || { phase14_source_fail source disallowed-harness-path; return 1; }
    done < <(git -C "$repo" diff --name-only "$product_revision...$harness_revision")
    PHASE14_HARNESS_TREE="$tree"
    PHASE14_PRODUCT_REVISION="$product_revision"
    PHASE14_PRODUCT_TREE="$product_tree"
}

phase14_source_method_map() {
    local contract="$1" inventory="$2" method_map fqn
    method_map="$(jq -c --slurpfile inventory "$inventory" '
      [.sourceFamilies[] as $family | [$inventory[0].scenarios[] |
        select(.evidenceSource | startswith($family.evidenceSourcePrefix)) |
        {family:$family.id, project:$family.project, scenarioId:.id, classification, executionClass, workloads, evidenceSource,
          fqn:(.evidenceSource | sub("^test:";"") | split(";case=")[0]),
          selector:(if (.evidenceSource | contains(";case=")) then (.evidenceSource | sub("^[^;]*;case=";"")) else null end),
          sanitizer:$family.sanitizer, validator:$family.admissibilityValidator}]] | add |
      sort_by(.family,.fqn,.scenarioId)
    ' "$contract")" || return 1
    while IFS= read -r fqn; do
        method_map="$(jq -c --arg fqn "$fqn" --arg methodId "$(phase14_source_method_id "$fqn")" \
          'map(if .fqn == $fqn then .methodId=$methodId else . end)' <<< "$method_map")" || return 1
    done < <(jq -r '[.[].fqn] | unique[]' <<< "$method_map")
    [[ "$(jq 'length' <<< "$method_map")" == 103 && "$(jq '[.[].fqn] | unique | length' <<< "$method_map")" == 25 ]] || return 1
    printf '%s\n' "$method_map"
}

phase14_source_write_provenance() {
    local assembly="$1" output="$2" revision="$3" tree="$4" sha
    sha="$(phase14_source_hash "$assembly")" || return 1
    jq -S -n --arg revision "$revision" --arg tree "$tree" --arg sha "$sha" \
      '{schemaVersion:1,buildRevision:$revision,buildTree:$tree,assemblySha256:$sha}' > "$output"
    chmod 600 "$output"
}

phase14_source_validate_target() {
    local repo="$1" project="$2" target="$3" project_directory expected
    [[ "$target" == /* && -f "$target" && ! -L "$target" ]] || return 1
    project_directory="$(dirname "$repo/$project")"
    expected="$(readlink -e -- "$project_directory")/bin/Release/"
    [[ "$target" == "$expected"* ]] || return 1
    phase14_source_no_symlink_path "$project_directory" "$target"
}

phase14_source_collect() (
    local repo="$1" output_root="$2" harness_revision="$3" catalog_root="$4" collector_image="$5" contract="$6" inventory="$7"
    local final="$output_root/source-import" collection input private_results method_map inventory_hash family project fqn method_id method raw target
    local acceptance_entries acceptance_family acceptance_fqn fragments trial sanitized first_sanitized assembly acceptance_inputs after_inputs target_sha tested_assembly_sha_file tested_assembly_sha
    local catalog_lock catalog_lock_metadata catalog_lock_fd
    deploy_require_commands git jq sha256sum stat readlink dotnet cmp flock || return 1
    if ! deploy_is_safe_absolute_path "$output_root" || ! deploy_is_safe_absolute_path "$catalog_root"; then
        phase14_source_fail options absolute-roots-required
        return 1
    fi
    [[ "$collector_image" =~ ^[^[:space:]@]+@sha256:[0-9a-f]{64}$ ]] || { phase14_source_fail options invalid-collector-image; return 1; }
    PHASE14_COLLECTOR_IMAGE="$collector_image"
    if ! phase14_source_no_symlink_ancestors "$output_root" || ! phase14_source_no_symlink_ancestors "$catalog_root"; then
        phase14_source_fail options symlink-root-ancestor
        return 1
    fi
    output_root="$(readlink -e -- "$output_root" 2>/dev/null)" || { phase14_source_fail output-root unsafe-root; return 1; }
    catalog_root="$(readlink -e -- "$catalog_root" 2>/dev/null)" || { phase14_source_fail catalog-root unsafe-root; return 1; }
    deploy_validate_output_root "$output_root" output-root || return 1
    [[ -d "$catalog_root" && ! -L "$catalog_root" ]] || { phase14_source_fail catalog-root unsafe-root; return 1; }
    phase14_source_validate_contract "$contract" "$inventory" || return 1
    phase14_source_validate_repository "$repo" "$contract" "$harness_revision" || return 1
    method_map="$(phase14_source_method_map "$contract" "$inventory")" || return 1
    inventory_hash="$(jq -S -c . "$inventory" | sha256sum)"; inventory_hash="${inventory_hash%% *}"
    if [[ -e "$final" || -L "$final" ]]; then
        acceptance_inputs="$(phase14_source_catalog_identity "$catalog_root")" || { phase14_source_fail catalog-root invalid-catalog; return 1; }
        phase14_source_validate_publication "$final" "$contract" "$inventory" "$inventory_hash" "$PHASE14_PRODUCT_REVISION" \
          "$PHASE14_PRODUCT_TREE" "$harness_revision" "$PHASE14_HARNESS_TREE" "$method_map" "$acceptance_inputs" ||
          { phase14_source_fail resume tampered-publication; return 1; }
        return 0
    fi
    umask 077
    collection="$(mktemp -d "$output_root/.source-collection.stage.XXXXXX")" || return 1
    # shellcheck disable=SC2064 # Capture this invocation's private collection path.
    trap "rm -rf -- '$collection'" EXIT
    input="$collection/input"; private_results="$collection/private-results"
    install -d -m 700 "$input" "$private_results"
    PHASE14_COLLECTION_ALLOWED_ROOT="$input"
    PHASE14_HARNESS_REVISION="$harness_revision"
    export PHASE14_COLLECTION_ALLOWED_ROOT PHASE14_HARNESS_REVISION

    while IFS=$'\t' read -r family project; do
        phase14_source_validate_repository "$repo" "$contract" "$harness_revision" || return 1
        phase14_source_run_build "$repo" "$project" "$repo=hvo-source" || return 1
        phase14_source_validate_repository "$repo" "$contract" "$harness_revision" || return 1
        target="$(phase14_source_resolve_target "$repo" "$project")" || return 1
        phase14_source_validate_target "$repo" "$project" "$target" || { phase14_source_fail collection invalid-target; return 1; }
        while IFS= read -r fqn; do
            method_id="$(phase14_source_method_id "$fqn")"; method="$input/$family/$method_id"; raw="$private_results/$family/$method_id"
            install -d -m 700 "$input/$family" "$method" "$method/results" "$method/fragments" "$private_results/$family" "$raw"
            target_sha="$(phase14_source_hash "$target")" || return 1
            phase14_source_validate_repository "$repo" "$contract" "$harness_revision" || return 1
            phase14_source_run_standard_test "$repo" "$project" "$fqn" "$raw" "$method" || return 1
            phase14_source_validate_repository "$repo" "$contract" "$harness_revision" || return 1
            [[ "$(phase14_source_hash "$target")" == "$target_sha" ]] || { phase14_source_fail collection tested-assembly-changed; return 1; }
            phase14_source_sanitize_trx "$repo" "$raw" "$fqn" "$method/results/result.trx" || return 1
            rm -rf -- "$raw"
            install -m 600 "$target" "$method/test-assembly.dll"
            phase14_source_write_provenance "$method/test-assembly.dll" "$method/assembly-provenance.json" \
              "$harness_revision" "$PHASE14_HARNESS_TREE" || return 1
        done < <(jq -r --arg family "$family" '[.[] | select(.family == $family) | .fqn] | unique[]' <<< "$method_map")
    done < <(jq -r '[.[] | select(.family != "cameraagent-acceptance") | [.family,.project]] | unique[] | @tsv' <<< "$method_map")

    acceptance_entries="$(jq -c '[.[] | select(.family == "cameraagent-acceptance")]' <<< "$method_map")"
    [[ "$(jq 'length' <<< "$acceptance_entries")" == 6 && "$(jq '[.[].fqn] | unique | length' <<< "$acceptance_entries")" == 1 ]] || return 1
    acceptance_family="cameraagent-acceptance"; acceptance_fqn="$(jq -r '.[0].fqn' <<< "$acceptance_entries")"
    project="$(jq -r '.[0].project' <<< "$acceptance_entries")"; method_id="$(phase14_source_method_id "$acceptance_fqn")"
    method="$input/$acceptance_family/$method_id"; fragments="$method/fragments"
    install -d -m 700 "$input/$acceptance_family" "$method" "$method/results" "$method/trial-results" "$fragments"
    catalog_lock="$catalog_root/.catalog.lock"
    [[ -f "$catalog_lock" && ! -L "$catalog_lock" ]] || { phase14_source_fail catalog-root unsafe-lock; return 1; }
    catalog_lock_metadata="$(stat -c '%u:%h:%a' -- "$catalog_lock" 2>/dev/null)" || return 1
    [[ "$catalog_lock_metadata" == "$(id -u):1:600" ]] || { phase14_source_fail catalog-root unsafe-lock; return 1; }
    exec {catalog_lock_fd}>> "$catalog_lock" || return 1
    flock -x "$catalog_lock_fd" || return 1
    [[ "$(stat -Lc '%d:%i' -- "$catalog_lock")" == "$(stat -Lc '%d:%i' -- "/dev/fd/$catalog_lock_fd")" ]] ||
      { phase14_source_fail catalog-root changed-lock; return 1; }
    acceptance_inputs="$(phase14_source_catalog_identity "$catalog_root")" || { phase14_source_fail catalog-root invalid-catalog; return 1; }
    tested_assembly_sha_file="$private_results/test-assembly.sha256"
    phase14_source_validate_repository "$repo" "$contract" "$harness_revision" || return 1
    (unset HVO_ISSUE_107_DEPENDENCY HVO_EVIDENCE_TRIAL VSTEST_TESTCASE_FILTER; \
      phase14_source_run_issue211 "$repo" "$catalog_root" "$collector_image" "$private_results" "$method" "$repo=hvo-source" "$tested_assembly_sha_file") || return 1
    phase14_source_validate_repository "$repo" "$contract" "$harness_revision" || return 1
    after_inputs="$(phase14_source_catalog_identity "$catalog_root")" || { phase14_source_fail catalog-root invalid-catalog; return 1; }
    [[ "$after_inputs" == "$acceptance_inputs" ]] || { phase14_source_fail catalog-root changed-during-acceptance; return 1; }
    shopt -s nullglob dotglob
    local acceptance_fragments=("$fragments"/*) trial_directories=("$private_results/phase14-trx"/*)
    shopt -u nullglob dotglob
    [[ "${#acceptance_fragments[@]}" == 18 && "${#trial_directories[@]}" == 5 ]] || { phase14_source_fail collection invalid-acceptance-layout; return 1; }
    first_sanitized=''
    for trial in 1 2 3 4 5; do
        raw="$private_results/phase14-trx/trial-$trial"
        [[ -d "$raw" && ! -L "$raw" ]] || { phase14_source_fail collection invalid-acceptance-layout; return 1; }
        sanitized="$collection/acceptance-trial-$trial.trx"
        phase14_source_sanitize_trx "$repo" "$raw" "$acceptance_fqn" "$sanitized" || return 1
        install -m 600 "$sanitized" "$method/trial-results/trial-$trial.trx"
        if [[ -z "$first_sanitized" ]]; then first_sanitized="$sanitized"; fi
        rm -rf -- "$raw"
    done
    install -m 600 "$first_sanitized" "$method/results/result.trx"
    phase14_source_safe_file "$tested_assembly_sha_file" || { phase14_source_fail collection missing-tested-assembly-digest; return 1; }
    tested_assembly_sha="$(<"$tested_assembly_sha_file")"
    [[ "$tested_assembly_sha" =~ ^[0-9a-f]{64}$ ]] || { phase14_source_fail collection invalid-tested-assembly-digest; return 1; }
    target="$(phase14_source_resolve_target "$repo" "$project")" || return 1
    phase14_source_validate_target "$repo" "$project" "$target" || { phase14_source_fail collection invalid-target; return 1; }
    [[ "$(phase14_source_hash "$target")" == "$tested_assembly_sha" ]] || { phase14_source_fail collection tested-assembly-changed; return 1; }
    install -m 600 "$target" "$method/test-assembly.dll"
    [[ "$(phase14_source_hash "$method/test-assembly.dll")" == "$tested_assembly_sha" ]] || return 1
    rm -rf -- "$private_results"
    phase14_source_write_provenance "$method/test-assembly.dll" "$method/assembly-provenance.json" \
      "$harness_revision" "$PHASE14_HARNESS_TREE" || return 1
    PHASE14_SOURCE_COLLECTION_ROOT="$input" phase14_source_import \
      "$repo" "$input" "$output_root" "$harness_revision" "$contract" "$inventory" "$acceptance_inputs" || return 1
    flock -u "$catalog_lock_fd"
    exec {catalog_lock_fd}>&-
    rm -rf -- "$collection"
    trap - EXIT
)

phase14_source_validate_json_text() {
    local path="$1" prohibited
    prohibited='["password","secret","token","cookie","authorization","connectionstring","exception","stacktrace","responsebody"]'
    jq -e --argjson prohibited "$prohibited" '
      def clean_string:
        (all(explode[]; . >= 32 and . != 127)) and
        (test("(^|[[:space:]])/(?:[^/[:space:]]+/)+[^[:space:]]*") | not) and
        (test("(^|[[:space:]])[A-Za-z]:[\\\\/]") | not) and
        (test("[A-Za-z][A-Za-z0-9+.-]*://[^/[:space:]]+") | not) and
        (test("(^|[[:space:]])//[^/[:space:]]+") | not);
      [paths(objects) as $p | (getpath($p) | keys[]) | ascii_downcase] as $keys |
      all($keys[]; . as $key | $prohibited | index($key) == null) and
      all(.. | strings; clean_string)
    ' "$path" >/dev/null 2>&1
}

phase14_source_validate_fragment() {
    local path="$1" scenario="$2" selector="$3" revision="$4" tree="$5" observation hash
    phase14_source_safe_file "$path" && phase14_source_validate_json_text "$path" &&
    jq -e --arg scenario "$scenario" --arg selector "$selector" --arg revision "$revision" --arg tree "$tree" '
      def id: type == "string" and test("^[a-z0-9][a-z0-9-]{0,127}$");
      def unit: . == "milliseconds" or . == "seconds" or . == "bytes" or . == "captures" or
        . == "captures-per-second" or . == "count" or . == "percent";
      (keys | sort) == (["schemaVersion","scenarioId","caseSelector","observationId","sourceRevision","sourceTree","assertions","measurements"] | sort) and
      .schemaVersion == 1 and .scenarioId == $scenario and (.observationId | id) and
      .caseSelector == (if $selector == "" then null else $selector end) and
      .sourceRevision == $revision and .sourceTree == $tree and
      (.assertions | type == "array" and length >= 1 and length <= 64 and length == ([.[].id] | unique | length) and
        all(.[]; (keys | sort) == ["id","passed"] and (.id | id) and .passed == true)) and
      (.measurements | type == "array" and length <= 64 and length == ([.[].id] | unique | length) and
        all(.[]; (keys | sort) == ["id","unit","value"] and (.id | id) and
          (.value | numbers) >= 0 and (.value | floor) == .value and (.unit | unit)))
    ' "$path" >/dev/null 2>&1 || return 1
    observation="$(jq -r '.observationId' "$path")" || return 1
    hash="$(printf '%s' "$observation" | sha256sum)" || return 1
    [[ "$(basename "$path")" == "$scenario--${hash%% *}.json" ]]
}

phase14_source_validate_input_method() {
    local directory="$1" fragments="$2" assembly="$3" provenance="$4" family="$5" entry count=0
    if ! phase14_source_safe_directory "$directory" || ! phase14_source_safe_directory "$directory/results" ||
      ! phase14_source_safe_directory "$fragments" || ! phase14_source_safe_file "$assembly" ||
      ! phase14_source_safe_file "$provenance"; then
        phase14_source_fail input unsafe-method-entry
        return 1
    fi
    shopt -s nullglob dotglob
    local root_entries=("$directory"/*) results_entries=("$directory/results"/*) fragment_entries=("$fragments"/*)
    shopt -u nullglob dotglob
    local expected_root_count=4
    [[ "$family" != cameraagent-acceptance ]] || expected_root_count=5
    [[ "${#root_entries[@]}" == "$expected_root_count" ]] || { phase14_source_fail input unexpected-method-entry; return 1; }
    for entry in "${root_entries[@]}"; do
        [[ "$entry" == "$directory/results" || "$entry" == "$fragments" || "$entry" == "$assembly" || "$entry" == "$provenance" ||
           ( "$family" == cameraagent-acceptance && "$entry" == "$directory/trial-results" ) ]] ||
          { phase14_source_fail input unexpected-method-entry; return 1; }
        phase14_source_no_symlink_path "$directory" "$entry" || { phase14_source_fail input unsafe-method-entry; return 1; }
    done
    [[ "${#results_entries[@]}" == 1 && "${results_entries[0]}" == *.trx ]] ||
      { phase14_source_fail input invalid-method-layout; return 1; }
    phase14_source_safe_file "${results_entries[0]}" || { phase14_source_fail input unsafe-trx; return 1; }
    if [[ "$family" == cameraagent-acceptance ]]; then
        phase14_source_safe_directory "$directory/trial-results" || { phase14_source_fail input invalid-method-layout; return 1; }
        local trial_entries=("$directory/trial-results"/*)
        [[ "${#trial_entries[@]}" == 5 ]] || { phase14_source_fail input invalid-method-layout; return 1; }
        for entry in "${trial_entries[@]}"; do
            if [[ ! "$entry" =~ /trial-[1-5]\.trx$ ]] || ! phase14_source_safe_file "$entry" ||
              ! phase14_source_no_symlink_path "$directory" "$entry"; then
                phase14_source_fail input unsafe-trx
                return 1
            fi
        done
    fi
    for entry in "${fragment_entries[@]}"; do
        if [[ "$entry" != *.json ]] || ! phase14_source_safe_file "$entry" || ! phase14_source_no_symlink_path "$directory" "$entry"; then
            phase14_source_fail input unsafe-fragment
            return 1
        fi
        count=$((count + 1))
    done
    [[ "$count" -ge 1 ]] || { phase14_source_fail input invalid-method-layout; return 1; }
}

phase14_source_validate_publication() {
    local root="$1" contract="$2" inventory="$3" expected_inventory="$4" product_revision="$5" product_tree="$6"
    local harness_revision="$7" harness_tree="$8" method_map="$9" index commit path length digest bundle base scenario
    local family method_id expected actual entry expected_acceptance_inputs="${10}"
    index="$root/source-import-index.json"; commit="$root/source-import-commit.json"
    phase14_source_safe_directory "$root" && phase14_source_no_symlink_path "$root" "$root" &&
      phase14_source_safe_file "$index" && phase14_source_safe_file "$commit" || return 1
    length="$(stat -c %s -- "$index")"; digest="$(phase14_source_hash "$index")" || return 1
    jq -e --argjson length "$length" --arg digest "$digest" '
      (keys | sort) == (["schemaVersion","indexByteLength","indexSha256"] | sort) and
      .schemaVersion == 1 and .indexByteLength == $length and .indexSha256 == $digest
    ' "$commit" >/dev/null || return 1
    jq -e --arg inventory "$expected_inventory" --arg harnessRevision "$harness_revision" --arg harnessTree "$harness_tree" \
      --arg productRevision "$product_revision" --arg productTree "$product_tree" --argjson mappings "$method_map" \
      --argjson acceptanceInputs "$expected_acceptance_inputs" '
        (keys | sort) == (["schemaVersion","status","campaignStatus","evidenceHarnessRevision","evidenceHarnessTree","inventorySha256","acceptanceInputs","entries","methodBundles"] | sort) and
      .schemaVersion == 1 and .status == "recorded" and .campaignStatus == "not-run" and
       .evidenceHarnessRevision == $harnessRevision and .evidenceHarnessTree == $harnessTree and .inventorySha256 == $inventory and
       .acceptanceInputs == $acceptanceInputs and
       (.acceptanceInputs | keys | sort) == (["collectorImage","catalogManifestSha256","catalogDatabaseSha256","catalogDatabaseByteLength"] | sort) and
       (.acceptanceInputs.collectorImage | test("^[^[:space:]@]+@sha256:[0-9a-f]{64}$")) and
       all(.acceptanceInputs.catalogManifestSha256,.acceptanceInputs.catalogDatabaseSha256; test("^[0-9a-f]{64}$")) and
       (.acceptanceInputs.catalogDatabaseByteLength | numbers) > 0 and
      (.entries | length == 103 and length == ([.[].scenarioId] | unique | length)) and
      ([.entries[].scenarioId] | sort) == ([$mappings[].scenarioId] | sort) and
      (.methodBundles | length == 25 and length == ([.[].relativePath] | unique | length)) and
      all(.methodBundles[]; (keys | sort) == ["byteLength","relativePath","sha256"] and
        (.relativePath | test("^methods/[a-z0-9-]+/[0-9a-f]{16}/source-bundle\\.json$")) and
        (.byteLength | numbers) > 0 and (.byteLength | floor) == .byteLength and (.sha256 | test("^[0-9a-f]{64}$"))) and
      all(.entries[];
        . as $entry | ($mappings[] | select(.scenarioId == $entry.scenarioId)) as $mapping |
        (keys | sort) == (["scenarioId","status","artifactRelativePath","artifactSchemaVersion","artifactByteLength",
          "artifactSha256","sanitizer","admissibilityValidator","sourceRevision","sourceTree"] | sort) and
        .status == "recorded" and .artifactSchemaVersion == 1 and (.artifactByteLength | numbers) > 0 and
        (.artifactByteLength | floor) == .artifactByteLength and (.artifactSha256 | test("^[0-9a-f]{64}$")) and
        .artifactRelativePath == ("methods/" + $mapping.family + "/" + $mapping.methodId + "/acceptance-artifacts/" + .scenarioId + ".json") and
        .sanitizer == $mapping.sanitizer and .admissibilityValidator == $mapping.validator and
        .sourceRevision == $productRevision and .sourceTree == $productTree) and
      ([.methodBundles[].relativePath] | sort) == ([$mappings[] | "methods/" + .family + "/" + .methodId + "/source-bundle.json"] | unique | sort)
    ' "$index" >/dev/null || return 1
    while IFS=$'\t' read -r scenario path length digest; do
        phase14_source_safe_relative_path "$path" && phase14_source_safe_file "$root/$path" &&
          phase14_source_no_symlink_path "$root" "$root/$path" &&
          [[ "$(stat -c %s -- "$root/$path")" == "$length" && "$(phase14_source_hash "$root/$path")" == "$digest" ]] || return 1
        jq -e --arg scenario "$scenario" --arg inventory "$expected_inventory" --arg revision "$product_revision" --arg tree "$product_tree" \
          --argjson mappings "$method_map" '
          ($mappings[] | select(.scenarioId == $scenario)) as $mapping |
          (keys | sort) == (["schemaVersion","runId","scenarioId","inventorySha256","sourceRevision","sourceTree","classification",
            "executionClass","evidenceSource","workloads","outcome","startedAt","completedAt","assertions","outputs","measurements"] | sort) and
          .schemaVersion == 1 and .runId == "phase14-source-import" and .scenarioId == $scenario and
          .inventorySha256 == $inventory and .sourceRevision == $revision and .sourceTree == $tree and
          .classification == $mapping.classification and .executionClass == $mapping.executionClass and
          .evidenceSource == $mapping.evidenceSource and .workloads == $mapping.workloads and
          .outcome == "passed" and .outputs == [] and
          all(.assertions[]; .passed == true)
        ' "$root/$path" >/dev/null || return 1
    done < <(jq -r '.entries[] | [.scenarioId,.artifactRelativePath,.artifactByteLength,.artifactSha256] | @tsv' "$index")
    while IFS=$'\t' read -r path length digest; do
        bundle="$root/$path"
        phase14_source_safe_relative_path "$path" && phase14_source_safe_file "$bundle" &&
          phase14_source_no_symlink_path "$root" "$bundle" && [[ "$(stat -c %s -- "$bundle")" == "$length" ]] &&
          [[ "$(phase14_source_hash "$bundle")" == "$digest" ]] || return 1
        base="$(dirname "$bundle")"
        family="${path#methods/}"; family="${family%%/*}"; method_id="${path#methods/"$family"/}"; method_id="${method_id%%/*}"
        jq -e --arg family "$family" --arg methodId "$method_id" --arg productRevision "$product_revision" \
          --arg productTree "$product_tree" --arg harnessRevision "$harness_revision" --arg harnessTree "$harness_tree" \
          --argjson mappings "$method_map" --slurpfile index "$index" '
          . as $bundle |
          (keys | sort) == (["schemaVersion","requestedSourceRevision","requestedSourceTree","evidenceHarnessRevision","evidenceHarnessTree","acceptanceInputs",
            "cleanSource","testAssemblyRelativePath","testAssemblySha256","testAssemblyBuildRevision","testAssemblyBuildTree",
            "trxRelativePath","trxByteLength","trxSha256","trialResults","sourceEvidenceRelativePath","sourceEvidenceByteLength","sourceEvidenceSha256","entries"] | sort) and
           .schemaVersion == 2 and .cleanSource == true and .requestedSourceRevision == $productRevision and
          .requestedSourceTree == $productTree and .evidenceHarnessRevision == $harnessRevision and .evidenceHarnessTree == $harnessTree and
           .testAssemblyBuildRevision == $harnessRevision and .testAssemblyBuildTree == $harnessTree and
           .acceptanceInputs == (if $family == "cameraagent-acceptance" then $index[0].acceptanceInputs else null end) and
           .testAssemblyRelativePath == "test-assembly.dll" and .trxRelativePath == "result.trx" and
           (.trialResults | type == "array" and length == (if $family == "cameraagent-acceptance" then 5 else 1 end) and
             (keys | sort) == (if $family == "cameraagent-acceptance" then [0,1,2,3,4] else [0] end) and
             all(.[]; (keys | sort) == ["byteLength","id","relativePath","sha256"] and
               (.byteLength | numbers) > 0 and (.byteLength | floor) == .byteLength and (.sha256 | test("^[0-9a-f]{64}$"))) and
             (if $family == "cameraagent-acceptance" then
               [.[] | .id] == ["trial-1","trial-2","trial-3","trial-4","trial-5"] and
               [.[] | .relativePath] == ["trial-results/trial-1.trx","trial-results/trial-2.trx","trial-results/trial-3.trx","trial-results/trial-4.trx","trial-results/trial-5.trx"] and
               .[0].byteLength == $bundle.trxByteLength and .[0].sha256 == $bundle.trxSha256
             else . == [{id:"method",relativePath:"trial-results/method.trx",byteLength:$bundle.trxByteLength,sha256:$bundle.trxSha256}] end)) and
          .sourceEvidenceRelativePath == "source-evidence.json" and .entries == (input.entries) and
          ([.entries[].scenarioId] | sort) == ([$mappings[] | select(.family == $family and .methodId == $methodId) | .scenarioId] | sort) and
          all(.entries[]; . as $entry | ($mappings[] | select(.scenarioId == $entry.scenarioId)) as $mapping |
            (keys | sort) == (["scenarioId","evidenceSource","sourceRevision","sourceTree","outcome","assertions","outputs","measurements"] | sort) and
            .evidenceSource == $mapping.evidenceSource and .sourceRevision == $productRevision and .sourceTree == $productTree and
            .outcome == "passed" and all(.assertions[]; .passed) and .outputs == [])
         ' "$bundle" "$base/source-evidence.json" >/dev/null || return 1
        while IFS=$'\t' read -r path length digest; do
            phase14_source_safe_relative_path "$path" && phase14_source_safe_file "$base/$path" && phase14_source_no_symlink_path "$root" "$base/$path" &&
              [[ "$length" == null || "$(stat -c %s -- "$base/$path")" == "$length" ]] &&
              [[ "$(phase14_source_hash "$base/$path")" == "$digest" ]] || return 1
        done < <(jq -r '[.testAssemblyRelativePath,"null",.testAssemblySha256],
          [.trxRelativePath,(.trxByteLength|tostring),.trxSha256],
          [.sourceEvidenceRelativePath,(.sourceEvidenceByteLength|tostring),.sourceEvidenceSha256],
          (.trialResults[] | [.relativePath,(.byteLength|tostring),.sha256]) | @tsv' "$bundle")
    done < <(jq -r '.methodBundles[] | [.relativePath,.byteLength,.sha256] | @tsv' "$index")
    shopt -s nullglob dotglob
    local root_entries=("$root"/*) family_entries=("$root/methods"/*)
    shopt -u nullglob dotglob
    [[ "${#root_entries[@]}" == 3 && "${#family_entries[@]}" == 3 ]] || return 1
    for entry in "${root_entries[@]}"; do
        [[ "$entry" == "$index" || "$entry" == "$commit" || "$entry" == "$root/methods" ]] || return 1
    done
    expected="$(jq -r '[.[].family] | unique | sort | join("\n")' <<< "$method_map")"
    actual="$(printf '%s\n' "${family_entries[@]##*/}" | sort)"; [[ "$actual" == "$expected" ]] || return 1
    while IFS= read -r family; do
        shopt -s nullglob dotglob
        local published_methods=("$root/methods/$family"/*)
        shopt -u nullglob dotglob
        expected="$(jq -r --arg family "$family" '[.[] | select(.family == $family) | .methodId] | unique | sort | join("\n")' <<< "$method_map")"
        actual="$(printf '%s\n' "${published_methods[@]##*/}" | sort)"; [[ "$actual" == "$expected" ]] || return 1
    done < <(jq -r '[.[].family] | unique[]' <<< "$method_map")
    while IFS=$'\t' read -r family method_id; do
        base="$root/methods/$family/$method_id"
        phase14_source_safe_directory "$base" && phase14_source_safe_directory "$base/acceptance-artifacts" || return 1
        shopt -s nullglob dotglob
        local method_files=("$base"/*) artifacts=("$base/acceptance-artifacts"/*) trial_files=("$base/trial-results"/*)
        shopt -u nullglob dotglob
        [[ "${#method_files[@]}" == 6 ]] || return 1
        for entry in "${method_files[@]}"; do
            [[ "$entry" == "$base/acceptance-artifacts" || "$entry" == "$base/source-bundle.json" ||
               "$entry" == "$base/source-evidence.json" || "$entry" == "$base/result.trx" || "$entry" == "$base/test-assembly.dll" ||
               "$entry" == "$base/trial-results" ]] || return 1
        done
        expected="$(jq -r --arg family "$family" --arg methodId "$method_id" '[.[] | select(.family == $family and .methodId == $methodId) | (.scenarioId + ".json")] | sort | join("\n")' <<< "$method_map")"
        actual="$(printf '%s\n' "${artifacts[@]##*/}" | sort)"; [[ "$actual" == "$expected" ]] || return 1
        if [[ "$family" == cameraagent-acceptance ]]; then
            expected=$'trial-1.trx\ntrial-2.trx\ntrial-3.trx\ntrial-4.trx\ntrial-5.trx'
        else
            expected='method.trx'
        fi
        actual="$(printf '%s\n' "${trial_files[@]##*/}" | sort)"; [[ "$actual" == "$expected" ]] || return 1
        phase14_source_safe_directory "$base/trial-results" || return 1
        cmp -s "$base/result.trx" "${trial_files[0]}" || return 1
    done < <(jq -r '[.[] | [.family,.methodId]] | unique[] | @tsv' <<< "$method_map")
}

phase14_source_import() {
    local repo="$1" input_root="$2" output_root="$3" harness_revision="$4" contract="$5" inventory="$6"
    local final="$output_root/source-import" stage method_map method_id family fqn input_method fragment_dir assembly provenance
    local scenario selector fragment source_file trx_file bundle_file artifact_file relative artifact_length artifact_sha assembly_sha
    local source_length source_sha trx_length trx_sha inventory_hash index_entries='[]' method_entries actual_count bundle_entries='[]'
    local fragment_data observation_ids trial_results trial_source trial_path trial_length trial_sha
    local acceptance_inputs="${7:-${PHASE14_ACCEPTANCE_INPUTS:-}}"
    deploy_require_commands git jq sha256sum stat readlink dotnet || return 1
    if ! deploy_is_safe_absolute_path "$input_root" || ! deploy_is_safe_absolute_path "$output_root"; then
        phase14_source_fail options absolute-roots-required
        return 1
    fi
    if [[ "$input_root" == "$output_root" || "$output_root" == "$input_root/"* ]] ||
      { [[ "$input_root" == "$output_root/"* && "${PHASE14_SOURCE_COLLECTION_ROOT:-}" != "$input_root" ]]; }; then
        phase14_source_fail options roots-overlap
        return 1
    fi
    if ! phase14_source_no_symlink_ancestors "$input_root" || ! phase14_source_no_symlink_ancestors "$output_root"; then
        phase14_source_fail options symlink-root-ancestor
        return 1
    fi
    input_root="$(readlink -e -- "$input_root" 2>/dev/null)" || { phase14_source_fail input unsafe-root; return 1; }
    output_root="$(readlink -e -- "$output_root" 2>/dev/null)" || { phase14_source_fail output-root unsafe-root; return 1; }
    phase14_source_safe_directory "$input_root" || { phase14_source_fail input unsafe-root; return 1; }
    deploy_validate_output_root "$output_root" output-root || return 1
    phase14_source_validate_contract "$contract" "$inventory" || return 1
    phase14_source_validate_repository "$repo" "$contract" "$harness_revision" || return 1
    jq -e '(keys | sort) == (["collectorImage","catalogManifestSha256","catalogDatabaseSha256","catalogDatabaseByteLength"] | sort) and
      (.collectorImage | test("^[^[:space:]@]+@sha256:[0-9a-f]{64}$")) and
      all(.catalogManifestSha256,.catalogDatabaseSha256; test("^[0-9a-f]{64}$")) and
      (.catalogDatabaseByteLength | numbers) > 0' <<< "$acceptance_inputs" >/dev/null ||
      { phase14_source_fail input invalid-acceptance-inputs; return 1; }
    inventory_hash="$(jq -S -c . "$inventory" | sha256sum)"; inventory_hash="${inventory_hash%% *}"
    method_map="$(phase14_source_method_map "$contract" "$inventory")" || return 1
    if [[ -e "$final" || -L "$final" ]]; then
        phase14_source_validate_publication "$final" "$contract" "$inventory" "$inventory_hash" "$PHASE14_PRODUCT_REVISION" \
          "$PHASE14_PRODUCT_TREE" "$harness_revision" "$PHASE14_HARNESS_TREE" "$method_map" "$acceptance_inputs" ||
          { phase14_source_fail resume tampered-publication; return 1; }
        return 0
    fi
    umask 077
    stage="$(mktemp -d "$output_root/.source-import.stage.XXXXXX")" || return 1
    # shellcheck disable=SC2064 # Capture this invocation's staging path before local scope ends.
    trap "rm -rf -- '$stage'" RETURN
    shopt -s nullglob dotglob
    local input_families=("$input_root"/*)
    shopt -u nullglob dotglob
    [[ "${#input_families[@]}" == 3 ]] ||
      { phase14_source_fail input unexpected-family; return 1; }
    while IFS= read -r family; do
        shopt -s nullglob dotglob
        local input_methods=("$input_root/$family"/*)
        shopt -u nullglob dotglob
        if ! phase14_source_safe_directory "$input_root/$family" || ! phase14_source_no_symlink_path "$input_root" "$input_root/$family" ||
          [[ "${#input_methods[@]}" != "$(jq --arg family "$family" '[.[] | select(.family == $family) | .methodId] | unique | length' <<< "$method_map")" ]]; then
            phase14_source_fail input unexpected-method
            return 1
        fi
        while IFS= read -r method_id; do
            [[ -d "$input_root/$family/$method_id" && ! -L "$input_root/$family/$method_id" ]] ||
              { phase14_source_fail input unexpected-method; return 1; }
        done < <(jq -r --arg family "$family" '[.[] | select(.family == $family) | .methodId] | unique[]' <<< "$method_map")
    done < <(jq -r '[.[].family] | unique[]' <<< "$method_map")
    observation_ids='[]'
    while IFS=$'\t' read -r family fqn; do
        method_id="$(phase14_source_method_id "$fqn")"
        input_method="$input_root/$family/$method_id"
        fragment_dir="$input_method/fragments"; assembly="$input_method/test-assembly.dll"; provenance="$input_method/assembly-provenance.json"
        phase14_source_validate_input_method "$input_method" "$fragment_dir" "$assembly" "$provenance" "$family" || return 1
        assembly_sha="$(phase14_source_hash "$assembly")" || return 1
        if LC_ALL=C grep -F -a -q -- "$repo/" "$assembly"; then
            phase14_source_fail input assembly-contains-repository-path
            return 1
        fi
        jq -e --arg revision "$harness_revision" --arg tree "$PHASE14_HARNESS_TREE" --arg sha "$assembly_sha" '
          (keys | sort) == (["schemaVersion","buildRevision","buildTree","assemblySha256"] | sort) and
          .schemaVersion == 1 and .buildRevision == $revision and .buildTree == $tree and .assemblySha256 == $sha
        ' "$provenance" >/dev/null || { phase14_source_fail input assembly-provenance-mismatch; return 1; }
        method_entries="$(jq -c --arg family "$family" --arg fqn "$fqn" '[.[] | select(.family == $family and .fqn == $fqn)]' <<< "$method_map")"
        shopt -s nullglob dotglob
        local fragments=("$fragment_dir"/*)
        shopt -u nullglob dotglob
        actual_count="${#fragments[@]}"
        [[ "$actual_count" -ge "$(jq 'length' <<< "$method_entries")" ]] || { phase14_source_fail input fragment-cardinality; return 1; }
        if [[ "$family" == cameraagent-acceptance && "$actual_count" != 18 ]]; then
            phase14_source_fail input fragment-cardinality
            return 1
        fi
        install -d -m 700 "$stage/methods" "$stage/methods/$family" "$stage/methods/$family/$method_id" \
          "$stage/methods/$family/$method_id/acceptance-artifacts" "$stage/methods/$family/$method_id/trial-results"
        install -m 600 "$assembly" "$stage/methods/$family/$method_id/test-assembly.dll"
        for fragment in "${fragments[@]}"; do
            scenario="$(jq -r '.scenarioId' "$fragment")"
            jq -e --arg scenario "$scenario" 'any(.[]; .scenarioId == $scenario)' <<< "$method_entries" >/dev/null ||
              { phase14_source_fail input unexpected-fragment; return 1; }
            selector="$(jq -r --arg scenario "$scenario" '.[] | select(.scenarioId == $scenario) | .selector // ""' <<< "$method_entries")"
            phase14_source_validate_fragment "$fragment" "$scenario" "$selector" "$PHASE14_PRODUCT_REVISION" "$PHASE14_PRODUCT_TREE" ||
              { phase14_source_fail input invalid-fragment; return 1; }
            observation_ids="$(jq -c --arg id "$scenario:$(jq -r '.observationId' "$fragment")" 'if index($id) then error("duplicate observation") else . + [$id] end' <<< "$observation_ids")" ||
              { phase14_source_fail input duplicate-observation; return 1; }
        done
        fragment_data="$(jq -s 'sort_by(.scenarioId,.observationId)' "${fragments[@]}")" || return 1
        jq -e --argjson mappings "$method_entries" '
          (([$mappings[].scenarioId] - [.[].scenarioId]) | length) == 0 and
          all(group_by(.scenarioId)[]; ([.[].assertions] | unique | length) == 1 and
            ([.[].measurements] | unique | length) == 1 and
            ((length == 1) or (.[0].measurements | all(.id != "observation-count"))))
        ' <<< "$fragment_data" >/dev/null || { phase14_source_fail input incompatible-observations; return 1; }
        if [[ "$family" == cameraagent-acceptance ]]; then
            jq -e 'all(group_by(.scenarioId)[];
              if .[0].scenarioId == "raw-boundary-sidecar-directory-sync" or
                 .[0].scenarioId == "processing-output-before-node-commit" or
                 .[0].scenarioId == "transient-runtime-candidate-journal-after-commit" then
                length == 5 and ([.[].observationId | capture("^(?<trial>trial-[1-5])-" ).trial] | sort) ==
                  ["trial-1","trial-2","trial-3","trial-4","trial-5"]
              else length == 1 and (.[0].observationId | startswith("trial-1-")) end)' <<< "$fragment_data" >/dev/null ||
              { phase14_source_fail input incompatible-observations; return 1; }
        fi
        source_file="$stage/methods/$family/$method_id/source-evidence.json"
        jq -S -n --argjson mappings "$method_entries" --argjson fragments "$fragment_data" '
          {schemaVersion:1,entries:[$mappings[] as $mapping | [$fragments[] | select(.scenarioId == $mapping.scenarioId)] as $observations |
            $observations[0] |
            {scenarioId,evidenceSource:$mapping.evidenceSource,sourceRevision,sourceTree,outcome:"passed",
              assertions,outputs:[],measurements:(.measurements + (if ($observations|length) > 1 then
                [{id:"observation-count",value:($observations|length),unit:"count"}] else [] end))}]}
        ' > "$source_file"; chmod 600 "$source_file"
        trx_file="$stage/methods/$family/$method_id/result.trx"
        phase14_source_sanitize_trx "$repo" "$input_method/results" "$fqn" "$trx_file"
        trial_results='[]'
        if [[ "$family" == cameraagent-acceptance ]]; then
            for trial in 1 2 3 4 5; do
                trial_source="$input_method/trial-results/trial-$trial.trx"
                trial_path="$stage/methods/$family/$method_id/trial-results/trial-$trial.trx"
                install -m 600 "$trial_source" "$trial_path"
                trial_length="$(stat -c %s "$trial_path")"; trial_sha="$(phase14_source_hash "$trial_path")"
                trial_results="$(jq -c --arg id "trial-$trial" --arg path "trial-results/trial-$trial.trx" \
                  --argjson length "$trial_length" --arg sha "$trial_sha" \
                  '. + [{id:$id,relativePath:$path,byteLength:$length,sha256:$sha}]' <<< "$trial_results")"
            done
            cmp -s "$trx_file" "$stage/methods/$family/$method_id/trial-results/trial-1.trx" ||
              { phase14_source_fail input inconsistent-acceptance-trx; return 1; }
        else
            trial_path="$stage/methods/$family/$method_id/trial-results/method.trx"
            install -m 600 "$trx_file" "$trial_path"
            trial_results="$(jq -cn --argjson length "$(stat -c %s "$trial_path")" \
              --arg sha "$(phase14_source_hash "$trial_path")" \
              '[{id:"method",relativePath:"trial-results/method.trx",byteLength:$length,sha256:$sha}]')"
        fi
        source_length="$(stat -c %s "$source_file")"; source_sha="$(phase14_source_hash "$source_file")"
        trx_length="$(stat -c %s "$trx_file")"; trx_sha="$(phase14_source_hash "$trx_file")"
        bundle_file="$stage/methods/$family/$method_id/source-bundle.json"
        jq -S -n --arg requestedRevision "$PHASE14_PRODUCT_REVISION" --arg requestedTree "$PHASE14_PRODUCT_TREE" \
          --arg harnessRevision "$harness_revision" --arg harnessTree "$PHASE14_HARNESS_TREE" --arg assemblySha "$assembly_sha" \
          --argjson trxLength "$trx_length" --arg trxSha "$trx_sha" --argjson trialResults "$trial_results" \
          --argjson sourceLength "$source_length" --arg sourceSha "$source_sha" --argjson acceptanceInputs "$acceptance_inputs" --arg family "$family" \
          --slurpfile evidence "$source_file" '{schemaVersion:2,requestedSourceRevision:$requestedRevision,requestedSourceTree:$requestedTree,
            evidenceHarnessRevision:$harnessRevision,evidenceHarnessTree:$harnessTree,cleanSource:true,
            acceptanceInputs:(if $family == "cameraagent-acceptance" then $acceptanceInputs else null end),
            testAssemblyRelativePath:"test-assembly.dll",testAssemblySha256:$assemblySha,
             testAssemblyBuildRevision:$harnessRevision,testAssemblyBuildTree:$harnessTree,
             trxRelativePath:"result.trx",trxByteLength:$trxLength,trxSha256:$trxSha,
             trialResults:$trialResults,
            sourceEvidenceRelativePath:"source-evidence.json",sourceEvidenceByteLength:$sourceLength,sourceEvidenceSha256:$sourceSha,
             entries:$evidence[0].entries}' > "$bundle_file"; chmod 600 "$bundle_file"
        relative="methods/$family/$method_id/source-bundle.json"
        bundle_entries="$(jq -c --arg path "$relative" --argjson length "$(stat -c %s "$bundle_file")" \
          --arg sha "$(phase14_source_hash "$bundle_file")" '. + [{relativePath:$path,byteLength:$length,sha256:$sha}]' <<< "$bundle_entries")"
        while IFS= read -r scenario; do
            artifact_file="$stage/methods/$family/$method_id/acceptance-artifacts/$scenario.json"
            jq -S -n --arg scenario "$scenario" --arg inventory "$inventory_hash" --arg revision "$PHASE14_PRODUCT_REVISION" \
              --arg tree "$PHASE14_PRODUCT_TREE" --arg runId phase14-source-import --argjson mappings "$method_entries" --slurpfile evidence "$source_file" '
              $mappings[] | select(.scenarioId == $scenario) as $mapping |
              $evidence[0].entries[] | select(.scenarioId == $scenario) as $entry |
              {schemaVersion:1,runId:$runId,scenarioId:$scenario,inventorySha256:$inventory,sourceRevision:$revision,sourceTree:$tree,
               classification:$mapping.classification,executionClass:$mapping.executionClass,evidenceSource:$mapping.evidenceSource,
               workloads:$mapping.workloads,outcome:"passed",startedAt:"2000-01-01T00:00:00Z",completedAt:"2000-01-01T00:00:00Z",
               assertions:$entry.assertions,outputs:[],measurements:$entry.measurements}
            ' > "$artifact_file"; chmod 600 "$artifact_file"
            [[ "$(stat -c %s "$artifact_file")" -le "$(jq -r '.artifactContract.maximumByteLength' "$contract")" ]] || return 1
            artifact_length="$(stat -c %s "$artifact_file")"; artifact_sha="$(phase14_source_hash "$artifact_file")"
            relative="methods/$family/$method_id/acceptance-artifacts/$scenario.json"
            index_entries="$(jq -c --arg scenario "$scenario" --arg relative "$relative" --argjson length "$artifact_length" \
              --arg sha "$artifact_sha" --arg revision "$PHASE14_PRODUCT_REVISION" --arg tree "$PHASE14_PRODUCT_TREE" \
              --argjson mappings "$method_entries" '. + [$mappings[] | select(.scenarioId == $scenario) |
                {scenarioId:$scenario,status:"recorded",artifactRelativePath:$relative,artifactSchemaVersion:1,
                 artifactByteLength:$length,artifactSha256:$sha,sanitizer,admissibilityValidator:.validator,
                 sourceRevision:$revision,sourceTree:$tree}]' <<< "$index_entries")"
        done < <(jq -r '.[].scenarioId' <<< "$method_entries")
    done < <(jq -r '[.[] | [.family,.fqn]] | unique[] | @tsv' <<< "$method_map")
    [[ "$(jq 'length' <<< "$index_entries")" == 103 ]] || return 1
    jq -S -n --arg revision "$harness_revision" --arg tree "$PHASE14_HARNESS_TREE" --arg inventory "$inventory_hash" \
      --argjson acceptanceInputs "$acceptance_inputs" \
      --argjson entries "$(jq 'sort_by(.scenarioId)' <<< "$index_entries")" --argjson bundles "$(jq 'sort_by(.relativePath)' <<< "$bundle_entries")" \
      '{schemaVersion:1,status:"recorded",campaignStatus:"not-run",evidenceHarnessRevision:$revision,evidenceHarnessTree:$tree,
        inventorySha256:$inventory,acceptanceInputs:$acceptanceInputs,entries:$entries,methodBundles:$bundles}' > "$stage/source-import-index.json"; chmod 600 "$stage/source-import-index.json"
    local index_length index_sha
    index_length="$(stat -c %s "$stage/source-import-index.json")"; index_sha="$(phase14_source_hash "$stage/source-import-index.json")"
    jq -S -n --argjson length "$index_length" --arg sha "$index_sha" \
      '{schemaVersion:1,indexByteLength:$length,indexSha256:$sha}' > "$stage/source-import-commit.json"; chmod 600 "$stage/source-import-commit.json"
    phase14_source_validate_publication "$stage" "$contract" "$inventory" "$inventory_hash" "$PHASE14_PRODUCT_REVISION" \
      "$PHASE14_PRODUCT_TREE" "$harness_revision" "$PHASE14_HARNESS_TREE" "$method_map" "$acceptance_inputs" ||
      { phase14_source_fail publication staged-validation-failed; return 1; }
    [[ "${PHASE14_SOURCE_IMPORT_FAILPOINT:-}" != before-publish ]] || { phase14_source_fail publication injected-failure; return 1; }
    mv -T -- "$stage" "$final" || return 1
    trap - RETURN
}
