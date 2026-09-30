(() => {
    "use strict";

    const observatories = [
        {
            id: "hualapai",
            code: "HVO",
            name: "Hualapai Valley Observatory",
            region: "Mohave County, Arizona",
            disclosure: "Approximate / 25 km",
            visibility: "public",
            status: "healthy",
            heartbeat: "8s ago",
            members: 3,
            profile: "pub-hvo-07",
            image: "assets/asi174-20260831-045406-utc.jpg",
            summary: "Primary all-sky survey with independent horizon coverage.",
            cameras: [
                {
                    id: "main-fisheye",
                    code: "W6",
                    name: "HVO Main Fisheye",
                    status: "online",
                    previewContent: "assets/asi174-20260831-045406-utc.jpg",
                    imageAge: "4s",
                    capture: "#84220",
                    captured: "03:13:55 UTC",
                    agent: "dev_01J2W6K4B95PA7M",
                    installation: "install-hvo-w6-02",
                    schedule: "Hualapai Night Schedule / r12",
                    pipeline: "Layered All-Sky Processing / r31",
                    ingest: "Acknowledged",
                    submission: "Accepted / #84220",
                    fleetObserved: "8s ago",
                    role: "Preview",
                    releaseArtifact: { capture: "#84220", captured: "2026-09-01 03:13:55 UTC", artifactId: "art-hvo-84220-preview", role: "Preview", variant: "central-display-v2", content: "assets/asi174-20260831-045406-utc.jpg", decision: "Released", releaseId: "release-art-84220-preview" },
                    localPrototype: true
                },
                {
                    id: "north-horizon",
                    code: "N1",
                    name: "North Horizon Imager",
                    status: "online",
                    previewContent: "assets/hualapai-night.jpg",
                    imageAge: "18s",
                    capture: "#31148",
                    captured: "03:13:41 UTC",
                    agent: "dev_01J2N1Q8PM2CV4R",
                    installation: "install-hvo-n1-01",
                    schedule: "North Horizon Night Watch / r8",
                    pipeline: "Horizon Contrast Survey / r14",
                    ingest: "Acknowledged",
                    submission: "Accepted / #31148",
                    fleetObserved: "11s ago",
                    role: "Preview",
                    releaseArtifact: { capture: "#31148", captured: "2026-09-01 03:13:41 UTC", artifactId: "art-hvo-31148-preview", role: "Preview", variant: "central-display-v2", content: "assets/hualapai-night.jpg", decision: "Released", releaseId: "release-art-31148-preview" }
                }
            ]
        },
        {
            id: "siding-spring",
            code: "SSO",
            name: "Siding Spring Field Station",
            region: "New South Wales, Australia",
            disclosure: "Approximate / 50 km",
            visibility: "public",
            status: "attention",
            heartbeat: "42s ago",
            members: 2,
            profile: "pub-sso-03",
            image: "assets/siding-spring-night.jpg",
            summary: "Southern-sky monitoring with one camera awaiting delivery review.",
            cameras: [
                {
                    id: "all-sky-south",
                    code: "S1",
                    name: "All-Sky South",
                    status: "online",
                    previewContent: "assets/siding-spring-night.jpg",
                    imageAge: "22s",
                    capture: "#19407",
                    captured: "03:13:37 UTC",
                    agent: "dev_01J2S1D6TQ7MV9K",
                    installation: "install-sso-s1-03",
                    schedule: "Southern Survey Window / r21",
                    pipeline: "Southern Sky Presentation / r9",
                    ingest: "Acknowledged",
                    submission: "Accepted / #19407",
                    fleetObserved: "42s ago",
                    role: "Preview",
                    releaseArtifact: { capture: "#19407", captured: "2026-09-01 03:13:37 UTC", artifactId: "art-sso-19407-preview", role: "Preview", variant: "central-display-v2", content: "assets/siding-spring-night.jpg", decision: "Released", releaseId: "release-art-19407-preview" }
                },
                {
                    id: "meteor-east",
                    code: "M2",
                    name: "Meteor East",
                    status: "attention",
                    imageAge: "7m 22s",
                    capture: "#7603",
                    captured: "03:06:19 UTC",
                    agent: "dev_01J2M2V4DK8TY3A",
                    installation: "install-sso-m2-01",
                    schedule: "Eastern Meteor Watch / r4",
                    pipeline: "Meteor Evidence Capture / r6",
                    ingest: "Acknowledged",
                    submission: "Retrying / #7604",
                    fleetObserved: "49s ago",
                    role: "Preview"
                }
            ]
        },
        {
            id: "desert-ridge",
            code: "DRO",
            name: "Desert Ridge Observatory",
            region: "Southwestern United States",
            disclosure: "Hidden",
            visibility: "private",
            status: "offline",
            heartbeat: "2h 18m ago",
            members: 1,
            profile: "No public profile",
            image: "assets/w6-current.jpg",
            summary: "Private commissioning site isolated from public discovery.",
            cameras: [
                {
                    id: "commissioning-sky",
                    code: "C1",
                    name: "Commissioning Sky Camera",
                    status: "offline",
                    imageAge: "2h 19m",
                    capture: "#1182",
                    captured: "00:55:04 UTC",
                    agent: "dev_01J2C1N5XZ8BW6H",
                    installation: "install-dro-c1-01",
                    schedule: "Commissioning Window / r3",
                    pipeline: "Commissioning Validation / r5",
                    ingest: "Acknowledged",
                    submission: "No report since #1182",
                    fleetObserved: "2h 18m ago",
                    role: "Preview"
                }
            ]
        }
    ];

    const cameraProcessingFixtures = {
        "hualapai/main-fisheye": {
            edge: {
                id: "edge-graph-w6-r31",
                revision: "Layered All-Sky Processing / r31",
                digest: "7f9c4a1d...c20e",
                schema: "cameraagent-local-profile-v2",
                observedAt: "2026-09-01 03:13:55 UTC",
                receivedAt: "2026-09-01 03:14:03 UTC",
                capture: "#84220",
                nodes: [
                    { id: "raw-evidence", title: "Durable raw evidence", type: "Capture source", requirement: "Required", level: 0, dependsOn: [], input: "CameraFrame / ASI174 Mono8", outputs: ["Raw / camera-native-v1"], options: "Immutable ingress source; not rerun by LogicHost." },
                    { id: "calibrate", title: "Reference calibration", type: "Shared transform", requirement: "Required", level: 1, dependsOn: ["raw-evidence"], input: "Raw + active reference set", outputs: ["Calibrated / linear-calibrated-v1"], options: "Reference set cal-hvo-w6-r8; fail closed on incompatibility." },
                    { id: "rolling", title: "Causal rolling mean", type: "Shared window", requirement: "Required", level: 2, dependsOn: ["calibrate"], input: "N-4..N calibrated", outputs: ["Combined / causal-mean-v1"], options: "Five compatible frames; endpoint N; no future context." },
                    { id: "quality", title: "Image quality", type: "Shared analyzer", requirement: "Optional", level: 2, dependsOn: ["calibrate"], input: "Calibrated", outputs: ["Metadata / image-quality-v2"], options: "Background, clipping, star residual, and sharpness facts." },
                    { id: "preview", title: "Encoded edge preview", type: "Shared transform", requirement: "Required", level: 3, dependsOn: ["rolling"], input: "Combined", outputs: ["Preview / edge-display-v3"], options: "JPEG 91; maximum dimension 1936; display tone map edge-v4." },
                    { id: "scene-layers", title: "Scene presentation layers", type: "Shared analyzer", requirement: "Optional", level: 3, dependsOn: ["rolling", "quality"], input: "Combined + projected scene", outputs: ["Metadata / presentation-layers-v2"], options: "Measured, expected, predicted, cardinal, circle, and corner payloads." },
                    { id: "materialize", title: "Edge presentation", type: "Shared transform", requirement: "Optional", level: 4, dependsOn: ["preview", "scene-layers"], input: "Preview + layer manifest", outputs: ["AnnotatedPreview / edge-layered-v2"], options: "Materialized local display; structured layers remain separate evidence." },
                    { id: "edge-transient", title: "Causal transient scan", type: "Edge-only analyzer", requirement: "Optional", level: 3, dependsOn: ["calibrate"], input: "N-2..N linear window", outputs: ["Metadata / transient-candidate-v1"], options: "Low-latency candidate delivery; never authoritative central validation." },
                    { id: "persist-deliver", title: "Persist and deliver", type: "CameraAgent orchestration", requirement: "Required", level: 5, dependsOn: ["preview"], input: "Required preview plus any produced optional products", outputs: ["Local archive", "Manifest-v2 outbox"], options: "Optional skips do not block required persistence; storage and upload acknowledgements remain independent." }
                ]
            },
            central: {
                id: "central-graph-hvo-r18",
                revision: "HVO central processing policy / r18",
                digest: "a4619c73...08bf",
                schema: "central-derivative-policy-v3",
                effectiveFrom: "2026-09-01 00:00:00 UTC",
                policy: "central-policy-hvo-18",
                nodes: [
                    { id: "ingested-artifacts", title: "Verified ingested artifacts", type: "Central source", requirement: "Required", level: 0, dependsOn: [], input: "Manifest-v2 artifact set", outputs: ["Authorized reconstructable inputs"], options: "Length, checksum, descriptor, profile, and scope verification." },
                    { id: "reconstruct", title: "Central reconstruction", type: "LogicHost infrastructure", requirement: "Required", level: 1, dependsOn: ["ingested-artifacts"], input: "Raw or compatible calibrated artifact", outputs: ["ProcessingArtifact input"], options: "Uses capture-time descriptors; never current CameraAgent configuration." },
                    { id: "central-preview", title: "Central preview", type: "Shared transform", requirement: "Required", level: 2, dependsOn: ["reconstruct"], input: "Reconstructed calibrated source", outputs: ["Preview / central-display-v2"], options: "JPEG 92; maximum dimension 1936; central tone map v2." },
                    { id: "cloud", title: "Cloud assessment", type: "Shared analyzer", requirement: "Optional", level: 2, dependsOn: ["reconstruct"], input: "Calibrated + environmental association", outputs: ["Metadata / cloud-assessment-v2"], options: "Observatory override 620000 millionths; stale input remains explicit." },
                    { id: "projected-scene", title: "Projected scene", type: "Shared analyzer", requirement: "Optional", level: 2, dependsOn: ["reconstruct"], input: "Capture geometry + catalog snapshots", outputs: ["Metadata / projected-scene-v2"], options: "HYG 4.4 + OpenNGC package identities pinned at execution." },
                    { id: "central-layers", title: "Central layer manifest", type: "Shared analyzer", requirement: "Optional", level: 3, dependsOn: ["projected-scene", "cloud"], input: "Projected scene + cloud facts", outputs: ["Metadata / presentation-layers-v2"], options: "Measured associations, expected context, and predicted diagnostics." },
                    { id: "central-materialize", title: "Central presentation", type: "Shared transform", requirement: "Optional", level: 4, dependsOn: ["central-preview", "central-layers"], input: "Preview + structured layers", outputs: ["AnnotatedPreview / central-layered-v2"], options: "Current policy presentation; no publication decision implied." },
                    { id: "centered-transient", title: "Centered transient validation", type: "Central-only window", requirement: "Optional", level: 3, dependsOn: ["reconstruct"], input: "N-2..N+2 compatible linear window", outputs: ["Assessment / transient-validation-v1"], options: "Waits for future context and preserves every assessment version." },
                    { id: "release-eligibility", title: "Release eligibility", type: "Central-only gate", requirement: "Optional", level: 5, dependsOn: ["central-preview"], input: "Complete Preview plus any produced optional presentation + policy", outputs: ["Eligibility decision"], options: "A complete Preview can be eligible without an AnnotatedPreview. Eligibility is not release; owner publication remains a separate decision." }
                ]
            },
            executions: [
                {
                    id: "edge-exec-w6-84220-r31",
                    origin: "edge",
                    originLabel: "Received edge execution",
                    graph: "edge-graph-w6-r31",
                    capture: "#84220",
                    state: "Produced",
                    tone: "success",
                    attempt: 1,
                    started: "2026-09-01 03:13:55 UTC",
                    completed: "2026-09-01 03:13:57 UTC",
                    duration: "2.02s",
                    trigger: "Local admitted capture",
                    source: "art-w6-84220-raw",
                    nodes: [
                        { name: "Reference calibration", state: "Produced", tone: "success", attempt: 1, duration: "412ms", reason: "-", output: "art-w6-84220-calibrated" },
                        { name: "Causal rolling mean", state: "Produced", tone: "success", attempt: 1, duration: "731ms", reason: "-", output: "art-w6-84220-combined" },
                        { name: "Image quality", state: "Produced", tone: "success", attempt: 1, duration: "167ms", reason: "-", output: "art-w6-84220-quality" },
                        { name: "Encoded edge preview", state: "Produced", tone: "success", attempt: 1, duration: "180ms", reason: "-", output: "art-w6-84220-edge-preview" },
                        { name: "Scene presentation layers", state: "Produced", tone: "success", attempt: 1, duration: "205ms", reason: "-", output: "art-w6-84220-edge-layers" },
                        { name: "Edge presentation", state: "Produced", tone: "success", attempt: 1, duration: "273ms", reason: "-", output: "art-w6-84220-edge-layered" },
                        { name: "Causal transient scan", state: "Skipped", tone: "pending", attempt: 1, duration: "34ms", reason: "transient.no-candidate", output: "-" },
                        { name: "Persist and deliver", state: "Completed", tone: "success", attempt: 1, duration: "21ms", reason: "-", output: "manifest-v2 set / acknowledged" }
                    ],
                    artifacts: [
                        { id: "art-w6-84220-calibrated", role: "Calibrated", variant: "linear-calibrated-v1", state: "Delivered", lineage: "art-w6-84220-raw" },
                        { id: "art-w6-84220-combined", role: "Combined", variant: "causal-mean-v1", state: "Local retained", lineage: "art-w6-84216-calibrated..art-w6-84220-calibrated" },
                        { id: "art-w6-84220-quality", role: "Metadata", variant: "image-quality-v2", state: "Delivered", lineage: "art-w6-84220-calibrated" },
                        { id: "art-w6-84220-edge-preview", role: "Preview", variant: "edge-display-v3", state: "Delivered", lineage: "art-w6-84220-combined" },
                        { id: "art-w6-84220-edge-layers", role: "Metadata", variant: "presentation-layers-v2", state: "Delivered", lineage: "art-w6-84220-combined" },
                        { id: "art-w6-84220-edge-layered", role: "AnnotatedPreview", variant: "edge-layered-v2", state: "Local retained", lineage: "art-w6-84220-edge-preview + art-w6-84220-edge-layers" }
                    ]
                },
                {
                    id: "central-exec-hvo-84220-r18",
                    origin: "central",
                    originLabel: "Central ingest-triggered execution",
                    graph: "central-graph-hvo-r18",
                    capture: "#84220",
                    state: "Produced",
                    tone: "success",
                    attempt: 1,
                    started: "2026-09-01 03:14:04 UTC",
                    completed: "2026-09-01 03:14:07 UTC",
                    duration: "2.38s",
                    trigger: "Verified central ingest",
                    source: "art-w6-84220-calibrated",
                    nodes: [
                        { name: "Central reconstruction", state: "Produced", tone: "success", attempt: 1, duration: "96ms", reason: "-", output: "input-hvo-84220-calibrated" },
                        { name: "Central preview", state: "Produced", tone: "success", attempt: 1, duration: "644ms", reason: "-", output: "art-hvo-84220-preview" },
                        { name: "Cloud assessment", state: "Produced", tone: "success", attempt: 1, duration: "381ms", reason: "-", output: "art-hvo-84220-cloud" },
                        { name: "Projected scene", state: "Produced", tone: "success", attempt: 1, duration: "502ms", reason: "-", output: "art-hvo-84220-scene" },
                        { name: "Central layer manifest", state: "Produced", tone: "success", attempt: 1, duration: "318ms", reason: "-", output: "art-hvo-84220-layers" },
                        { name: "Central presentation", state: "Produced", tone: "success", attempt: 1, duration: "439ms", reason: "-", output: "art-hvo-84220-layered" }
                    ],
                    artifacts: [
                        { id: "art-hvo-84220-preview", role: "Preview", variant: "central-display-v2", state: "Available", lineage: "art-w6-84220-calibrated" },
                        { id: "art-hvo-84220-cloud", role: "Metadata", variant: "cloud-assessment-v2", state: "Available", lineage: "art-w6-84220-calibrated" },
                        { id: "art-hvo-84220-layers", role: "Metadata", variant: "presentation-layers-v2", state: "Available", lineage: "art-hvo-84220-scene + art-hvo-84220-cloud" },
                        { id: "art-hvo-84220-layered", role: "AnnotatedPreview", variant: "central-layered-v2", state: "Available", lineage: "art-hvo-84220-preview + art-hvo-84220-layers" }
                    ]
                },
                {
                    id: "central-exec-hvo-84219-r18-reprocess-01",
                    origin: "central",
                    originLabel: "Central successor reprocess",
                    graph: "central-graph-hvo-r18",
                    capture: "#84219",
                    state: "Retryable failure",
                    tone: "warning",
                    attempt: 2,
                    started: "2026-09-01 03:19:12 UTC",
                    completed: "2026-09-01 03:19:14 UTC",
                    duration: "1.91s",
                    trigger: "Owner successor reprocess",
                    source: "art-w6-84219-calibrated",
                    nodes: [
                        { name: "Central reconstruction", state: "Produced", tone: "success", attempt: 1, duration: "102ms", reason: "-", output: "input-hvo-84219-calibrated" },
                        { name: "Central preview", state: "Produced", tone: "success", attempt: 1, duration: "651ms", reason: "-", output: "art-hvo-84219-preview-r2" },
                        { name: "Cloud assessment", state: "Retryable failure", tone: "warning", attempt: 2, duration: "1.15s", reason: "environment.association-pending", output: "-" },
                        { name: "Central layer manifest", state: "Waiting", tone: "pending", attempt: 0, duration: "-", reason: "processing.missing-input", output: "-" }
                    ],
                    artifacts: [
                        { id: "art-hvo-84219-preview-r2", role: "Preview", variant: "central-display-v2", state: "Available", lineage: "art-w6-84219-calibrated" }
                    ]
                }
            ],
            comparison: [
                { subject: "Source calibration", edge: "art-w6-84220-calibrated", central: "art-w6-84220-calibrated", result: "Exact shared input", match: true },
                { subject: "Preview recipe", edge: "encoded-preview / edge-display-v3", central: "encoded-preview / central-display-v2", result: "Different options and variant", match: false },
                { subject: "Preview output", edge: "art-w6-84220-edge-preview", central: "art-hvo-84220-preview", result: "Independent immutable artifacts", match: false },
                { subject: "Layer schema", edge: "presentation-layers-v2", central: "presentation-layers-v2", result: "Same structured schema", match: true },
                { subject: "Layer inputs", edge: "Local projected scene", central: "Pinned central catalogs + environment", result: "Different contextual inputs", match: false },
                { subject: "Publication", edge: "Not a local concern", central: "art-hvo-84220-preview released", result: "Central-only decision", match: false }
            ],
            presentation: {
                id: "presentation-policy-hvo-w6-r7",
                revision: "r7",
                base: "art-hvo-84220-preview",
                baseRule: "Prefer complete central Preview; uploaded edge Preview is a labeled temporary fallback",
                groups: ["measured", "context", "diagnostics"],
                publicEligibility: "Complete Preview only; release remains a separate owner decision"
            },
            proposal: {
                targetAgent: "dev_01J2W6K4B95PA7M",
                installation: "install-hvo-w6-02",
                expectedBase: "edge-graph-w6-r31 / 7f9c4a1d...c20e",
                candidate: "No successor proposal created",
                channel: "No agent-initiated proposal-channel evidence",
                state: "Not proposed"
            }
        }
    };

    const iconPaths = {
        home: '<path d="M3 9.5 10 3l7 6.5V17h-5v-5H8v5H3V9.5Z"></path>',
        observatory: '<path d="M10 17V8m0 0 4 4m-4-4-4 4"></path><circle cx="10" cy="5" r="2"></circle><path d="M4 17h12"></path>',
        camera: '<rect x="3" y="6" width="14" height="10" rx="2"></rect><circle cx="10" cy="11" r="3"></circle><path d="m6 6 1-2h6l1 2"></path>',
        operations: '<circle cx="10" cy="10" r="3"></circle><path d="M10 2v2m0 12v2M2 10h2m12 0h2M4.3 4.3l1.4 1.4m8.6 8.6 1.4 1.4m0-11.4-1.4 1.4m-8.6 8.6-1.4 1.4"></path>',
        public: '<circle cx="10" cy="10" r="7"></circle><path d="M3 10h14M10 3c2 2 3 4.3 3 7s-1 5-3 7c-2-2-3-4.3-3-7s1-5 3-7Z"></path>',
        image: '<rect x="3" y="4" width="14" height="12" rx="2"></rect><circle cx="7" cy="8" r="1.3"></circle><path d="m4 14 4-4 3 3 2-2 3 3"></path>',
        users: '<circle cx="8" cy="7" r="3"></circle><path d="M3 17c0-3 2-5 5-5s5 2 5 5m0-8c2 0 4 1.5 4 4v2"></path>',
        shield: '<path d="M10 2 4 4v5c0 4 2.5 7 6 9 3.5-2 6-5 6-9V4l-6-2Z"></path><path d="m7.5 10 1.6 1.6 3.5-4"></path>',
        database: '<ellipse cx="10" cy="5" rx="6" ry="3"></ellipse><path d="M4 5v5c0 1.7 2.7 3 6 3s6-1.3 6-3V5M4 10v5c0 1.7 2.7 3 6 3s6-1.3 6-3v-5"></path>',
        pipeline: '<circle cx="4" cy="10" r="2"></circle><circle cx="10" cy="5" r="2"></circle><circle cx="16" cy="10" r="2"></circle><circle cx="10" cy="15" r="2"></circle><path d="m6 9 2.5-2.5M11.5 6.5 14 9m-8 2 2.5 2.5m3-1L14 11"></path>',
        inbox: '<path d="M3 4h14v12H3V4Z"></path><path d="M3 11h4l1.5 2h3L13 11h4"></path>',
        bell: '<path d="M5 14h10l-1.5-2V8a3.5 3.5 0 0 0-7 0v4L5 14Zm3 2h4"></path>',
        chevron: '<path d="m7 4 6 6-6 6"></path>',
        scope: '<circle cx="10" cy="10" r="7"></circle><circle cx="10" cy="10" r="2"></circle><path d="M10 3v3m0 8v3M3 10h3m8 0h3"></path>',
        clock: '<circle cx="10" cy="10" r="7"></circle><path d="M10 6v4l3 2"></path>',
        warning: '<path d="M10 3 2.5 17h15L10 3Z"></path><path d="M10 8v4m0 2.3v.2"></path>',
        link: '<path d="M8 12 6.5 13.5a3 3 0 0 1-4-4L6 6a3 3 0 0 1 4 0m2 2 1.5-1.5a3 3 0 0 1 4 4L14 14a3 3 0 0 1-4 0"></path>'
    };

    const params = new URLSearchParams(window.location.search);
    const requestedObservatoryId = params.get("observatory");
    const requestedCameraId = params.get("camera");
    const requestedView = params.get("view");
    const observatory = requestedObservatoryId ? observatories.find(item => item.id === requestedObservatoryId) : null;
    const camera = observatory && requestedCameraId ? observatory.cameras.find(item => item.id === requestedCameraId) : null;
    const view = requestedView ?? (camera ? "camera" : observatory ? "observatory" : "welcome");
    const knownViews = new Set(["welcome", "observatories", "public", "operations", "observatory", "cameras", "camera", "processing", "archive", "events"]);
    const globalViews = new Set(["welcome", "observatories", "public", "events", "operations"]);
    const observatoryViews = new Set(["observatory", "cameras", "operations"]);
    const cameraViews = new Set(["camera", "processing", "archive", "events", "operations"]);
    const routeIsValid = knownViews.has(view)
        && (!requestedObservatoryId || observatory)
        && (!requestedCameraId || camera)
        && (camera ? cameraViews.has(view) : observatory ? observatoryViews.has(view) : globalViews.has(view));

    const elements = {
        header: document.getElementById("logicHeader"),
        headerToggle: document.getElementById("logicHeaderToggle"),
        primaryNavigation: document.getElementById("logicPrimaryNavigation"),
        sidebar: document.getElementById("logicSidebar"),
        sidebarClose: document.getElementById("logicSidebarClose"),
        sidebarBackdrop: document.getElementById("logicSidebarBackdrop"),
        sidebarToggle: document.getElementById("logicScopeToggle"),
        scopeNavigation: document.getElementById("logicScopeNavigation"),
        main: document.getElementById("main-content"),
        page: document.getElementById("logicPage"),
        toast: document.getElementById("logicToast"),
        toastText: document.getElementById("logicToastText")
    };
    let toastTimer;

    function icon(name, label = "") {
        const labelMarkup = label ? `<title>${escapeHtml(label)}</title>` : "";
        return `<svg viewBox="0 0 20 20"${label ? ' role="img"' : ' aria-hidden="true"'}>${labelMarkup}${iconPaths[name] ?? iconPaths.scope}</svg>`;
    }

    function routeHref(options = {}) {
        const next = new URLSearchParams();
        if (options.view && options.view !== "welcome") next.set("view", options.view);
        if (options.observatory) next.set("observatory", options.observatory);
        if (options.camera) next.set("camera", options.camera);
        const query = next.toString();
        return `logichost.html${query ? `?${query}` : ""}`;
    }

    function escapeHtml(value) {
        return String(value).replace(/[&<>'"]/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[character]);
    }

    function statusClass(status) {
        return ({ healthy: "success", online: "success", attention: "warning", offline: "pending" })[status] ?? "pending";
    }

    function statusLabel(status) {
        return ({ healthy: "Healthy", online: "Online", attention: "Attention", offline: "Offline" })[status] ?? status;
    }

    function navDot(status) {
        const tone = status === "attention" ? " warning" : status === "offline" ? " offline" : "";
        return `<span class="logic-nav-dot${tone}" aria-hidden="true"></span>`;
    }

    function renderScopeNavigation() {
        const globalActive = !observatory;
        elements.scopeNavigation.innerHTML = `
            <p class="logic-nav-group">Network</p>
            ${scopeLink(routeHref(), "home", "Network home", "All authorized observatories", "", globalActive && view === "welcome")}
            ${scopeLink(routeHref({ view: "observatories" }), "observatory", "Observatories", "Select one protected scope", String(observatories.length), globalActive && view === "observatories")}
            ${scopeLink(routeHref({ view: "public" }), "public", "Public sky", "Released previews only", "", globalActive && view === "public")}
            ${scopeLink(routeHref({ view: "events" }), "warning", "Events", "Authorized multi-site evidence", "", globalActive && view === "events")}
            ${scopeLink(routeHref({ view: "operations" }), "operations", "LogicHost operations", "Platform and central services", "", globalActive && view === "operations")}
            <p class="logic-nav-group">Your observatories</p>
            ${observatories.map(item => {
                const active = observatory?.id === item.id && !camera;
                const cameraLinks = observatory?.id === item.id
                    ? `<div class="logic-camera-links">${item.cameras.map(cameraItem => scopeLink(
                        routeHref({ observatory: item.id, camera: cameraItem.id }),
                        "camera",
                        cameraItem.name,
                        `${cameraItem.code} / ${statusLabel(cameraItem.status)}`,
                        navDot(cameraItem.status),
                        camera?.id === cameraItem.id
                    )).join("")}</div>`
                    : "";
                return `${scopeLink(
                    routeHref({ observatory: item.id }),
                    "observatory",
                    item.name,
                    `${item.code} / ${item.visibility} / ${statusLabel(item.status)}`,
                    navDot(item.status),
                    active
                )}${cameraLinks}`;
            }).join("")}`;
    }

    function scopeLink(href, iconName, title, subtitle, tail, active) {
        return `<a class="logic-scope-link${active ? " active" : ""}" href="${href}"${active ? ' aria-current="page"' : ""}>
            <span class="logic-nav-icon" aria-hidden="true">${icon(iconName)}</span>
            <span class="logic-nav-copy"><strong>${escapeHtml(title)}</strong><small>${escapeHtml(subtitle)}</small></span>
            ${tail ? (tail.startsWith("<") ? tail : `<span class="logic-nav-count">${escapeHtml(tail)}</span>`) : ""}
        </a>`;
    }

    function setPrimaryNavigation() {
        const active = observatory ? "observatories" : view;
        elements.primaryNavigation.querySelectorAll("a").forEach(link => {
            const isActive = link.dataset.logicNav === active;
            link.classList.toggle("active", isActive);
            if (isActive) link.setAttribute("aria-current", "page");
            else link.removeAttribute("aria-current");
        });
    }

    function pageHeader(eyebrow, title, description, authority = "LogicHost protected authority", actions = "") {
        return `<header class="logic-page-heading">
            <div><p class="eyebrow">${escapeHtml(eyebrow)}</p><h1 tabindex="-1">${escapeHtml(title)}</h1><p>${escapeHtml(description)}</p></div>
            <div class="logic-page-actions"><span class="logic-authority"><i></i>${escapeHtml(authority)}</span>${actions}</div>
        </header>`;
    }

    function breadcrumbs(items) {
        return `<nav class="logic-breadcrumbs" aria-label="Breadcrumb">${items.map((item, index) => {
            const content = item.href ? `<a href="${item.href}">${escapeHtml(item.label)}</a>` : `<span>${escapeHtml(item.label)}</span>`;
            return `${index ? "<span>/</span>" : ""}${content}`;
        }).join("")}</nav>`;
    }

    function observatoryTabs(item, active) {
        return `<nav class="logic-scope-tabs" aria-label="${escapeHtml(item.name)} sections">
            ${tabLink(routeHref({ observatory: item.id }), "Overview", active === "overview")}
            ${tabLink(routeHref({ view: "cameras", observatory: item.id }), `Cameras (${item.cameras.length})`, active === "cameras")}
            ${tabLink(routeHref({ view: "operations", observatory: item.id }), "Observatory operations", active === "operations")}
        </nav>`;
    }

    function cameraTabs(item, selectedCamera, active) {
        return `<nav class="logic-scope-tabs" aria-label="${escapeHtml(selectedCamera.name)} sections">
            ${tabLink(routeHref({ observatory: item.id, camera: selectedCamera.id }), "Current sky", active === "current")}
            ${tabLink(routeHref({ view: "processing", observatory: item.id, camera: selectedCamera.id }), "Processing", active === "processing")}
            ${tabLink(routeHref({ view: "archive", observatory: item.id, camera: selectedCamera.id }), "Archive", active === "archive")}
            ${tabLink(routeHref({ view: "events", observatory: item.id, camera: selectedCamera.id }), "Related events", active === "events")}
            ${tabLink(routeHref({ view: "operations", observatory: item.id, camera: selectedCamera.id }), "Camera operations", active === "operations")}
        </nav>`;
    }

    function tabLink(href, label, active) {
        return `<a class="${active ? "active" : ""}" href="${href}"${active ? ' aria-current="page"' : ""}>${escapeHtml(label)}</a>`;
    }

    function renderWelcome() {
        const cameraCount = observatories.reduce((total, item) => total + item.cameras.length, 0);
        const onlineCount = observatories.flatMap(item => item.cameras).filter(item => item.status === "online").length;
        const publicReleases = releasedImages();
        document.title = "Network Home | HVO SkyMonitor Prototype";
        elements.page.innerHTML = `
            <section class="logic-welcome" aria-labelledby="logic-welcome-title">
                <div class="logic-welcome-copy">
                    <p class="eyebrow">LogicHost / protected network</p>
                    <h1 id="logic-welcome-title">One network.<br>Clear boundaries.</h1>
                    <p>Move from network health to one observatory, then one camera. Local configuration and protected evidence retain their owning scope, while central events identify every contributing observatory.</p>
                    <div class="logic-welcome-actions"><a class="button primary" href="${routeHref({ view: "observatories" })}">Choose an observatory</a><a class="button secondary" href="${routeHref({ view: "public" })}">View public sky</a></div>
                </div>
                <div class="logic-welcome-network" aria-label="Observatory status summary">
                    ${observatories.map(item => `<a class="logic-node-row" href="${routeHref({ observatory: item.id })}" style="color:inherit;text-decoration:none"><span class="logic-node-symbol">${icon("observatory")}</span><span><strong>${escapeHtml(item.code)}</strong><small>${escapeHtml(item.name)}</small></span><span>${escapeHtml(statusLabel(item.status))}</span></a>`).join("")}
                </div>
            </section>

            <section class="logic-metrics" aria-label="Network summary">
                ${metric("Authorized observatories", observatories.length, "Each remains an isolated scope")}
                ${metric("Registered cameras", cameraCount, `${onlineCount} currently online`)}
                ${metric("Central ingest", "1 retry", "No durable evidence at risk")}
                ${metric("Public releases", publicReleases.length, "Preview roles only")}
            </section>

            <div class="logic-boundary">
                <span class="logic-boundary-icon">${icon("scope")}</span>
                <span><strong>Scope is part of every route.</strong><p>Cameras, schedules, pipelines, camera jobs, and protected images are never presented as reusable network-wide objects. Central events retain all authorized contributors, while only specifically released previews enter Public Sky.</p></span>
            </div>

            <div class="logic-grid sidebar-right">
                <section class="logic-panel">
                    <header class="logic-panel-heading"><div><h2>Observatory status</h2><p>Protected health summaries for observatories you can access.</p></div><a href="${routeHref({ view: "observatories" })}">Select scope</a></header>
                    <div class="logic-observatory-list">${observatories.map(renderObservatoryRow).join("")}</div>
                </section>
                <section class="logic-panel">
                    <header class="logic-panel-heading"><div><h2>Needs attention</h2><p>Central signals without assuming local acquisition has stopped.</p></div><span>2 items</span></header>
                    <div class="logic-observatory-list">
                        <a class="logic-observatory-row" style="grid-template-columns:auto 1fr auto" href="${routeHref({ observatory: "siding-spring", camera: "meteor-east" })}"><span class="status-icon warning" aria-hidden="true"></span><span><strong>Meteor East delivery retrying</strong><small>Source evidence remains at Siding Spring.</small></span><span class="logic-row-arrow">${icon("chevron")}</span></a>
                        <a class="logic-observatory-row" style="grid-template-columns:auto 1fr auto" href="${routeHref({ observatory: "desert-ridge" })}"><span class="status-icon pending" aria-hidden="true"></span><span><strong>Desert Ridge agent offline</strong><small>Last heartbeat 2h 18m ago.</small></span><span class="logic-row-arrow">${icon("chevron")}</span></a>
                    </div>
                </section>
            </div>

            <section class="logic-panel logic-grid">
                <header class="logic-panel-heading"><div><h2>Recent public releases</h2><p>Cross-observatory discovery contains released preview artifacts only.</p></div><a href="${routeHref({ view: "public" })}">Open Public Sky</a></header>
                <div class="logic-public-strip">${publicReleases.slice(0, 3).map(release => `<a class="logic-public-tile" href="${routeHref({ view: "public" })}"><img src="${release.release.content}" alt="Released preview from ${escapeHtml(release.camera.name)}"><span><strong>${escapeHtml(release.camera.name)}</strong><small>${escapeHtml(release.observatory.region)} / ${escapeHtml(release.release.captured)}</small></span></a>`).join("")}</div>
            </section>`;
    }

    function metric(label, value, detail) {
        return `<article class="logic-metric"><span>${escapeHtml(label)}</span><strong>${escapeHtml(value)}</strong><small>${escapeHtml(detail)}</small></article>`;
    }

    function renderObservatoryRow(item) {
        const online = item.cameras.filter(cameraItem => cameraItem.status === "online").length;
        return `<a class="logic-observatory-row" href="${routeHref({ observatory: item.id })}">
            <span class="status-icon ${statusClass(item.status)}" aria-hidden="true"></span>
            <span><strong>${escapeHtml(item.name)}</strong><small>${escapeHtml(item.region)} / ${escapeHtml(statusLabel(item.status))}</small></span>
            <span class="logic-row-fact"><span>Cameras</span><strong>${online} / ${item.cameras.length} online</strong></span>
            <span class="logic-row-fact"><span>Visibility</span><strong>${escapeHtml(capitalize(item.visibility))}</strong></span>
            <span class="logic-row-fact"><span>Heartbeat</span><strong>${escapeHtml(item.heartbeat)}</strong></span>
            <span class="logic-row-arrow">${icon("chevron")}</span>
        </a>`;
    }

    function renderObservatorySelection() {
        document.title = "Observatories | HVO SkyMonitor Prototype";
        elements.page.innerHTML = `
            ${pageHeader("Protected network / scope selection", "Choose an observatory", "Select an observatory before working with its cameras, schedules, pipelines, or protected image archive.", "Signed-in Owner / membership")}
            <div class="logic-boundary"><span class="logic-boundary-icon">${icon("shield")}</span><span><strong>Selection changes the data boundary.</strong><p>Moving between observatories does not carry camera, schedule, pipeline, event, or job selections forward. Each card below enters a separate authorized scope.</p></span></div>
            <section class="logic-panel logic-observatory-toolbar" aria-label="Observatory filters">
                <label class="logic-field">Find an observatory<input id="logicObservatorySearch" type="search" placeholder="Name, code, or region"></label>
                <label class="logic-field">Visibility<select id="logicVisibilityFilter"><option value="all">All visibility</option><option value="public">Public profile</option><option value="private">Private</option></select></label>
                <span id="logicResultCount" class="logic-result-count">${observatories.length} observatories</span>
            </section>
            <section id="logicObservatoryCards" class="logic-observatory-cards" aria-label="Available observatories">
                ${observatories.map(renderObservatoryCard).join("")}
                <p id="logicObservatoryEmpty" class="logic-empty" hidden>No observatories match this filter.</p>
            </section>`;
        bindObservatoryFilters();
    }

    function renderObservatoryCard(item) {
        const online = item.cameras.filter(cameraItem => cameraItem.status === "online").length;
        return `<article class="logic-observatory-card" data-observatory-card data-search="${escapeHtml(`${item.name} ${item.code} ${item.region}`.toLowerCase())}" data-visibility="${item.visibility}">
            <div class="logic-observatory-visual"><img src="${item.image}" alt="Illustrative sky preview for ${escapeHtml(item.name)}"><span class="logic-visibility${item.visibility === "private" ? " private" : ""}">${item.visibility === "public" ? "Public profile" : "Private"}</span><span><strong>${escapeHtml(item.name)}</strong><small>${escapeHtml(item.region)}</small></span></div>
            <div class="logic-observatory-body"><dl class="logic-card-facts"><div><dt>Status</dt><dd>${escapeHtml(statusLabel(item.status))}</dd></div><div><dt>Cameras</dt><dd>${online} / ${item.cameras.length} online</dd></div><div><dt>Members</dt><dd>${item.members}</dd></div></dl><div class="logic-card-actions"><span>${escapeHtml(item.summary)}</span><a href="${routeHref({ observatory: item.id })}">Enter scope</a></div></div>
        </article>`;
    }

    function bindObservatoryFilters() {
        const search = document.getElementById("logicObservatorySearch");
        const visibility = document.getElementById("logicVisibilityFilter");
        const cards = [...document.querySelectorAll("[data-observatory-card]")];
        const count = document.getElementById("logicResultCount");
        const empty = document.getElementById("logicObservatoryEmpty");
        const apply = () => {
            const query = search.value.trim().toLowerCase();
            const selectedVisibility = visibility.value;
            let visible = 0;
            cards.forEach(card => {
                const matches = (!query || card.dataset.search.includes(query)) && (selectedVisibility === "all" || card.dataset.visibility === selectedVisibility);
                card.hidden = !matches;
                if (matches) visible += 1;
            });
            count.textContent = `${visible} ${visible === 1 ? "observatory" : "observatories"}`;
            empty.hidden = visible > 0;
        };
        search.addEventListener("input", apply);
        visibility.addEventListener("change", apply);
    }

    function renderPublicSky() {
        const releases = releasedImages();
        document.title = "Public Sky | HVO SkyMonitor Prototype";
        elements.page.innerHTML = `
            ${pageHeader("Released network records / protected preview", "Public sky", "Preview the image records that anonymous visitors can discover after observatory owners explicitly release them.", "Protected preview of public projection")}
            <div class="logic-boundary public"><span class="logic-boundary-icon">${icon("public")}</span><span><strong>Public is a publication decision, not an observatory-wide data leak.</strong><p>This protected page previews the anonymous result. Only released JPEG, PNG, or WebP Preview and AnnotatedPreview artifacts appear. Raw frames, calibration evidence, exact locations, schedules, pipelines, protected events, and unreleased images remain scoped.</p></span></div>
            <section class="logic-image-grid" aria-label="Released public images">${releases.map(renderPublicImage).join("")}</section>`;
    }

    function releasedImages() {
        return observatories.flatMap(item => item.visibility === "public"
            ? item.cameras.filter(cameraItem => cameraItem.releaseArtifact?.decision === "Released" && cameraItem.releaseArtifact.content && cameraItem.releaseArtifact.captured).map(cameraItem => ({ observatory: item, camera: cameraItem, release: cameraItem.releaseArtifact }))
            : []);
    }

    function renderPublicImage(release) {
        return `<article class="logic-image-card">
            <figure><img src="${release.release.content}" alt="Released ${escapeHtml(release.release.role)} ${escapeHtml(release.release.artifactId)} from ${escapeHtml(release.camera.name)}"><span>Released ${escapeHtml(release.release.role)}</span></figure>
            <div class="logic-image-card-body"><h2>${escapeHtml(release.camera.name)}</h2><p>${escapeHtml(release.observatory.name)} / ${escapeHtml(release.observatory.disclosure)}</p><dl class="logic-release-facts"><div><dt>Captured</dt><dd>${escapeHtml(release.release.captured)}</dd></div><div><dt>Artifact</dt><dd>${escapeHtml(release.release.artifactId)}</dd></div><div><dt>Release</dt><dd>${escapeHtml(release.release.releaseId)}</dd></div></dl><button type="button" data-prototype-action="The public image detail is the next Public Sky prototype slice.">Open public record</button></div>
        </article>`;
    }

    function renderNetworkEvents() {
        document.title = "Events | HVO SkyMonitor Prototype";
        elements.page.innerHTML = `
            ${pageHeader("Central science / authorized evidence", "Events", "Review centrally validated events without collapsing their contributing observatory and camera identities.", "Protected multi-site read")}
            <div class="logic-boundary"><span class="logic-boundary-icon">${icon("warning")}</span><span><strong>An event is central; its evidence remains attributed.</strong><p>Single-site events retain one observatory contributor. Multi-site correlation can link evidence from several observatories only when the signed-in user is authorized for every contributing scope.</p></span></div>
            <section class="logic-status-deck attention"><div class="logic-status-primary"><span class="status-icon warning" aria-hidden="true"></span><span><strong>One event awaiting review</strong><small>Three bounded event fixtures / all contributing scopes authorized</small></span></div><dl class="logic-status-facts"><div><dt>Multi-site</dt><dd>1</dd></div><div><dt>Single-site</dt><dd>2</dd></div><div><dt>Confirmed</dt><dd>1</dd></div><div><dt>Public releases</dt><dd>0</dd></div></dl></section>
            <section class="logic-panel logic-grid"><header class="logic-panel-heading"><div><h2>Recent central events</h2><p>Contributor identity and authorization remain visible at the network level.</p></div><span>Illustrative fixtures</span></header><div class="logic-table-wrap"><table class="logic-table"><thead><tr><th>Event</th><th>Assessment</th><th>Contributing evidence</th><th>Access</th><th>Review</th></tr></thead><tbody>
                <tr><td><strong>E-1042 / 31 August Fireball</strong><small>03:13:56 UTC</small></td><td>Meteor / Fireball</td><td><strong>HVO / HVO Main Fisheye</strong><small>Five-frame centered window</small></td><td>1 of 1 authorized</td><td><span class="state-chip success">Confirmed</span></td></tr>
                <tr><td><strong>E-1039 / Southern track</strong><small>02:48:11 UTC</small></td><td>Meteor candidate</td><td><strong>HVO Main Fisheye + All-Sky South</strong><small>HVO and SSO remain named contributors</small></td><td>2 of 2 authorized</td><td><span class="state-chip warning">Review</span></td></tr>
                <tr><td><strong>E-1037 / Horizon flash</strong><small>01:22:40 UTC</small></td><td>Non-meteor transient</td><td><strong>HVO / North Horizon Imager</strong><small>Single-camera evidence</small></td><td>1 of 1 authorized</td><td><span class="state-chip pending">Closed</span></td></tr>
            </tbody></table></div></section>`;
    }

    function renderObservatoryOverview(item) {
        const online = item.cameras.filter(cameraItem => cameraItem.status === "online").length;
        document.title = `${item.name} | HVO SkyMonitor Prototype`;
        elements.page.innerHTML = `
            ${breadcrumbs([{ label: "Network", href: routeHref() }, { label: "Observatories", href: routeHref({ view: "observatories" }) }, { label: item.name }])}
            ${pageHeader(`${item.code} / protected observatory`, item.name, item.summary, "Signed-in Owner / observatory", `<a class="button secondary" href="${routeHref({ view: "operations", observatory: item.id })}">Observatory operations</a>`)}
            ${observatoryTabs(item, "overview")}
            <section class="logic-status-deck ${item.status}" aria-label="Observatory status"><div class="logic-status-primary"><span class="status-icon ${statusClass(item.status)}" aria-hidden="true"></span><span><strong>${escapeHtml(statusLabel(item.status))}</strong><small>${online} of ${item.cameras.length} cameras online / last heartbeat ${escapeHtml(item.heartbeat)}</small></span></div><dl class="logic-status-facts"><div><dt>Visibility</dt><dd>${escapeHtml(capitalize(item.visibility))}</dd></div><div><dt>Location disclosure</dt><dd>${escapeHtml(item.disclosure)}</dd></div><div><dt>Members</dt><dd>${item.members}</dd></div><div><dt>Public profile</dt><dd>${escapeHtml(item.profile)}</dd></div></dl></section>
            <div class="logic-grid sidebar-right">
                <section class="logic-panel"><header class="logic-panel-heading"><div><h2>Select a camera</h2><p>Camera routes retain this observatory identity.</p></div><a href="${routeHref({ view: "cameras", observatory: item.id })}">Camera inventory</a></header><div class="logic-camera-grid">${item.cameras.map(cameraItem => renderCameraCard(item, cameraItem)).join("")}</div></section>
                <section class="logic-panel"><header class="logic-panel-heading"><div><h2>Scope facts</h2><p>Central identities and publication state.</p></div><span>${escapeHtml(item.code)}</span></header><dl class="logic-facts"><div class="full"><dt>Observatory</dt><dd>${escapeHtml(item.name)}</dd></div><div><dt>Membership</dt><dd>Protected</dd></div><div><dt>Logical cameras</dt><dd>${item.cameras.length}</dd></div><div><dt>Region</dt><dd>${escapeHtml(item.region)}</dd></div><div><dt>Public location</dt><dd>${escapeHtml(item.disclosure)}</dd></div><div class="full"><dt>Isolation</dt><dd>Schedules, pipelines, camera jobs, and protected images stay in ${escapeHtml(item.code)}. Central events preserve contributor links.</dd></div></dl></section>
            </div>
            <section class="logic-panel logic-grid"><header class="logic-panel-heading"><div><h2>Observatory operations</h2><p>Central operations constrained to ${escapeHtml(item.name)}.</p></div><a href="${routeHref({ view: "operations", observatory: item.id })}">Open workspace</a></header><div class="logic-operation-grid">${observatoryOperationCards(item).slice(0, 3).join("")}</div></section>`;
    }

    function renderObservatoryCameras(item) {
        document.title = `Cameras - ${item.name} | HVO SkyMonitor Prototype`;
        elements.page.innerHTML = `
            ${breadcrumbs([{ label: "Network", href: routeHref() }, { label: item.name, href: routeHref({ observatory: item.id }) }, { label: "Cameras" }])}
            ${pageHeader(`${item.code} / camera selection`, "Choose a camera", `Every camera identity, installation, image, received schedule revision, and pipeline revision remains inside ${item.name}.`, "Signed-in Owner / camera registry")}
            ${observatoryTabs(item, "cameras")}
            <div class="logic-boundary"><span class="logic-boundary-icon">${icon("camera")}</span><span><strong>Cameras are not shared between observatories.</strong><p>A logical camera can retain replacement-installation history, but it belongs to this observatory. Selecting one never carries a camera identity from another site.</p></span></div>
            <section class="logic-camera-grid">${item.cameras.map(cameraItem => renderCameraCard(item, cameraItem)).join("")}</section>`;
    }

    function renderCameraCard(item, cameraItem) {
        const visual = cameraItem.previewContent
            ? `<span class="logic-camera-image"><img src="${cameraItem.previewContent}" alt="Exact latest retained preview from ${escapeHtml(cameraItem.name)}">${navDot(cameraItem.status)}</span>`
            : `<span class="logic-camera-image logic-camera-image-unavailable"><span>${icon("image")}<small>Exact preview not loaded</small></span>${navDot(cameraItem.status)}</span>`;
        return `<a class="logic-camera-card" href="${routeHref({ observatory: item.id, camera: cameraItem.id })}">${visual}<span class="logic-camera-copy"><span>${escapeHtml(cameraItem.code)} / ${escapeHtml(statusLabel(cameraItem.status))}</span><h3>${escapeHtml(cameraItem.name)}</h3><p>Latest complete retained Preview record ${escapeHtml(cameraItem.imageAge)} old.${cameraItem.previewContent ? "" : " Exact bytes are not loaded in this fixture."}</p><dl><div><dt>Retained capture</dt><dd>${escapeHtml(cameraItem.capture)}</dd></div><div><dt>Latest submission</dt><dd>${escapeHtml(cameraItem.submission)}</dd></div><div><dt>Schedule evidence</dt><dd>${escapeHtml(cameraItem.schedule)}</dd></div><div><dt>Pipeline evidence</dt><dd>${escapeHtml(cameraItem.pipeline)}</dd></div></dl></span></a>`;
    }

    function cameraPresentation(cameraItem) {
        const hasLayers = cameraItem.id === "main-fisheye";
        const hasCombined = ["main-fisheye", "north-horizon", "all-sky-south"].includes(cameraItem.id);
        const previewArtifactId = cameraItem.releaseArtifact?.capture === cameraItem.capture
            ? cameraItem.releaseArtifact.artifactId
            : `art-${cameraItem.code.toLowerCase()}-${captureNumber(cameraItem)}-preview`;
        const artifacts = [
            { artifactId: previewArtifactId, role: "Preview", variant: "central-display-v2", media: "image/jpeg", display: true },
            ...(hasCombined ? [{ artifactId: `art-${cameraItem.code.toLowerCase()}-${captureNumber(cameraItem)}-combined`, role: "Combined", variant: "causal-mean-v1", media: "image/fits", display: false }] : []),
            { artifactId: `art-${cameraItem.code.toLowerCase()}-${captureNumber(cameraItem)}-calibrated`, role: "Calibrated", variant: "linear-calibrated-v1", media: "image/fits", display: false },
            { artifactId: `art-${cameraItem.code.toLowerCase()}-${captureNumber(cameraItem)}-raw`, role: "Raw", variant: "camera-native-v1", media: "application/fits", display: false }
        ].map(artifact => ({
            ...artifact,
            objectState: "Available",
            reconstructionState: "Complete",
            publication: cameraItem.releaseArtifact?.artifactId === artifact.artifactId
                ? cameraItem.releaseArtifact
                : null
        }));
        return {
            capture: cameraItem.capture,
            captured: formatCameraTimestamp(cameraItem),
            firstReceived: shiftCameraTimestamp(cameraItem, 8),
            hasLayers,
            hasCombined,
            contentLoaded: Boolean(cameraItem.previewContent),
            defaultView: hasLayers && cameraItem.previewContent ? "layers" : "preview",
            previewArtifact: artifacts.find(artifact => artifact.role === "Preview"),
            artifacts
        };
    }

    function renderCameraCurrent(item, selectedCamera) {
        document.title = `${selectedCamera.name} | HVO SkyMonitor Prototype`;
        const presentation = cameraPresentation(selectedCamera);
        const presentationTitle = presentation.contentLoaded ? presentation.hasLayers ? "Layered presentation" : "Preview base" : "Preview metadata";
        const presentationDescription = presentation.contentLoaded
            ? presentation.hasLayers ? "The exact Preview artifact rendered with groups generated from retained layer payloads." : "One exact complete retained Preview artifact."
            : "A complete Preview role is recorded, but its exact bytes are not loaded in this bounded fixture.";
        const presentationVisual = presentation.contentLoaded
            ? `<img id="logicPresentationImage" src="${selectedCamera.previewContent}" alt="Preview artifact for capture ${escapeHtml(presentation.capture)}" aria-describedby="logicPresentationEvidence">${renderCentralLayers(presentation.hasLayers)}`
            : `<div class="logic-presentation-unavailable" role="status"><span>${icon("image")}</span><strong>Exact Preview bytes are not loaded</strong><small>Capture and artifact metadata remain available; no other camera image is substituted.</small></div>`;
        const localAction = selectedCamera.localPrototype
            ? `<a class="button secondary" href="dashboard.html">Open local CameraAgent prototype</a>`
            : `<button class="button secondary" type="button" data-prototype-action="A detailed CameraAgent projection is not loaded for this illustrative camera.">Open detailed camera view</button>`;
        elements.page.innerHTML = `
            ${breadcrumbs([{ label: "Network", href: routeHref() }, { label: item.name, href: routeHref({ observatory: item.id }) }, { label: selectedCamera.name }])}
            ${pageHeader(`${item.code} / ${selectedCamera.code} / protected camera`, selectedCamera.name, "Inspect the latest complete retained presentation selected for this logical camera.", "Signed-in Owner / protected read", localAction)}
            ${cameraTabs(item, selectedCamera, "current")}
            <div class="logic-boundary"><span class="logic-boundary-icon">${icon("link")}</span><span><strong>Retained central evidence, not a live feed.</strong><p>Capture time, first central receipt, fleet health, artifact availability, reconstruction, and publication are separate facts. CameraAgent still owns acquisition, schedule admission, and capture-pipeline configuration.</p></span></div>
            <section class="logic-current-status" aria-label="Camera and selected capture status">
                ${currentStatusFact("Fleet health", statusLabel(selectedCamera.status), `Camera report ${selectedCamera.fleetObserved}`, statusClass(selectedCamera.status))}
                ${currentStatusFact("Latest submission", selectedCamera.submission, "Independent of selected capture", submissionTone(selectedCamera.submission))}
                ${currentStatusFact("Selected capture", presentation.capture, presentation.captured, "success")}
                ${currentStatusFact("Central receipt", presentation.firstReceived, "First accepted receipt", "success")}
                ${currentStatusFact("Presentation", presentation.contentLoaded ? "Available" : "Metadata only", presentation.contentLoaded ? "Reconstruction complete" : "Exact bytes not loaded here", presentation.contentLoaded ? "success" : "pending")}
            </section>
            <section class="logic-retained-workspace">
                <article class="logic-retained-viewer">
                    <header class="logic-retained-heading"><div><p class="eyebrow">Selected latest retained capture</p><h2 id="logicPresentationTitle">${presentationTitle}</h2><p id="logicPresentationDescription">${presentationDescription}</p></div><span id="logicPresentationRole" class="logic-artifact-role">${presentation.contentLoaded ? presentation.hasLayers ? "Preview + generated SVG" : "Preview" : "Preview metadata"}</span></header>
                    <figure id="logicPresentationFigure" class="logic-presentation-figure view-${presentation.defaultView}${presentation.contentLoaded ? "" : " content-unloaded"}">
                        ${presentationVisual}
                        <p id="logicPresentationEvidence" class="visually-hidden">${presentation.contentLoaded ? presentation.hasLayers ? "Layered presentation showing measured associations for Deneb, Vega, and Altair; expected sky context; and a predicted geometry track from a one-hour-fourteen-minute-old snapshot. These are illustrative fixtures, not a scientific reduction of the preview." : "Complete retained Preview artifact with no generated layer groups displayed." : "A complete Preview role is recorded, but exact bytes are not loaded in this bounded fixture. No other camera image is substituted."}</p>
                        <figcaption><span><strong>${escapeHtml(presentation.capture)} / ${escapeHtml(selectedCamera.name)}</strong><small>${escapeHtml(presentation.captured)} / object Available / reconstruction Complete</small></span><button type="button" data-prototype-action="A large protected artifact viewer would open without changing the selected capture.">${icon("image")}<span class="visually-hidden">Open large protected image</span></button></figcaption>
                    </figure>
                    <div class="logic-presentation-switcher" role="group" aria-label="Retained presentation view">
                        <button type="button" data-presentation-view="preview" aria-pressed="${String(presentation.defaultView === "preview")}"><span>${presentation.contentLoaded ? "Preview base" : "Preview metadata"}</span><small>${presentation.contentLoaded ? "Complete display artifact" : "Exact bytes not loaded"}</small></button>
                        <button type="button" data-presentation-view="layers" aria-pressed="${String(presentation.defaultView === "layers")}"${presentation.hasLayers && presentation.contentLoaded ? "" : " disabled"}><span>Layered presentation</span><small>${presentation.hasLayers && presentation.contentLoaded ? "Generated from retained manifest" : "No loaded Preview and layer manifest"}</small></button>
                    </div>
                </article>
                <aside class="logic-retained-inspector">
                    <section><p class="eyebrow">Capture identity</p><h2>${escapeHtml(selectedCamera.name)}</h2><dl class="logic-facts"><div><dt>Captured</dt><dd>${escapeHtml(presentation.captured)}</dd></div><div><dt>First received</dt><dd>${escapeHtml(presentation.firstReceived)}</dd></div><div class="full"><dt>Installation at capture</dt><dd>${escapeHtml(selectedCamera.installation)}</dd></div><div class="full"><dt>Schedule evidence</dt><dd>${escapeHtml(selectedCamera.schedule)}</dd></div><div class="full"><dt>Pipeline evidence</dt><dd>${escapeHtml(selectedCamera.pipeline)}</dd></div><div><dt>Object</dt><dd>Available</dd></div><div><dt>Reconstruction</dt><dd>Complete</dd></div></dl></section>
                    ${renderLayerControls(presentation.hasLayers && presentation.contentLoaded)}
                    <section class="logic-release-panel"><p class="eyebrow">Preview artifact publication</p><strong>${presentation.previewArtifact.publication ? "Release decision: Released" : "Not released"}</strong><span>${presentation.previewArtifact.publication ? `${presentation.previewArtifact.artifactId} / ${presentation.previewArtifact.publication.releaseId} / observatory profile ${item.visibility}` : "This exact Preview artifact has no current release decision."}</span></section>
                    <div class="logic-inspector-actions"><a class="button primary" href="${routeHref({ view: "archive", observatory: item.id, camera: selectedCamera.id })}">Open camera archive</a><a class="button secondary" href="${routeHref({ view: "operations", observatory: item.id, camera: selectedCamera.id })}">Camera operations</a></div>
                </aside>
            </section>
            <div class="logic-grid two">
                ${renderLineagePanel(selectedCamera, presentation)}
                ${renderArtifactInventory(selectedCamera, presentation)}
            </div>`;
        bindCameraPresentation(presentation);
    }

    function currentStatusFact(label, value, detail, tone) {
        return `<article><span class="status-icon ${tone}" aria-hidden="true"></span><span><small>${escapeHtml(label)}</small><strong>${escapeHtml(value)}</strong><em>${escapeHtml(detail)}</em></span></article>`;
    }

    function submissionTone(submission) {
        if (submission.startsWith("Accepted")) return "success";
        if (submission.startsWith("Retrying")) return "warning";
        return "pending";
    }

    function renderCentralLayers(available) {
        if (!available) return "";
        return `<svg class="logic-central-layers" viewBox="0 0 1000 625" preserveAspectRatio="xMidYMid meet" aria-hidden="true">
            <g data-central-layer="measured" class="logic-layer-measured"><g transform="translate(540 278)"><circle r="9"></circle><path d="M-15 0H15M0-15V15"></path><text x="18" y="-12">DENEB / MEASURED</text></g><g transform="translate(698 244)"><circle r="9"></circle><path d="M-15 0H15M0-15V15"></path><text x="-18" y="-12" text-anchor="end">VEGA / MEASURED</text></g><g transform="translate(507 378)"><circle r="9"></circle><path d="M-15 0H15M0-15V15"></path><text x="18" y="-12">ALTAIR / MEASURED</text></g></g>
            <g data-central-layer="context" class="logic-layer-context"><path d="M540 278 698 244 507 378 540 278"></path><text x="570" y="335">EXPECTED SKY CONTEXT</text></g>
            <g data-central-layer="diagnostics" class="logic-layer-diagnostics"><path d="M345 498C425 430 507 355 590 310S720 226 786 210"></path><rect x="650" y="155" width="245" height="50" rx="6"></rect><text x="666" y="177">PREDICTED TRACK / GEOMETRY</text><text x="666" y="194">SNAPSHOT AGE 1H 14M</text></g>
        </svg>`;
    }

    function renderLayerControls(available) {
        if (!available) return `<section class="logic-layer-controls unavailable"><p class="eyebrow">Layered presentation</p><strong>No complete layer manifest</strong><span>No generated groups from another capture are substituted.</span></section>`;
        return `<section class="logic-layer-controls"><p class="eyebrow">Layered presentation</p><strong>Three generated SVG groups</strong><span>Built from one complete retained manifest and its structured layer payloads; the groups are not separate artifacts.</span><label><input type="checkbox" data-central-layer-toggle="measured" checked><i class="measured"></i><span>Measured associations</span></label><label><input type="checkbox" data-central-layer-toggle="context" checked><i class="context"></i><span>Expected sky context</span></label><label><input type="checkbox" data-central-layer-toggle="diagnostics" checked><i class="diagnostics"></i><span>Predicted diagnostics</span></label></section>`;
    }

    function renderLineagePanel(cameraItem, presentation) {
        const sources = explicitLineageFor(cameraItem, presentation);
        const sourceMarkup = sources.length
            ? `<div class="logic-lineage-strip">${sources.map(source => `<span class="${source.relation === "Endpoint N" ? "reference" : ""}"><b>${escapeHtml(source.relation)}</b><strong>${escapeHtml(source.capture)}</strong><small>${escapeHtml(source.artifactId)}</small></span>`).join("")}</div>`
            : `<div class="logic-lineage-missing"><span>${icon("link")}</span><strong>Source identities are not loaded</strong><small>The Combined role is present, but this bounded fixture does not invent source captures or artifact IDs.</small></div>`;
        return `<section class="logic-panel"><header class="logic-panel-heading"><div><h2>Source lineage</h2><p>${sources.length ? "Explicit source artifact identities from this illustrative retained-lineage fixture." : "No authoritative source-lineage fixture is loaded for this capture."}</p></div><span>${sources.length ? `${sources.length} ${sources.length === 1 ? "source" : "sources"}` : "Not loaded"}</span></header>${sourceMarkup}<dl class="logic-facts"><div><dt>Combination</dt><dd>${presentation.hasCombined ? "Causal arithmetic mean" : "None"}</dd></div><div><dt>Registration</dt><dd>${presentation.hasCombined ? "Not established" : "Not applicable"}</dd></div><div class="full"><dt>Boundary</dt><dd>Only explicitly identified artifacts from ${escapeHtml(cameraItem.name)} may appear here.</dd></div></dl></section>`;
    }

    function explicitLineageFor(cameraItem, presentation) {
        if (cameraItem.id === "main-fisheye") {
            return [
                { relation: "N-4", capture: "#84216", artifactId: "art-w6-84216-calibrated" },
                { relation: "N-3", capture: "#84217", artifactId: "art-w6-84217-calibrated" },
                { relation: "N-2", capture: "#84218", artifactId: "art-w6-84218-calibrated" },
                { relation: "N-1", capture: "#84219", artifactId: "art-w6-84219-calibrated" },
                { relation: "Endpoint N", capture: "#84220", artifactId: "art-w6-84220-calibrated" }
            ];
        }
        if (!presentation.hasCombined) {
            const raw = presentation.artifacts.find(artifact => artifact.role === "Raw");
            return [{ relation: "Source", capture: presentation.capture, artifactId: raw.artifactId }];
        }
        return [];
    }

    function renderArtifactInventory(cameraItem, presentation) {
        return `<section class="logic-panel"><header class="logic-panel-heading"><div><h2>Artifact inventory</h2><p>Role presence is separate from byte availability, reconstruction, and per-artifact publication.</p></div><span>${presentation.artifacts.length} retained roles</span></header><div class="logic-artifact-list">${presentation.artifacts.map(artifact => `<article><span class="logic-artifact-role">${artifact.role}</span><span><strong>${artifact.variant}</strong><small>${artifact.artifactId} / ${artifact.media} / ${artifact.objectState} / ${artifact.reconstructionState}</small><em>${artifact.publication ? `Release decision: ${artifact.publication.decision} / ${artifact.publication.releaseId}` : "Not released"}</em></span><button type="button" data-prototype-action="${artifact.display ? `${artifact.role} would open through protected authorized content.` : artifact.role === "Raw" ? "Raw download requires a short-lived audited authorization; inline raw display is not offered." : `${artifact.role} is retained scientific evidence and is not treated as a display-ready image.`}">${artifact.display ? "View" : artifact.role === "Raw" ? "Authorize download" : "Inspect"}</button></article>`).join("")}</div></section>`;
    }

    function bindCameraPresentation(presentation) {
        const figure = document.getElementById("logicPresentationFigure");
        const image = document.getElementById("logicPresentationImage");
        const title = document.getElementById("logicPresentationTitle");
        const description = document.getElementById("logicPresentationDescription");
        const role = document.getElementById("logicPresentationRole");
        const evidence = document.getElementById("logicPresentationEvidence");
        const buttons = [...document.querySelectorAll("[data-presentation-view]")];
        const views = {
            preview: presentation.contentLoaded
                ? ["Preview base", "One exact complete display-ready Preview artifact without grouped presentation layers.", "Preview"]
                : ["Preview metadata", "A complete Preview role is recorded, but its exact bytes are not loaded in this bounded fixture.", "Preview metadata"],
            layers: ["Layered presentation", "The exact Preview artifact rendered with groups generated from a complete retained manifest and structured payloads.", "Preview + generated SVG"]
        };
        const updateEvidence = selected => {
            if (selected === "preview") {
                evidence.textContent = presentation.contentLoaded
                    ? "Complete retained Preview artifact with no generated layer groups displayed."
                    : "A complete Preview role is recorded, but exact bytes are not loaded in this bounded fixture. No other camera image is substituted.";
                return;
            }
            const selectedGroups = [...document.querySelectorAll("[data-central-layer-toggle]:checked")].map(input => input.parentElement.textContent.trim());
            const details = [];
            if (selectedGroups.includes("Measured associations")) details.push("Measured fixtures include Deneb, Vega, and Altair.");
            if (selectedGroups.includes("Predicted diagnostics")) details.push("Diagnostics include a predicted geometry track from a one-hour-fourteen-minute-old snapshot.");
            evidence.textContent = selectedGroups.length
                ? `Layered presentation showing ${selectedGroups.join(", ")}. ${details.join(" ")} These are illustrative fixtures, not a scientific reduction of the preview.`
                : "Layered presentation selected with no generated groups visible.";
        };
        const select = selected => {
            const button = buttons.find(item => item.dataset.presentationView === selected);
            if (!button || button.disabled) return;
            figure.className = `logic-presentation-figure view-${selected}`;
            buttons.forEach(item => item.setAttribute("aria-pressed", String(item === button)));
            title.textContent = views[selected][0];
            description.textContent = views[selected][1];
            role.textContent = views[selected][2];
            if (image) image.alt = `Preview artifact for capture ${presentation.capture}`;
            updateEvidence(selected);
        };
        buttons.forEach(button => button.addEventListener("click", () => select(button.dataset.presentationView)));
        document.querySelectorAll("[data-central-layer-toggle]").forEach(input => input.addEventListener("change", () => {
            document.querySelector(`[data-central-layer="${input.dataset.centralLayerToggle}"]`)?.classList.toggle("layer-hidden", !input.checked);
            updateEvidence(figure.classList.contains("view-layers") ? "layers" : "preview");
        }));
    }

    function renderCameraProcessing(item, selectedCamera) {
        const fixture = cameraProcessingFixtures[`${item.id}/${selectedCamera.id}`];
        document.title = `Processing - ${selectedCamera.name} | HVO SkyMonitor Prototype`;
        if (!fixture) {
            renderMissingCameraProcessing(item, selectedCamera);
            return;
        }
        const selectedExecution = fixture.executions.find(execution => execution.origin === "central" && execution.state === "Produced") ?? fixture.executions[0];
        const selectedNode = fixture.edge.nodes[0];
        const localAction = selectedCamera.localPrototype
            ? `<a class="button secondary" href="operations.html?section=pipeline">Open local pipeline prototype</a>`
            : "";
        elements.page.innerHTML = `
            ${breadcrumbs([{ label: item.name, href: routeHref({ observatory: item.id }) }, { label: selectedCamera.name, href: routeHref({ observatory: item.id, camera: selectedCamera.id }) }, { label: "Processing" }])}
            ${pageHeader(`${item.code} / ${selectedCamera.code} / two execution authorities`, "Processing", `Inspect received CameraAgent execution evidence and operate LogicHost-owned central processing for ${selectedCamera.name}.`, "Signed-in Owner / protected processing", localAction)}
            ${cameraTabs(item, selectedCamera, "processing")}
            <div class="logic-boundary processing"><span class="logic-boundary-icon">${icon("pipeline")}</span><span><strong>Two graphs, two authorities.</strong><p>The edge graph is immutable received evidence from CameraAgent. The central graph, jobs, successor processing, and presentation policy belong to LogicHost. Similar controls never imply one shared runtime or direct access to the camera.</p></span></div>
            <section class="logic-current-status" aria-label="Camera processing evidence status">
                ${currentStatusFact("Edge graph evidence", fixture.edge.revision, `Observed with capture ${fixture.edge.capture}`, "success")}
                ${currentStatusFact("Central policy", fixture.central.revision, `Effective ${fixture.central.effectiveFrom}`, "success")}
                ${currentStatusFact("Latest central execution", selectedExecution.state, selectedExecution.id, selectedExecution.tone)}
                ${currentStatusFact("Camera report", statusLabel(selectedCamera.status), `Observed ${selectedCamera.fleetObserved}`, statusClass(selectedCamera.status))}
                ${currentStatusFact("Proposal channel", "No channel evidence", "Independent of camera heartbeat", "pending")}
            </section>
            <section class="logic-processing-graphs" aria-label="Edge and central processing graphs">
                ${renderProcessingGraph(fixture.edge, "edge")}
                ${renderProcessingGraph(fixture.central, "central")}
            </section>
            <section id="logicProcessingNodeInspector" class="logic-panel logic-processing-node-inspector" aria-live="polite">
                ${renderProcessingNodeInspector(fixture.edge, selectedNode, "edge")}
            </section>
            <section class="logic-panel logic-processing-executions">
                <header class="logic-panel-heading"><div><h2>Execution evidence</h2><p>Imported edge outcomes and LogicHost jobs use the same evidence vocabulary while preserving origin and authority.</p></div><span>${fixture.executions.length} illustrative executions</span></header>
                <div class="logic-processing-execution-layout">
                    <div class="logic-processing-execution-list" role="group" aria-label="Select processing execution">
                        ${fixture.executions.map(execution => renderProcessingExecutionButton(execution, execution.id === selectedExecution.id)).join("")}
                    </div>
                    <div id="logicProcessingExecutionDetail" class="logic-processing-execution-detail" aria-live="polite">
                        ${renderProcessingExecutionDetail(selectedExecution)}
                    </div>
                </div>
            </section>
            ${renderProcessingComparison(fixture)}
            <div class="logic-processing-bottom-grid">
                ${renderProcessingPresentation(fixture, selectedCamera)}
                ${renderEdgeProposal(fixture, selectedCamera)}
            </div>`;
        bindCameraProcessing(fixture);
    }

    function renderMissingCameraProcessing(item, selectedCamera) {
        elements.page.innerHTML = `
            ${breadcrumbs([{ label: item.name, href: routeHref({ observatory: item.id }) }, { label: selectedCamera.name, href: routeHref({ observatory: item.id, camera: selectedCamera.id }) }, { label: "Processing" }])}
            ${pageHeader(`${item.code} / ${selectedCamera.code} / processing evidence`, "Processing", `Inspect received edge evidence and central processing for ${selectedCamera.name}.`, "Signed-in Owner / protected processing")}
            ${cameraTabs(item, selectedCamera, "processing")}
            <div class="logic-boundary processing"><span class="logic-boundary-icon">${icon("pipeline")}</span><span><strong>No processing record from another camera is substituted.</strong><p>Graphs, node outcomes, jobs, and deployment proposals remain bound to one observatory, logical camera, agent, and installation.</p></span></div>
            <section class="logic-panel logic-processing-missing"><span>${icon("database")}</span><div><p class="eyebrow">Bounded processing fixture</p><h2>Detailed execution evidence is not loaded</h2><p>This prototype knows the last received pipeline label for ${escapeHtml(selectedCamera.name)}, but it does not invent graph nodes, job attempts, outputs, or channel state.</p><dl class="logic-facts"><div><dt>Received pipeline label</dt><dd>${escapeHtml(selectedCamera.pipeline)}</dd></div><div><dt>Latest capture</dt><dd>${escapeHtml(selectedCamera.capture)}</dd></div><div><dt>Agent</dt><dd>${escapeHtml(selectedCamera.agent)}</dd></div><div><dt>Installation</dt><dd>${escapeHtml(selectedCamera.installation)}</dd></div></dl></div></section>`;
    }

    function renderProcessingGraph(graph, origin) {
        const levels = [...new Set(graph.nodes.map(node => node.level))].sort((left, right) => left - right);
        const isEdge = origin === "edge";
        const facts = isEdge
            ? `${graph.schema} / observed ${graph.observedAt} / received ${graph.receivedAt}`
            : `${graph.schema} / effective ${graph.effectiveFrom} / ${graph.policy}`;
        return `<article class="logic-panel logic-processing-graph ${origin}">
            <header class="logic-processing-graph-heading"><div><p class="eyebrow">${isEdge ? "Received edge graph evidence" : "LogicHost-owned central graph"}</p><h2>${escapeHtml(graph.revision)}</h2><p>${escapeHtml(facts)}</p></div><span class="logic-authority${isEdge ? " local" : ""}"><i></i>${isEdge ? "CameraAgent local" : "LogicHost central"}</span></header>
            <dl class="logic-processing-graph-identity"><div><dt>Graph</dt><dd>${escapeHtml(graph.id)}</dd></div><div><dt>Plan digest</dt><dd>${escapeHtml(graph.digest)} <span>(abridged)</span></dd></div></dl>
            <ol class="logic-processing-levels" aria-label="${isEdge ? "Received edge" : "Central"} graph nodes by dependency level">
                ${levels.map(level => `<li class="logic-processing-level"><span>Level ${level}</span><div>${graph.nodes.filter(node => node.level === level).map(node => renderProcessingNode(node, origin, isEdge && node.id === graph.nodes[0].id)).join("")}</div></li>`).join("")}
            </ol>
            <footer><span>${isEdge ? `Captured with ${escapeHtml(graph.capture)}; this is not asserted to be the agent's current active graph.` : "Topological levels show dependencies, not concurrent execution."}</span>${isEdge ? `<button type="button" data-prototype-action="A bounded CameraAgent proposal draft would start from this received graph without editing it or contacting the agent.">Use as proposal basis</button>` : `<button type="button" data-prototype-action="LogicHost would create an immutable successor central-graph draft. The effective graph and historical jobs would remain unchanged.">Create successor graph draft</button>`}</footer>
        </article>`;
    }

    function renderProcessingNode(node, origin, selected) {
        const dependencies = node.dependsOn.length ? node.dependsOn.join(", ") : "Graph input";
        return `<button type="button" class="logic-processing-node ${selected ? "active" : ""}" data-processing-node="${origin}:${escapeHtml(node.id)}" aria-pressed="${String(selected)}"><span><strong>${escapeHtml(node.title)}</strong><small>${escapeHtml(node.type)} / ${escapeHtml(node.requirement)}</small></span><em>Depends on: ${escapeHtml(dependencies)}</em></button>`;
    }

    function renderProcessingNodeInspector(graph, node, origin) {
        const isEdge = origin === "edge";
        return `<header class="logic-panel-heading"><div><p class="eyebrow">Selected ${isEdge ? "edge" : "central"} node</p><h2>${escapeHtml(node.title)}</h2><p>${escapeHtml(node.options)}</p></div><span class="logic-authority${isEdge ? " local" : ""}"><i></i>${isEdge ? "Received / read only" : "LogicHost-owned"}</span></header><dl class="logic-processing-node-facts"><div><dt>Stable node ID</dt><dd>${escapeHtml(node.id)}</dd></div><div><dt>Operation</dt><dd>${escapeHtml(node.type)}</dd></div><div><dt>Requirement</dt><dd>${escapeHtml(node.requirement)}</dd></div><div><dt>Graph revision</dt><dd>${escapeHtml(graph.revision)}</dd></div><div><dt>Input selection</dt><dd>${escapeHtml(node.input)}</dd></div><div><dt>Outputs</dt><dd>${node.outputs.map(escapeHtml).join("; ")}</dd></div><div class="full"><dt>Dependencies</dt><dd>${node.dependsOn.length ? node.dependsOn.map(escapeHtml).join(", ") : "Explicit graph input"}</dd></div></dl>`;
    }

    function renderProcessingExecutionButton(execution, selected) {
        return `<button type="button" class="logic-processing-execution ${selected ? "active" : ""}" data-processing-execution="${escapeHtml(execution.id)}" aria-pressed="${String(selected)}"><span class="logic-origin ${execution.origin}">${execution.origin === "edge" ? "Edge evidence" : "Central job"}</span><strong>${escapeHtml(execution.capture)} / ${escapeHtml(execution.state)}</strong><small>${escapeHtml(execution.id)}</small><span>${escapeHtml(execution.started)} / ${escapeHtml(execution.duration)}</span></button>`;
    }

    function renderProcessingExecutionDetail(execution) {
        const central = execution.origin === "central";
        return `<header><div><p class="eyebrow">${escapeHtml(execution.originLabel)}</p><h3>${escapeHtml(execution.id)}</h3><p>${escapeHtml(execution.trigger)} / graph ${escapeHtml(execution.graph)}</p></div><span class="state-chip ${escapeHtml(execution.tone)}">${escapeHtml(execution.state)}</span></header>
            <dl class="logic-processing-execution-facts"><div><dt>Capture</dt><dd>${escapeHtml(execution.capture)}</dd></div><div><dt>Source</dt><dd>${escapeHtml(execution.source)}</dd></div><div><dt>Started</dt><dd>${escapeHtml(execution.started)}</dd></div><div><dt>Completed</dt><dd>${escapeHtml(execution.completed)}</dd></div><div><dt>Attempt</dt><dd>${execution.attempt}</dd></div><div><dt>Duration</dt><dd>${escapeHtml(execution.duration)}</dd></div></dl>
            <div class="logic-table-wrap"><table class="logic-table logic-processing-table"><caption class="visually-hidden">Node outcomes for ${escapeHtml(execution.id)}</caption><thead><tr><th scope="col">Node</th><th scope="col">Outcome</th><th scope="col">Attempt</th><th scope="col">Duration</th><th scope="col">Reason</th><th scope="col">Output</th></tr></thead><tbody>${execution.nodes.map(node => `<tr><th scope="row">${escapeHtml(node.name)}</th><td><span class="state-chip ${escapeHtml(node.tone)}">${escapeHtml(node.state)}</span></td><td>${node.attempt}</td><td>${escapeHtml(node.duration)}</td><td>${escapeHtml(node.reason)}</td><td>${escapeHtml(node.output)}</td></tr>`).join("")}</tbody></table></div>
            <div class="logic-processing-execution-artifacts"><h4>Recorded artifacts</h4>${execution.artifacts.map(artifact => `<article><span class="logic-artifact-role">${escapeHtml(artifact.role)}</span><span><strong>${escapeHtml(artifact.id)}</strong><small>${escapeHtml(artifact.variant)} / ${escapeHtml(artifact.state)}</small><em>Source: ${escapeHtml(artifact.lineage)}</em></span></article>`).join("")}</div>
            <footer>${central ? `<button class="button secondary" type="button" data-processing-successor="${escapeHtml(execution.id)}">Create successor reprocess</button><span>Creates a new central job and artifacts; this execution remains immutable.</span>` : `<span>Edge outcomes are imported evidence. Retry and local reacquisition remain CameraAgent operations.</span>`}</footer>`;
    }

    function renderProcessingComparison(fixture) {
        const differenceCount = fixture.comparison.filter(item => !item.match).length;
        return `<section class="logic-panel logic-processing-compare"><header class="logic-panel-heading"><div><h2>Edge and central output comparison</h2><p>Compare exact execution inputs, recipe selections, outputs, and authority without declaring either origin universally preferable.</p></div><label><input id="logicProcessingDifferences" type="checkbox"> Show differences only</label></header><div class="logic-table-wrap"><table class="logic-table"><caption class="visually-hidden">Comparison of edge execution edge-exec-w6-84220-r31 and central execution central-exec-hvo-84220-r18</caption><thead><tr><th scope="col">Subject</th><th scope="col">Received edge execution</th><th scope="col">Central execution</th><th scope="col">Assessment</th></tr></thead><tbody id="logicProcessingCompareRows">${fixture.comparison.map(item => `<tr data-processing-match="${item.match ? "same" : "different"}"><th scope="row">${escapeHtml(item.subject)}</th><td>${escapeHtml(item.edge)}</td><td>${escapeHtml(item.central)}</td><td><span class="state-chip ${item.match ? "success" : "warning"}">${escapeHtml(item.result)}</span></td></tr>`).join("")}</tbody></table></div><footer><span id="logicProcessingCompareCount">${fixture.comparison.length} comparison facts / ${differenceCount} differences</span><button type="button" data-prototype-action="A detailed pixel, metric, and checksum comparison would open for the two explicitly selected immutable executions.">Open detailed comparison</button></footer></section>`;
    }

    function renderProcessingPresentation(fixture, selectedCamera) {
        return `<section class="logic-panel logic-processing-presentation"><header class="logic-panel-heading"><div><p class="eyebrow">LogicHost-owned presentation</p><h2>Central overlay policy</h2><p>Prepare a successor presentation policy without changing uploaded edge artifacts or an existing release.</p></div><span>${escapeHtml(fixture.presentation.revision)}</span></header>
            <figure id="logicProcessingOverlayFigure" class="logic-presentation-figure view-layers"><img src="${escapeHtml(selectedCamera.previewContent)}" alt="Central Preview artifact ${escapeHtml(fixture.presentation.base)} with draft generated overlays" aria-describedby="logicProcessingOverlaySummary">${renderCentralLayers(true)}<figcaption><span><strong>${escapeHtml(fixture.presentation.base)}</strong><small>Exact retained central Preview / draft visibility only</small></span></figcaption></figure>
            <div class="logic-processing-policy-controls"><dl><div><dt>Policy</dt><dd>${escapeHtml(fixture.presentation.id)}</dd></div><div><dt>Base rule</dt><dd>${escapeHtml(fixture.presentation.baseRule)}</dd></div><div><dt>Public eligibility</dt><dd>${escapeHtml(fixture.presentation.publicEligibility)}</dd></div></dl><fieldset><legend>Generated overlay groups</legend><label><input type="checkbox" data-processing-overlay="measured" checked><i class="measured"></i><span>Measured associations</span></label><label><input type="checkbox" data-processing-overlay="context" checked><i class="context"></i><span>Expected sky context</span></label><label><input type="checkbox" data-processing-overlay="diagnostics" checked><i class="diagnostics"></i><span>Predicted diagnostics</span></label></fieldset><label class="logic-field">Successor output<select id="logicProcessingPresentationOutput"><option value="layers">Structured groups only</option><option value="materialized">Groups plus AnnotatedPreview</option></select></label><p id="logicProcessingOverlaySummary" role="status">Draft shows 3 generated groups: Measured associations, Expected sky context, and Predicted diagnostics. They are rendered over ${escapeHtml(fixture.presentation.base)}; visibility does not mutate the artifact or publication state.</p><div class="logic-processing-policy-actions"><button id="logicProcessingPreviewPolicy" class="button primary" type="button">Preview successor policy</button><button class="button secondary" type="button" data-prototype-action="LogicHost would validate and create an immutable successor central presentation-policy revision; current outputs and releases would remain unchanged.">Review activation</button></div></div></section>`;
    }

    function renderEdgeProposal(fixture, selectedCamera) {
        const proposal = fixture.proposal;
        return `<section class="logic-panel logic-edge-proposal"><header class="logic-panel-heading"><div><p class="eyebrow">Future management boundary</p><h2>CameraAgent pipeline proposal</h2><p>Prepare an immutable desired revision for later CameraAgent retrieval without implying inbound access or immediate activation.</p></div><span class="state-chip pending">${escapeHtml(proposal.state)}</span></header><div class="logic-boundary"><span class="logic-boundary-icon">${icon("link")}</span><span><strong>Agent-initiated transport only.</strong><p>The channel contract is intentionally not designed here. Heartbeat, proposal retrieval, local acceptance, staging, and active-revision reporting remain separate evidence.</p></span></div><dl class="logic-processing-proposal-facts"><div><dt>Target logical camera</dt><dd>${escapeHtml(selectedCamera.name)}</dd></div><div><dt>Target agent</dt><dd>${escapeHtml(proposal.targetAgent)}</dd></div><div><dt>Installation</dt><dd>${escapeHtml(proposal.installation)}</dd></div><div><dt>Expected base</dt><dd>${escapeHtml(proposal.expectedBase)}</dd></div><div><dt>Candidate</dt><dd>${escapeHtml(proposal.candidate)}</dd></div><div><dt>Channel evidence</dt><dd>${escapeHtml(proposal.channel)}</dd></div></dl><ol class="logic-proposal-lifecycle" aria-label="Potential CameraAgent proposal lifecycle"><li class="current">Draft</li><li>Await contact</li><li>Retrieved</li><li>Accepted or rejected</li><li>Staged</li><li>Active revision reported</li></ol><footer><button class="button secondary" type="button" data-prototype-action="A proposal draft would bind this exact agent, installation, expected base revision, capabilities, and expiry. It would not contact or activate CameraAgent.">Prepare bounded proposal</button><span>Focus, physical calibration acquisition, camera controls, and local admission remain CameraAgent-only.</span></footer></section>`;
    }

    function bindCameraProcessing(fixture) {
        const nodeInspector = document.getElementById("logicProcessingNodeInspector");
        const graphByOrigin = { edge: fixture.edge, central: fixture.central };
        document.querySelectorAll("[data-processing-node]").forEach(button => button.addEventListener("click", () => {
            const [origin, nodeId] = button.dataset.processingNode.split(":");
            const graph = graphByOrigin[origin];
            const node = graph.nodes.find(candidate => candidate.id === nodeId);
            if (!node) return;
            document.querySelectorAll("[data-processing-node]").forEach(candidate => {
                const selected = candidate === button;
                candidate.classList.toggle("active", selected);
                candidate.setAttribute("aria-pressed", String(selected));
            });
            nodeInspector.innerHTML = renderProcessingNodeInspector(graph, node, origin);
        }));

        const executionDetail = document.getElementById("logicProcessingExecutionDetail");
        const bindSuccessor = () => executionDetail.querySelector("[data-processing-successor]")?.addEventListener("click", event => {
            showToast(`A new LogicHost successor job would be created from ${event.currentTarget.dataset.processingSuccessor}; existing execution and artifacts remain immutable.`);
        });
        document.querySelectorAll("[data-processing-execution]").forEach(button => button.addEventListener("click", () => {
            const execution = fixture.executions.find(candidate => candidate.id === button.dataset.processingExecution);
            if (!execution) return;
            document.querySelectorAll("[data-processing-execution]").forEach(candidate => {
                const selected = candidate === button;
                candidate.classList.toggle("active", selected);
                candidate.setAttribute("aria-pressed", String(selected));
            });
            executionDetail.innerHTML = renderProcessingExecutionDetail(execution);
            bindSuccessor();
        }));
        bindSuccessor();

        const differences = document.getElementById("logicProcessingDifferences");
        const comparisonRows = [...document.querySelectorAll("[data-processing-match]")];
        const comparisonCount = document.getElementById("logicProcessingCompareCount");
        differences.addEventListener("change", () => {
            comparisonRows.forEach(row => { row.hidden = differences.checked && row.dataset.processingMatch === "same"; });
            const visible = comparisonRows.filter(row => !row.hidden).length;
            comparisonCount.textContent = differences.checked ? `${visible} differences shown` : `${comparisonRows.length} comparison facts / ${comparisonRows.length - comparisonRows.filter(row => row.dataset.processingMatch === "same").length} differences`;
        });

        const overlayInputs = [...document.querySelectorAll("[data-processing-overlay]")];
        const overlaySummary = document.getElementById("logicProcessingOverlaySummary");
        const output = document.getElementById("logicProcessingPresentationOutput");
        const updateOverlayDraft = preview => {
            const selected = overlayInputs.filter(input => input.checked).map(input => input.parentElement.textContent.trim());
            overlayInputs.forEach(input => document.querySelector(`[data-central-layer="${input.dataset.processingOverlay}"]`)?.classList.toggle("layer-hidden", !input.checked));
            const outputLabel = output.value === "materialized" ? "and materializes a successor AnnotatedPreview" : "and retains structured groups without materialization";
            const selectedLabel = selected.length ? `: ${selected.join(", ")}` : "";
            overlaySummary.textContent = `Draft ${preview ? "preview " : ""}shows ${selected.length} generated group${selected.length === 1 ? "" : "s"}${selectedLabel} over ${fixture.presentation.base} ${outputLabel}. Visibility does not mutate the base artifact or publication state.`;
        };
        overlayInputs.forEach(input => input.addEventListener("change", () => updateOverlayDraft(false)));
        output.addEventListener("change", () => updateOverlayDraft(false));
        document.getElementById("logicProcessingPreviewPolicy").addEventListener("click", () => updateOverlayDraft(true));
    }

    function renderCameraArchive(item, selectedCamera) {
        const entries = cameraArchiveEntries(selectedCamera);
        document.title = `Archive - ${selectedCamera.name} | HVO SkyMonitor Prototype`;
        elements.page.innerHTML = `
            ${breadcrumbs([{ label: item.name, href: routeHref({ observatory: item.id }) }, { label: selectedCamera.name, href: routeHref({ observatory: item.id, camera: selectedCamera.id }) }, { label: "Archive" }])}
            ${pageHeader(`${item.code} / ${selectedCamera.code} / retained captures`, "Camera archive", `Browse a bounded newest-first central history across installations explicitly linked to ${selectedCamera.name}.`, "Protected camera archive")}
            ${cameraTabs(item, selectedCamera, "archive")}
            <div class="logic-boundary"><span class="logic-boundary-icon">${icon("image")}</span><span><strong>Metadata can outlive image content.</strong><p>These six illustrative records are filtered in the browser. Production will use authorized keyset paging; role presence alone will never imply that bytes are available or reconstruction is complete.</p></span></div>
            <section class="logic-panel logic-archive-toolbar" aria-label="Loaded camera archive filters">
                <label class="logic-field">Find loaded capture<input id="logicArchiveSearch" type="search" placeholder="Capture ID, state, or installation"></label>
                <label class="logic-field">Central state<select id="logicArchiveState"><option value="all">All states</option><option value="complete">Complete</option><option value="attention">Needs attention</option><option value="unavailable">Content unavailable</option></select></label>
                <label class="logic-field">Display artifact<select id="logicArchiveProduct"><option value="all">All records</option><option value="display">Display image retained</option><option value="none">No display image</option></select></label>
                <div class="logic-archive-layout" role="group" aria-label="Archive layout"><button class="active" type="button" data-archive-layout="grid" aria-pressed="true">${icon("image")}<span class="visually-hidden">Grid</span></button><button type="button" data-archive-layout="compact" aria-pressed="false">${icon("database")}<span class="visually-hidden">Compact</span></button></div>
                <span id="logicArchiveCount" class="logic-result-count">${entries.length} loaded records</span>
            </section>
            <section id="logicCameraArchive" class="logic-camera-archive" aria-label="Loaded captures">
                ${entries.map(entry => renderArchiveCard(selectedCamera, entry)).join("")}
                <p id="logicArchiveEmpty" class="logic-empty" hidden>No loaded captures match these filters.</p>
            </section>
            <footer class="logic-archive-footer"><span>Newest first / bounded fixture page / no total count implied</span><button class="button secondary" type="button" data-prototype-action="Production archive paging will request the next authorized keyset page without loading complete history.">Load older records</button></footer>`;
        bindCameraArchive();
    }

    function cameraArchiveEntries(cameraItem) {
        const base = captureNumber(cameraItem);
        const specifications = [
            { offset: 0, state: "complete", status: "Complete", object: "Available", reconstruction: "Complete", display: true, thumbnailLoaded: Boolean(cameraItem.previewContent), role: cameraItem.role, artifacts: 8 },
            { offset: 1, state: "complete", status: "Complete", object: "Available", reconstruction: "Complete", display: true, thumbnailLoaded: false, role: "Preview", artifacts: 7 },
            { offset: 2, state: "attention", status: "Awaiting reference", object: "Available", reconstruction: "PendingReference", display: false, thumbnailLoaded: false, role: "No display image", artifacts: 3, reason: "Rig profile reference pending" },
            { offset: 3, state: "complete", status: "Complete", object: "Available", reconstruction: "Complete", display: true, thumbnailLoaded: false, role: "Preview", artifacts: 6 },
            { offset: 4, state: "attention", status: "Quarantined", object: "Quarantined", reconstruction: "Quarantined", display: false, thumbnailLoaded: false, role: "No display image", artifacts: 2, reason: "Integrity evidence retained" },
            { offset: 5, state: "unavailable", status: "Content expired", object: "Expired", reconstruction: "Complete", display: false, thumbnailLoaded: false, role: "No display image", artifacts: 6, reason: "Capture metadata retained" }
        ];
        return specifications.map(specification => {
            const id = `#${base - specification.offset}`;
            const captured = shiftCameraTimestamp(cameraItem, specification.offset * -12);
            const installation = specification.offset < 4 ? cameraItem.installation : `${cameraItem.installation}-prior`;
            return {
                ...specification,
                id,
                captured,
                firstReceived: shiftCameraTimestamp(cameraItem, specification.offset * -12 + 8),
                installation,
                publication: specification.offset === 0 && cameraItem.releaseArtifact?.capture === cameraItem.capture
                    ? cameraItem.releaseArtifact
                    : null,
                search: `${id} ${specification.status} ${specification.object} ${specification.reconstruction} ${installation} ${captured}`.toLowerCase()
            };
        });
    }

    function renderArchiveCard(cameraItem, entry) {
        const visual = entry.thumbnailLoaded
            ? `<figure><img loading="lazy" src="${cameraItem.previewContent}" alt="${escapeHtml(entry.role)} for capture ${escapeHtml(entry.id)}"><span class="logic-archive-role">${escapeHtml(entry.role)}</span><span class="logic-archive-time">${escapeHtml(entry.captured)}</span></figure>`
            : entry.display
                ? `<div class="logic-archive-unavailable retained"><span>${icon("image")}</span><strong>Display image retained</strong><small>Thumbnail bytes are not loaded in this bounded fixture.</small></div>`
                : `<div class="logic-archive-unavailable"><span>${icon(entry.object === "Expired" ? "clock" : "warning")}</span><strong>${escapeHtml(entry.status)}</strong><small>${escapeHtml(entry.reason)}</small></div>`;
        return `<article class="logic-archive-card" data-archive-card data-search="${escapeHtml(entry.search)}" data-state="${entry.state}" data-product="${entry.display ? "display" : "none"}">${visual}<div class="logic-archive-card-body"><header><div><p class="eyebrow">Retained capture</p><h2>${escapeHtml(entry.id)}</h2></div><span class="state-chip ${entry.state === "complete" ? "success" : entry.state === "attention" ? "warning" : "pending"}">${escapeHtml(entry.status)}</span></header><p>${escapeHtml(entry.captured)} / first received ${escapeHtml(entry.firstReceived)}</p><dl class="logic-archive-facts"><div><dt>Object</dt><dd>${escapeHtml(entry.object)}</dd></div><div><dt>Reconstruction</dt><dd>${escapeHtml(entry.reconstruction)}</dd></div><div><dt>Artifacts</dt><dd>${entry.artifacts} recorded</dd></div><div><dt>Publication</dt><dd>${entry.publication ? `Released / ${escapeHtml(entry.publication.artifactId)}` : "Not released"}</dd></div><div class="full"><dt>Installation at capture</dt><dd>${escapeHtml(entry.installation)}</dd></div></dl><footer><button type="button" data-prototype-action="Capture ${escapeHtml(entry.id)} detail will open with this archive query and logical-camera scope preserved.">Open capture evidence</button>${entry.display ? `<button type="button" data-prototype-action="The exact complete ${escapeHtml(entry.role)} for capture ${escapeHtml(entry.id)} would be fetched through protected authorized content; no other capture image is substituted.">Large image</button>` : ""}</footer></div></article>`;
    }

    function bindCameraArchive() {
        const archive = document.getElementById("logicCameraArchive");
        const search = document.getElementById("logicArchiveSearch");
        const state = document.getElementById("logicArchiveState");
        const product = document.getElementById("logicArchiveProduct");
        const cards = [...archive.querySelectorAll("[data-archive-card]")];
        const count = document.getElementById("logicArchiveCount");
        const empty = document.getElementById("logicArchiveEmpty");
        const apply = () => {
            const query = search.value.trim().toLowerCase();
            let visible = 0;
            cards.forEach(card => {
                const matches = (!query || card.dataset.search.includes(query)) && (state.value === "all" || card.dataset.state === state.value) && (product.value === "all" || card.dataset.product === product.value);
                card.hidden = !matches;
                if (matches) visible += 1;
            });
            count.textContent = `${visible} loaded ${visible === 1 ? "record" : "records"}`;
            empty.hidden = visible > 0;
        };
        search.addEventListener("input", apply);
        state.addEventListener("change", apply);
        product.addEventListener("change", apply);
        document.querySelectorAll("[data-archive-layout]").forEach(button => button.addEventListener("click", () => {
            const compact = button.dataset.archiveLayout === "compact";
            archive.classList.toggle("compact", compact);
            document.querySelectorAll("[data-archive-layout]").forEach(item => {
                item.classList.toggle("active", item === button);
                item.setAttribute("aria-pressed", String(item === button));
            });
        }));
    }

    function renderCameraEventsPlaceholder(item, selectedCamera) {
        const title = "Related events";
        const description = "This route filters event contributions from this camera; the central event can retain evidence from additional authorized observatories.";
        document.title = `${title} - ${selectedCamera.name} | HVO SkyMonitor Prototype`;
        elements.page.innerHTML = `
            ${breadcrumbs([{ label: item.name, href: routeHref({ observatory: item.id }) }, { label: selectedCamera.name, href: routeHref({ observatory: item.id, camera: selectedCamera.id }) }, { label: title }])}
            ${pageHeader(`${item.code} / ${selectedCamera.code}`, title, description, "Scoped prototype route")}
            ${cameraTabs(item, selectedCamera, "events")}
            <section class="logic-placeholder"><div><span>${icon("warning")}</span><h2>${escapeHtml(title)} is the next detail slice</h2><p>This camera view will show its contributions, not redefine central event ownership. Multi-site records must name every contributing observatory and enforce access across all of them.</p><a class="button secondary" href="${routeHref({ observatory: item.id, camera: selectedCamera.id })}">Return to current sky</a></div></section>`;
    }

    function captureNumber(cameraItem) {
        return Number(cameraItem.capture.replace("#", ""));
    }

    function formatCameraTimestamp(cameraItem) {
        return shiftCameraTimestamp(cameraItem, 0);
    }

    function shiftCameraTimestamp(cameraItem, seconds) {
        const time = cameraItem.captured.replace(" UTC", "");
        const timestamp = new Date(`2026-09-01T${time}Z`);
        timestamp.setUTCSeconds(timestamp.getUTCSeconds() + seconds);
        return `${timestamp.toISOString().slice(0, 10)} ${timestamp.toISOString().slice(11, 19)} UTC`;
    }

    function renderLogicHostOperations() {
        document.title = "LogicHost Operations | HVO SkyMonitor Prototype";
        elements.page.innerHTML = `
            ${pageHeader("Central platform / LogicHost only", "LogicHost operations", "Inspect shared central infrastructure without presenting CameraAgent-local configuration as a central concern.", "Protected platform service view")}
            <div class="logic-boundary"><span class="logic-boundary-icon">${icon("operations")}</span><span><strong>This workspace is not an observatory or camera.</strong><p>It covers central trust, ingest, processing, persistence, notifications, and public curation. Enter an observatory before changing membership, camera installations, policy overrides, or publication decisions for that scope.</p></span></div>
            <section class="logic-status-deck"><div class="logic-status-primary"><span class="status-icon success" aria-hidden="true"></span><span><strong>Central services healthy</strong><small>Ingest accepting artifacts / workers processing / public projection available</small></span></div><dl class="logic-status-facts"><div><dt>Ingest queue</dt><dd>1 retry</dd></div><div><dt>Central jobs</dt><dd>3 active</dd></div><div><dt>Object storage</dt><dd>Healthy</dd></div><div><dt>Notifications</dt><dd>Available</dd></div></dl></section>
            <section class="logic-operation-grid">${logicHostOperationCards().join("")}</section>
            <section class="logic-panel logic-grid"><header class="logic-panel-heading"><div><h2>Service posture</h2><p>Central dependencies remain separate from CameraAgent capture health.</p></div><span>03:14 UTC</span></header><div class="logic-table-wrap"><table class="logic-table"><thead><tr><th>Service</th><th>Role</th><th>State</th><th>Current work</th></tr></thead><tbody><tr><td><strong>Artifact ingest</strong><small>Authenticated device API</small></td><td>Verify manifests and checksums</td><td><span class="state-chip success">Healthy</span></td><td>0 waiting / 1 bounded retry</td></tr><tr><td><strong>Central workers</strong><small>Durable job scheduler</small></td><td>Reconstruction and derivatives</td><td><span class="state-chip success">Healthy</span></td><td>3 active / 12 complete</td></tr><tr><td><strong>Publication projection</strong><small>Anonymous read model</small></td><td>Released records only</td><td><span class="state-chip success">Healthy</span></td><td>${releasedImages().length} image releases loaded</td></tr><tr><td><strong>Mail delivery</strong><small>Review-gated notifications</small></td><td>Owner and event notices</td><td><span class="state-chip success">Available</span></td><td>0 pending</td></tr></tbody></table></div></section>`;
    }

    function logicHostOperationCards() {
        return [
            operationCard("inbox", "Ingest and reconstruction", "Verify device submissions, preserve provenance, and inspect durable reconstruction state.", "Platform", "Inspect queue"),
            operationCard("pipeline", "Central processing jobs", "Cancel, requeue, or create successor jobs without rewriting historical evidence.", "Manager / Owner", "Open jobs"),
            operationCard("database", "Data services", "Inspect SQL Server, Redis, and provider-neutral object-storage health and capacity.", "Platform", "View health"),
            operationCard("shield", "Trust service posture", "Inspect the central services that enforce observatory membership, device registration, and revocation.", "Platform", "View service"),
            operationCard("public", "Publication projection", "Inspect released-record projection health and public-homepage curation state.", "Platform Editor for curation", "View service"),
            operationCard("bell", "Notifications", "Inspect durable, review-gated owner and event notification delivery.", "Platform", "View delivery")
        ];
    }

    function operationCard(iconName, title, description, authority, action, href = "") {
        const control = href
            ? `<a href="${href}">${escapeHtml(action)}</a>`
            : `<button type="button" data-prototype-action="${escapeHtml(`${title} will be detailed in a later prototype slice.`)}">${escapeHtml(action)}</button>`;
        return `<article class="logic-operation-card"><span class="logic-operation-icon">${icon(iconName)}</span><h3>${escapeHtml(title)}</h3><p>${escapeHtml(description)}</p><footer><span>${escapeHtml(authority)}</span>${control}</footer></article>`;
    }

    function renderObservatoryOperations(item) {
        document.title = `Operations - ${item.name} | HVO SkyMonitor Prototype`;
        elements.page.innerHTML = `
            ${breadcrumbs([{ label: "Network", href: routeHref() }, { label: item.name, href: routeHref({ observatory: item.id }) }, { label: "Operations" }])}
            ${pageHeader(`${item.code} / central observatory scope`, "Observatory operations", `Manage LogicHost-owned membership, logical cameras, central policy, and publication for ${item.name}.`, "Signed-in Owner / observatory")}
            ${observatoryTabs(item, "operations")}
            <div class="logic-boundary"><span class="logic-boundary-icon">${icon("scope")}</span><span><strong>No reusable configuration crosses observatory boundaries.</strong><p>Actions here target ${escapeHtml(item.name)} only. Camera schedules and capture pipelines are displayed as received evidence; they remain locally owned and editable at each CameraAgent.</p></span></div>
            <section class="logic-operation-grid">${observatoryOperationCards(item).join("")}</section>
            <section class="logic-panel logic-grid"><header class="logic-panel-heading"><div><h2>Received camera configuration evidence</h2><p>Capture-time revisions preserved by LogicHost, not central schedule or pipeline editors.</p></div><span>${item.cameras.length} cameras</span></header><div class="logic-table-wrap"><table class="logic-table"><thead><tr><th>Camera</th><th>Local schedule evidence</th><th>Local pipeline evidence</th><th>Latest retained capture</th></tr></thead><tbody>${item.cameras.map(cameraItem => `<tr><td><strong>${escapeHtml(cameraItem.name)}</strong><small>${escapeHtml(cameraItem.installation)}</small></td><td>${escapeHtml(cameraItem.schedule)}</td><td>${escapeHtml(cameraItem.pipeline)}</td><td><strong>${escapeHtml(cameraItem.capture)}</strong><small>${escapeHtml(cameraItem.ingest)}</small></td></tr>`).join("")}</tbody></table></div></section>`;
    }

    function observatoryOperationCards(item) {
        return [
            operationCard("users", "Members and access", `Manage protected access to ${item.name} without granting access to another observatory.`, "Owner", "Review members"),
            operationCard("camera", "Logical cameras", "Create stable camera identities, inspect installations, and preserve replacement history.", "Owner", "Manage cameras", routeHref({ view: "cameras", observatory: item.id })),
            operationCard("public", "Public profile", `${capitalize(item.visibility)} observatory profile with ${item.disclosure.toLowerCase()} location disclosure.`, "Owner", "Review publication"),
            operationCard("pipeline", "Central processing policy", "Apply central job policy overrides while preserving each camera's local capture pipeline authority.", "Owner", "Review policy"),
            operationCard("shield", "Device registrations", "Provision, assign, inspect, and centrally revoke CameraAgent device relationships.", "Owner", "Open registrations"),
            operationCard("image", "Protected archive", "Inspect observatory-scoped central images and their immutable source lineage.", "Viewer+", "Open archive")
        ];
    }

    function renderCameraOperations(item, selectedCamera) {
        document.title = `Operations - ${selectedCamera.name} | HVO SkyMonitor Prototype`;
        elements.page.innerHTML = `
            ${breadcrumbs([{ label: item.name, href: routeHref({ observatory: item.id }) }, { label: selectedCamera.name, href: routeHref({ observatory: item.id, camera: selectedCamera.id }) }, { label: "Operations" }])}
            ${pageHeader(`${item.code} / ${selectedCamera.code} / central camera scope`, "Camera operations", `Inspect central state and create central successor work for ${selectedCamera.name} without taking over local acquisition.`, "Signed-in Owner / camera")}
            ${cameraTabs(item, selectedCamera, "operations")}
            <div class="logic-boundary"><span class="logic-boundary-icon">${icon("camera")}</span><span><strong>Remote visibility does not move local authority.</strong><p>LogicHost owns registration, ingest, central jobs, archive, and release decisions. ${escapeHtml(selectedCamera.name)} continues to own schedule admission and capture-pipeline configuration.</p></span></div>
            <section class="logic-operation-grid">
                ${operationCard("pipeline", "Central processing", "Compare received edge execution evidence, inspect central jobs, and create successor central processing without taking over local acquisition.", "Manager / Owner", "Open processing", routeHref({ view: "processing", observatory: item.id, camera: selectedCamera.id }))}
                ${operationCard("shield", "Device relationship", `Inspect ${selectedCamera.agent}, installation history, heartbeat, and revocation state.`, "Owner", "View registration")}
                ${operationCard("public", "Image publication", selectedCamera.releaseArtifact ? `Released ${selectedCamera.releaseArtifact.artifactId} under ${selectedCamera.releaseArtifact.releaseId}.` : "No explicit released-artifact fixture is loaded for this camera.", "Owner", "Review releases")}
            </section>
            <div class="logic-grid two">
                <section class="logic-panel"><header class="logic-panel-heading"><div><h2>Central relationship</h2><p>Stable logical identity and current installation.</p></div><span class="state-chip ${statusClass(selectedCamera.status)}">${escapeHtml(statusLabel(selectedCamera.status))}</span></header><dl class="logic-facts"><div class="full"><dt>Logical camera</dt><dd>${escapeHtml(selectedCamera.name)}</dd></div><div class="full"><dt>Installation</dt><dd>${escapeHtml(selectedCamera.installation)}</dd></div><div><dt>Device</dt><dd>${escapeHtml(selectedCamera.code)}</dd></div><div><dt>Latest retained</dt><dd>${escapeHtml(selectedCamera.capture)} / ${escapeHtml(selectedCamera.ingest)}</dd></div><div class="full"><dt>Latest submission</dt><dd>${escapeHtml(selectedCamera.submission)}</dd></div><div class="full"><dt>Protected device identity</dt><dd>${escapeHtml(selectedCamera.agent)}</dd></div></dl></section>
                <section class="logic-panel"><header class="logic-panel-heading"><div><h2>Local evidence boundary</h2><p>Received revisions are read-only in LogicHost.</p></div><span class="logic-authority local"><i></i>CameraAgent local</span></header><dl class="logic-facts"><div class="full"><dt>Schedule evidence</dt><dd>${escapeHtml(selectedCamera.schedule)}</dd></div><div class="full"><dt>Pipeline evidence</dt><dd>${escapeHtml(selectedCamera.pipeline)}</dd></div><div class="full"><dt>Central behavior</dt><dd>Preserve exact capture-time revisions; do not edit or silently replace them.</dd></div></dl></section>
            </div>`;
    }

    function renderMissingRoute() {
        document.title = "Scope not loaded | HVO SkyMonitor Prototype";
        elements.page.innerHTML = `<section class="logic-placeholder"><div><span>${icon("warning")}</span><h1>Requested network scope is not loaded</h1><p>The observatory, camera, or page is outside this bounded prototype. No record from another observatory has been substituted.</p><a class="button secondary" href="${routeHref({ view: "observatories" })}">Choose an observatory</a></div></section>`;
    }

    function capitalize(value) {
        return value.charAt(0).toUpperCase() + value.slice(1);
    }

    function renderRoute() {
        renderScopeNavigation();
        setPrimaryNavigation();
        if (!routeIsValid) {
            renderMissingRoute();
            return;
        }
        if (camera && view === "operations") renderCameraOperations(observatory, camera);
        else if (camera && view === "processing") renderCameraProcessing(observatory, camera);
        else if (camera && view === "archive") renderCameraArchive(observatory, camera);
        else if (camera && view === "events") renderCameraEventsPlaceholder(observatory, camera);
        else if (camera) renderCameraCurrent(observatory, camera);
        else if (observatory && view === "operations") renderObservatoryOperations(observatory);
        else if (observatory && view === "cameras") renderObservatoryCameras(observatory);
        else if (observatory) renderObservatoryOverview(observatory);
        else if (view === "observatories") renderObservatorySelection();
        else if (view === "public") renderPublicSky();
        else if (view === "events") renderNetworkEvents();
        else if (view === "operations") renderLogicHostOperations();
        else renderWelcome();
        bindPrototypeActions();
    }

    function bindPrototypeActions() {
        document.querySelectorAll("[data-prototype-action]").forEach(control => control.addEventListener("click", event => {
            if (control.tagName === "A") event.preventDefault();
            showToast(control.dataset.prototypeAction);
        }));
    }

    function showToast(message) {
        clearTimeout(toastTimer);
        elements.toastText.textContent = message;
        elements.toast.hidden = false;
        toastTimer = setTimeout(() => { elements.toast.hidden = true; }, 4200);
    }

    function setSidebarOpen(open, restoreFocus = true) {
        document.body.classList.toggle("logic-sidebar-open", open);
        elements.sidebarToggle.setAttribute("aria-expanded", String(open));
        elements.sidebarBackdrop.hidden = !open;
        elements.header.inert = open;
        elements.main.inert = open;
        if (open) elements.sidebarClose.focus();
        else if (restoreFocus && window.innerWidth <= 940) elements.sidebarToggle.focus();
    }

    function trapSidebarFocus(event) {
        if (event.key !== "Tab" || !document.body.classList.contains("logic-sidebar-open")) return;
        const focusable = [...elements.sidebar.querySelectorAll('a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])')];
        if (!focusable.length) return;
        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
        }
    }

    elements.headerToggle.addEventListener("click", () => {
        if (window.innerWidth > 780) {
            showToast("Signed in with illustrative Observatory Owner access. Central services are healthy.");
            return;
        }
        const open = !elements.header.classList.contains("menu-open");
        elements.header.classList.toggle("menu-open", open);
        elements.headerToggle.setAttribute("aria-expanded", String(open));
        if (open) elements.primaryNavigation.querySelector("a")?.focus();
        else elements.headerToggle.focus();
    });
    elements.sidebarToggle.addEventListener("click", () => setSidebarOpen(true));
    elements.sidebarClose.addEventListener("click", () => setSidebarOpen(false));
    elements.sidebarBackdrop.addEventListener("click", () => setSidebarOpen(false));
    document.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            if (document.body.classList.contains("logic-sidebar-open")) setSidebarOpen(false);
            if (elements.header.classList.contains("menu-open")) {
                elements.header.classList.remove("menu-open");
                elements.headerToggle.setAttribute("aria-expanded", "false");
                elements.headerToggle.focus();
            }
        }
        trapSidebarFocus(event);
    });
    window.addEventListener("resize", () => {
        if (window.innerWidth > 940 && document.body.classList.contains("logic-sidebar-open")) {
            setSidebarOpen(false, false);
            (elements.sidebar.querySelector(".logic-scope-link.active") ?? elements.sidebar.querySelector(".logic-scope-link"))?.focus();
        }
        if (window.innerWidth > 780 && elements.header.classList.contains("menu-open")) {
            elements.header.classList.remove("menu-open");
            elements.headerToggle.setAttribute("aria-expanded", "false");
        }
    });

    renderRoute();
})();
