# Contributing to SkyMonitor

Start with the [development branch guide](docs/development-v1.md) for issue
ownership, local validation, and the pull request workflow.

For an operator-visible change, add one [changelog fragment](changelog.d/README.md)
and run `./scripts/release:changelog --check`. The release PR assembles the
fragments into `CHANGELOG.md`.

Use the generated test counts in the full CI Build summary or reproduce them
locally with the [category audit](docs/runbooks/ci-pipeline.md#categories).
If a project gains or loses its Unit selection, update
`tests/unit-test-projects.json` and the corresponding workflow commands.
