const operationsSections = {
    overview: { title: "Operations", group: "Local administration", render: renderOverview },
    site: { title: "Observatory & location", group: "Setup", render: renderSite },
    camera: { title: "Camera & rig", group: "Setup", render: renderCamera },
    registration: { title: "Registration", group: "Setup", render: renderRegistration },
    schedule: { title: "Schedule", group: "Capture", render: renderSchedule },
    focus: { title: "Focus", group: "Capture", render: renderFocus },
    calibration: { title: "Calibration", group: "Capture", render: renderCalibration },
    pipeline: { title: "Pipeline", group: "Processing", render: renderPipeline },
    environment: { title: "Environment", group: "Processing", render: renderEnvironment },
    transients: { title: "Transients", group: "Processing", render: renderTransients },
    automations: { title: "Automations", group: "Automation", render: renderAutomations },
    delivery: { title: "Delivery", group: "Data", render: renderDelivery },
    storage: { title: "Storage & retention", group: "Data", render: renderStorage },
    health: { title: "Health & diagnostics", group: "System", render: renderHealth },
    control: { title: "System control", group: "System", render: renderControl },
    software: { title: "Software & catalog", group: "System", render: renderSoftware }
};

const pipelineNodeDetails = {
    projected: { title: "Projected scene", type: "Required transform", description: "Projects catalog objects and image geometry directly from the immutable raw frame.", input: "$raw", output: "projected-scene-v1", alias: "projected-scene / ProjectedScene", options: "HYG 4.2 / magnitude 6.5" },
    calibration: { title: "Calibration", type: "Required transform", description: "Selects compatible references and corrects the immutable source without modifying raw evidence.", input: "$raw", output: "pseudo-calibrated", alias: "calibration / Calibration", options: "CalibrationLibrary / fail closed" },
    rolling: { title: "Rolling combination", type: "Required window transform", description: "Combines a causal five-frame window. The current baseline is not geometrically registered.", input: "calibration", output: "rolling-mean", alias: "rolling / RollingCombination", options: "Window 5 / endpoint N" },
    combined: { title: "Combined preview", type: "Required transform", description: "Tone maps the rolling combination into the base display product used by presentation materialization.", input: "rolling", output: "combined-preview", alias: "combined-preview / CombinedPreview", options: "mono16-asinh-v2" },
    cloud: { title: "Cloud assessment", type: "Optional analyzer", description: "Compares the calibrated frame with the active clear reference and produces bounded quality evidence.", input: "calibration", output: "cloud-assessment-v1 + cloud-mask", alias: "cloud / CloudAssessment", options: "16 x 16 grid / mask included" },
    overlay: { title: "Overlay manifest", type: "Required fan-in", description: "Records the selected presentation-layer artifacts and their exact variants before composition.", input: "combined preview + layer outputs", output: "w6-overlay-manifest", alias: "overlay-manifest / OverlayManifest", options: "Four presentation producers" },
    materialize: { title: "Presentation materializer", type: "Required fan-in", description: "Composes the retained presentation layers over the combined preview using the overlay manifest.", input: "combined-preview + overlay-manifest", output: "w6-annotated-preview", alias: "presentation-materializer / PresentationMaterializer", options: "Layered All-Sky Processing variants" }
};

const automationDefinitions = [
    { kind: "timelapse", title: "Hourly Sky Motion Timelapse", description: "Generate each closed hourly segment, then finalize the night playback after capture-window close.", trigger: "Hourly boundary", input: "Processed captures", next: "21:00 local", task: "Sky Motion Timelapse / revision 3" },
    { kind: "trail", title: "Nightly Star Trail", description: "Create a quality-gated lighten composite when the observing window has closed.", trigger: "Capture window close", input: "Calibrated captures", next: "05:08 local", task: "Night Star Trail / revision 2" },
    { kind: "keogram", title: "Nightly Keogram", description: "Sample a north-south slice every minute and finalize it at the observing-window boundary.", trigger: "Capture window close", input: "Processed captures", next: "05:08 local", task: "North-South Keogram / revision 1" },
    { kind: "summary", title: "Observing Day Summary", description: "Commit capture coverage, weather, processing, product, and event statistics at day rollover.", trigger: "Observing-day rollover", input: "Durable day facts", next: "Tomorrow 12:00", task: "Observing Day Summary / revision 1" },
    { kind: "archive", title: "Archive Eligible History", description: "Apply retention only after time policy and every required consumer acknowledgement permit removal.", trigger: "Daily fixed time", input: "Eligible evidence", next: "12:15 local", task: "Standard Evidence Retention / revision 10" }
];

function pageHeader(group, title, description, actions = "") {
    return `
        <header class="ops-page-heading">
            <div><p class="eyebrow">${group}</p><h2 tabindex="-1">${title}</h2><p>${description}</p></div>
            <div class="ops-page-actions"><span class="ops-authority"><i></i>CameraAgent local authority</span>${actions}</div>
        </header>`;
}

function prototypeButton(label, message, style = "secondary") {
    return `<button class="button ${style}" type="button" data-prototype-action="${message}">${label}</button>`;
}

function confirmButton(label, title, message, result, style = "secondary") {
    return `<button class="button ${style}" type="button" data-confirm-title="${title}" data-confirm-text="${message}" data-confirm-result="${result}">${label}</button>`;
}

function renderOverview() {
    return `
        ${pageHeader("HVO Main Fisheye / local authority", "Operations", "Configure and operate this camera without making local acquisition dependent on LogicHost.", `<a class="button secondary" href="dashboard.html">View current sky</a>${confirmButton("Pause capture", "Pause acquisition?", "The active exposure can finish, then new capture admission will pause until explicitly resumed.", "Pause intent recorded; runtime state is unchanged in this prototype.")}`)}
        <section class="ops-state-deck" aria-label="Current operating state">
            <div class="ops-state-primary"><span class="ops-state-orbit" aria-hidden="true"><i></i></span><span><strong>Capture loop running</strong><span>Night regime / minimum-start cadence</span></span></div>
            <dl class="ops-state-facts">
                <div><dt>Next capture</dt><dd>6 seconds</dd></div>
                <div><dt>Admission</dt><dd>Schedule open</dd></div>
                <div><dt>Active profile</dt><dd>Night Fisheye Capture</dd></div>
                <div><dt>LogicHost</dt><dd>Acknowledged</dd></div>
            </dl>
            <a class="button primary" href="index.html?run=84220">Latest run</a>
        </section>

        <div class="ops-grid sidebar-right">
            <section class="ops-panel">
                <header class="ops-panel-heading"><div><h3>Needs attention</h3><p>Items that do not stop local acquisition but should be reviewed.</p></div><span>2 open</span></header>
                <div class="ops-attention-list">
                    <article class="ops-attention-item warning"><span class="status-icon warning" aria-hidden="true"></span><span><strong>Deployment location awaits central review</strong><small>Local geometry remains authoritative while LogicHost reviews version loc-w6-04.</small></span><a href="operations.html?section=site" data-operations-section="site">Review</a></article>
                    <article class="ops-attention-item info"><span class="status-icon running" aria-hidden="true"></span><span><strong>One delivery is retrying</strong><small>Capture #84216 is durable locally; the outbox is applying bounded backoff.</small></span><a href="operations.html?section=delivery" data-operations-section="delivery">Inspect</a></article>
                </div>
            </section>

            <section class="ops-panel">
                <header class="ops-panel-heading"><div><h3>Active configuration</h3><p>Every capture binds these exact revisions.</p></div><span>Capture #84220</span></header>
                <div class="ops-configuration-grid">
                    <a class="ops-config-card" href="operations.html?section=camera" data-operations-section="camera"><span>Camera &amp; rig</span><strong>Night Fisheye Capture / r17</strong><small>Validated 28 Aug</small></a>
                    <a class="ops-config-card" href="operations.html?section=schedule" data-operations-section="schedule"><span>Schedule</span><strong>Hualapai Night Schedule / r12</strong><small>Next transition 05:08</small></a>
                    <a class="ops-config-card" href="operations.html?section=pipeline" data-operations-section="pipeline"><span>Pipeline</span><strong>Layered All-Sky Processing</strong><small>14 configured steps</small></a>
                    <a class="ops-config-card" href="operations.html?section=calibration" data-operations-section="calibration"><span>Calibration</span><strong>August 2026 Calibration Library</strong><small>4 active references</small></a>
                </div>
            </section>
        </div>

        <section class="ops-panel ops-grid" aria-labelledby="lane-heading">
            <header class="ops-panel-heading"><div><h3 id="lane-heading">Durable work lanes</h3><p>Image freshness and system safety remain separate signals.</p></div><a href="operations.html?section=health" data-operations-section="health">Full diagnostics</a></header>
            <div class="ops-lane-list">
                <div class="ops-lane"><span><strong>Raw ingress</strong><small>Immutable evidence</small></span><span class="ops-meter"><i style="width: 6%"></i></span><span>0 pending</span></div>
                <div class="ops-lane"><span><strong>Frame processing</strong><small>Current graph</small></span><span class="ops-meter blue"><i style="width: 18%"></i></span><span>1 active</span></div>
                <div class="ops-lane"><span><strong>Central delivery</strong><small>Durable outbox</small></span><span class="ops-meter attention"><i style="width: 31%"></i></span><span>1 retry</span></div>
                <div class="ops-lane"><span><strong>Storage pressure</strong><small>312 GB available</small></span><span class="ops-meter blue"><i style="width: 38%"></i></span><span>38% used</span></div>
            </div>
        </section>

        <div class="ops-grid two">
            <section class="ops-panel">
                <header class="ops-panel-heading"><div><h3>Current operating facts</h3><p>Bounded signals for this CameraAgent process.</p></div><span>03:14 UTC</span></header>
                <div class="ops-metric-grid">
                    <div class="ops-metric"><span>Capture age</span><strong>4s</strong><small>Expected &lt; 20s</small></div>
                    <div class="ops-metric"><span>Last processing</span><strong>8.03s</strong><small>14 steps</small></div>
                    <div class="ops-metric"><span>Sensor</span><strong>-4.8 C</strong><small>Stable +/-0.2</small></div>
                    <div class="ops-metric"><span>Cloud estimate</span><strong>8%</strong><small>Confidence 0.91</small></div>
                </div>
            </section>
            <section class="ops-panel">
                <header class="ops-panel-heading"><div><h3>Recent changes</h3><p>Authenticated configuration and control history.</p></div><span>UTC</span></header>
                <ol class="ops-audit-list">
                    <li><time>02:55:10</time><strong>Schedule</strong><span>Temporary override expired normally.</span></li>
                    <li><time>00:14:42</time><strong>Delivery</strong><span>LogicHost acknowledgment resumed.</span></li>
                    <li><time>Aug 30</time><strong>Pipeline</strong><span>Revision r31 activated at capture boundary.</span></li>
                    <li><time>Aug 28</time><strong>Rig</strong><span>Profile r17 validation completed.</span></li>
                </ol>
            </section>
        </div>`;
}

