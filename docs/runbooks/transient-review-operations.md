# Transient Review Operations

Issue #118 adds central event review, deterministic derivatives, notification dispatch, reprocessing, and optional payload release. SQL event history, lineage, reviews, notifications, and release audit remain durable indefinitely.

## API Preconditions

All mutation endpoints require one strong event `If-Match` value and one `Idempotency-Key`. Missing headers return 428, stale state returns 412, and reuse of a key for a different request returns 409. Exact replay is evaluated before the ETag.

- `POST /api/v1.0/transient-events/{eventId}/reviews` requires an authenticated non-system reviewer with write access.
- `POST /api/v1.0/transient-events/{eventId}/reprocessing-jobs` requires `api.admin`.
- `POST /api/v1.0/transient-events/{eventId}/notifications/{notificationId}/retry` requires `api.admin`; the notification ID is available in event history.
- `POST /api/v1.0/transient-events/{eventId}/payload-release` requires `api.admin` and explicit release enablement. It returns 200 when deletion reaches a terminal state in the request and 202 with the durable pending release when work remains scheduled.
- `GET /api/v1.0/transient-events/{eventId}/derivatives/{derivativeId}/content` is owner scoped, supports one byte range, and returns 410 after payload release.

Cross-owner access is returned as 404 unless the credential has `api.admin`. API responses and telemetry never expose object keys, storage paths, credentials, recipients, actors, idempotency keys, checksums as labels, or worker lease tokens.

## Notifications

Eligible reviewed or overridden Meteor and Fireball events create durable `Pending` email notifications. The worker commits a `Fenced` state before SMTP. A successful send appends `Sent`; SMTP failure appends `Failed`. A fence younger than `CentralTransientNotification:FenceTimeout` is treated as active. An expired fence is ambiguous and becomes `Failed` without resend. An administrator can retry that failed notification, which appends a new notification and dispatch identity.

## Reprocessing

Reprocessing freezes source event version, producer/recipe/options identities, and ordered artifact evidence. Equivalent requests converge to one job. Execution verifies every frozen object before appending a new assessment and event version. Prior assessments, reviews, derivatives, and event versions remain immutable; current review state resets to `NeedsReview`.

## Payload Release

`TransientPayloadRelease:Enabled` defaults to `false`. Enable it only after confirming external retention policy and backups. Eligibility requires terminal review state, no active derivative or reprocessing job, and no pending notification. Artifacts referenced by another event, a clear-reference designation, or unrelated active work are preserved.

All source and derivative targets receive durable ordered item rows before hold filtering. A short serializable transaction reserves and revalidates one item, then commits before MinIO DELETE. The worker retains the hashed object application lock across reservation, DELETE, and a separate compare-and-swap finalization transaction; no SQL transaction for that release remains open during object-store I/O. Held items become terminal `PreservedHeld` rows without deletion. Retryable storage failures clear the renewable token, increment `RetryCount`, and schedule bounded exponential backoff. Exhausting `MaximumRetryCount`, or encountering terminal storage/configuration failure, records terminal item `Failed` with a bounded reason and fails the parent after every item is terminal. Stale reservations are reclaimable after `ReservationLeaseTimeout`, object absence is idempotent success, and parent completion occurs in a separate transaction only after every item is terminal.

The lock order is parent application lock, object application lock, release-item row lock, then target row lock. Operators can tune `ReservationLeaseTimeout`, `InitialRetryDelay`, `MaximumRetryDelay`, and `MaximumRetryCount`; defaults are one minute, five seconds, five minutes, and five attempts. Released source and derivative objects become `Expired`; derivative reads return 410 while SQL lineage, terminal items, reservation snapshots, failure reasons, and release audit remain available.

## Health And Evidence

The `central-transient-lifecycle` health check reports actionable notification and release parent/item counts and ages, retry-due and reserved/stale-reservation counts, and pending/reserved logical bytes. Immutable terminal failures remain available through bounded telemetry and operational release queries; health does not scan all historical failures. Runtime signal definitions and privacy exclusions are pinned in `docs/validation/transient-review-runtime-signals.json`.

Issue #250 uses focused deterministic correctness gates in this branch. Claimable statistical W2/W3M after-performance evidence is deferred to the dedicated consolidation issue; the Manual harness remains compile-checked and rejects claimable `after` runs explicitly.
