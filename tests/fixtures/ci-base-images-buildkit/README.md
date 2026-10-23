# Base-image parser comparison

`cases.json` retains Dockerfile inputs and BuildKit's expected instruction names
and physical source lines (`instructions`), or an expected unterminated-heredoc
failure (`instructions: null`). An optional `guard_error` records the offline
guard's exact rejection at the opener on line 2. With non-null `instructions`,
this is an intentional compatibility limit: BuildKit accepts the input while
the guard rejects it. With null `instructions`, both reject it, with different
diagnostics. Omitting `guard_error` requires the guard to match the BuildKit
instruction expectation.

The ordinary offline `scripts/test:ci-base-images` suite checks both outcomes.
For each intentionally rejected header it also replaces the final stage with an
unpinned FROM and removes the terminator in a separate variant. Both must still
fail at the opener, before any body can hide the later FROM. A guard rejection
alone is not evidence that BuildKit rejects the Dockerfile.

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

The compatibility cases cover single/double quoted and escaped dollar/backtick
delimiters, quoted brace delimiters, brace substitutions or quoted/escaped brace
text in commands, and substitutions containing spaces or apparent heredoc words.
Plain brace text and dollar/backtick command text without `${` are positive
controls. See the [guard grammar](../../../docs/runbooks/ci-pipeline.md#base-image-heredoc-grammar)
for the exact restrictions. The optional oracle always checks `instructions`,
including cases with `guard_error`; it never treats the guard's rejection as a
BuildKit failure.

In particular, the unquoted `${FOO:- <<FAKE}` command makes BuildKit discover an
additional heredoc with delimiter `FAKE}`. The corpus includes both its
unterminated form and a form supplying both bodies. The guard rejects either
header immediately; the quoted form does not introduce that extra body.

Syntax reference: [BuildKit v0.26.1](https://github.com/moby/buildkit/blob/v0.26.1/frontend/dockerfile/parser/parser.go).