function renderCamera() {
    return `
        ${pageHeader("Setup / versioned capture profile", "Camera & rig", "Keep the camera implementation, native readout, and physical optics explicit while activating them as one capture-time profile.", `${prototypeButton("Compare revisions", "Revision comparison opened in prototype.")} ${prototypeButton("Create draft", "A new draft would be based on active revision r17.", "primary")}`)}
        <section class="ops-profile-banner"><div><span class="status-icon success" aria-hidden="true"></span><span><strong>Night Fisheye Capture r17 is active</strong><small>Validated before activation / bound to 1,284 captures</small></span></div><dl><div><dt>Activated</dt><dd>28 Aug 2026</dd></div><div><dt>Draft</dt><dd>None</dd></div><div><dt>Apply boundary</dt><dd>Next capture</dd></div></dl></section>

        <div class="ops-grid sidebar-right">
            <div class="ops-grid">
                <section class="ops-panel">
                    <header class="ops-panel-heading"><div><h3>Camera module</h3><p>Implementation identity and the selected physical or virtual device.</p></div><span>Ready</span></header>
                    <dl class="ops-facts">
                        <div><dt>Module</dt><dd>VirtualSky</dd></div><div><dt>Camera model</dt><dd>ASI676MC profile</dd></div>
                        <div><dt>Device identity</dt><dd><code>virtual-asi676mc-w6</code></dd></div><div><dt>Connection</dt><dd>In process</dd></div>
                        <div><dt>Sensor geometry</dt><dd>3552 x 3552</dd></div><div><dt>Pixel size</dt><dd>2.0 microns</dd></div>
                        <div class="full"><dt>Implementation boundary</dt><dd>Module options remain separate from physical rig and optics facts.</dd></div>
                    </dl>
                </section>
                <section class="ops-panel">
                    <header class="ops-panel-heading"><div><h3>Native readout</h3><p>Canonical sensor facts used to validate calibration and processing compatibility.</p></div><span>RAW16</span></header>
                    <div class="ops-table-wrap"><table class="ops-table"><thead><tr><th>Property</th><th>Active value</th><th>Capability</th><th>Validation</th></tr></thead><tbody>
                        <tr><td><strong>Region</strong></td><td>0, 0 / 3552 x 3552</td><td>Full frame</td><td><span class="state-chip success">Valid</span></td></tr>
                        <tr><td><strong>Binning</strong></td><td>1 x 1</td><td>1 x 1</td><td><span class="state-chip success">Native</span></td></tr>
                        <tr><td><strong>Pixel format</strong></td><td>Bayer RGGB16</td><td>RAW16</td><td><span class="state-chip success">Valid</span></td></tr>
                        <tr><td><strong>Meaningful depth</strong></td><td>12 bits</td><td>16-bit container</td><td><span class="state-chip success">Explicit</span></td></tr>
                        <tr><td><strong>Stride / order</strong></td><td>7104 bytes / LE</td><td>Canonical</td><td><span class="state-chip success">Valid</span></td></tr>
                    </tbody></table></div>
                </section>
            </div>
            <div class="ops-grid">
                <section class="ops-panel">
                    <header class="ops-panel-heading"><div><h3>Optics &amp; projection</h3><p>Physical geometry remains independent of camera implementation.</p></div><span>r17</span></header>
                    <div class="ops-geometry-visual" aria-label="Equidistant fisheye projection preview"><span class="geo-north">N / +0.8 deg</span><span class="geo-mask">Horizon mask 6.2%</span></div>
                    <dl class="ops-facts"><div><dt>Lens</dt><dd>180-degree fisheye</dd></div><div><dt>Projection</dt><dd>Equidistant</dd></div><div><dt>Orientation</dt><dd>0.8 deg east</dd></div><div><dt>Image circle</dt><dd>1712 px radius</dd></div></dl>
                </section>
                <section class="ops-panel">
                    <header class="ops-panel-heading"><div><h3>Validated capabilities</h3><p>Unsupported combinations cannot be activated.</p></div></header>
                    <div class="ops-capability-list"><span class="ops-capability"><i></i>Full-frame acquisition</span><span class="ops-capability"><i></i>RAW16 readout</span><span class="ops-capability"><i></i>Exposure control</span><span class="ops-capability"><i></i>Gain control</span><span class="ops-capability"><i></i>Sensor temperature</span><span class="ops-capability"><i></i>Deterministic virtual frames</span></div>
                </section>
            </div>
        </div>

        <section class="ops-panel ops-grid">
            <header class="ops-panel-heading"><div><h3>Revision history</h3><p>Activation never rewrites the profile bound to historical captures.</p></div><a href="index.html?run=84220">Inspect capture binding</a></header>
            <div class="ops-table-wrap"><table class="ops-table"><thead><tr><th>Revision</th><th>State</th><th>Change</th><th>Validated</th><th>Capture range</th></tr></thead><tbody>
                <tr class="current-row"><td><strong>r17</strong></td><td><span class="state-chip success">Active</span></td><td>Orientation refined to +0.8 deg</td><td>28 Aug / owner</td><td>#82937 onward</td></tr>
                <tr><td><strong>r16</strong></td><td>Superseded</td><td>Explicit 12-bit meaningful depth</td><td>12 Aug / owner</td><td>#80341-82936</td></tr>
                <tr><td><strong>r15</strong></td><td>Superseded</td><td>Initial Hualapai deployment geometry</td><td>03 Aug / owner</td><td>#78102-80340</td></tr>
            </tbody></table></div>
        </section>`;
}

function renderSchedule() {
    return `
        ${pageHeader("Capture / deterministic admission", "Schedule", "Define when capture may start, then apply day, twilight, and night exposure policies without catch-up captures.", `${confirmButton("Temporary override", "Create a schedule override?", "A bounded force-open or force-closed override takes precedence until its expiry.", "Override intent recorded; no schedule state changed.")} ${prototypeButton("Edit draft", "Schedule draft r13 would be created from active revision r12.", "primary")}`)}
        <section class="ops-profile-banner"><div><span class="status-icon success" aria-hidden="true"></span><span><strong>Hualapai Night Schedule r12 is open</strong><small>Astronomical night / minimum-start cadence / no manual override</small></span></div><dl><div><dt>Local timezone</dt><dd>America/Phoenix</dd></div><div><dt>Next transition</dt><dd>05:08:42</dd></div><div><dt>Active regime</dt><dd>Night</dd></div></dl></section>

        <section class="ops-panel">
            <header class="ops-panel-heading"><div><h3>Tonight</h3><p>Solar boundaries are calculated for the active deployment-location snapshot.</p></div><span>31 Aug / local time</span></header>
            <div class="ops-sky-clock"><strong>Capture window open / current 20:14</strong><span>12:00</span><span>18:00</span><span>00:00</span><span>06:00</span><span>12:00</span></div>
        </section>

        <div class="ops-grid two">
            <section class="ops-panel">
                <header class="ops-panel-heading"><div><h3>Capture regimes</h3><p>Setpoints are selected when each exposure is admitted.</p></div><span>r12</span></header>
                <div class="ops-regime-grid">
                    <article class="ops-regime-card"><span>Closed</span><h4>Day</h4><dl><div><dt>Admission</dt><dd>Blocked</dd></div><div><dt>Boundary</dt><dd>Sun &gt; -6 deg</dd></div></dl></article>
                    <article class="ops-regime-card"><span>Reduced</span><h4>Twilight</h4><dl><div><dt>Exposure</dt><dd>0.50s</dd></div><div><dt>Cadence</dt><dd>30s</dd></div><div><dt>Gain</dt><dd>24</dd></div><div><dt>Boundary</dt><dd>-6 to -18 deg</dd></div></dl></article>
                    <article class="ops-regime-card active"><span>Active now</span><h4>Night</h4><dl><div><dt>Exposure</dt><dd>5.00s</dd></div><div><dt>Cadence</dt><dd>10s min-start</dd></div><div><dt>Gain</dt><dd>82</dd></div><div><dt>Boundary</dt><dd>Below -18 deg</dd></div></dl></article>
                </div>
            </section>
            <section class="ops-panel">
                <header class="ops-panel-heading"><div><h3>Weekly availability</h3><p>Solar boundaries resolve inside each enabled local day.</p></div><span>7 enabled</span></header>
                <div class="ops-week">
                    ${["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"].map(day => `<div class="ops-week-row"><span>${day}</span><span class="ops-week-bar"><i></i></span><small>Dusk -&gt; dawn</small></div>`).join("")}
                </div>
            </section>
        </div>

        <div class="ops-grid two">
            <section class="ops-panel">
                <header class="ops-panel-heading"><div><h3>Exceptions &amp; blackouts</h3><p>Date-specific rules outrank the repeating week.</p></div>${prototypeButton("Add exception", "A schedule exception editor would open.")}</header>
                <div class="ops-table-wrap"><table class="ops-table"><thead><tr><th>Date</th><th>Rule</th><th>Reason</th><th>Expiry</th></tr></thead><tbody><tr><td>05 Sep 2026</td><td><strong>Force closed</strong></td><td>Lens maintenance</td><td>06 Sep / 12:00</td></tr><tr><td>12 Sep 2026</td><td><strong>Start +45m</strong></td><td>Site access window</td><td>One night</td></tr></tbody></table></div>
            </section>
            <section class="ops-panel">
                <header class="ops-panel-heading"><div><h3>Admission precedence</h3><p>The first blocking condition wins and remains reason-coded.</p></div></header>
                <div class="ops-progress-steps"><div class="ops-progress-step"><strong>Safety</strong><span>Durable ingress and storage</span></div><div class="ops-progress-step"><strong>Pause</strong><span>Authenticated local control</span></div><div class="ops-progress-step"><strong>Schedule</strong><span>Window and exceptions</span></div><div class="ops-progress-step"><strong>Cadence</strong><span>Minimum-start policy</span></div></div>
            </section>
        </div>`;
}

