# Released 0.9.0 time-lapse jobs

These are stored documents, not jobs re-frozen by the implementation under test.

- Release tag: `v0.9.0`, commit `6262d564a2e0aa03d4fbb8a934e68f74a9f27ea0`.
- Capture baseline: `1d6115ff`. The SQLite store, CameraAgent preset contract/options and central preset/request/JSON contracts at this baseline are byte-for-byte identical to the release (verified with `git diff v0.9.0 -- <paths>` before editing).
- `working-job.sql`: actual database snapshot after the released `SqliteTimeLapseStore.EnqueueAsync` and `ClaimAsync` code paths. Exported with SQLite `iterdump`, with its `user_version=1` retained explicitly. It includes the exact released schema, immutable-input triggers, frozen preset and automation occurrence. The job is Working, with source discovery not yet frozen, so it exercises crash recovery as well as upgrade reads.
- `central-request.json`: actual `CentralTimeLapseJson.Serialize` output from the unchanged released contract. Canonical request identity: `C560BD6DD3C7B6B33FDD26DF89552F155064A5843B5E7ED23FF7F4B701116B2F`.
- Neither preset has a renderer-version field. Missing means `hvo-timelapse-stack-display-v2`, never the current default.

The fixtures use synthetic public test locations and rigs. No private deployment state is included.

The Unit upgrade test restores the SQL directly, reads and re-enqueues the unchanged preset, recovers the Working job, completes an empty source plan, then reopens it. The native FFmpeg test supplies four synthetic 512x512 Mono16 captures in the fixture's window after upgrade, resumes the same stored job, publishes a checksummed MP4 and verifies its first frame retains the released rendering identity. The central Unit test reads the released JSON through retained-job validation and pins its identity and serialization.
