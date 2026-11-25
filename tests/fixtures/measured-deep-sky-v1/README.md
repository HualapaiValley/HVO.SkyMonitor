# Independent extended-source oracle for #1926

Frozen before the extractor. The independent #1926 review in the assignment
permits a documented equivalent to the pending #524 sensor renderer. This
fixture uses analytic diffuse profiles, controlled point/line confounders,
independent sample noise and known translations. Code units are synthetic;
these data establish no absolute photometry, morphology or sensitivity claim.

Run `python3 oracle.py <directory>` and compare the emitted manifest. Binary
series are five 20 s exposures in slot order, each with contiguous little-endian
uint16 R, G, B planes, 96 × 96. Translations are defined by the generator in
pixel-edge coordinates. Register to slot 2. The fixture driver carries its own
translations; source location and expected identities belong to the scorer.
The held-out seed and source placement differ from the development case.

Predetermined outcomes:

- The correctly registered five-exposure diffuse series has one qualified
  association if a unique projected object falls within 2.0 pixels of the
  measured centroid. Radial second moment of the measured core lies between
  1.8 and 5.5 pixels RMS. Positive net signal in a centered 11 × 11 aperture
  lies between 500 and 2000 stack code units above local background. A
  single-exposure input is outside this post-stack acceptance.
- The paired blank, PSF, two-PSF blend, long trail and coverage step yield
  zero reportable associations with the same catalog target. For the seam
  fixture, suppress two slots on the bright side of the step. Also run a
  positive with fewer than three slots throughout the source and background.
  This case records insufficient support.
- A mirror transform or independently incompatible reference evidence yields
  no association. Two plausible canonical object IDs remain ambiguous.
  Aliases of the same canonical object identify one object.
- A normal retained short sequence is a separate corpus negative. Replay
  positive/negative outcomes after archive reopening from exact retained
  payloads. This oracle supplies no provenance for the ordinary capture.
