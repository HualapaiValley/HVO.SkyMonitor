# Retained ISS CSV/OMM capture fixture

The orbital elements for NORAD 25544 come from US Space Force / Space-Track
public GP data distributed by CelesTrak. The source is the ISS row in the
[retained October 2026 snapshot](../../orbital-prediction-v1/virtual-camera/snapshots/six-facts/manifest.json).
The fixture transcribes its OMM fields to `source.csv` offline. The UTC in
the manifest records the original retrieval. Coverage is limited to this row.

`manifest.json` pins the fixture CSV bytes and the normalized `records.json`.
The fixture parser and normalizer identities describe this offline mapping.
CameraAgent's archive integration test installs all three files, predicts
the five-second exposure from the local store and checks the replay checksum
after restart with a changed rig and site.
