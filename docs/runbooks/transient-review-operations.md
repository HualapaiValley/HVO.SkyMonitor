# Transient Review Operations

Issue #118 adds central event review, deterministic derivatives, notification dispatch, reprocessing, and optional payload release. SQL event history, lineage, reviews, notifications, and release audit remain durable indefinitely.

## API Preconditions

All mutation endpoints require one strong event `If-Match` value and one `Idempotency-Key`. Missing headers return 428, stale state returns 412, and reuse of a key for a different request returns 409. Exact replay is evaluated before the ETag.

- `POST /api/v1.0/transient-events/{eventId}/reviews` requires an authenticated non-system reviewer with write access.
- `POST /api/v1.0/transient-events/{eventId}/reprocessing-jobs` requires `api.admin`.
- `POST /api/v1.0/transient-events/{eventId}/notifications/{notificationId}/retry` requires `api.admin`; the notification ID is available in event history.
- `POST /api/v1.0/transient-events/{eventId}/payload-release` requires `api.admin` and explicit release enablement.
- `GET /api/v1.0/transient-events/{eventId}/derivatives/{derivativeId}/content` is owner scoped, supports one byte range, and returns 410 after payload release.

Cross-owner access is returned as 404 unless the credential has `api.admin`. API responses and telemetry never expose object keys, storage paths, credentials, recipients, actors, idempotency keys, checksums as labels, or worker lease tokens.

## Notifications

Eligible reviewed or overridden Meteor and Fireball events create durable `Pending` email notifications. The worker commits a `Fenced` state before SMTP. A successful send appends `Sent`; SMTP failure appends `Failed`. A fence younger than `CentralTransientNotification:FenceTimeout` is treated as active. An expired fence is ambiguous and becomes `Failed` without resend. An administrator can retry that failed notification, which appends a new notification and dispatch identity.

## Reprocessing

Reprocessing freezes source event version, producer/recipe/options identities, and ordered artifact evidence. Equivalent requests converge to one job. Execution verifies every frozen object before appending a new assessment and event version. Prior assessments, reviews, derivatives, and event versions remain immutable; current review state resets to `NeedsReview`.

## Payload Release

`TransientPayloadRelease:Enabled` defaults to `false`. Enable it only after confirming external retention policy and backups. Eligibility requires terminal review state, no active derivative or reprocessing job, and no pending notification. Artifacts referenced by another event, a clear-reference designation, or unrelated active work are preserved.

Release markers are committed before deletion. `CentralTransientPayloadReleaseWorker` resumes pending markers after restart, and deletion is idempotent when an object is already absent. Released source and derivative objects become `Expired`; derivative reads return 410 while SQL lineage and audit remain available.

## Health And Evidence

The `central-transient-lifecycle` health check reports bounded notification and release backlog counts and ages. Runtime signal definitions and privacy exclusions are pinned in `docs/validation/transient-review-runtime-signals.json`. Canonical performance evidence is written under `TestResults/issue-118/<revision>/`.
