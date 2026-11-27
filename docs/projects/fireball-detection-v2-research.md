# Stream-Native Fireball Detection V2: Literature Survey, Source-Code Audit, and License Matrix

Status: research deliverable for [#143](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/143)
(`roadmap:RM-008`, `component:cameraagent`). Verification date: `2026-11-27`.
Companion design specification: [fireball-detection-v2-architecture.md](fireball-detection-v2-architecture.md).

## 1. Scope and Verification Methodology

This document records the cited literature survey, open-source code inspection,
license/redistribution compliance matrix, and reuse-versus-reimplement
decisions for standalone CameraAgent stream-native fireball and transient
detection (V2). It delivers the research foundation required by
[#143](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/143) without
implementing runtime code, altering the delivered V1 still-frame transient
subsystem ([#65](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/65),
[`fireball-transient-detection.md`](fireball-transient-detection.md)), or
claiming physical detection sensitivity or field false-positive rates.

### Standalone CameraAgent Boundary

- **In scope:** capture, volatile ring buffering, trigger and promotion logic,
  local validation, deterministic reprocessing, optional local/external AI
  assessment, timing provenance, and evidence export from one standalone
  CameraAgent instance.
- **Scientific background only:** multi-station triangulation, atmospheric
  trajectory estimation, dark-flight modeling, and heliocentric orbit inversion
  (e.g., GMN, FRIPON, NASA ASFN, CAMS, DFN/GFO, WMPL). These inform single-station
  timing precision, calibration metadata, and portable observation export seams,
  not a multi-station coordinator in CameraAgent.
- **Unchanged concurrent slices:** V1 still-frame transient processing (#65) and
  the separately activated bounded satellite/sky-context slice (#527–#530,
  #1391, #1402, #1640–#1644) keep their own specifications and gates.

### Evidence Discipline

Every GitHub repository below was inspected directly via the GitHub API on
`2026-11-27` to verify live repository status, primary `LICENSE`/`COPYING`
files, file headers, submodules, and latest push timestamps. Published papers
were verified by DOI or NTRS citation. Throughout this audit:

1. **Published methods** (mathematical formulations, signal-processing stages,
   compression layouts, and calibration principles described in peer-reviewed
   literature) are strictly separated from **reusable source code** available
   under a verified OSI-approved license.
2. **Repository code licenses**, **submodule/runtime dependency licenses**,
   **trained model/weight licenses**, and **codec patent encumbrances** are
   audited separately.
3. No benchmark number or license term is inferred when primary documentation is
   absent; unverified provenance is recorded explicitly as a compliance risk.
4. `HVO.SkyMonitor` currently carries no root open-source `LICENSE` file in the
   repository (all rights reserved by default unless designated otherwise by the
   owner, with third-party attributions tracked in
   [`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md)). Consequently,
   copyleft (`GPL-3.0`, `AGPL-3.0`) and non-commercial/non-compete source code
   cannot be copied into `src/` without an explicit legal and licensing
   decision.

---

## 2. Primary Source Profiles

### 2.1 Global Meteor Network (GMN) and RMS

- **Peer-reviewed methodology:** Vida, D., Šegon, D., Gural, P. S., Brown, P. G.,
  McIntyre, M. J. M., Dijkema, T. J., Pavletić, L., Kukić, P., Mazur, M. J.,
  Eschman, P., Roggemans, P., Merlak, A., & Zubović, D. (2021). *The Global
  Meteor Network – Methodology and first results.* Monthly Notices of the Royal
  Astronomical Society (MNRAS), 506(4), 5046–5074.
  <https://doi.org/10.1093/mnras/stab2008>
- **Source repository:** <https://github.com/CroatianMeteorNetwork/RMS>
- **Verified code status (2026-11-27):** Active (`pushed_at: 2026-11-26`,
  Python/Cython, 227 stars, not archived).
- **Verified license (by artifact):**
  - **RMS code:** `GPL-3.0` (`LICENSE` SHA `733c072369ca77331f392c40da7404c85c36542c`;
    source headers in `RMS/BufferedCapture.py` and `RMS/EventMonitor.py` state
    `GPL-3.0-or-later`).
  - **Bundled ML code & weights:** `RMS/MLFilter.py` derives from
    <https://github.com/fiachraf/meteorml> (also verified `GPL-3.0`, last pushed
    `2022-07-25`) and ships binary `.tflite` weights (`share/meteorml32.tflite`,
    `share/hyper_model.tflite`) without separate permissive model or
    training-image licenses.
  - **GMN high-level public data products:** High-level reduced outputs
    (radiants, orbits, trajectories, magnitudes, and masses) published at
    <https://globalmeteornetwork.org/data/> are released under **CC BY 4.0**
    (attribution required). Raw station image/video archives are not covered by
    that blanket CC BY 4.0 data grant unless separately licensed (unknown/separate
    image rights).
- **Architecture & acquisition:** Multi-process Python capture (`BufferedCapture`
  in `RMS/BufferedCapture.py`) reading H.264/H.265 RTSP IP cameras (commonly
  Sony IMX291/IMX307 boards at 25 fps, 1280×720) via GStreamer (`gi.repository.Gst`)
  with fallback to OpenCV (`cv2`). Supports dusk-to-dawn scheduling and daytime
  mode switching.
- **Buffering & compression:** Uses ping-pong shared-memory NumPy arrays
  (`array1`, `array2`) collecting 256-frame blocks (~10.24 s at 25 fps) and
  compressing each block via **Four-frame Temporal Pixel (FTP)** compression
  (`RMS/Compression.py`, `CompressionCy.pyx`) into an `FF` FITS file holding
  four 2D arrays (`maxpixel`, `maxframe`, `avepixel`, `stdpixel`).
- **Fireball preservation & trigger behavior:** Because 256-frame FTP summaries
  lose frame-by-frame intensity evolution for saturated/fragmenting fireballs,
  `RMS/VideoExtraction.py` runs an online bright-pixel extractor over the
  uncompressed block before discard, writing cropped multi-frame fireball
  streams (`FR*.bin`) alongside full-block `FF` files. Optional `EventMonitor.py`
  and `RawFrameSave.py` support continuous ring/event extraction.
- **Detector stages:** Two-stage classical + optional ML post-filter:
  1. Thresholding on `(maxpixel - avepixel) / stdpixel`, 3D spatial-temporal
     grouping (`Grouping3Dcy.pyx`), and Kernel-based Hough Transform (KHT) line
     detection (`RMS/Detection.py`).
  2. Optional `RMS/MLFilter.py` post-filter using LiteRT (`ai_edge_litert`) /
     `tflite_runtime` / TensorFlow Lite over extracted crops to reject clouds,
     insects, planes, and artifacts.
- **Timing & dropped-frame handling (distinguishing capture vs. archived timing):**
  - **Live capture timing:** Current RMS (`RMS/BufferedCapture.py#L545`) reads
    **per-frame GStreamer PTS**, applies online smoothing/regression to track
    frame timing, and **resets the regression when frame drops are detected**,
    complemented by RTSP reconnect diagnostics (`RtspProbeResult`). Separate
    clock-domain issues remain: camera-clock drift relative to host NTP and
    encoded IP-camera pipeline latency/jitter still require external calibration
    for sub-frame absolute accuracy.
  - **Archived `FF` block timing:** Each 256-frame `FF*.fits` summary records a
    block-start UTC timestamp (`start_time1`/`start_time2`) and per-pixel
    `maxframe` index, so downstream FTP reduction commonly reconstructs frame
    times from block start plus nominal FPS unless externally corrected.
- **Calibration & products:** Automated nightly star extraction (`CALSTARS`),
  platepar radial-polynomial astrometry, photometric zero-point calibration,
  `FF*.fits`, `FR*.bin`, `FTPdetectinfo*.txt`, and nightly archive tarballs.
- **Dependency risk:** Heavy Python/Cython/OpenCV/GStreamer/SciPy/TFLite stack;
  `GPL-3.0` prevents direct code reuse inside HVO.SkyMonitor.

### 2.2 FRIPON and FreeTure

- **Peer-reviewed methodology:** Colas, F., Zanda, B., Bouley, S., Jeanne, S.,
  Malgoyre, A., Birlan, M., Blanpain, C., Gattacceca, J., Jorda, L., Lecubin, J.,
  Marmo, C., Rault, J. L., Vaubaillon, J., Vernazza, P., et al. (2020).
  *FRIPON: a worldwide network to track incoming meteoroids.* Astronomy &
  Astrophysics (A&A), 644, A53.
  <https://doi.org/10.1051/0004-6361/202038649>
- **Source repositories:**
  - Upstream: <https://github.com/fripon/freeture>
  - DFN operational fork: <https://github.com/desertfireballnetwork/freeture_DFN>
- **Verified code status (2026-11-27):** Upstream `fripon/freeture` is C++, 58
  stars, not archived, but last pushed `2021-11-15` (low upstream GitHub
  activity); `desertfireballnetwork/freeture_DFN` last pushed `2023-02-13`.
  `fripon/Fripipe` (central processing) last pushed `2017-12-04`.
- **Verified license:** `GPL-3.0` (`COPYING` SHA `94a9ed024d3859793618152ea559a168bbcbb5e2`;
  file headers in `src/AcqThread.cpp`, `src/DetThread.cpp`, and
  `src/DetectionTemporal.cpp` declare `GPL-3.0-or-later`).
- **Architecture & acquisition:** Three-thread C++ daemon (`AcqThread`,
  `DetThread`, `StackThread`) built on Boost and OpenCV. Supports raw
  uncompressed all-sky cameras via GigE Vision (`CameraGigeAravis`,
  `CameraGigePylon`, `CameraGigeTis`), V4L2 (`CameraV4l2`), video files
  (`CameraVideo`), and image sequences; FRIPON stations typically deploy
  monochrome 1296×964 GigE all-sky cameras at 30 fps.
- **Buffering & trigger behavior:** Uses an in-memory `boost::circular_buffer<Frame>`
  protected by mutex/condition variable (`DetThread.cpp`) holding configurable
  pre-event history. When `DetectionTemporal` (frame-to-frame difference,
  dynamic mask update via `Mask`, local/global event ROI clustering in
  `LocalEvent`/`GlobalEvent`) triggers, `DetThread` waits for configurable
  post-event frames (`mWaitFramesToCompleteEvent`, `mNbWaitFrames`) and flushes
  the pre-event + event + post-event frames to disk as FITS 2D/3D cubes,
  stacked composites, and optional video.
- **Timing & dropped-frame handling:** Timestamps frames against the
  NTP-disciplined Linux system clock; Colas et al. (2020) describe typical
  **~20 ms NTP host clock accuracy** for FreeTure stations rather than a
  guaranteed network-wide absolute exposure-timing bound across all stations
  and camera readout paths. Separate scheduled long-exposure acquisitions (e.g.,
  every 10 minutes) provide astrometric/photometric star calibration frames.
- **Dependency risk:** Native C++/Boost/OpenCV/Aravis/CFITSIO dependencies,
  stagnant upstream GitHub maintenance since 2021, and `GPL-3.0` copyleft.
  Its architectural separation of acquisition, circular buffer, temporal
  detector, and periodic calibration stack is a strong conceptual reference.

### 2.3 NASA All Sky Fireball Network (ASFN) and ASGARD

- **Peer-reviewed / technical publication:** Cooke, W. J., & Moser, D. E.
  (2012). *The Status of the NASA All Sky Fireball Network.* Proceedings of the
  International Meteor Conference (IMC 2011, Sibiu), NASA Technical Reports
  Server (NTRS) Document ID `20120004179` (Report M12-1533).
  <https://ntrs.nasa.gov/citations/20120004179>
- **Software availability & license (by artifact):** The NASA NTRS *Public Use
  Permitted* determination applies **only to the published NTRS paper
  (`20120004179`)**, not to ASGARD software or network datasets. **ASGARD**
  (*All Sky and Guided Automatic Real-time Detection*, originally developed by
  R. Weryk at the University of Western Ontario for the Southern Ontario Meteor
  Network and adopted by NASA MEO) is **not published in an open, maintained
  public repository under a verified OSI-approved license** suitable for
  third-party reuse. Published literature describes its concepts; the software
  is not available as reusable open-source code for HVO.SkyMonitor.
- **Architecture & methodology (from published descriptions):** Autonomous
  Linux stations capturing continuous 30 fps all-sky monochrome video (originally
  analog NTSC/Watec cameras through frame grabbers, later upgraded to digital
  sensors). Performs real-time frame-difference/threshold candidate detection
  over a rolling memory buffer, writes event video + summary stills + timing
  logs on trigger, and uploads detections for automated multi-station trajectory
  and orbit calculation.
- **Takeaway:** Useful operational benchmark for single-station autonomous
  trigger-and-promote design and multi-station metadata interchange, but zero
  reusable code.

### 2.4 CAMS (Cameras for Allsky Meteor Surveillance) and Gural Detection Algorithms

- **Peer-reviewed methodology:**
  - Jenniskens, P., Gural, P. S., Dynneson, L., Grigsby, B. J., Newman, K. E.,
    Borden, M., Koop, M., & Holman, D. (2011). *CAMS: Cameras for Allsky Meteor
    Surveillance to establish minor meteor showers.* Icarus, 216(1), 40–61.
    <https://doi.org/10.1016/j.icarus.2011.08.012>
  - Gural, P. S. (2008). *Algorithms and Software for Meteor Detection.* Earth,
    Moon, and Planets, 102(1–4), 269–275.
    <https://doi.org/10.1007/s11038-007-9161-7>
- **Software availability & license:** **Published description only — no
  reusable OSI-licensed source code.** CAMS station capture and Gural's
  `FTP_Detect` / matched-filter detection executables have historically been
  distributed as closed-source compiled binaries to network participants under
  restricted research agreements rather than public OSI licenses.
- **Methodology highlights:** Originated **Four-frame Temporal Pixel (FTP)**
  block compression (256 frames compressed to `maxpixel`, `maxframe`,
  `avepixel`, `stdpixel`) and multi-stage clutter-suppressed detection
  (background subtraction, exceedance thresholding, Hough transform /
  matched-filter velocity-vector tracking) designed to achieve faint-meteor
  sensitivity while reducing overnight storage by ~64× for 8-bit video.
- **Limitations for bright fireballs:** Pure FTP compression collapses multiple
  bright pixels hitting the same cell across 256 frames into a single peak
  value and frame index, losing saturation/flare time-series detail unless
  paired with an uncompressed/native clip ring buffer (the gap later addressed
  by RMS `FR` files and FreeTure's frame ring).

### 2.5 MeteorDL

- **Source repository:** <https://github.com/satmonkey/MeteorDL>
- **Verified code status (2026-11-27):** Python prototype, 20 stars, not
  archived, last pushed `2021-09-11` (unmaintained for >5 years).
- **Verified license (by artifact):**
  - **Code:** Repository `LICENSE` is `MIT` (SHA
    `6c63ebdb0540932a2779c4065c5315efdb7a7f11`).
  - **Training images & committed model weights (unknown rights / compliance gap):**
    The repository commits pre-trained SSD MobileNet V2 FPNLite 640×640 weights
    (`tflite-real-640-4/model_full_integer_quant_edgetpu.tflite`,
    `tflite-real-640-4/saved_model/model_float32.tflite`,
    `trt_fp16_dir/saved_model/saved_model.pb`). The `MeteorDL` README describes
    retraining on **`~1,000` custom meteor images in Pascal VOC format** with
    augmentation, **without identifying any upstream source (such as GMN/RMS)**
    and with **unknown/undocumented image dataset rights, weight license, or
    model card**.
- **Architecture & buffering:** Captures RTSP/IP/USB/MIPI camera streams via
  OpenCV (FFmpeg or GStreamer backend). Maintains a ring buffer of frames (GPU
  ring buffer on CUDA PC; `dvg-ringbuffer` Cython CPU ring buffer on Jetson
  Nano and Raspberry Pi 4). Computes a **1-second `maxpixel` composite image**
  and feeds it to a TensorFlow 2 Object Detection / TFLite EdgeTPU model (640×640
  input) on CUDA GPU (tested GTX 1080), Jetson Nano (TensorRT FP16), or
  Raspberry Pi 4 + Google Coral Edge TPU USB.
- **Trigger & products:** On positive neural detection, extracts a configurable
  pre-event + post-event multi-second window from the ring buffer and encodes
  an MP4 event clip.
- **Limitations & risk:** End-to-end neural detection on 1-second maxpixel
  images lacks deterministic linear photometry, produces no astrometric/scientific
  stills or native bitstream preservation, relies on deprecated TF2 Object
  Detection API / Coral Edge TPU toolchain, has unverified model/dataset rights,
  and has been unmaintained since 2021. However, its **1-second maxpixel
  screening over a bounded ring buffer** is a clean, low-rate screening pattern.

### 2.6 Fast Meteor Detection Toolbox (FMDT)

- **Peer-reviewed publication:** Ciocan, C., Kandeepan, M., Cassagne, A.,
  Vaubaillon, J., Zander, F., & Lacassagne, L. (2023). *A new meteor detection
  application robust to camera movements.* GRETSI / arXiv preprint.
  <https://doi.org/10.48550/arXiv.2309.06027>
- **Source repository:** <https://github.com/alsoc/fmdt>
- **Verified code status (2026-11-27):** Actively maintained C/C++ project
  (`pushed_at: 2026-06-10`, 10 stars, not archived).
- **Verified license & submodule audit:**
  - `alsoc/fmdt`: `MIT` (`LICENSE` SHA `815d5a07141561ffca0cfdc21bdcba143d4e1408`).
  - Submodule `alsoc/ffmpeg-io` (`pushed_at: 2026-04-10`): `MIT` (invokes
    external `ffmpeg` CLI via pipes — same process boundary used by
    `HVO.SkyMonitor.Video.FFmpeg`).
  - Submodule `alsoc/nrc2` (`pushed_at: 2026-04-10`): `BSL-1.0` (Boost Software
    License 1.0, permissive).
  - Submodule `Mashpoe/c-vector` (`pushed_at: 2025-10-16`): `BSD-3-Clause`.
  - Submodule `aff3ct/streampu` (`pushed_at: 2026-11-25`): `MIT`.
  - Optional visualization links OpenCV (`Apache-2.0` since OpenCV 4.5); core
    detection does not require OpenCV.
- **Architecture & detector stages:** Dataflow task-graph pipeline (`StreamPU`)
  executing deterministic classical computer vision stages:
  1. Hysteresis thresholding (`sigma` / dual-threshold segmentation).
  2. Connected-components labeling (CCL) and morphological feature extraction.
  3. Inter-frame motion compensation (robust to jittering/airborne mounts,
     optional/simplifiable for fixed ground cameras).
  4. k-NN ROI association and multi-frame track classification (star, meteor,
     noise).
- **Published embedded performance evidence (attributed per source):**
  - **Ciocan et al. (2023, `arXiv:2309.06027`):** Report **26 fps at ~6 W** on
    a Raspberry Pi 4 and an **84% aggregate detection rate** on their evaluated
    meteor campaign video workload.
  - **`alsoc/fmdt` repository README:** Self-reports **30 fps at ~4 W** instant
    power on a Raspberry Pi 4 (`1.5 GHz`) on an HD video sequence (summary note
    without a full benchmark table or detection-rate table).
  - Hardware throughput and detection rates on HVO's own sensor geometry and
    operating conditions remain open target-hardware qualification questions
    (`W7–W12`).
- **Limitations & relevance:** Designed primarily as a stream/file offline or
  airborne CLI tracker (8-bit video input via `ffmpeg-io`, no native FITS/Mono16
  linear astrometry or ring-buffered Silo durability). Crucially, it is the
  **strongest verified permissively licensed (`MIT`) classical C/C++ reference**
  proving that deterministic, CPU-only connected-component + k-NN stream
  tracking runs comfortably in real time on Raspberry Pi–class ARM64 hardware
  without requiring a GPU or NPU.

### 2.7 Desert Fireball Network (DFN) and Global Fireball Observatory (GFO)

- **Peer-reviewed methodology:**
  - Howie, R. M., Paxman, J., Bland, P. A., Towner, M. C., Cupak, M., Sansom,
    E. K., & Devillepoix, H. A. R. (2017). *How to build a continental scale
    fireball camera network.* Experimental Astronomy, 43(3), 237–266.
    <https://doi.org/10.1007/s10686-017-9532-7>
  - Devillepoix, H. A. R., Cupák, M., Bland, P. A., Sansom, E. K., Towner,
    M. C., Howie, R. M., et al. (2020). *A Global Fireball Observatory.*
    Planetary and Space Science, 191, 105036.
    <https://doi.org/10.1016/j.pss.2020.105036>
- **Source repositories:** <https://github.com/desertfireballnetwork>
- **Verified license audit (2026-11-27):** Mixed per-repository licensing:
  - Permissive `MIT`: `RAW2FITS`, `dtf_triangulation`, `DFN_darkflight`,
    `dfn_vo_cli_tools`, `flictl`, `alpha_beta_modules`.
  - Copyleft `GPL-3.0`: `freeture_DFN` (video camera fork of FreeTure), `RMS`
    fork.
  - **No license file (`null` — all rights reserved / unusable without grant):**
    `data-pipeline_de-bruijn`, `GFE_converters`, `particle_filter`,
    `videoFireball` (archived), `Desert-Fireball-GUI_classic`.
- **Engineering lessons:** Demonstrates that sub-millisecond timing accuracy
  requires explicit hardware/GNSS clock discipline (DFN embeds GNSS-locked De
  Bruijn liquid-crystal shutter codes into 30 s DSLR still exposures, whereas
  stream-native cameras require monotonic + GNSS/PPS/NTP clock-quality
  provenance per frame). Also illustrates the operational value of retaining
  high-dynamic-range still frames alongside video triggers for bright fireball
  astrometry and photometry.

### 2.8 AllSky7 (AMS / Mike Hankey)

- **Operational reference:** <https://allsky7.net> / American Meteor Society;
  contributor repository <https://github.com/mikehankey/allsky7_contrib>.
- **Verified license (2026-11-27):** `mikehankey/allsky7_contrib` carries a
  custom **`AllSky7 Contributor License Agreement (AS7-CLA v1)`** restricting
  public use to **non-commercial purposes only**, reserving commercial rights
  exclusively to AllSky7, and containing an explicit **non-compete clause**
  (Section 4). The core AllSky7 station suite is distributed under network
  participation terms, **not an OSI-approved open-source license**.
- **Architecture lessons (observation only — zero code reuse):** Uses dual
  RTSP streams (HD 1920×1080 + SD 704×576 at 25 fps) written continuously into
  **rolling 1-minute MP4 segments** on local disk as a persistent disk ring
  buffer. Detection runs asynchronously on the lighter SD stream, then promotes
  matching HD/SD event crops, full event clips, and stacked still composites
  while aging out non-event 1-minute segments. This disk-ring pattern informs
  CameraAgent's optional bounded NVMe spill tier for high-bitrate streams
  (Section 4.3 of the companion architecture doc), while keeping zero code or
  protocol dependency on AllSky7.

### 2.9 WesternMeteorPyLib (WMPL) and Monte Carlo Trajectory Methods (Scientific Background)

- **Peer-reviewed methodology:** Vida, D., Gural, P. S., Brown, P. G.,
  Campbell-Brown, M., & Wiegert, P. (2020). *Estimating trajectories of
  meteors: an observational Monte Carlo approach – I. Theory.* MNRAS, 491(2),
  2688–2705. <https://doi.org/10.1093/mnras/stz3160>
- **Source repository:** <https://github.com/wmpg/WesternMeteorPyLib>
- **Verified status & license (2026-11-27):** Actively maintained Python
  library (`pushed_at: 2026-11-26`, 46 stars), **`MIT` License** (SHA
  `c8184dd845ecc47e1d4cc037bef0ecebd9a824e8`).
- **Role in #143 (scientific background only):** Out of scope for standalone
  CameraAgent runtime execution, but defines the scientific data requirements a
  single-station export must satisfy if ever consumed by downstream multi-station
  solvers: exact per-frame UTC exposure midpoints/bounds, explicit timing
  uncertainty/clock-sync facts, sub-pixel centroid coordinates with optical
  calibration/distortion model provenance, and unsaturated vs. saturated
  photometry flags.

### 2.10 Deep Learning Classification, Explainability, and Embedded Inference Literature

- **Peer-reviewed methodology:** Peña-Asensio, E., Trigo-Rodríguez, J. M.,
  Grèbol-Tomàs, P., Regordosa-Avellana, D., & Rimola, A. (2023). *Deep machine
  learning for meteor monitoring: Advances with transfer learning and
  gradient-weighted class activation mapping.* Planetary and Space Science,
  238, 105802 (`arXiv:2310.16826`).
  <https://doi.org/10.1016/j.pss.2023.105802>
- **Verified findings from Peña-Asensio et al. (2023, `arXiv:2310.16826`):**
  - Evaluated transfer-learned CNN candidate-image classifiers on Spanish Meteor
    Network (**SPMN**) candidate images, reporting high meteor-class precision
    (up to 98% on evaluated SPMN candidate sets) when distinguishing meteors
    from false triggers such as clouds, insects, aircraft, and static artifacts.
  - Demonstrated **Grad-CAM** (combined with early convolutional activations)
    for spatial localization and human-inspectable visual attribution on those
    candidate images.
  - **Scope limit of cited paper:** Peña-Asensio et al. (2023) did **not**
    benchmark two-stage classical + ML pipelines against continuous end-to-end
    neural video detectors, did **not** evaluate Raspberry Pi CPU fallback
    latency/thermals, and did **not** establish saturated-fireball recall
    guarantees.
- **Resulting HVO V2 architectural design choices and open qualification questions:**
  - HVO selects a **two-stage architecture** (mandatory deterministic Stage 1
    classical candidate generation + optional asynchronous Stage 2 AI
    assessment with append-only `transient-assessment-producer-v2` records) as
    an **HVO engineering and scientific-integrity decision**, motivated by
    preserving deterministic linear residual measurement on raw streams and
    decoupling acquisition from optional ML execution.
  - **Open qualification questions (to be measured in `W7–W12`, not assumed
    from literature):** actual Raspberry Pi 5 CPU Stage 2 inference latency and
    thermal headroom, Stage 1 vs. Stage 2 recall on saturated/fragmenting
    fireballs, and precision/recall tradeoffs on HVO optics and sky conditions.

---

## 3. Comprehensive Comparative Matrix

Every cell distinguishes verified repository facts (`2026-11-27`) from published
paper descriptions.

| Dimension | GMN / RMS | FRIPON / FreeTure | NASA ASFN / ASGARD | CAMS / `FTP_Detect` | MeteorDL | FMDT (`alsoc/fmdt`) | DFN / GFO | AllSky7 (AMS) | WMPL (Background) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Primary reference** | Vida et al. (2021), `10.1093/mnras/stab2008`; GitHub | Colas et al. (2020), `10.1051/0004-6361/202038649`; GitHub | Cooke & Moser (2012), NTRS `20120004179` | Jenniskens et al. (2011), `10.1016/j.icarus.2011.08.012`; Gural (2008) | GitHub `satmonkey/MeteorDL` | Ciocan et al. (2023), `arXiv:2309.06027`; GitHub | Howie et al. (2017); Devillepoix et al. (2020) | `allsky7.net`; `allsky7_contrib` | Vida et al. (2020), `10.1093/mnras/stz3160`; GitHub |
| **Architecture** | Multi-process Python/Cython edge station daemon | 3-thread C++ daemon (Acq/Det/Stack) + central server | Autonomous Linux station daemon + central solver | Windows/Linux capture + batch detector + central solver | Python real-time detector script (PC/Jetson/Pi) | C/C++ StreamPU dataflow task graph CLI | Autonomous DSLR + GNSS LC-shutter observatory | Ubuntu Mini-PC 7-camera dome + AMS cloud sync | Offline Python trajectory/orbit physics library |
| **Acquisition formats** | RTSP H.264/H.265 (GStreamer/OpenCV), 8-bit 1280×720 @ 25 fps typical | Raw GigE (Aravis/Pylon/TIS), V4L2, USB, video; 8/12-bit 1296×964 @ 30 fps | Monochrome all-sky 30 fps video (analog grabber / digital) | 8-bit analog/digital video @ 25–30 fps | RTSP/USB/MIPI via OpenCV (FFmpeg/GStreamer) 720p/1080p | 8-bit video via `ffmpeg-io` (FFmpeg pipe) | 14-bit DSLR RAW still exposures (~30 s) + video (`freeture_DFN`) | Dual RTSP H.264 (HD 1080p + SD 576p) @ 25 fps | Consumes reduced astrometric/photometric text/pickle files |
| **Buffering** | Ping-pong shared-memory 256-frame blocks (~10.24 s) | In-memory `boost::circular_buffer<Frame>` | In-memory rolling frame buffer | 256-frame RAM block compressed to 4-plane FTP | CUDA GPU ring (PC) or `dvg-ringbuffer` CPU ring (Jetson/Pi) | Streaming pipeline frames (no long pre-event ring) | On-disk RAW capture spool + event pruning | Disk ring of rolling 1-minute MP4 segments | N/A (offline post-processing) |
| **Trigger behavior** | Continuous 256-frame FTP + online bright-pixel `FR` fireball extractor | Temporal frame-diff + ROI clustering -> wait post-trigger frames -> flush ring | Real-time frame-diff/threshold -> save event clip + summary | Post-block detection on FTP maxpixel/maxframe planes | 1-second maxpixel -> TF2/TFLite object detector -> save pre/post clip | Continuous per-frame CCL + motion comp + k-NN tracker | Post-exposure streak detection on 30 s stills | Async detection on SD 1-min MP4 -> promote HD+SD clips | N/A |
| **Timing model** | Capture: per-frame GStreamer PTS with online smoothing/regression (`RMS/BufferedCapture.py#L545`); archived `FF` blocks store block-start UTC + `maxframe` index (commonly reduced with nominal FPS) | Host NTP UTC per frame; Colas et al. (2020) describe typical ~20 ms NTP host clock accuracy (not a guaranteed network-wide absolute exposure bound) | NTP/GPS host clock per video frame | NTP/GPS-stamped block start + frame index | Host wall clock / video timestamps | Frame index in input stream | GNSS-disciplined LC shutter De Bruijn timecode (sub-ms relative) | NTP/GPS + 1-min MP4 timestamps | Requires per-frame UTC + timing uncertainty inputs |
| **Dropped-frame handling** | `RMS/BufferedCapture.py#L545` resets PTS smoothing/regression on detected drops + `RtspProbeResult` reconnects; camera-clock drift and IP latency remain separate calibration concerns | Acquisition thread logs/handles drops; ring preserves actual captured frames | Frame drop monitoring in capture loop | Frame-index gaps break nominal FPS assumption if undetected | Best-effort OpenCV capture loop | Sequential decoded frame stream | Shutter pulse sequence encodes gaps directly | Missing MP4/frame gaps handled at segment boundary | Models timing offsets between stations via Monte Carlo |
| **Calibration** | Nightly star `CALSTARS` + `platepar` radial polynomial + photometry | Periodic long-exposure FITS captures + central astrometry | All-sky star plate astrometry | Star field calibration per camera | Static mask BMP only (no astrometry/photometry) | None in C core (`pyFMDT` adds astrophotometry) | Deep-star astrometry on high-res RAW stills | Star-plate astrometry via AMS pipeline | Consumes externally calibrated RA/Dec & magnitudes |
| **Event products** | `FF*.fits` (4-plane), `FR*.bin` (cropped fireball video), `FTPdetectinfo`, tarballs | FITS 2D/3D frame cubes, stacked FITS/PNG, optional video | Event video clip, composite still, astrometry logs | `FF*.bin` (4-plane FTP), detection logs | MP4 event video clip only | Track logs, bounding boxes, annotated MP4 (`fmdt-visu`) | RAW/FITS stills, cropped event stacks, timecode tables | HD/SD MP4 event clips, stacked composites, JSON | Solved trajectory pickles, orbit tables, plots |
| **Detector stages** | Threshold + 3D grouping + KHT + optional `MLFilter` (TFLite/LiteRT) | Dynamic mask + frame diff + ROI clustering (`DetectionTemporal`) | Frame diff + thresholding + linear motion validation | FTP compression -> matched filter / Hough (`FTP_Detect`) | Single-stage neural object detector on 1 s maxpixel | Hysteresis -> CCL -> motion comp -> k-NN track classifier | Classical streak + CNN verification (GFO literature) | Classical motion/streak + ML rejection | Multi-station Monte Carlo solver (background) |
| **Multi-station (background)** | Central GMN server runs WMPL daily | Central FRIPON server triangulates + meteorite search | NASA MEO central trajectory/orbit pipeline | CAMS central coincidence + orbit catalog | None (standalone or feeds RMS) | Designed for multi-camera airborne/space campaigns | GFO central triangulation + dark-flight meteorite recovery | AMS central multi-station solver | Core Monte Carlo trajectory & orbit engine |
| **Performance evidence** | Vida et al. (2021) report **>450 GMN cameras across 30 countries** (mid-2021, RPi 3/4-class @ 720p 25 fps) | Proven on 100+ Debian mini-PCs @ 1296×964 30 fps (Colas et al. 2020) | Operational US all-sky network (Cooke & Moser 2012) | Operational multi-camera clusters (Jenniskens et al. 2011) | Tested on GTX 1080, Jetson Nano 4 GB, RPi4 + Coral TPU (README, no formal benchmark paper) | **Ciocan et al. (2023):** 26 fps at ~6 W on RPi4, 84% aggregate detection.<br>**FMDT README:** 30 fps at ~4 W on RPi4 (1.5 GHz) HD sequence. | Operational continental/global network (Howie et al. 2017; Devillepoix et al. 2020) | Operational Ubuntu Mini-PCs running 7× dual-stream cameras | Benchmarked on simulated & real multi-station datasets (Vida et al. 2020) |
| **Maintenance status (2026-11-27)** | **Active** (`pushed_at: 2026-11-26`) | **Low/Stagnant** on GitHub (`fripon/freeture`: `2021-11-15`; `freeture_DFN`: `2023-02-13`) | Institutional (no public GitHub repo) | Institutional / private binaries | **Unmaintained** (`2021-09-11`) | **Active** (`pushed_at: 2026-06-10`; `streampu`: `2026-11-25`) | Mixed active/archived repos (`2019–2026`) | Active network (`allsky7_contrib`: `2024-09-28`) | **Active** (`pushed_at: 2026-11-26`) |
| **Dependency risk** | Python + Cython + GStreamer + OpenCV + LiteRT/TFLite + GPL | C++ + Boost + OpenCV + Aravis/Pylon + CFITSIO + GPL | Closed/hardware-specific legacy stack | Closed binaries, Windows/legacy focus | Deprecated TF2 Object Detection API + Coral EdgeTPU + unverified weights | C/C++ + CMake + external `ffmpeg` CLI + optional OpenCV | Mixed/missing licenses across repos | Closed/non-commercial ecosystem | Python scientific stack (SciPy/Astropy/Numba) |
| **Software / data / model license** | **Code:** `GPL-3.0` (`GPL-3.0-or-later`). **ML:** `meteorml` `GPL-3.0`; `.tflite` weights & training images lack separate permissive license. **High-level GMN data** (radiants/orbits/magnitudes/masses at `globalmeteornetwork.org/data/`): **`CC BY 4.0`** (raw imagery separate/unknown). | **Code:** `GPL-3.0` (`GPL-3.0-or-later`). | **NTRS Paper only:** Public Use Permitted. **ASGARD Code & data:** Not available under public OSI license. | **Papers:** Academic. **Code:** Closed/restricted binaries; no OSI license. | **Code:** `MIT`. **Training images (~1,000 custom VOC) & committed weights:** Unknown/undocumented rights. | **Code:** `MIT`. **Submodules:** `ffmpeg-io` (`MIT`), `nrc2` (`BSL-1.0`), `c-vector` (`BSD-3-Clause`), `streampu` (`MIT`). | **Mixed:** `RAW2FITS`/`dtf_triangulation` `MIT`; `freeture_DFN` `GPL-3.0`; `data-pipeline_de-bruijn` & `GFE_converters` unlicensed. | **Restrictive:** `AS7-CLA v1` (non-commercial, non-compete); not OSI. | **Code:** `MIT`. |

---

## 4. License, Redistribution, Codec, and Model Compliance Matrix

### 4.1 Software and Runtime Dependency Compliance

| Component / Candidate | Verified License | Permitted Use in HVO.SkyMonitor | Prohibited Actions / Constraints |
| --- | --- | --- | --- |
| **GMN / RMS** (`CroatianMeteorNetwork/RMS`) | **Code:** `GPL-3.0` (`GPL-3.0-or-later`); **high-level data** (`globalmeteornetwork.org/data/`): `CC BY 4.0`; **raw imagery & `.tflite` weights:** separate / undocumented | Study published Vida et al. (2021) paper and architectural concepts; high-level GMN tabular data (`CC BY 4.0`) may be used with attribution where applicable. | **Do NOT copy RMS source files, Cython modules, `.tflite` weights, or unverified raw station imagery** into `HVO.SkyMonitor`. |
| **FRIPON / FreeTure** (`fripon/freeture`, `freeture_DFN`) | `GPL-3.0` (`GPL-3.0-or-later`) | Study Colas et al. (2020) paper and ring-buffer/thread architecture only. | **Do NOT copy or link C++ code** into `HVO.SkyMonitor`. |
| **fiachraf/meteorml** | `GPL-3.0` | Study ML feature ideas only. | **Do NOT copy code or trained weights.** |
| **AllSky7** (`mikehankey/allsky7_contrib`) | Custom `AS7-CLA v1` (non-commercial, non-compete) | High-level architectural comparison only. | **Strictly prohibited:** no code copying, derivative works, or CLA adoption. |
| **NASA ASGARD & CAMS `FTP_Detect`** | **NTRS Paper `20120004179` only:** Public Use Permitted; **ASGARD & CAMS code/binaries:** closed / institutional / no OSI grant | Clean-room implementation of peer-reviewed published mathematics (FTP 4-plane summary, matched-filter/Hough concepts). | **Do NOT incorporate binaries or non-public source**, and do not treat the NTRS paper determination as a license for ASGARD software or data. |
| **MeteorDL** (`satmonkey/MeteorDL`) | **Code:** `MIT`; **~1,000 custom VOC training images & committed weights:** unknown/undocumented rights | Architectural reference (1 s maxpixel screening + ring buffer). | **Do NOT ship or embed `tflite-real-640-4` / `trt_fp16_dir` weights** without verified dataset/model licensing. |
| **FMDT** (`alsoc/fmdt` + submodules) | `MIT` + `BSL-1.0` + `BSD-3-Clause` | Permissive reference; could be invoked as an optional external CLI/runner prototype under `#140` or clean-room adapted in C# with attribution in `THIRD-PARTY-NOTICES.md`. | Must retain MIT/BSL-1.0/BSD-3-Clause notices if any code is vendored; optional OpenCV (`Apache-2.0`) avoided in core. |
| **WMPL** (`wmpg/WesternMeteorPyLib`) | `MIT` | Scientific reference for single-station export fields; optional offline analysis outside CameraAgent. | Out of scope for standalone CameraAgent runtime. |
| **FFmpeg CLI** (operator/container installed, already used via `src/HVO.SkyMonitor.Video.FFmpeg`) | Core `LGPL-2.1+`; optional GPL components (e.g., `libx264`/`libx265`) require `--enable-gpl`; upstream NVENC via `ffnvcodec` (`FFmpeg/configure#L3592`) loads the NVIDIA driver dynamically and **does not by itself force `--enable-nonfree`**, whereas linking proprietary SDKs (e.g., `--enable-cuda-nvcc`, `--enable-libnpp`, `libfdk_aac`) does force `--enable-nonfree` | Invoke external `/usr/bin/ffmpeg` and `/usr/bin/ffprobe` across an OS process boundary (matching `FFmpegOptions` in `src/HVO.SkyMonitor.Video.FFmpeg/FFmpegOptions.cs`), never statically link GPL/nonfree FFmpeg libraries into .NET assemblies. | **Redistribution & patent gate:** Base redistribution decisions on the **actual `ffmpeg -buildconf` flags and linked components** of any shipped binary/image (`LGPL` vs `GPL` vs `--enable-nonfree`), avoid bundling `--enable-nonfree` builds, and account for H.264/H.265 patent exposure (prefer passthrough bitstream copy for native clips, distro-supplied packages or VAAPI/royalty-free codecs where redistributed). |
| **ONNX Runtime** (`Microsoft.ML.OnnxRuntime`) | `MIT` (core & managed .NET bindings); vendor EPs (`CUDA`, `TensorRT`, `OpenVINO`, `DirectML`, `CoreML`) carry separate vendor redistributable terms | Preferred optional local AI inference engine for V2 Stage 2 classifiers (CPU EP built-in; GPU/NPU EPs optional via `#140` certified extension packages or operator-installed runtimes). | Never bundle proprietary CUDA/TensorRT runtimes inside base CameraAgent images without explicit vendor redistribution review; keep CPU EP as deterministic baseline. |
| **ZWO ASI SDK** (`libASICamera2.so`, already wrapped in `src/HVO.SkyMonitor.CameraAgent.Modules.Zwo`) | Proprietary vendor binary SDK | Operator-supplied or separately qualified native library loaded dynamically via `AsiNativeApi`, matching delivered `#265`–`#267` boundary. | Extending `AsiNativeApi` with video-mode entry points (`ASIStartVideoCapture`, `ASIGetVideoData`, `ASIStopVideoCapture`, `ASIGetDroppedFrames`) requires no new license category beyond existing ZWO runtime loading. |

### 4.2 Model Weights and Training Data Compliance Rule

Unlike source code, a neural network checkpoint carries **two independent legal
layers**: (a) the weight license itself and (b) the license and redistribution
rights of the training dataset (distinguishing, for example, GMN's `CC BY 4.0`
high-level tabular data at `globalmeteornetwork.org/data/` from raw station
imagery with separate/unverified rights, FRIPON archives, or synthetic
VirtualSky renders). For V2:

1. **No third-party weights without provenance:** CameraAgent will never ship
   or certify model files whose training dataset license and weight redistribution
   license are undocumented (ruling out `MeteorDL`'s committed `.tflite`/SavedModel
   files and `RMS`'s GPL-entangled `.tflite` weights).
2. **Safe serialization format only:** In-process/local inference accepts only
   static graph/tensor formats (`ONNX` `.onnx` with SHA-256 verification and
   canonical preprocessing metadata). Arbitrary code-executing formats (Python
   `pickle`, untrusted TorchScript, Keras `Lambda` layers) are unconditionally
   rejected.
3. **Synthetic-first baseline:** Initial V2 qualification fixtures and any
   prototype classifier weights must use deterministic `VirtualSky` scenarios
   (extending `src/HVO.SkyMonitor.Imaging/VirtualTransientScenario.cs`) or
   explicitly licensed public datasets with recorded `THIRD-PARTY-NOTICES.md`
   entries.

---

## 5. Reuse-versus-Reimplement Decision Ledger

| Capability / Candidate | Decision | Rationale and Target Owner |
| --- | --- | --- |
| **Four-frame Temporal Pixel (FTP) / 1 s Max-Pixel Screening** (CAMS / GMN / MeteorDL published method) | **Clean-room reimplement in C#** | Simple, deterministic single-pass `Span<ushort>`/`Span<byte>` arithmetic (`maxpixel`, `maxframe`, running mean/MAD) over Mono16/Mono8/RGGB16 luminance in `HVO.SkyMonitor.Imaging`. Avoids GPL/Python/Cython dependencies and guarantees byte-identical reproducibility across x64 and ARM64. |
| **Classical Candidate Extraction & Multi-Frame Tracking** (V1 `Linear16TransientExtraction` + FMDT CCL/k-NN + Gural/RMS 3D grouping concepts) | **Extend existing HVO C# Imaging/Processing core** | `HVO.SkyMonitor.Imaging` already owns deterministic residual qualification, saturation bridging, connected components, fragment grouping, and profile measurement (`src/HVO.SkyMonitor.Imaging/Linear16TransientExtraction.cs`). Adding temporal multi-frame track linking and 1 s chunk screening extends one verified codebase with zero native-interop or GPL risk. |
| **Bounded Volatile Ring Buffer** (FreeTure `boost::circular_buffer` / MeteorDL ring / AllSky7 disk segments) | **Clean-room implement in C# (`CameraAgent.Common`)** | Native C# bounded `ArrayPool`/pinned slab RAM ring + optional NVMe segment spill integrates directly with `SiloFilePayloadSpool`, `CameraAgent:Silo:MaximumStagedBytes`, and deterministic cancellation/crash recovery without Boost/OpenCV/Python runtimes. |
| **Encoded Stream Demux/Decode & Display Clip Encode** (RTSP/H.264/H.265 + MP4 export) | **Reuse HVO subprocess boundary (`HVO.SkyMonitor.Video.FFmpeg`)** | `FFmpegOptions` and `FFmpegTimeLapseEncoder` already enforce closed profiles, fully qualified paths, address-space/RSS rlimits, scratch caps, and binary SHA-256 verification. Extending this subprocess boundary isolates untrusted codec parsing from CameraAgent's .NET process and preserves LGPL/GPL process separation. |
| **Raw Camera High-Rate Video Acquisition** (ZWO ASI video mode) | **Extend `HVO.SkyMonitor.CameraAgent.Modules.Zwo` + `AgentCore` stream seam** | `ZwoAsiCameraModule` already loads `libASICamera2.so` (`SupportedSdkMajorMinor = "1.41"`). Adding an opt-in stream interface in `HVO.SkyMonitor.AgentCore` alongside `ICameraModule` avoids breaking V1 still-frame capture modules. |
| **Optional Local AI Classification (Stage 2)** | **Adopt `Microsoft.ML.OnnxRuntime` (`MIT`) behind `#140` / `transient-detector-provider-v1` seam** | Clean .NET API, MIT core license, CPU fallback on x64 and ARM64, and optional CUDA/TensorRT/OpenVINO/NPU execution providers without coupling core `Processing` or `Imaging` to ML packages. |
| **External / Custom AI & Accelerator Services** | **Defer to `#140` Certified External Processing Bridge** | Sandboxed executable / Unix socket / authenticated HTTP or gRPC bridge keeps custom Python/PyTorch/Coral/Hailo/cloud classifiers completely outside CameraAgent's trust boundary and avoids duplicating `#140` lifecycle work. |
| **FMDT (`alsoc/fmdt`) C/C++ Toolbox** | **Reference & optional external benchmark oracle only (do not vendor into core)** | Clean MIT codebase with valuable embedded RPi4 benchmarks, but HVO's linear 16-bit scientific pipeline and C# determinism fit better in `HVO.SkyMonitor.Imaging`. FMDT may be used as an independent external comparison baseline during prototyping. |
| **WMPL (`wmpg/WesternMeteorPyLib`)** | **Do not integrate into CameraAgent; align export schema** | Multi-station trajectory/orbit math is outside standalone CameraAgent scope. Preserve UTC midpoint, clock-error bounds, calibration identity, and saturation flags so exported V2 observations can be ingested by WMPL or similar tools offline. |
| **GMN/RMS, FreeTure, AllSky7, ASGARD, CAMS binaries** | **Do not reuse code** | Blocked by `GPL-3.0`, `AS7-CLA v1`, or unavailable/closed source; published algorithms and operational failure lessons are captured clean-room in the companion architecture specification. |

---

## 6. References

1. Vida, D., et al. (2021). *The Global Meteor Network – Methodology and first results.* MNRAS, 506(4), 5046–5074. <https://doi.org/10.1093/mnras/stab2008>
2. CroatianMeteorNetwork. *RMS (RPi Meteor Station).* GitHub (`GPL-3.0`, inspected 2026-11-27). <https://github.com/CroatianMeteorNetwork/RMS>
3. Colas, F., et al. (2020). *FRIPON: a worldwide network to track incoming meteoroids.* Astronomy & Astrophysics, 644, A53. <https://doi.org/10.1051/0004-6361/202038649>
4. FRIPON. *FreeTure.* GitHub (`GPL-3.0`, inspected 2026-11-27). <https://github.com/fripon/freeture>
5. Cooke, W. J., & Moser, D. E. (2012). *The Status of the NASA All Sky Fireball Network.* NASA NTRS `20120004179`. <https://ntrs.nasa.gov/citations/20120004179>
6. Jenniskens, P., et al. (2011). *CAMS: Cameras for Allsky Meteor Surveillance to establish minor meteor showers.* Icarus, 216(1), 40–61. <https://doi.org/10.1016/j.icarus.2011.08.012>
7. Gural, P. S. (2008). *Algorithms and Software for Meteor Detection.* Earth, Moon, and Planets, 102(1–4), 269–275. <https://doi.org/10.1007/s11038-007-9161-7>
8. satmonkey. *MeteorDL.* GitHub (`MIT` code / undocumented model weights, inspected 2026-11-27). <https://github.com/satmonkey/MeteorDL>
9. Vida, D., Gural, P. S., Brown, P. G., Campbell-Brown, M., & Wiegert, P. (2020). *Estimating trajectories of meteors: an observational Monte Carlo approach – I. Theory.* MNRAS, 491(2), 2688–2705. <https://doi.org/10.1093/mnras/stz3160>
10. Western Meteor Physics Group. *WesternMeteorPyLib (WMPL).* GitHub (`MIT`, inspected 2026-11-27). <https://github.com/wmpg/WesternMeteorPyLib>
11. Peña-Asensio, E., Trigo-Rodríguez, J. M., Grèbol-Tomàs, P., Regordosa-Avellana, D., & Rimola, A. (2023). *Deep machine learning for meteor monitoring: Advances with transfer learning and gradient-weighted class activation mapping.* Planetary and Space Science, 238, 105802. <https://doi.org/10.1016/j.pss.2023.105802>
12. Howie, R. M., et al. (2017). *How to build a continental scale fireball camera network.* Experimental Astronomy, 43(3), 237–266. <https://doi.org/10.1007/s10686-017-9532-7>
13. Devillepoix, H. A. R., et al. (2020). *A Global Fireball Observatory.* Planetary and Space Science, 191, 105036. <https://doi.org/10.1016/j.pss.2020.105036>
14. Ciocan, C., Kandeepan, M., Cassagne, A., Vaubaillon, J., Zander, F., & Lacassagne, L. (2023). *A new meteor detection application robust to camera movements* (Fast Meteor Detection Toolbox / FMDT). <https://doi.org/10.48550/arXiv.2309.06027>; GitHub (`MIT`, inspected 2026-11-27): <https://github.com/alsoc/fmdt>
15. AllSky7 / American Meteor Society contributor repository (`AS7-CLA v1`, inspected 2026-11-27): <https://github.com/mikehankey/allsky7_contrib>
16. fiachraf. *meteorml.* GitHub (`GPL-3.0`, inspected 2026-11-27). <https://github.com/fiachraf/meteorml>