function renderPipeline() {
    return `
        ${pageHeader("Processing / configured dependency graph", "Pipeline", "Configure unique step IDs, registered operation types, and explicit dependencies, then validate the whole CameraAgent graph.", `<a class="button secondary" href="index.html?run=84220">View latest run</a>${prototypeButton("Create draft", "A future versioned pipeline draft would start from Layered All-Sky Processing.", "primary")}`)}
        <section class="ops-profile-banner"><div><span class="status-icon success" aria-hidden="true"></span><span><strong>Layered All-Sky Processing is valid</strong><small>14 configured steps / 11 required / deterministic topological order</small></span></div><dl><div><dt>Schema</dt><dd>cameraagent-capture-pipeline-v2</dd></div><div><dt>Last run</dt><dd>8.03s</dd></div><div><dt>Draft lifecycle</dt><dd>Design target</dd></div></dl></section>

        <section class="ops-pipeline-workspace" aria-label="Pipeline configuration graph">
            <div class="ops-pipeline-canvas">
                <div class="ops-pipeline-graph">
                    <button class="ops-pipeline-node acquire active" type="button" data-pipeline-node="projected" aria-pressed="true"><strong>Projected scene</strong><small>ProjectedScene / required</small><i class="node-port"></i></button>
                    <button class="ops-pipeline-node calibrate" type="button" data-pipeline-node="calibration" aria-pressed="false"><strong>Calibration</strong><small>Calibration / required</small><i class="node-port"></i></button>
                    <button class="ops-pipeline-node combine" type="button" data-pipeline-node="rolling" aria-pressed="false"><strong>Rolling combination</strong><small>RollingCombination / required</small><i class="node-port"></i></button>
                    <button class="ops-pipeline-node scene" type="button" data-pipeline-node="combined" aria-pressed="false"><strong>Combined preview</strong><small>CombinedPreview / required</small><i class="node-port"></i></button>
                    <button class="ops-pipeline-node cloud" type="button" data-pipeline-node="cloud" aria-pressed="false"><strong>Cloud assessment</strong><small>Optional / analyzer</small><i class="node-port"></i></button>
                    <button class="ops-pipeline-node materialize" type="button" data-pipeline-node="overlay" aria-pressed="false"><strong>Overlay manifest</strong><small>OverlayManifest / required</small><i class="node-port"></i></button>
                    <button class="ops-pipeline-node retain" type="button" data-pipeline-node="materialize" aria-pressed="false"><strong>Presentation materializer</strong><small>PresentationMaterializer / required</small></button>
                </div>
            </div>
            <aside class="ops-pipeline-inspector" aria-live="polite"><p class="eyebrow" id="pipelineNodeType">Required transform</p><h3 id="pipelineNodeTitle">Projected scene</h3><p id="pipelineNodeDescription">Projects catalog objects and image geometry directly from the immutable raw frame.</p><dl class="ops-node-facts"><div><dt>Step ID / registered type</dt><dd id="pipelineNodeAlias">projected-scene / ProjectedScene</dd></div><div><dt>Input selection</dt><dd id="pipelineNodeInput">$raw</dd></div><div><dt>Outputs</dt><dd id="pipelineNodeOutput">projected-scene-v1</dd></div><div><dt>Effective options</dt><dd id="pipelineNodeOptions">HYG 4.2 / magnitude 6.5</dd></div></dl><p class="ops-inspector-footnote">Representative 7-step projection. Calibrated preview, quality, presentation layers, storage, and telemetry remain part of the 14-step effective graph.</p></aside>
        </section>

        <div class="ops-grid two">
            <section class="ops-panel">
                <header class="ops-panel-heading"><div><h3>Configuration validation</h3><p>Checks run before CameraAgent startup and future revision activation.</p></div><span>5 / 5 passed</span></header>
                <div class="ops-validation-list"><div class="ops-validation-item"><i>+</i>Step IDs are unique and operation types are registered.</div><div class="ops-validation-item"><i>+</i>Required dependencies form an acyclic graph.</div><div class="ops-validation-item"><i>+</i>Every input role has one compatible producer.</div><div class="ops-validation-item"><i>+</i>Operation options pass type-specific validation.</div><div class="ops-validation-item"><i>+</i>Storage depends on all required retained products.</div></div>
            </section>
            <section class="ops-panel">
                <header class="ops-panel-heading"><div><h3>Execution boundary</h3><p>Configuration shape does not imply runtime concurrency.</p></div><span>Current behavior</span></header>
                <div class="ops-note-banner"><span class="status-icon warning" aria-hidden="true"></span><span><strong>Sequential topological execution.</strong> Independent branches are shown to make dependencies testable; the current worker executes eligible nodes sequentially.</span></div>
                <dl class="ops-facts"><div><dt>Failure policy</dt><dd>Required nodes fail closed</dd></div><div><dt>Activation</dt><dd>Next capture boundary</dd></div><div><dt>History</dt><dd>Immutable revisions</dd></div><div><dt>Rollback</dt><dd>Creates new activation</dd></div></dl>
            </section>
        </div>`;
}

function renderFocus() {
    return `
        ${pageHeader("Capture / manual optical setup", "Focus", "Use repeatable preview evidence to adjust the lens by hand. This page does not imply a motorized focuser or autofocus capability.", `<a class="button secondary" href="dashboard.html">Current sky</a><button id="focusStart" class="button primary" type="button">Start focus session</button>`)}
        <div class="focus-boundary"><strong>Manual focus only.</strong> Loosen, adjust, and secure the lens at the camera. CameraAgent measures image sharpness but does not move hardware.</div>

        <section class="focus-workspace">
            <div class="focus-viewer">
                <div class="focus-image"><img src="assets/w6-current.jpg" alt="Magnified star field used for manual focus"><span class="focus-reticle" aria-hidden="true"></span><span class="focus-target-label">Target region / 12 detected stars</span></div>
                <footer class="focus-viewer-toolbar"><span>Preview is temporary and is not admitted to the normal capture pipeline.</span>${prototypeButton("Select region", "Focus target selection enabled in prototype.")}</footer>
            </div>
            <aside class="focus-inspector">
                <div class="focus-session-state"><span><strong id="focusStatus">Ready to measure</strong><small id="focusStatusDetail">No focus session is active</small></span><span id="focusStateChip" class="state-chip neutral">Idle</span></div>
                <div class="focus-score"><span>Median FWHM / lower is better</span><strong id="focusScore">3.42 <small>px</small></strong><svg class="focus-trend" viewBox="0 0 240 90" role="img" aria-label="Recent focus score trend"><path class="grid" d="M0 20H240M0 45H240M0 70H240"></path><path id="focusTrendLine" d="M5 25 50 31 95 39 140 52 185 58 235 62"></path><circle cx="5" cy="25" r="3"></circle><circle cx="50" cy="31" r="3"></circle><circle cx="95" cy="39" r="3"></circle><circle cx="140" cy="52" r="3"></circle><circle cx="185" cy="58" r="3"></circle><circle cx="235" cy="62" r="3"></circle></svg></div>
                <div class="focus-controls"><label>Preview exposure<input type="text" value="1.000s" aria-label="Focus preview exposure"></label><label>Preview gain<input type="number" value="110" aria-label="Focus preview gain"></label><label>Interval<select aria-label="Focus preview interval"><option>2 seconds</option><option>5 seconds</option></select></label><label>Metric<select aria-label="Focus quality metric"><option>Median FWHM</option><option>Laplacian score</option></select></label></div>
                <button id="focusSample" class="button secondary" type="button" disabled>Record next sample</button>
            </aside>
        </section>

        <section class="ops-panel ops-grid">
            <header class="ops-panel-heading"><div><h3>Manual focusing procedure</h3><p>Keep the measurement setup fixed while comparing adjustments.</p></div><span>No motor control</span></header>
            <div class="ops-step-list"><article class="ops-step"><strong>Choose a field</strong><span>Select a stable star-rich region away from the horizon and bright obstructions.</span></article><article class="ops-step"><strong>Start previews</strong><span>Use temporary exposure and gain values that avoid clipping the target stars.</span></article><article class="ops-step"><strong>Adjust by hand</strong><span>Move the lens in small increments and wait for the next measured sample.</span></article><article class="ops-step"><strong>Secure &amp; verify</strong><span>Tighten the lens, collect at least three stable samples, and retain the session result.</span></article></div>
        </section>

        <section class="ops-panel ops-grid">
            <header class="ops-panel-heading"><div><h3>Focus session history</h3><p>Measurements document optical setup; they do not alter active camera profiles.</p></div>${prototypeButton("Export evidence", "Focus session evidence export prepared in prototype.")}</header>
            <div class="ops-table-wrap"><table class="ops-table"><thead><tr><th>Session</th><th>Best FWHM</th><th>Target</th><th>Conditions</th><th>Result</th></tr></thead><tbody><tr><td><strong>28 Aug / 04:12 UTC</strong></td><td>2.18 px</td><td>Zenith field</td><td>Clear / 9 C</td><td><span class="state-chip success">Accepted</span></td></tr><tr><td><strong>12 Aug / 03:44 UTC</strong></td><td>2.34 px</td><td>North field</td><td>Thin cloud / 11 C</td><td>Superseded</td></tr></tbody></table></div>
        </section>`;
}

