# Installer configuration-default generations

Frozen output of each configuration-default generation the CameraAgent installer ships
(`CameraConfigurationDefaults.Shipped`, Refs #1779). A completed installation is recognised by the canonical identity
of the `camera-module.json` its generation wrote, so a shipped generation's output must never change. A change to what
the installer writes is a new generation, with a new fixture here.

Every file is the `camera-module.json` its generation writes for the same answers: the installer's default location
(latitude 0, longitude 0, elevation 0, time zone `UTC`), lighting omitted, and application identity
`5afd133a-ddd2-4d1a-975b-a957c29784a5`. Each file is that output byte for byte, followed by one newline.

| File | Generation | Pipeline | Source |
| --- | --- | --- | --- |
| `installer-defaults-1.camera-module.json` | `installer-defaults-1` | 11 steps | Written by the installer at `35da16df` (the first parent of the merge of Refs #1722), whose configuration every release up to `v0.9.1-dev.11` also wrote. Captured by the 2026-11-10 closed-issue acceptance audit (F3, finding B) as the retained configuration of a completed install. |
| `installer-defaults-2.camera-module.json` | `installer-defaults-2` | 13 steps | Written by the installer at `d0244860` (after the merge of Refs #1722) for the same answers, captured by the same audit. Adds `CloudAssessment` and `CloudPresentation`. |

`CameraConfigurationDefaultsTests` regenerates each generation and requires it to equal its file.
