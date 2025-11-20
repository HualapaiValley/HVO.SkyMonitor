# Future Infrastructure TODOs

These items are intentionally postponed until after the current modernization plan. Revisit this list during the next planning cycle.

## 1. Development Certificates & HTTPS (Plan Item 22)

- Provide a `scripts/get-certs.sh` helper that issues self-signed wildcard certificates for `*.skymonitor.local`.
- Store generated certs under a developer-specific directory (e.g., `~/.skymonitor/certs`).
- Update `docker-compose.apps.yml` to mount the cert bundle into the host and agent containers, wiring the appropriate Kestrel and OpenIddict endpoints via environment variables.
- Extend integration tests (or a smoke test) to exercise at least one HTTPS flow once the infrastructure is stable.

> Status: Deferred. Track progress in the next roadmap once HTTP-only validation is fully baked.