function renderSite() {
    return `
        ${pageHeader("Setup / geometry authority", "Observatory & location", "Keep local capture geometry available offline while showing the separate LogicHost Observatory assignment and review state.", `${prototypeButton("View revision history", "Location revision history opened in prototype.")} ${prototypeButton("Create local draft", "A local site draft would be created from loc-w6-04.", "primary")}`)}
        <div class="ops-note-banner"><span class="status-icon warning" aria-hidden="true"></span><span><strong>Central review pending.</strong> LogicHost has not yet acknowledged location version loc-w6-04. Local geometry remains active and capture continues normally.</span></div>
        <div class="ops-grid sidebar-right">
            <section class="ops-panel"><header class="ops-panel-heading"><div><h3>Deployment location</h3><p>Protected local snapshot used for schedules, projection, and capture evidence.</p></div><span>loc-w6-04</span></header><div class="ops-map"><i class="ops-map-pin"></i><span class="ops-map-label">Hualapai Valley Observatory</span></div></section>
            <section class="ops-panel"><header class="ops-panel-heading"><div><h3>Local site profile</h3><p>Available even when standalone or disconnected.</p></div><span class="state-chip success">Active</span></header><dl class="ops-facts"><div class="full"><dt>Friendly name</dt><dd>Hualapai Valley Observatory</dd></div><div><dt>Latitude</dt><dd>35.102840 N</dd></div><div><dt>Longitude</dt><dd>113.884120 W</dd></div><div><dt>Elevation</dt><dd>1,487 m</dd></div><div><dt>Timezone</dt><dd>America/Phoenix</dd></div><div class="full"><dt>Source</dt><dd>Operator surveyed / +/- 8 m</dd></div></dl></section>
        </div>
        <div class="ops-grid two"><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Authority boundary</h3><p>Local and central facts remain explicit.</p></div></header><div class="ops-table-wrap"><table class="ops-table"><thead><tr><th>Fact</th><th>Authority</th><th>Local behavior</th></tr></thead><tbody><tr><td><strong>Capture geometry</strong></td><td>CameraAgent</td><td>Editable as versioned local draft</td></tr><tr><td><strong>Offline schedule location</strong></td><td>CameraAgent</td><td>Uses active protected snapshot</td></tr><tr><td><strong>Observatory membership</strong></td><td>LogicHost</td><td>Read-only assignment</td></tr><tr><td><strong>Reconciliation decision</strong></td><td>LogicHost</td><td>Never silently replaces local facts</td></tr></tbody></table></div></section><section class="ops-panel"><header class="ops-panel-heading"><div><h3>LogicHost assignment</h3><p>Received during device registration.</p></div><span class="state-chip warning">Review</span></header><dl class="ops-facts"><div class="full"><dt>Observatory</dt><dd>Hualapai Valley Observatory</dd></div><div><dt>Device</dt><dd>W6</dd></div><div><dt>Membership</dt><dd>Active</dd></div><div><dt>Acknowledged version</dt><dd>loc-w6-03</dd></div><div><dt>Proposed version</dt><dd>loc-w6-04</dd></div></dl><a class="text-button" href="operations.html?section=registration" data-operations-section="registration">View registration state</a></section></div>`;
}

function renderRegistration() {
    return `
        ${pageHeader("Setup / central integration", "Registration", "Inspect this CameraAgent identity, its LogicHost enrollment, Observatory assignment, and current credential health.", `<button class="button secondary" type="button" disabled title="Revoke this device in LogicHost before clearing local credentials">Clear credentials after revocation</button>`)}
        <section class="ops-profile-banner"><div><span class="status-icon success" aria-hidden="true"></span><span><strong>HVO Main Fisheye is active</strong><small>Device W6 / provisioning envelope redeemed / heartbeat and upload authorization healthy</small></span></div><dl><div><dt>State</dt><dd>Active</dd></div><div><dt>Last heartbeat</dt><dd>8s ago</dd></div><div><dt>LogicHost</dt><dd>logic.hvo.local</dd></div></dl></section>
        <div class="ops-note-banner"><span class="status-icon running" aria-hidden="true"></span><span><strong>Revocation is central-first.</strong> Revoke HVO Main Fisheye in LogicHost, wait until this page reports Revoked, then clear the protected local credentials. Clearing an active registration is not offered.</span></div>
        <section class="ops-panel"><header class="ops-panel-heading"><div><h3>Provisioning lifecycle</h3><p>Owner-mediated registration preserves an explicit trust boundary.</p></div><span>Completed 19 Jul</span></header><div class="ops-progress-steps"><div class="ops-progress-step"><strong>Identity created</strong><span>Local device ID and verification code</span></div><div class="ops-progress-step"><strong>Owner registered</strong><span>Observatory assignment selected centrally</span></div><div class="ops-progress-step"><strong>Envelope imported</strong><span>Protected endpoints and credentials</span></div><div class="ops-progress-step"><strong>Activated</strong><span>Heartbeat and delivery authorized</span></div></div></section>
        <div class="ops-grid two"><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Local identity</h3><p>Sensitive values remain redacted.</p></div><span class="state-chip success">Protected</span></header><dl class="ops-facts"><div class="full"><dt>Device ID</dt><dd><code>dev_01J2W6K4B95PA7M</code></dd></div><div><dt>Friendly name</dt><dd>HVO Main Fisheye</dd></div><div><dt>Mode</dt><dd>Connected</dd></div><div><dt>Credential</dt><dd>Device key</dd></div><div><dt>Local owner</dt><dd>Configured</dd></div><div class="full"><dt>State directory</dt><dd><code>protected / configured path</code></dd></div></dl></section><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Central relationship</h3><p>LogicHost owns membership and revocation.</p></div><span>Acknowledged</span></header><dl class="ops-facts"><div class="full"><dt>Observatory</dt><dd>Hualapai Valley Observatory</dd></div><div><dt>Heartbeat</dt><dd>Healthy</dd></div><div><dt>Upload scope</dt><dd>Device W6 only</dd></div><div><dt>Location</dt><dd>Review pending</dd></div><div><dt>Revocation</dt><dd>Not requested</dd></div></dl></section></div>`;
}

function renderCalibration() {
    return `
        ${pageHeader("Capture / immutable references", "Calibration", "Manage compatible bias, dark, flat, and defect evidence without weakening raw-source immutability.", `${prototypeButton("Acquire references", "Calibration acquisition workflow opened in prototype.", "primary")}`)}
        <section class="ops-status-rail"><div><span>Library</span><strong>2026.08</strong><small>Active</small></div><div><span>Active references</span><strong>4</strong><small>Bias / dark / flat / defect</small></div><div><span>Compatibility</span><strong>Complete</strong><small>Night regime</small></div><div><span>Next review</span><strong>18 days</strong><small>Temperature drift</small></div></section>
        <div class="ops-grid sidebar-right"><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Reference library</h3><p>Selection matches the exact sensor and readout facts bound to a capture.</p></div><span>4 active / 14 retained</span></header><article class="ops-library-card"><span class="ops-library-icon"><svg viewBox="0 0 20 20"><circle cx="10" cy="10" r="5"></circle></svg></span><span><strong>Dark reference / 5.000s / gain 82</strong><small>-5 C / synthetic immutable evidence / checksum verified</small></span><span class="state-chip success">Active</span></article><article class="ops-library-card"><span class="ops-library-icon"><svg viewBox="0 0 20 20"><path d="M4 10h12M10 4v12"></path></svg></span><span><strong>Bias reference / gain 82</strong><small>Synthetic immutable evidence / native geometry</small></span><span class="state-chip success">Active</span></article><article class="ops-library-card"><span class="ops-library-icon"><svg viewBox="0 0 20 20"><circle cx="10" cy="10" r="7"></circle><circle cx="10" cy="10" r="2"></circle></svg></span><span><strong>Flat reference / current virtual rig</strong><small>Pixel response and radial vignetting / checksum verified</small></span><span class="state-chip success">Active</span></article><article class="ops-library-card"><span class="ops-library-icon"><svg viewBox="0 0 20 20"><path d="m4 4 12 12M16 4 4 16"></path></svg></span><span><strong>Defect reference / virtual-w6</strong><small>184 pixels / 12 columns / checksum verified</small></span><span class="state-chip success">Active</span></article></section><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Current selection</h3><p>Exactly one compatible bias, dark, flat, and defect reference for capture #84220.</p></div><span class="state-chip success">Valid</span></header><dl class="ops-facts"><div><dt>Geometry</dt><dd>3552 x 3552</dd></div><div><dt>CFA</dt><dd>RGGB</dd></div><div><dt>Exposure</dt><dd>5.000s</dd></div><div><dt>Gain</dt><dd>82</dd></div><div><dt>Temperature</dt><dd>-4.8 C</dd></div><div><dt>Validity</dt><dd>Through 18 Sep</dd></div></dl><a class="text-button" href="index.html?run=84220">Inspect calibration stage</a></section></div>`;
}

