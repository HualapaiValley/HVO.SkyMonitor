# SIMBAD Endpoint Coordinate Evidence

The constellation endpoint fixture uses SIMBAD ICRS coordinates at epoch and
equinox J2000. The records were retrieved on 2026-07-12 from SIMBAD4 release 1.8
using these identifier queries:

- `https://simbad.u-strasbg.fr/simbad/sim-id?Ident=HIP%2025336&output.format=ASCII`
- `https://simbad.u-strasbg.fr/simbad/sim-id?Ident=HIP%2065474&output.format=ASCII`
- `https://simbad.u-strasbg.fr/simbad/sim-id?Ident=HIP%2069701&output.format=ASCII`
- `https://simbad.u-strasbg.fr/simbad/sim-id?Ident=HIP%2027989&output.format=ASCII`

Retained coordinate rows:

| HIP | ICRS J2000 coordinate | Source |
| --- | --- | --- |
| 25336 | `05 25 07.86325 +06 20 58.9318` | `2007A&A...474..653V` |
| 65474 | `13 25 11.57937 -11 09 40.7501` | `2007A&A...474..653V` |
| 69701 | `14 16 00.8682760805 -06 00 01.969483331` | `2020yCat.1350....0G` |
| 27989 | `05 55 10.30536 +07 24 25.4304` | `2007A&A...474..653V` |

The decimal-degree values in `hualapai-fisheye-v1.json` are direct conversions
of these retained sexagesimal rows. A checksum of this file is part of the
validation contract.
