# Silo storage per capture for #1300

The Stock Silo integration fixture uses deterministic 1936 × 1216 pixels at
the reference ASI174MM frame size. It publishes and reads back three 16-bit
science images, a Mono8 preview, an RGB24 annotated preview, and both
presentation images encoded as JPEG at quality 85.

| Retained image payloads per capture | Bytes | Reduction from packed baseline |
| --- | ---: | ---: |
| Raw, calibrated, combined, packed preview and packed annotated preview | 23,541,760 | Baseline |
| Raw, calibrated, combined, JPEG preview and JPEG annotated preview | 14,410,046 | 38.8% |
| Daytime JPEG preview and JPEG annotated preview after holds release | 284,990 | 98.8% |

The preview JPEG was 138,507 bytes and the annotated preview JPEG was
146,483 bytes. On home-dev-01 (Linux x64), the two JPEG encodes took 49.72 ms
and allocated 9,704,440 managed bytes in this run. The test checks exact
version SHA-256 and byte equality for all three lossless science payloads,
then decodes both retained JPEGs and checks their dimensions. It also
verifies that the raw version is readable before expiry and that exact raw,
calibrated, combined and packed presentation roles settle as
`policy-expired` after hold release.

The totals cover image payloads. Populated qualification in #1974 also
measures captured-scene entropy, metadata, Silo journal and filesystem
overhead, shared versions, staged raw bytes, reclaim-pending trash, and
throughput at the deployed cadence. The 284,990-byte figure describes
retained payloads after settlement. Each capture still incurs the initial
full-precision raw write.

Reproduce on a Docker-capable Linux host with the pinned .NET 10.0.402 SDK
and the disposable Stock Silo image specified in the integration fixture.

```bash
install -d -m 0700 /tmp/hvo1300
export TMPDIR=/tmp/hvo1300 MSBUILDDISABLENODEREUSE=1
flock --close /tmp/hvo-heavy-gate.lock dotnet test \
  tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/HVO.SkyMonitor.CameraAgent.IntegrationTests.csproj \
  --configuration Release \
  --filter 'FullyQualifiedName~StockSiloDaytimeProfileKeepsJpegAndSettlesFullSizeExpiryAfterHolds' \
  --logger 'console;verbosity=detailed'
```

See [capture storage profiles](../runbooks/cameraagent-silo-runtime.md#capture-storage-profiles)
for policy configuration, JPEG graph nodes and reopen lifetimes.