function renderEnvironment() {
    return `
        ${pageHeader("Processing / independent observations", "Environment", "Track weather and camera conditions independently from image processing, with explicit freshness and capture associations.", `${prototypeButton("Acquire now", "An on-demand environmental acquisition was queued in prototype.", "primary")}`)}
        <div class="ops-reading-grid"><article class="ops-reading"><span>Air temperature</span><strong>9.2 C</strong><small>Fresh / 18s ago</small></article><article class="ops-reading"><span>Relative humidity</span><strong>23%</strong><small>Fresh / 18s ago</small></article><article class="ops-reading"><span>Wind</span><strong>2.8 m/s</strong><small>Gust 4.1 m/s</small></article><article class="ops-reading"><span>Cloud estimate</span><strong>8%</strong><small>Image assessment 0.91</small></article><article class="ops-reading"><span>Pressure</span><strong>846 hPa</strong><small>Stable</small></article><article class="ops-reading"><span>Sky quality</span><strong>20.7</strong><small>mag/arcsec2</small></article><article class="ops-reading"><span>Precipitation</span><strong>None</strong><small>Dry / fresh</small></article><article class="ops-reading"><span>Sensor</span><strong>-4.8 C</strong><small>Camera telemetry</small></article></div>
        <div class="ops-grid two"><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Source schedule</h3><p>Environmental observations remain targetless durable history.</p></div><span>Next poll 42s</span></header><div class="ops-table-wrap"><table class="ops-table"><thead><tr><th>Source</th><th>Trigger</th><th>Freshness</th><th>State</th></tr></thead><tbody><tr><td><strong>Virtual weather station</strong></td><td>Every 60 seconds</td><td>18s</td><td><span class="state-chip success">Fresh</span></td></tr><tr><td><strong>Camera telemetry</strong></td><td>After every capture</td><td>4s</td><td><span class="state-chip success">Fresh</span></td></tr><tr><td><strong>Image cloud assessment</strong></td><td>Every capture</td><td>4s</td><td><span class="state-chip success">Fresh</span></td></tr></tbody></table></div></section><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Delivery policy</h3><p>Environment delivery is independent from artifact delivery.</p></div><span>Acknowledged</span></header><dl class="ops-facts"><div><dt>LogicHost export</dt><dd>Enabled</dd></div><div><dt>Pending</dt><dd>0</dd></div><div><dt>Oldest record</dt><dd>30 days</dd></div><div><dt>Capture links</dt><dd>Available</dd></div><div class="full"><dt>Last acknowledgment</dt><dd>03:13:58 UTC / environment #319401</dd></div></dl></section></div>`;
}

function renderTransients() {
    return `
        ${pageHeader("Processing / independent detector lane", "Transients", "Inspect local causal candidate extraction and the handoff to authoritative centered validation without conflating it with display processing.", `<a class="button secondary" href="index.html?run=84220">Open event run</a>${prototypeButton("Review detector profile", "Transient detector profile opened in prototype.")}`)}
        <section class="ops-profile-banner"><div><span class="status-icon warning" aria-hidden="true"></span><span><strong>Hybrid detector active / one confirmed event</strong><small>Causal edge extraction remains independent from the current display product.</small></span></div><dl><div><dt>Profile</dt><dd>meteor-v1.4</dd></div><div><dt>Window</dt><dd>N-2..N+2</dd></div><div><dt>Pending context</dt><dd>0</dd></div></dl></section>
        <div class="ops-grid two"><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Recent detector outcomes</h3><p>No-candidate is a successful bounded result.</p></div><span>Last 6 captures</span></header><div class="ops-table-wrap"><table class="ops-table"><thead><tr><th>Endpoint</th><th>Edge outcome</th><th>Central state</th><th>Evidence</th></tr></thead><tbody><tr class="current-row"><td><strong>#84220</strong></td><td>1 causal candidate</td><td><span class="state-chip warning">Confirmed fireball</span></td><td>6 retained artifacts</td></tr><tr><td><strong>#84219</strong></td><td>No candidate</td><td>Not required</td><td>Extraction receipt</td></tr><tr><td><strong>#84218</strong></td><td>No candidate</td><td>Not required</td><td>Extraction receipt</td></tr></tbody></table></div></section><section class="ops-panel"><header class="ops-panel-heading"><div><h3>31 August Fireball</h3><p>Event E-1042 / centered context was resolved and assessed centrally.</p></div><span class="state-chip warning">Fireball</span></header><dl class="ops-facts"><div><dt>Causal frames</dt><dd>N-2, N-1, N</dd></div><div><dt>Centered window</dt><dd>N-2 through N+2</dd></div><div><dt>Confidence</dt><dd>0.94</dd></div><div><dt>Review</dt><dd>Owner confirmed</dd></div><div class="full"><dt>Local hold</dt><dd>Raw evidence pinned by event reference</dd></div></dl></section></div>`;
}

function renderAutomations() {
    return `
        ${pageHeader("Automation / typed scheduled work", "Automations", "Generate observing-day products and perform bounded maintenance through registered task types, explicit triggers, and durable run history.", `${prototypeButton("Create automation", "A typed automation wizard would open; arbitrary commands are not accepted.", "primary")}`)}
        <div class="ops-note-banner"><span class="status-icon success" aria-hidden="true"></span><span><strong>Capture Schedule remains separate.</strong> Capture Schedule admits exposures; Automations consume durable data or perform explicitly supported maintenance after defined boundaries.</span></div>
        <nav class="automation-tabs" aria-label="Automation views"><button class="active" type="button" data-automation-view="definitions" aria-pressed="true">Definitions</button><button type="button" data-automation-view="schedule" aria-pressed="false">Schedule</button><button type="button" data-automation-view="runs" aria-pressed="false">Run history</button></nav>
        <div id="automationDefinitions" class="automation-view">
            <section class="ops-status-rail"><div><span>Enabled</span><strong>5</strong><small>Typed definitions</small></div><div><span>Next run</span><strong>21:00</strong><small>Hourly timelapse</small></div><div><span>Running</span><strong>0</strong><small>No active tasks</small></div><div><span>Last 24 hours</span><strong>14 / 14</strong><small>Successful runs</small></div></section>
            <div class="automation-card-grid">${automationDefinitions.map(renderAutomationCard).join("")}
                <article class="automation-card unsupported"><header><span class="automation-icon reboot"><svg viewBox="0 0 20 20"><path d="M10 2v7"></path><path d="M6 4a7 7 0 1 0 8 0"></path></svg></span><div><h3>Scheduled CameraAgent Restart</h3><p>Requires authenticated lifecycle support, graceful drain, restart verification, and a terminal receipt.</p></div><span class="state-chip neutral">Design target</span></header><dl><div><dt>Task type</dt><dd>Not registered</dd></div><div><dt>Interface</dt><dd>Installer CLI only</dd></div><div><dt>Scheduling</dt><dd>Unavailable</dd></div><div><dt>Safety</dt><dd>Lifecycle service required</dd></div></dl><footer><span>Arbitrary command execution is never allowed.</span><button class="button secondary small" type="button" disabled>Unavailable</button></footer></article>
            </div>
        </div>
        <div id="automationSchedule" class="automation-view" hidden>
            <section class="ops-panel"><header class="ops-panel-heading"><div><h3>Next 24 hours</h3><p>Triggers use America/Phoenix and the active observing-day and capture-window boundaries.</p></div><span>18:00 - 18:00 local</span></header><div class="automation-day-track"><span class="automation-night" style="left:5%;width:41%">Capture window / 19:08 - 05:08</span><i class="automation-now" style="left:9%"><small>Now 20:14</small></i><span class="automation-run-marker" style="left:12.5%;top:4.5rem"><strong>21:00</strong><small>Hourly timelapse</small></span><span class="automation-run-marker" style="left:46%;top:7.9rem"><strong>05:08</strong><small>Trail + keogram + final timelapse</small></span><span class="automation-run-marker" style="left:75%;top:4.5rem"><strong>12:00</strong><small>Observing day summary</small></span><span class="automation-run-marker" style="left:76%;top:7.9rem"><strong>12:15</strong><small>Archive eligible history</small></span></div></section>
            <div class="ops-grid two"><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Trigger vocabulary</h3><p>Task types opt into only compatible durable triggers.</p></div></header><div class="ops-validation-list"><div class="ops-validation-item"><i>+</i>Hourly boundary after the source interval closes.</div><div class="ops-validation-item"><i>+</i>Capture-window close from the active schedule.</div><div class="ops-validation-item"><i>+</i>Observing-day rollover at local noon.</div><div class="ops-validation-item"><i>+</i>Fixed local time with deterministic DST behavior.</div><div class="ops-validation-item"><i>+</i>Manual successor run from retained source evidence.</div></div></section><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Execution ownership</h3><p>Local and central definitions remain separate.</p></div></header><dl class="ops-facts"><div><dt>Local definitions</dt><dd>Edited here</dd></div><div><dt>Offline behavior</dt><dd>Continues normally</dd></div><div><dt>Central definitions</dt><dd>LogicHost-owned</dd></div><div><dt>Remote mutation</dt><dd>Not permitted</dd></div><div class="full"><dt>Current executor</dt><dd>HVO Main Fisheye / local durable scheduler</dd></div></dl></section></div>
        </div>
        <div id="automationRuns" class="automation-view" hidden>
            <section class="ops-panel"><header class="ops-panel-heading"><div><h3>Recent retained runs</h3><p>Each retry or regeneration is a successor; generated products are never rewritten.</p></div><span>Completed examples</span></header><div class="ops-table-wrap"><table class="ops-table"><thead><tr><th>Run</th><th>Automation</th><th>Trigger</th><th>Source window</th><th>Outcome</th><th>Product</th></tr></thead><tbody><tr><td>05:18</td><td><strong>Nightly Star Trail</strong></td><td>Window close</td><td>30 Aug observing day</td><td><span class="state-chip success">Succeeded</span></td><td><a href="product.html?product=aug30-star-trail">30 August Star Trail</a></td></tr><tr><td>05:19</td><td><strong>Hourly Sky Motion Timelapse</strong></td><td>Final segment</td><td>30 Aug observing day</td><td><span class="state-chip warning">Partial</span></td><td><a href="product.html?product=aug30-timelapse">30 August Timelapse</a></td></tr><tr><td>05:12</td><td><strong>Nightly Keogram</strong></td><td>Window close</td><td>29 Aug observing day</td><td><span class="state-chip success">Succeeded</span></td><td><a href="product.html?product=aug29-keogram">29 August Keogram</a></td></tr><tr><td>04:00</td><td><strong>Hourly Sky Motion Timelapse</strong></td><td>Hourly</td><td>03:00 - 04:00 / 30 Aug day</td><td><span class="state-chip success">Succeeded</span></td><td>Segment 9 of 10</td></tr></tbody></table></div></section>
        </div>`;
}

