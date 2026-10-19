# Retained SGP4 software source

The production numerical core is the C# implementation from
[aholinch/sgp4 at 552cb1489a52c3023ae70cb6c7e239e84c5950fe](https://github.com/aholinch/sgp4/tree/552cb1489a52c3023ae70cb6c7e239e84c5950fe),
maintained locally by HVO after import. The upstream headers credit David
Vallado and coauthors and identify the underlying SGP4 work and history.
Only `src/csharp/SGP4.cs` and `src/csharp/ElsetRec.cs` were imported.
Their complete upstream headers are retained. The executable, TLE wrapper,
other helpers, SGP.NET, and AGPL CelesTrak C# source were not imported.

The upstream license is the Unlicense. Its complete unchanged text is
[SGP4-UNLICENSE.txt](SGP4-UNLICENSE.txt), SHA256
`88d9b4eb60579c191ec391ca04c16130572d7eedc4a86daa58bf28c6e14c9bcd`.

## Original and retained content pins

SHA256 values identify file bytes, including line endings. Retained paths are
relative to `src/HVO.SkyMonitor.Astronomy/Orbital/Propagation/`.

| File | Original upstream SHA256 | Retained adapted SHA256 |
| --- | --- | --- |
| `SGP4.cs` | `fd66e164f17ee23a424e517e131a59dad6f3217dc178ce06165d3b597ab4d419` | `12bedc539f4e1d058575b4e390e53d3ea3a9372dfce24fc78477b0a16c264434` |
| `ElsetRec.cs` | `ed45d790765fdd131a41e952efa12b38f83bb66cfec69d4b3719e16912c8b1de` | `d4fd8d3624e3b24c5c4a723ea0f139b34d98b6ba731f3214b3afcad6b323fddd` |

The HVO-authored `Sgp4ObjectPropagator.cs` adapter has SHA256
`2a2d3f00cc0e51c47d1af314d7330f5c6d6319d6dd8d3593e5f7ee6379bced35`.
It is repository code under the repository's license, rather than an upstream
third-party file. These three pins are inputs to the complete prediction
engine implementation identity. An upstream revision alone omits HVO's adapter
and adaptations; a platform-specific compiled DLL hash is not the retained
software implementation identity. The issue owner binds the complete engine,
including its coordinate, time, sampling and search implementation.

## Mechanical adaptations

- Normalize mixed CRLF/LF to LF and apply repository whitespace formatting.
  Add a two-line provenance pointer before each unchanged upstream header.
- Move both types from `sgp4` to
  `HVO.SkyMonitor.Astronomy.Orbital.Propagation` and make them internal.
- Make the utility-only `SGP4` class static and its `fmod` helper private
  (a static class cannot contain a protected member).
- Make the three private numeric constants `pi`, `twopi` and `deg2rad`
  `const` instead of mutable static fields. Their expressions are unchanged.
- Remove four compiler-diagnosed self-assignments in `sgp4`:
  `satrec.am = satrec.am`, `satrec.em = satrec.em`,
  `satrec.mm = satrec.mm` and `satrec.nm = satrec.nm`.
  These have no numerical effect and otherwise produce CS1717 under
  `-warnaserror`.
- Seal `ElsetRec`. Initialize `satid` to `string.Empty` and make the unused
  `intldesg` nullable. The adapter always overwrites `satid` with canonical
  catalog text. Add an internal `Copy()` using `MemberwiseClone`; all its
  existing fields are values or immutable strings, so evaluations share no
  mutable storage.

No analyzer suppression was added. After applying exactly these transformations
to the retained original files, comparison with the adapted files while
ignoring whitespace passes for both files. This checks that formatter changes
did not conceal another code or comment change.

## Algorithm corrections

None. In particular, upstream `initl` computes AFSPC `gsto1`, then discards it
and assigns `gstime(epoch + 2433281.5)` regardless of operation mode. That
behavior remains present; mode `a` still controls the deep-space periodic
branch. The full AFSPC/WGS72 vector pack passes the frozen acceptance limits
with this behavior. This is measured agreement within the declared limits,
not a claim of exact algorithm equivalence with another implementation.

The core also combines its split Julian epoch once inside `sgp4init`.
The adapter preserves midnight and subday ticks separately on input and uses
UTC tick differences for evaluation times. The inherited combination has not
been silently replaced. Future source upgrades or algorithm corrections need
their own reviewed diff, provenance pins and complete numerical qualification.
