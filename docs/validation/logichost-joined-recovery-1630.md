# Joined LogicHost recovery fixtures (#1630)

Test-only preparation for [#1244](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1244),
based on `development/v1` at `2f4eace674ff2c145ff765ac025c55382afe74d1`.
The fixture composes the retained [SQL toolkit evidence](issue-255-candidate.md),
[filesystem qualification](logichost-filesystem-qualification-586.md),
[#1608 decision](logichost-upgrade-recovery-1608.md), and
[#1615 comparison](upgrade-recovery-cost-1615.md). Those standalone results
remain credited with their original source and topology identities.

## Fixture and admission boundary

`JoinedRecoveryFixtureTests.JoinedRestore_AdmitsOnlyPairedSqlObjectsAndHostState`
runs sequentially in the existing LogicHost IntegrationTests assembly. It creates
one uniquely labelled, disposable SQL Server container, run-owned source and
restore databases, and private source/backup/target filesystem trees. Credentials,
instance ID, key material, provider generations and modification times are
generated per run. Payloads, SQL artifact/frame IDs, fixture catalog and logical
keys are deterministic. No shared application state is read or changed.

The current canonical EF migration creates the schema. Identity options match
LogicHost's explicit v3 configuration; model-mismatch checks remain enabled.
The fixture owns its EF SQL Server service provider/model cache, so bare Identity
contexts from other migration fixtures cannot replace its canonical v3 model. A
Docker-free regression warms a bare model first and compares both models with the
canonical migration, keeping model validation enabled. The existing canonical
graph seed helper supplies graph/assignment rows. The
small corpus consists of one frame, two SQL-referenced artifact objects and one
diagnostic object: 60 payload bytes across the two configured buckets. A disposable
Identity user has an ephemeral password and six claims anchoring instance,
cutoff, public configuration, Data Protection key ID, catalog manifest and catalog
database identities. The installed fixture catalog comes from the existing
`CatalogFixtureInstallation` helper. These claims have meaning only to this test.

Expected payload hashes, exact bucket/key/generation/type/time/length facts,
private host-state bytes and SQL semantic facts are retained before backup,
independently of the backup inventory or restored target. Public config stores
only a credential reference. Key-ring bytes and credential material are never
published, including their private preservation fingerprints.

The harness executes `deploy/sql/backup-restore-checkdb.sql` through the existing
SQL harness: real COPY_ONLY with CHECKSUM, metadata verification, VERIFYONLY,
FILELISTONLY-derived MOVE, actual separate restore, full CHECKDB and canonical
claim-row invariants. A guarded test-only substitution retains the verification database on both
success and failure, so C# can query or attribute the restored target. It also
omits the toolkit cleanup-failure check for that deliberately retained target,
preserving the sanitized stage error. The shipped script and its standalone
cleanup behavior are unchanged. The final owner cleanup
removes that dedicated container, including its source/target databases and backup.

Existing `FilesystemObjectBackup.BackupAsync`, `RestoreAsync` and `VerifyAsync`
produce and restore the completed offline object copy. Admission then reads
committed descriptor/generation bytes without opening or reconciling the provider.
It compares them with the pre-backup expected facts, verifies SQL references and
semantic state, and compares every expected host-state file. SQL stores logical
object references in the current schema; the independently retained fixture facts
bind those references to the exact provider generations. No schema change is made.

## Retained refusal controls

The test retains 32 negative cases: missing/truncated SQL; missing or mismatched
instance/config/key/catalog components; truncated catalog data; missing object
bucket/descriptor/generation; truncated descriptor or payload; altered same-length
payload; wrong generation/type/length/SHA/key/time; an extra host component; mixed
object cutoff; wrong SQL object reference; and substitutions to each of the six
SQL host-identity anchors.

The mixed-cutoff control uses a real later provider write and a separately
completed, self-verifying backup. Payload content is unchanged while generation
identity changes. It must fail pairing despite passing its own offline verifier.

Every rejection checks that source SQL, source objects/host/backup bytes, target
SQL and target filesystem bytes remain unchanged across admission. The damaged
inputs remain present until those checks finish. An unexpected failure retains
the dedicated container and private tree, with a local attribution receipt
containing exact owned resource identifiers but no credentials/key bytes. The
outer MSTest boundary emits only phase, bounded category/SQL error number and
cleanup booleans. Success explicitly deletes only the owned container/tree.

## Reproduction and evidence

Run on native Linux x64 with the repository SDK and Docker, from an isolated
checkout of the pinned candidate. Heavy commands use the host-wide lock:

```bash
flock -o /tmp/hvo-heavy-gate.lock timeout 900 \
  dotnet build tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --configuration Release -warnaserror -nodeReuse:false -p:UseSharedCompilation=false
flock -o /tmp/hvo-heavy-gate.lock timeout 600 \
  dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --no-build --configuration Release --filter 'FullyQualifiedName~JoinedRecoveryFixtureTests' \
  -nodeReuse:false -p:UseSharedCompilation=false --logger trx
```

Focused native correction: **4 passed**, including the joined test's 32 refusal
controls and three existing migration cases in the same process. Source
`b79a1f6f76274c47b2270aedc3cf9f761ab89572`. The
[sanitized native record](evidence/issue-1630/joined-result.json) has SHA-256
`06a0c6cac3f8b2aff2d607436baa4dad72499f1302acf9afe3d70336fa9e2323`.
It pins the tool SQL SHA-256, SQL image digest, actual SQL backup file SHA-256,
SQL semantic digest, public config/catalog digests and every expected object fact.

Environment: Ubuntu 24.04.5 LTS, Linux 6.8.0-142-generic, native x64;
12 exposed CPUs (Intel Core i9-14900K), approximately 15 GiB RAM; Docker 29.8.1,
overlayfs driver, ordinary ext4-host temporary directories. SDK 10.0.402,
.NET/EF Core 10.0.13, MSTest 4.5.1 and Testcontainers 4.16.0; SQL Server
2022 CU26 `16.0.4265.3`, pinned by the fixture image digest. Release build.
The authoring machine's macOS environment supplied no SQL/integration result;
the Docker-free metadata regression also passed there.

| Phase | Elapsed ms |
| --- | ---: |
| container-start | 3713 |
| canonical-seed | 2622 |
| sql-backup-restore-checkdb | 764 |
| object-backup | 22 |
| object-host-restore | 29 |
| joined-admission | 73 |
| refusals | 8919 |

These are single phase observations without warm-up or a percentile estimate.
The native SQL script separately reported backup 122 ms, restore
276 ms, and 584 ms overall (including CHECKDB/invariants).
It reported six source/restored claim rows with equal canonical SHA-256 and one
COPY_ONLY media family. Engine `backup_size` was 8,413,184 bytes; the independently
hashed physical backup file was 8,441,856 bytes including media overhead.
CHECKDB/invariants passed; SQL semantic facts and all three restored objects
matched their retained expectations. Cleanup verified the exact owned container
was absent through the configured Docker API and the owned filesystem tree was
removed.

## Candidate gates and correction evidence

Profile: `development-v1`; Tier C, deep durability review. The classifier selected
`mode=full`, `complete=false`, `logichost=true`; deployment, shared, CameraAgent,
combined, delivery, secret-scan-contract and release-script flags were false.
Both the full review range and the correction range use that selector. The
classifier does not request full qualification dispatch. Parent owns draft PR,
independent review, current-head v1 checks and finalization.

The complete initial candidate block ran on `2c37f185`: tool/solution restore,
warning-clean Debug (257 s) and Release (165 s), formatting, package audit,
all four mandatory control guards, complete Docker-disabled Unit and complete
Integration with coverage collection. Package audit found no vulnerabilities or
deprecated occurrences. The declared-inventory documentation audit also passed.

Two affected portions required correction. Formatting identified the new fixture
files; the formatter-only delta had identical Roslyn tokens and corrected full
format verification passed. The complete initial Integration selection ran 956
cases: 955 passed and only the new fixture failed, because earlier bare contexts
had populated EF's shared Identity v1 model cache. The owned v3 EF service root
and Docker-free regression address that interaction without suppressing model
validation.

The complete Unit gate passed in 473 s: 13,598 passed, 124 skipped, zero failed.
Production catalog input was unset and Docker used the prescribed invalid
endpoint. Most skips require approved production catalog bundles; provider
ambiguity and runtime-user permission controls also report existing conditional
skips. Final affected Unit selection passed all 19 cases, including the new
metadata regression. Replacing its previous 18-case slot retains effective
selection evidence of 13,599 passed / 124 skipped / zero failed (13,723 cases).
Unchanged Unit suites were not repeated.

Runtime execution totals exceed declared discovery inventories in three untouched
Unit assemblies: combined +5, Processing +17, CameraAgent +23. The operations-doc
audit validates declared inventory totals; no exhaustive discovery/qualification
claim is made here.

The correction ran affected warning-clean Debug/Release builds, the affected
Docker-disabled Unit selection, and the four-case focused migration/recovery
combination on the final code. Complete LogicHost Integration passed **583/583**,
with zero skips/failures
(883 s command elapsed, including test-host/coverage overhead). The six unaffected
Integration assemblies retain their green 373-case evidence, completing all 956
Integration cases. Final whole-solution formatting passed (225 s command elapsed).
All four control guards passed again under native Bash 5.2; the declared-inventory
documentation audit passed with Unit=13678 / Integration=956. Builds/tests used
`flock -o`, bounded `timeout`, `-nodeReuse:false` and
`-p:UseSharedCompilation=false`; Docker-free control guards require no heavy-build
slot. Gate ledgers include lock waiting in command elapsed time; build execution
was 71 s Debug and 80 s Release for the corrected Integration project.

The final review-range selector is:

```bash
bash scripts/ci:classify pull_request 2f4eace674ff2c145ff765ac025c55382afe74d1 HEAD
```

```text
mode=full
deployment=false
deployment_catalog=false
deployment_shards=false
deployment_installer=false
complete=false
shared=false
cameraagent=false
logichost=true
combined=false
delivery=false
secret_scan_contract=false
release_scripts=false
```

Exact commands, redacted complete logs, checksums and coverage/TRX locations are
retained in the issue's registered private validation scratch and implementation
handoff. The initial failed format/model-cache gates remain in the ledger with
their passing corrections; cancelled lock waiters are identified separately from
test failures. Three failed development fixture runs were preserved for
attribution, then their exact label/receipt-matched containers and private trees
were removed. Final resource inspection found no run-owned #1630 container or
attribution tree. The registered validation checkout/build/results/log artifacts
remain for parent review and later lifecycle cleanup.

No production code, provider implementation, migration, dependency or other test
assembly changed.

## Post-restore failure preservation correction (F1)

Independent review identified that the initial hook retained only the normal
verification target; the toolkit CATCH still deleted it on a verification
failure. Native reproduction on `1be29948` returned sanitized error 51266 after
actual restore, with source and backup present but the target absent. The
permanent canonical regression
`SqlFailure_AfterRestorePreservesSourceTargetBackupAndAttribution` was committed
at `489e5e87` and failed specifically with `postrestore-target-preserved` before
the hook was corrected. Its Release build was warning-clean. Failed resources
were retained until their attribution/ownership was checked, then the exact
label-matched container and run tree were removed; the private receipt and
failure logs remain in registered evidence scratch.

The correction extends only the test hook to retain the CATCH target and skip
its intentional-retention cleanup-failure guard. The standalone SQL script is
byte-identical. The new control uses the canonical corpus and semantic facts,
verifies the original safe error, both database contents, backup bytes,
object/host files and the private attribution receipt before exact owned cleanup.
It supplements the 32 admission refusal cases; it does not add production
interrupted-restore behavior. Declared Integration inventory becomes 957,
including 584 in the LogicHost assembly.

The corrected behavioral candidate `202206f2` passed affected warning-clean
Debug (70 s) and Release (66 s), the five-case migration/joined selection
(52 s command), all 19 fixture Unit cases, and complete LogicHost Integration
**584/584** with no failures or skips (785 s command). Full solution format
verification passed (133 s), as did all four control guards and the documentation
inventory audit. Both the correction and full-range classifiers selected only
LogicHost (`complete=false`); profile remains `development-v1`. Unchanged tool,
package, other integration and deployment-shard evidence remains credited.

The [sanitized F1 before/after record](evidence/issue-1630/postrestore-result.json)
has SHA-256 `448058a8eae30e7a79fe7c92820526d3f368471704f7bb84ab644ce461806d05`.
It binds the failing and corrected source identities, exact test, original safe
error, preservation observations, unchanged toolkit and affected case counts.
Native failed/passing TRX, coverage, logs, commands, cleanup receipts and checksums
remain in registered private evidence scratch. The later target merge introduces
CameraAgent retained-read attribution and presentation defaults without conflicts;
the issue's behavioral sources remain unchanged. Affected synchronization checks
and current-head v1 checks are recorded in the PR ledger.

## Limits and parent acceptance

The authoring host is macOS ARM64 without Docker. SQL/integration validation runs
remotely on native Linux x64, with ephemeral Testcontainers and ordinary temporary
filesystem directories. The object directories are on an ext4 host filesystem,
but there is no dedicated qualification mount, production UID/mount preflight,
application image, service deployment or hardware campaign. Docker SQL data uses
the disposable container's own storage. This is no new native ext4 or ARM64 SQL
qualification.

Identity/byte comparisons establish fixture pairing. They do not establish an
authentication restart or admission into a running LogicHost. There are no
application writers in the fixture; no central writer-fencing API is implemented.
Production recovery formats/APIs, installation receipts, migrations, new storage
backends, encryption/off-host operation, integrated interrupted restore, runtime
reopening, production catalogs and #1244 completion remain outside this change.

Phase times are one small software-fixture observation, including startup and
connection/tool overhead where stated. No scale or site RPO/RTO, achieved service
downtime, full-stack ARM64, production throughput/backlog or comparative runtime
savings are claimed. Production I/O/CPU/RSS/latency comparisons are N/A because
only test infrastructure changes; the retained phase and byte counts provide an
absolute fixture baseline without a production-performance inference.
