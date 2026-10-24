# F7 private qualification controls

`scripts/test:catalog-f7-controls` runs the 90 current controls against **whole
selected modules in private fake namespaces**. It also tests typed terminal
rejections, deterministic multi-chunk reconstruction, corrupt evidence rejection,
and failed/partial publication preservation. It uses registered `hvo scratch`
output, the host heavy-gate lock, a 180-second timeout, 256 MiB address-space
limit, 65,536-byte records and 8 MiB aggregate output. Supply an existing
registered scratch with `--output-root PATH` to retain results at a known path.

The selected modules never run natively in this test. Fake imports supply all
process, filesystem, clock, identity, wait, signal and workload interactions.
The control adapter itself only reads its own metadata and writes its bounded
output. Passing these controls establishes the private model's behavior; native
campaign performance, actual selected child identity/wait/resource attribution,
whole-cohort containment, and filesystem crash survival require separate evidence.

## Recovered source and v8 transformations

The input was the reconstructed 101,877-byte v7 harness, SHA256
`ab794cf711c7cffe29e87794f81c34256bf55724343c6a2948323c6fe14f0554`.
Its recovery report retains reconstruction recipes and provenance gaps. It is
not the missing original 894-member sealed packet. The original dated 128 IDs
and source literals remain externally preserved; this directory does not claim
their original serialized inventory bytes or invent their inventory hashes.

The five selected executor files were copied in full. Four remain byte-exact:

| File | Recovered/current SHA256 |
| --- | --- |
| `finalize_evidence.py` | `ac997177c90ace52cf82bfbc3d9159d0f1216e324f04a3de15ff0dc86695aa97` |
| `preserve_incomplete.py` | `17e2dd7aa2f587a30ebabe419f0b42966398e67b77000207ff090799a39ace46` |
| `external-afterseal.py` | `33dc5525556f13e6a8d49adafc41d0ae90dcc57cff8536f7f3edf039ee67b0e9` |
| `durable_publication.py` | `efa5084fc05bde931a258fabe30f0b08dbe56fbafda2ead7e45f9992d2287e75` |

Recovered `execute.py` was 12,295 bytes, SHA256
`924831844f816b3bdc02776d2fe4b6ebff6af505dfe93b194d68bc190d4cd7d1`.
Its only transformation replaces two fixture hostname literals with
`f7-fixture.invalid`; it is now 12,303 bytes, SHA256
`67931225d18e7231fafcb0e2a71268677b11cdc5db144b7c918bb15f74c0767d`.
The same substitution was applied consistently to the harness and resource data.

`controls/qualification-v8.py` retains the original current90 case IDs, order,
fake interactions and rejecting predicates, including all 24 whole-execute
terminal discriminators. Its changes from recovered v7 are:

- Move fallback inspection fault injection from `stat` to `lstat`, allowing
  descriptor verification and the named 17-byte partial write before inspection
  fails. Both independently named faults remain mandatory in the oracle.
- Require Python's actual `PermissionError`/13 and `NotADirectoryError`/20
  inspection types for the EACCES and ENOTDIR cases.
- Reset publication progress per case so an early failure cannot cite the
  previous case's evidence.
- Encode the complete snapshot JSON with deterministic gzip, then publish
  ordered bounded chunks and an index. Keep separate exact raw/encoded/chunk
  lengths and hashes, file/directory identity, ordinary reservations and partial
  charging. Completed and failed evidence use the same format. No trace is
  dropped, no oracle is weakened, and no resource or output cap is increased.

`controls/evidence_codec.py` verifies the complete reconstruction with a bounded
decoder. It rejects missing, reordered, substituted, corrupt, truncated, trailing
or overflowing evidence. `CURRENT90-SOURCE-LITERALS.json` is a derived comparison
inventory of the recovered case literals, not an original dated inventory seal.

Only fixture data consumed by these fakes is included: the resource subsection
of candidate pins, all 119 exact ordered gate IDs with empty argv metadata, and
the 38 expected stage member bindings. The selected finalizer reads `argv` only
to record it; these empty arrays authorize no command. Stage hashes describe the
recovered source-stage fixture; the corresponding 37 broad source files, stage
archive, hardware infrastructure and other executor modules are not vendored or
executed. Original command argv/path data and all recovery material remain
external. No native qualification or source-packet closure follows from a pass.
