# D3-Celestial Constellation Topology

The embedded constellation figure resource is a deterministic derivative of
D3-Celestial `v0.7.32`, copyright Olaf Frohn and licensed BSD-3-Clause. The
artwork represents one conventional set of stick figures; the International
Astronomical Union standardizes constellation names and boundaries, not these
line choices.

## Pinned Evidence

- Upstream: https://github.com/ofrohn/d3-celestial/tree/v0.7.32
- Constellation lines source SHA-256:
  `294f66bef5d5cf50b1e17f16d2efa1d97a15131612c68dd935adef6e7373e13c`
- HIP lookup source SHA-256:
  `8e76cd774d38f8d232cfccfb0b72d6fba5832d36eeff1f514e46c25423e2ecb8`
- Generated topology SHA-256:
  `70c253a00e0909ae0236dec0411afe837ebf8e493b2be7f84373b63c95c91621`
- Preprocessing version: `hip-coordinate-map-v1`
- Result: 88 constellation identifiers and 743 ordered segments.

## Reproduction

Run:

```bash
bash scripts/catalog/build-d3-constellation-topology.sh \
  src/HVO.SkyMonitor.Astronomy/Data/d3-celestial-v0.7.32-topology.tsv
```

The script downloads both pinned source files, verifies their hashes, maps each
line endpoint's exact J2000 coordinate pair to the matching HIP identifier, and
fails unless every endpoint resolves, the expected counts match, and the
serialized resource has the pinned generated SHA-256. Runtime recomputes that
same embedded-resource hash before checking production topology endpoints and
joining those HIP identifiers to catalog objects. No network or source JSON
parsing occurs during application startup.

## HYG 4.2 Compatibility

The 743 D3 segments contain 757 distinct HIP endpoints. HYG 4.2 resolves 756 of
them; HIP `55203`, used only by the `UMA` segment from HIP `55219`, is absent
from the pinned HYG source. Production startup verifies the complete endpoint
set and fails unless this is the only unresolved HIP. Scene construction omits
that single segment rather than fabricating a star outside the HYG provenance
chain. Any future source/topology version must review this compatibility rule.