function renderAutomationCard(automation) {
    const icons = { timelapse: `<path d="m7 4 9 6-9 6V4Z"></path>`, trail: `<path d="M3 14c4-7 9-9 14-8M5 17c3-5 7-7 12-7"></path>`, keogram: `<path d="M3 4v12h14M6 7v6m3-8v10m3-7v5m3-7v8"></path>`, summary: `<path d="M4 16V9m4 7V5m4 11v-4m4 4V3"></path>`, archive: `<rect x="3" y="5" width="14" height="12"></rect><path d="M2 3h16v4H2V3Zm6 7h4"></path>` };
    return `<article class="automation-card"><header><span class="automation-icon ${automation.kind}"><svg viewBox="0 0 20 20">${icons[automation.kind]}</svg></span><div><h3>${automation.title}</h3><p>${automation.description}</p></div><span class="state-chip success">Enabled</span></header><dl><div><dt>Trigger</dt><dd>${automation.trigger}</dd></div><div><dt>Runs on</dt><dd>HVO Main Fisheye</dd></div><div><dt>Input</dt><dd>${automation.input}</dd></div><div><dt>Next run</dt><dd>${automation.next}</dd></div></dl><footer><span>${automation.task}</span>${prototypeButton("Edit", `${automation.title} editor opened in prototype.`)}</footer></article>`;
}

function renderDelivery() {
    return `
        ${pageHeader("Data / one-way central integration", "Delivery", "Monitor policy-selected exports, durable retries, and LogicHost acknowledgements without treating central availability as capture correctness.", `${prototypeButton("Export policy", "Delivery export policy opened in prototype.")} ${prototypeButton("Retry eligible", "One eligible outbox item would be retried.", "primary")}`)}
        <section class="ops-status-rail"><div><span>Mode</span><strong>Connected</strong><small>Raw + manifest export</small></div><div><span>LogicHost</span><strong>Healthy</strong><small>18 ms</small></div><div><span>Pending</span><strong>1</strong><small>Retrying</small></div><div><span>Last acknowledgment</span><strong>4s ago</strong><small>Capture #84220</small></div></section>
        <div class="ops-grid sidebar-right"><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Durable outbox</h3><p>Payloads remain pinned until required consumers acknowledge them.</p></div><span>1 active / 0 quarantined</span></header><div class="ops-table-wrap"><table class="ops-table"><thead><tr><th>Capture</th><th>Payload</th><th>State</th><th>Attempt</th><th>Next action</th></tr></thead><tbody><tr class="current-row"><td><strong>#84216</strong></td><td>Raw + manifest / 24.1 MB</td><td><span class="state-chip warning">Retrying</span></td><td>4</td><td>03:18:19 UTC</td></tr><tr><td><strong>#84220</strong></td><td>Raw + manifest / 24.1 MB</td><td><span class="state-chip success">Acknowledged</span></td><td>1</td><td>Complete</td></tr><tr><td><strong>#84219</strong></td><td>Raw + manifest / 24.1 MB</td><td><span class="state-chip success">Acknowledged</span></td><td>2</td><td>Complete</td></tr></tbody></table></div></section><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Export policy</h3><p>Central delivery is not generic two-way sync.</p></div><span>policy r8</span></header><dl class="ops-facts"><div class="full"><dt>Default capture export</dt><dd>Raw frame + capture manifest</dd></div><div><dt>Annotated preview</dt><dd>Disabled</dd></div><div><dt>Event evidence</dt><dd>On candidate</dd></div><div><dt>Environment</dt><dd>Enabled</dd></div><div><dt>Bandwidth limit</dt><dd>12 MB/s</dd></div><div class="full"><dt>Central edit authority</dt><dd>None for local profiles or retention</dd></div></dl></section></div>`;
}

function renderStorage() {
    return `
        ${pageHeader("Data / local evidence safety", "Storage & retention", "Keep immutable evidence, durable lanes, retention eligibility, and disk pressure visible as one local safety boundary.", `${prototypeButton("Retention policy", "Retention policy r10 opened in prototype.")} ${prototypeButton("Run reconciliation", "A bounded storage reconciliation would be requested.")}`)}
        <div class="ops-grid sidebar-right"><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Local capacity</h3><p>Pressure shortens only eligible history; held evidence remains pinned.</p></div><span class="state-chip success">Healthy</span></header><div class="ops-storage-visual"><div class="ops-donut"><span>38%</span></div><dl class="ops-facts"><div><dt>Used</dt><dd>192 GB</dd></div><div><dt>Available</dt><dd>312 GB</dd></div><div><dt>Warning</dt><dd>75%</dd></div><div><dt>Critical</dt><dd>90%</dd></div><div class="full"><dt>Root</dt><dd><code>configured local storage / W6</code></dd></div></dl></div></section><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Retention holds</h3><p>Reasons evidence cannot yet be removed.</p></div><span>28 artifacts</span></header><dl class="ops-facts"><div><dt>Raw ingress</dt><dd>2</dd></div><div><dt>Delivery pending</dt><dd>2</dd></div><div><dt>31 August Fireball</dt><dd>22</dd></div><div><dt>Quarantine</dt><dd>0</dd></div><div class="full"><dt>Oldest held evidence</dt><dd>Capture #84216 / delivery retry</dd></div></dl></section></div>
        <div class="ops-grid two"><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Retention classes</h3><p>Eligibility requires time policy and all required acknowledgements.</p></div></header><div class="ops-table-wrap"><table class="ops-table"><thead><tr><th>Class</th><th>Policy</th><th>Current</th><th>Held</th></tr></thead><tbody><tr><td><strong>Raw evidence</strong></td><td>7 days</td><td>68.4 GB</td><td>4 files</td></tr><tr><td><strong>Display products</strong></td><td>30 days</td><td>21.8 GB</td><td>8 files</td></tr><tr><td><strong>Manifests &amp; receipts</strong></td><td>30 days</td><td>1.2 GB</td><td>6 files</td></tr><tr><td><strong>Event evidence</strong></td><td>Event hold</td><td>2.1 GB</td><td>10 files</td></tr></tbody></table></div></section><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Startup reconciliation</h3><p>Filesystem and SQLite ownership remain consistent.</p></div><span>Last 02:51 UTC</span></header><div class="ops-validation-list"><div class="ops-validation-item"><i>+</i>12,842 sidecars matched durable records.</div><div class="ops-validation-item"><i>+</i>No orphaned payloads found.</div><div class="ops-validation-item"><i>+</i>No malformed manifests quarantined.</div><div class="ops-validation-item"><i>+</i>All event holds resolve to retained evidence.</div></div></section></div>`;
}

