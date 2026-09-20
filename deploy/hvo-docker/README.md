# SkyMonitor Shared Services

This stack provisions the shared SkyMonitor Redis and Mailpit services on `hvo-docker.hvo.lan`. It is separate from application stacks and retains data in named Docker volumes.

LogicHost object storage is not a shared service. It is a dedicated filesystem root on the LogicHost host itself, qualified by `./scripts/qualify:filesystem-object-store`; see `docs/runbooks/infra-operations.md`.

## Prerequisites

- A Docker context or SSH account with Docker access to `hvo-docker.hvo.lan`.
- A root repository `.env` file created from `.env.template`, containing the shared-service credentials. Do not commit that file.

## Deploy

```bash
docker context create hvo-docker --docker "host=ssh://roys@hvo-docker.hvo.lan"
docker --context hvo-docker compose --env-file .env -f deploy/hvo-docker/docker-compose.shared-services.yml up -d
docker --context hvo-docker compose --env-file .env -f deploy/hvo-docker/docker-compose.shared-services.yml ps
```

The stack creates the `skymonitor_default` network. Remote application containers that consume these services must join this external network and use the service names `redis` and `mailpit`.

## Endpoints

- Redis: port `6379`, authenticated with `REDIS_PASSWORD`
- Mailpit SMTP: port `1025`
- Mailpit web UI: port `8025`

Restrict these ports at the host firewall or reverse proxy to the networks that require them. Mailpit is a development/test mail capture service and must not be exposed publicly.
