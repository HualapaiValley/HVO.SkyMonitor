# Base-image parser comparison

`cases.json` retains Dockerfile inputs, expected instruction names and physical
source lines, or an expected unterminated-heredoc failure. The ordinary offline
`scripts/test:ci-base-images` suite checks the Python guard against these cases.

The optional independent oracle uses the local Docker Buildx/BuildKit parser.
With a local Docker daemon and Buildx supporting JSON `targets` calls, run from
the repository root:

```bash
./scripts/test:ci-base-images-buildkit
```

This compares BuildKit's real FROM start lines against the same retained
expectations, including rejected unterminated bodies. `--call=targets` parses
only; it does not build images, execute payloads or resolve base images. The
script explicitly uses the `default` local context and builder, never a remote
builder. Docker is not a dependency of the offline guard or Development v1 CI.

Syntax reference: [BuildKit v0.26.1](https://github.com/moby/buildkit/blob/v0.26.1/frontend/dockerfile/parser/parser.go).