function renderHealth() {
    return `
        ${pageHeader("System / bounded diagnostics", "Health & diagnostics", "Separate capture freshness, durable work pressure, host resources, and central dependencies without exposing sensitive payload data.", `<a class="button secondary" href="index.html?run=84220">Inspect latest run</a>${prototypeButton("Refresh checks", "Health checks refreshed in prototype.", "primary")}`)}
        <section class="ops-status-rail"><div><span>Overall</span><strong>Healthy</strong><small>/health 200</small></div><div><span>Image freshness</span><strong>Current</strong><small>4 seconds</small></div><div><span>Local lanes</span><strong>Healthy</strong><small>1 active</small></div><div><span>Central dependency</span><strong>Available</strong><small>18 ms</small></div></section>
        <div class="ops-grid three"><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Acquisition</h3><p>Camera and admission path.</p></div><span class="state-chip success">Healthy</span></header><dl class="ops-facts"><div><dt>Camera</dt><dd>Ready</dd></div><div><dt>Last start</dt><dd>4s ago</dd></div><div><dt>Clock drift</dt><dd>+12 ms</dd></div><div><dt>Failures / 1h</dt><dd>0</dd></div></dl></section><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Processing</h3><p>Graph worker and durable lane.</p></div><span class="state-chip success">Healthy</span></header><dl class="ops-facts"><div><dt>Active</dt><dd>1</dd></div><div><dt>Pending</dt><dd>0</dd></div><div><dt>p95 latency</dt><dd>8.4s</dd></div><div><dt>Failures / 1h</dt><dd>1</dd></div></dl></section><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Host</h3><p>Bounded resource signals.</p></div><span class="state-chip success">Healthy</span></header><dl class="ops-facts"><div><dt>CPU</dt><dd>18%</dd></div><div><dt>Working set</dt><dd>428 MB</dd></div><div><dt>Storage</dt><dd>38%</dd></div><div><dt>Uptime</dt><dd>12d 4h</dd></div></dl></section></div>
        <section class="ops-panel ops-grid"><header class="ops-panel-heading"><div><h3>Dependency checks</h3><p>Central failures degrade integration state but never masquerade as empty data.</p></div><span>03:14:01 UTC</span></header><div class="ops-table-wrap"><table class="ops-table"><thead><tr><th>Check</th><th>Scope</th><th>State</th><th>Latency / age</th><th>Detail</th></tr></thead><tbody><tr><td><strong>Local SQLite</strong></td><td>Required</td><td><span class="state-chip success">Healthy</span></td><td>2 ms</td><td>WAL writable</td></tr><tr><td><strong>Capture storage</strong></td><td>Required</td><td><span class="state-chip success">Healthy</span></td><td>312 GB free</td><td>Write probe verified</td></tr><tr><td><strong>Camera module</strong></td><td>Required</td><td><span class="state-chip success">Healthy</span></td><td>4s</td><td>Last exposure complete</td></tr><tr><td><strong>LogicHost</strong></td><td>Optional</td><td><span class="state-chip success">Available</span></td><td>18 ms</td><td>Authenticated heartbeat</td></tr><tr><td><strong>Catalog</strong></td><td>Processing</td><td><span class="state-chip success">Healthy</span></td><td>Local</td><td>Checksum verified</td></tr></tbody></table></div></section>`;
}

function renderControl() {
    return `
        ${pageHeader("System / authenticated local actions", "System control", "Perform only bounded CameraAgent controls that preserve accepted evidence and produce an auditable terminal receipt.", "")}
        <div class="ops-note-banner"><span class="status-icon success" aria-hidden="true"></span><span><strong>Capture loop is running normally.</strong> No pause, schedule override, drain, or shutdown transition is active.</span></div>
        <div class="ops-grid two"><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Capture control</h3><p>These actions affect local admission, not historical evidence.</p></div></header><div class="ops-action-list"><article class="ops-action-row"><span class="ops-action-icon"><svg viewBox="0 0 20 20"><path d="M7 5v10m6-10v10"></path></svg></span><span><strong>Pause acquisition</strong><small>Finish the current exposure, then block new capture admission.</small></span>${confirmButton("Pause", "Pause acquisition?", "The active exposure can finish. Processing and delivery of accepted work continue.", "Pause intent recorded; runtime state is unchanged.")}</article><article class="ops-action-row"><span class="ops-action-icon"><svg viewBox="0 0 20 20"><rect x="3" y="4" width="14" height="13" rx="2"></rect><path d="M3 8h14"></path></svg></span><span><strong>Temporary schedule override</strong><small>Force open or closed with an explicit expiry and reason.</small></span>${confirmButton("Create", "Create override?", "A bounded schedule override would take precedence until its specified expiry.", "Override intent recorded; runtime state is unchanged.")}</article><article class="ops-action-row"><span class="ops-action-icon"><svg viewBox="0 0 20 20"><path d="M4 5h12v8H8l-4 4V5Z"></path></svg></span><span><strong>Acquire environment now</strong><small>Request an independent durable weather observation.</small></span>${prototypeButton("Acquire", "Environmental acquisition requested in prototype.")}</article></div></section><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Worker &amp; lifecycle boundary</h3><p>Accepted evidence must reach a terminal durable state.</p></div></header><div class="ops-action-list"><article class="ops-action-row"><span class="ops-action-icon"><svg viewBox="0 0 20 20"><path d="M3 10h14M13 6l4 4-4 4"></path></svg></span><span><strong>Request graceful drain</strong><small>Stop acquisition and finish accepted processing before host shutdown.</small></span>${confirmButton("Drain", "Request graceful drain?", "New capture admission would stop while accepted processing and persistence complete.", "Drain intent recorded; runtime state is unchanged.")}</article><article class="ops-action-row danger"><span class="ops-action-icon"><svg viewBox="0 0 20 20"><path d="M10 2v7"></path><path d="M6 4a7 7 0 1 0 8 0"></path></svg></span><span><strong>Restart or upgrade host</strong><small>No authenticated runtime lifecycle service is currently exposed to this UI.</small></span><button class="button secondary" type="button" disabled>CLI only</button></article><article class="ops-action-row danger"><span class="ops-action-icon"><svg viewBox="0 0 20 20"><path d="M4 6h12M8 6V4h4v2m-6 0 1 11h6l1-11"></path></svg></span><span><strong>Purge local evidence</strong><small>Deliberately excluded from routine controls; retention owns normal removal.</small></span><button class="button secondary" type="button" disabled>Unavailable</button></article></div></section></div>
        <section class="ops-panel ops-grid"><header class="ops-panel-heading"><div><h3>Recent control receipts</h3><p>Mutating operations preserve actor, reason, start, and terminal result.</p></div><span>UTC</span></header><div class="ops-table-wrap"><table class="ops-table"><thead><tr><th>Time</th><th>Action</th><th>Actor</th><th>Reason</th><th>Result</th></tr></thead><tbody><tr><td>30 Aug / 02:55</td><td><strong>Override expired</strong></td><td>System</td><td>Bounded maintenance window</td><td><span class="state-chip success">Completed</span></td></tr><tr><td>30 Aug / 02:31</td><td><strong>Force closed</strong></td><td>Local owner</td><td>Clean image circle</td><td><span class="state-chip success">Completed</span></td></tr><tr><td>28 Aug / 04:02</td><td><strong>Resume acquisition</strong></td><td>Local owner</td><td>Rig revision activated</td><td><span class="state-chip success">Completed</span></td></tr></tbody></table></div></section>`;
}

function renderSoftware() {
    return `
        ${pageHeader("System / verified local assets", "Software & catalog", "Inspect installed application and astronomy-catalog identities without implying an automatic update or remote lifecycle service.", `${prototypeButton("Verify checksums", "Installed software and catalog checksums verified in prototype.", "primary")}`)}
        <div class="ops-grid two"><section class="ops-panel"><header class="ops-panel-heading"><div><h3>CameraAgent software</h3><p>Lifecycle operations are currently installer and CLI driven.</p></div><span class="state-chip success">Verified</span></header><dl class="ops-facts"><div><dt>Version</dt><dd>9.0.0-preview.14</dd></div><div><dt>Channel</dt><dd>Local prototype</dd></div><div><dt>Runtime</dt><dd>.NET 10 / linux-x64</dd></div><div><dt>Uptime</dt><dd>12d 4h</dd></div><div class="full"><dt>Image digest</dt><dd><code>sha256:9a1c...e842</code></dd></div><div><dt>Rollback candidate</dt><dd>preview.13</dd></div><div><dt>Update service</dt><dd>Not configured</dd></div></dl></section><section class="ops-panel"><header class="ops-panel-heading"><div><h3>Astronomy catalog</h3><p>Read-only local snapshot with explicit provenance.</p></div><span class="state-chip success">Production</span></header><dl class="ops-facts"><div><dt>Catalog</dt><dd>HYG 4.2</dd></div><div><dt>Rows</dt><dd>119,625</dd></div><div><dt>SQLite schema</dt><dd>user_version 2</dd></div><div><dt>Selection</dt><dd>Current</dd></div><div class="full"><dt>Checksum</dt><dd><code>sha256:b51d...0b9e2</code></dd></div><div><dt>Previous</dt><dd>HYG 4.1</dd></div><div><dt>Restart required</dt><dd>No</dd></div></dl></section></div>
        <section class="ops-panel ops-grid"><header class="ops-panel-heading"><div><h3>Lifecycle boundary</h3><p>The static design distinguishes evidence from capabilities not yet exposed through Blazor.</p></div></header><div class="ops-table-wrap"><table class="ops-table"><thead><tr><th>Operation</th><th>Current interface</th><th>UI state</th><th>Safety boundary</th></tr></thead><tbody><tr><td><strong>Status &amp; verification</strong></td><td>UI / CLI</td><td><span class="state-chip success">Available</span></td><td>Read-only installed facts</td></tr><tr><td><strong>Catalog select / rollback</strong></td><td>CLI</td><td>Informational</td><td>Atomic validation and activation</td></tr><tr><td><strong>Application upgrade / rollback</strong></td><td>Installer CLI</td><td>Informational</td><td>Candidate health before capture resumes</td></tr><tr><td><strong>Uninstall / purge</strong></td><td>Installer CLI</td><td>Not exposed</td><td>Separate confirmation and retained state</td></tr></tbody></table></div></section>`;
}

function currentSectionFromUrl() {
    const requested = new URLSearchParams(window.location.search).get("section") ?? "overview";
    return operationsSections[requested] ? requested : "overview";
}

