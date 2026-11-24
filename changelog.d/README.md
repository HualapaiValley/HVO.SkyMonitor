# Changelog fragments

Add one fragment for a PR that changes what operators or users notice. Use a
unique issue number or PR slug and a lowercase section in the filename, such as
`1234.fixed.md` or `camera-setup.changed.md`. The sections are `added`,
`changed`, `deprecated`, `removed`, `fixed`, and `security`.

Write one Markdown bullet and finish it with the issue number. Use two spaces
to indent continuation lines and a final newline. Example for `1234.fixed.md`

```markdown
- The installer now reports when a camera needs a restart after an upgrade. (#1234)
```

Use `./scripts/release:changelog --check` before committing. For a final
release, follow the [release runbook](../docs/runbooks/releases.md#cutting-a-release).

When an in-flight PR next needs a conflict resolution or correction, move its
Unreleased item from `CHANGELOG.md` into a fragment of the same section. Keep
the base branch's changelog entries, validate the fragment, and commit the
conversion with the correction.
