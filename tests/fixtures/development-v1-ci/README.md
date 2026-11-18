# Producer-name YAML regressions

These ten workflows are valid under actionlint 1.7.12. At `6fe1c805` the producer
contract accepts each one despite its parsed job mapping containing a reserved
producer. The corrected contract rejects every fixture. `r4-callee.yml` supports
the reusable-call cases and is not itself a negative fixture.

| Fixture | Review finding |
| --- | --- |
| `flow-sequence.yml` | O-M1.1: column-zero sequence close truncates jobs |
| `flow-map.yml` | O-M1.2: column-zero map close truncates jobs |
| `double-quoted-continuation.yml` | O-M1.3: double-quoted continuation truncates jobs |
| `single-quoted-continuation.yml` | O-M1.4: single-quoted continuation truncates jobs |
| `flow-sequence-caller.yml` | O-M1.5: truncation hides a reusable caller |
| `decoy-before-comment.yml` | O-M1.6: decoy precedes `jobs: # comment` |
| `decoy-before-quoted.yml` | S1 / O-M1.7: decoy precedes `"jobs":` |
| `decoy-after-comment.yml` | O-M2: decoy follows `jobs: # comment` |
| `decoy-after-quoted.yml` | O-M2: decoy follows `"jobs":` |
| `forged-header-caller.yml` | O-M3: quoted input forges a regex job boundary |

The contract reads their actual YAML mappings before checking rejection. Keep
these outside `.github/workflows`; they are offline fixtures, not CI producers.