function renderOperationsSection(sectionName, updateHistory = false) {
    const section = operationsSections[sectionName] ?? operationsSections.overview;
    const page = document.getElementById("operationsPage");
    page.innerHTML = section.render();
    document.title = `${section.title} | HVO SkyMonitor Prototype`;

    document.querySelectorAll(".operations-navigation a[data-operations-section]").forEach(link => {
        const active = link.dataset.operationsSection === sectionName;
        link.classList.toggle("active", active);
        if (active) link.setAttribute("aria-current", "page"); else link.removeAttribute("aria-current");
    });

    if (updateHistory) {
        const url = new URL(window.location.href);
        if (sectionName === "overview") url.searchParams.delete("section"); else url.searchParams.set("section", sectionName);
        if (sectionName !== "automations") url.searchParams.delete("view");
        window.history.pushState({ section: sectionName }, "", url);
    }

    setOperationsSidebarOpen(false);
    initializeSectionInteractions(sectionName);
    if (updateHistory) requestAnimationFrame(() => page.querySelector("h2")?.focus());
    document.getElementById("main-content").scrollTo?.({ top: 0 });
    window.scrollTo({ top: 0, behavior: "instant" });
}

function initializeSectionInteractions(sectionName) {
    if (sectionName === "pipeline") {
        document.querySelectorAll("[data-pipeline-node]").forEach(button => button.addEventListener("click", () => selectPipelineNode(button.dataset.pipelineNode)));
    }
    if (sectionName === "focus") initializeFocusSession();
    if (sectionName === "automations") initializeAutomationViews();
}

function initializeAutomationViews() {
    const requested = new URLSearchParams(window.location.search).get("view") ?? "definitions";
    function select(view) {
        const validView = ["definitions", "schedule", "runs"].includes(view) ? view : "definitions";
        document.querySelectorAll("[data-automation-view]").forEach(button => {
            const active = button.dataset.automationView === validView;
            button.classList.toggle("active", active);
            button.setAttribute("aria-pressed", String(active));
        });
        document.getElementById("automationDefinitions").hidden = validView !== "definitions";
        document.getElementById("automationSchedule").hidden = validView !== "schedule";
        document.getElementById("automationRuns").hidden = validView !== "runs";
        const url = new URL(window.location.href);
        if (validView === "definitions") url.searchParams.delete("view"); else url.searchParams.set("view", validView);
        window.history.replaceState({ section: "automations", view: validView }, "", url);
    }
    document.querySelectorAll("[data-automation-view]").forEach(button => button.addEventListener("click", () => select(button.dataset.automationView)));
    select(requested);
}

function selectPipelineNode(nodeId) {
    const detail = pipelineNodeDetails[nodeId];
    if (!detail) return;
    document.querySelectorAll("[data-pipeline-node]").forEach(button => {
        const active = button.dataset.pipelineNode === nodeId;
        button.classList.toggle("active", active);
        button.setAttribute("aria-pressed", String(active));
    });
    document.getElementById("pipelineNodeType").textContent = detail.type;
    document.getElementById("pipelineNodeTitle").textContent = detail.title;
    document.getElementById("pipelineNodeDescription").textContent = detail.description;
    document.getElementById("pipelineNodeAlias").textContent = detail.alias;
    document.getElementById("pipelineNodeInput").textContent = detail.input;
    document.getElementById("pipelineNodeOutput").textContent = detail.output;
    document.getElementById("pipelineNodeOptions").textContent = detail.options;
}

function initializeFocusSession() {
    const start = document.getElementById("focusStart");
    const sample = document.getElementById("focusSample");
    let sampleIndex = 0;
    let sessionActive = false;
    let sessionComplete = false;
    const samples = ["3.16", "2.74", "2.31", "2.18"];
    start.addEventListener("click", () => {
        if (sessionComplete) {
            document.getElementById("focusStatus").textContent = "Session result retained";
            document.getElementById("focusStatusDetail").textContent = "Measurement evidence saved / camera profile unchanged";
            document.getElementById("focusStateChip").textContent = "Saved";
            start.textContent = "Start new session";
            sessionComplete = false;
            sampleIndex = 0;
            showOperationsToast("Manual focus evidence retained; no camera profile or hardware state changed.");
            return;
        }
        if (sessionActive) {
            sessionActive = false;
            sample.disabled = true;
            start.textContent = "Start new session";
            document.getElementById("focusStatus").textContent = "Session ended without saving";
            document.getElementById("focusStatusDetail").textContent = `${sampleIndex} samples measured / no result retained`;
            const chip = document.getElementById("focusStateChip");
            chip.textContent = "Idle";
            chip.className = "state-chip neutral";
            showOperationsToast("Focus preview ended; no session evidence was retained.");
            return;
        }
        sampleIndex = 0;
        sessionActive = true;
        document.getElementById("focusStatus").textContent = "Manual session active";
        document.getElementById("focusStatusDetail").textContent = "Adjust the lens, then record another sample";
        const chip = document.getElementById("focusStateChip");
        chip.textContent = "Measuring";
        chip.className = "state-chip running";
        start.textContent = "End session";
        sample.disabled = false;
        showOperationsToast("Focus preview started; no motor command or runtime capture was issued.");
    });
    sample.addEventListener("click", () => {
        document.getElementById("focusScore").innerHTML = `${samples[sampleIndex]} <small>px</small>`;
        sampleIndex += 1;
        if (sampleIndex === samples.length) {
            sessionActive = false;
            sample.disabled = true;
            start.textContent = "Save session result";
            sessionComplete = true;
            document.getElementById("focusStatus").textContent = "Stable best result";
            document.getElementById("focusStatusDetail").textContent = "Four improving samples / verify lens lock";
            const chip = document.getElementById("focusStateChip");
            chip.textContent = "Best 2.18 px";
            chip.className = "state-chip success";
        }
    });
}

let operationsToastTimer;
function showOperationsToast(message) {
    const toast = document.getElementById("operationsToast");
    document.getElementById("operationsToastText").textContent = message;
    toast.hidden = false;
    clearTimeout(operationsToastTimer);
    operationsToastTimer = setTimeout(() => { toast.hidden = true; }, 4000);
}

const operationsLayout = document.getElementById("operationsLayout");
const operationsNavToggle = document.getElementById("operationsNavToggle");
const operationsMainContent = document.getElementById("main-content");
const operationsSidebar = document.getElementById("operationsSidebar");
const operationsSidebarClose = document.getElementById("operationsSidebarClose");
const operationsSidebarBackdrop = document.getElementById("operationsSidebarBackdrop");
const operationsDialog = document.getElementById("operationsConfirmDialog");
const mobileOperationsNavigation = window.matchMedia("(max-width: 940px)");
let pendingConfirmationResult = "";

function setOperationsSidebarOpen(open, restoreFocus = false) {
    const mobile = mobileOperationsNavigation.matches;
    const shouldOpen = mobile && open;
    operationsLayout.classList.toggle("sidebar-open", shouldOpen);
    operationsNavToggle.setAttribute("aria-expanded", String(shouldOpen));
    operationsSidebarBackdrop.hidden = !shouldOpen;
    operationsSidebar.inert = mobile && !shouldOpen;
    operationsMainContent.inert = shouldOpen;
    operationsSidebar.setAttribute("aria-hidden", String(mobile && !shouldOpen));
    if (shouldOpen) requestAnimationFrame(() => operationsSidebarClose.focus());
    if (!shouldOpen && restoreFocus) operationsNavToggle.focus();
}

operationsNavToggle.addEventListener("click", () => {
    const open = !operationsLayout.classList.contains("sidebar-open");
    setOperationsSidebarOpen(open, !open);
});
operationsSidebarClose.addEventListener("click", () => setOperationsSidebarOpen(false, true));
operationsSidebarBackdrop.addEventListener("click", () => setOperationsSidebarOpen(false, true));
mobileOperationsNavigation.addEventListener("change", () => setOperationsSidebarOpen(false));
document.addEventListener("keydown", event => {
    if (!operationsLayout.classList.contains("sidebar-open")) return;
    if (event.key === "Escape") {
        setOperationsSidebarOpen(false, true);
        return;
    }
    if (event.key !== "Tab") return;
    const focusable = [...operationsSidebar.querySelectorAll("a[href], button:not([disabled])")].filter(element => element.offsetParent !== null);
    if (focusable.length === 0) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
    }
});

document.addEventListener("click", event => {
    const sectionLink = event.target.closest("a[data-operations-section]");
    if (sectionLink) {
        event.preventDefault();
        renderOperationsSection(sectionLink.dataset.operationsSection, true);
        return;
    }
    const prototypeAction = event.target.closest("[data-prototype-action]");
    if (prototypeAction) {
        showOperationsToast(prototypeAction.dataset.prototypeAction);
        return;
    }
    const confirmation = event.target.closest("[data-confirm-title]");
    if (confirmation) {
        document.getElementById("operationsDialogTitle").textContent = confirmation.dataset.confirmTitle;
        document.getElementById("operationsDialogText").textContent = confirmation.dataset.confirmText;
        pendingConfirmationResult = confirmation.dataset.confirmResult;
        operationsDialog.returnValue = "";
        operationsDialog.showModal();
    }
});

operationsDialog.addEventListener("close", () => {
    const confirmed = operationsDialog.returnValue === "confirm";
    operationsDialog.returnValue = "";
    if (confirmed) showOperationsToast(pendingConfirmationResult);
    pendingConfirmationResult = "";
});

window.addEventListener("popstate", () => renderOperationsSection(currentSectionFromUrl()));
setOperationsSidebarOpen(false);
renderOperationsSection(currentSectionFromUrl());
