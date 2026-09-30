const graphWidth = 2670;
const graphHeight = 1180;

const nodes = [
    {
        id: "acquire",
        label: "Acquire image",
        type: "Camera exposure",
        group: "Acquisition",
        x: 48,
        y: 350,
        required: true,
        dependencies: [],
        outputs: ["raw-frame", "capture-manifest"],
        artifactCount: 2,
        description: "Admits the scheduled exposure and commits immutable raw evidence before processing begins.",
        runner: "CameraAgent / camera module"
    },
    {
        id: "calibration",
        label: "Calibration",
        type: "Dark and flat correction",
        group: "Acquisition",
        x: 272,
        y: 350,
        required: true,
        dependencies: ["acquire"],
        outputs: ["calibrated-frame"],
        artifactCount: 1,
        description: "Applies the selected dark and flat reference set to immutable raw evidence.",
        runner: "CameraAgent / in-process"
    },
    {
        id: "enhance",
        label: "Image enhancement",
        type: "Rolling stack and tone map",
        group: "Edge processing",
        x: 510,
        y: 80,
        required: true,
        dependencies: ["calibration"],
        outputs: ["rolling-mean", "display-preview"],
        artifactCount: 2,
        description: "Combines the calibrated frame with the bounded rolling window and produces a display-ready preview.",
        runner: "CameraAgent / in-process"
    },
    {
        id: "cloud",
        label: "Cloud assessment",
        type: "Quality filter",
        group: "Edge processing",
        x: 510,
        y: 350,
        required: false,
        dependencies: ["calibration"],
        outputs: ["cloud-assessment", "cloud-mask"],
        artifactCount: 2,
        description: "Compares the calibrated frame with the clear-sky reference and emits cloud coverage evidence.",
        runner: "CameraAgent / in-process"
    },
    {
        id: "scene",
        label: "Scene projection",
        type: "Overlay filter",
        group: "Edge processing",
        x: 510,
        y: 620,
        required: false,
        dependencies: ["calibration"],
        outputs: ["projected-scene"],
        artifactCount: 1,
        description: "Projects catalog objects and sky geometry into image coordinates for independent overlay producers.",
        runner: "CameraAgent / in-process"
    },
    {
        id: "stars",
        label: "Stars and labels",
        type: "Annotation overlay",
        group: "Edge processing",
        x: 750,
        y: 80,
        required: false,
        dependencies: ["scene"],
        outputs: ["star-label-layer"],
        artifactCount: 1,
        description: "Renders catalog star markers, named-object labels, and magnitude-bounded label placement as a transparent layer.",
        runner: "CameraAgent / in-process"
    },
    {
        id: "constellations",
        label: "Constellation lines",
        type: "Annotation overlay",
        group: "Edge processing",
        x: 750,
        y: 195,
        required: false,
        dependencies: ["scene"],
        outputs: ["constellation-layer"],
        artifactCount: 1,
        description: "Draws configured constellation segments from the projected scene while preserving catalog provenance.",
        runner: "CameraAgent / in-process"
    },
    {
        id: "cardinals",
        label: "Cardinal corners",
        type: "Orientation overlay",
        group: "Edge processing",
        x: 750,
        y: 310,
        required: false,
        dependencies: ["scene"],
        outputs: ["cardinal-corner-layer"],
        artifactCount: 1,
        description: "Places north, east, south, and west orientation marks at the calibrated image boundary.",
        runner: "CameraAgent / in-process"
    },
    {
        id: "imageCircle",
        label: "Image boundary",
        type: "Geometry overlay",
        group: "Edge processing",
        x: 750,
        y: 425,
        required: false,
        dependencies: ["scene"],
        outputs: ["image-boundary-layer"],
        artifactCount: 1,
        description: "Renders the valid calibrated image circle and masks pixels outside the presentation boundary.",
        runner: "CameraAgent / in-process"
    },
    {
        id: "cloudOverlay",
        label: "Cloud mask overlay",
        type: "Assessment overlay",
        group: "Edge processing",
        x: 750,
        y: 540,
        required: false,
        dependencies: ["cloud"],
        outputs: ["cloud-mask-layer"],
        artifactCount: 1,
        description: "Transforms cloud assessment tiles into a transparent mask and coverage label layer.",
        runner: "CameraAgent / in-process"
    },
    {
        id: "environment",
        label: "Environment facts",
        type: "Metadata overlay",
        group: "Edge processing",
        x: 750,
        y: 655,
        required: false,
        dependencies: ["enhance"],
        outputs: ["environment-facts-layer"],
        artifactCount: 1,
        description: "Formats capture time, exposure, temperature, and associated environmental facts as a presentation layer.",
        runner: "CameraAgent / in-process"
    },
    {
        id: "overlayManifest",
        label: "Overlay manifest",
        type: "Layer lineage",
        group: "Edge processing",
        x: 990,
        y: 350,
        required: true,
        dependencies: ["enhance", "stars", "constellations", "cardinals", "imageCircle", "cloudOverlay", "environment"],
        optionalDependencies: ["stars", "constellations", "cardinals", "imageCircle", "cloudOverlay", "environment"],
        outputs: ["overlay-manifest"],
        artifactCount: 1,
        description: "Records the ordered layer set, visibility defaults, checksums, and source lineage used for materialization.",
        runner: "CameraAgent / in-process"
    },
    {
        id: "materialize",
        label: "Materialize image",
        type: "Layer compositor",
        group: "Edge processing",
        x: 1230,
        y: 350,
        required: true,
        dependencies: ["enhance", "overlayManifest"],
        outputs: ["annotated-preview"],
        artifactCount: 1,
        description: "Composes the selected presentation layers while preserving each independent source artifact and its lineage.",
        runner: "CameraAgent / in-process"
    },
    {
        id: "save",
        label: "Durable local save",
        type: "Artifact publication",
        group: "Durable publication",
        x: 1470,
        y: 350,
        required: true,
        dependencies: ["materialize"],
        outputs: ["local-artifact-set"],
        artifactCount: 16,
        description: "Publishes the complete artifact set and checksums atomically under the CameraAgent retention policy.",
        runner: "CameraAgent / local storage"
    },
    {
        id: "relay",
        label: "Relay to LogicHost",
        type: "Durable outbox",
        group: "Durable publication",
        x: 1710,
        y: 350,
        required: false,
        dependencies: ["save"],
        outputs: ["upload-receipt"],
        artifactCount: 1,
        description: "Leases the retained manifest and artifacts, verifies central status, and performs idempotent delivery.",
        runner: "CameraAgent / outbox worker"
    },
    {
        id: "ingest",
        label: "Verify and ingest",
        type: "Central acceptance",
        group: "LogicHost",
        x: 1950,
        y: 350,
        required: false,
        dependencies: ["relay"],
        outputs: ["central-capture", "ingest-ack"],
        artifactCount: 2,
        description: "Authenticates the device, verifies provenance and checksums, and commits the central capture record.",
        runner: "LogicHost / ingest API"
    },
    {
        id: "notify",
        label: "Notify operator",
        type: "Optional notification",
        group: "LogicHost",
        x: 2190,
        y: 350,
        required: false,
        dependencies: ["ingest"],
        outputs: ["notification-receipt"],
        artifactCount: 0,
        description: "Publishes the configured capture notification without affecting acquisition or durable evidence.",
        runner: "LogicHost / notification worker"
    },
    {
        id: "transientInput",
        label: "Linear detector input",
        type: "Calibration and masks",
        group: "Transient lane",
        x: 272,
        y: 900,
        required: false,
        dependencies: ["acquire"],
        outputs: ["linear-detector-input"],
        artifactCount: 1,
        description: "Builds the transient lane's compatible linear luminance input with exact calibration, image-circle, horizon, obstruction, bad-pixel, and saturation masks.",
        runner: "CameraAgent / transient worker"
    },
    {
        id: "frameHistory",
        label: "Durable frame window",
        type: "N-2, N-1, N",
        group: "Transient lane",
        x: 272,
        y: 1040,
        required: false,
        dependencies: ["acquire"],
        outputs: ["ordered-source-references"],
        artifactCount: 3,
        description: "Resolves the compatibility-aware causal source history N-2, N-1, N by capture sequence. Future context remains a LogicHost responsibility.",
        runner: "CameraAgent / transient runtime store"
    },
    {
        id: "causal",
        label: "Causal candidate scan",
        type: "N-2, N-1 -> target N",
        group: "Transient lane",
        x: 510,
        y: 900,
        required: false,
        dependencies: ["transientInput", "frameHistory"],
        outputs: ["causal-extraction-receipt"],
        artifactCount: 1,
        description: "Creates a provisional background from N-2 and N-1, extracts bounded residual candidates from N, and completes immediately when no candidate is present.",
        runner: "CameraAgent / transient worker"
    },
    {
        id: "candidate",
        label: "Persist candidate",
        type: "Restart-safe journal",
        group: "Transient lane",
        x: 750,
        y: 900,
        required: false,
        dependencies: ["causal"],
        outputs: ["provisional-candidate"],
        artifactCount: 1,
        description: "Allocates a stable candidate identity, then persists geometry, measured features, lineage, and the canonical causal receipt. LogicHost allocates authoritative event identity.",
        runner: "CameraAgent / transient journal"
    },
    {
        id: "retainEvent",
        label: "Retain event evidence",
        type: "Source hold and lineage",
        group: "Transient lane",
        x: 990,
        y: 900,
        required: false,
        dependencies: ["candidate"],
        outputs: ["candidate-evidence-hold"],
        artifactCount: 3,
        description: "Pins the causal source artifacts and candidate evidence until the Hybrid handoff is durably acknowledged or explicitly quarantined.",
        runner: "CameraAgent / retention fence"
    },
    {
        id: "transientRelay",
        label: "Relay candidate",
        type: "Independent Hybrid outbox",
        group: "Transient lane",
        x: 1470,
        y: 900,
        required: false,
        dependencies: ["retainEvent"],
        outputs: ["candidate-acceptance-ack"],
        artifactCount: 1,
        description: "Submits the causal candidate and N-2, N-1, N source references after their artifacts are centrally acknowledged. Acceptance freezes central validation work; it does not mean validation has completed.",
        runner: "CameraAgent / transient delivery"
    },
    {
        id: "centeredWindow",
        label: "Resolve centered window",
        type: "Exact N-2 through N+2",
        group: "LogicHost transient",
        x: 1950,
        y: 900,
        required: false,
        dependencies: ["transientRelay"],
        outputs: ["frozen-five-frame-window"],
        artifactCount: 5,
        description: "Resolves N+1 and N+2 by capture sequence, verifies one exact compatible five-frame window, and freezes it for authoritative processing.",
        runner: "LogicHost / transient scheduler"
    },
    {
        id: "centralValidation",
        label: "Validate fireball event",
        type: "Centered extraction and assessment",
        group: "LogicHost transient",
        x: 2190,
        y: 900,
        required: false,
        dependencies: ["centeredWindow"],
        outputs: ["event-version", "crop", "preview", "mask", "overlay", "reconstruction"],
        artifactCount: 6,
        description: "Reconstructs detector inputs, performs centered extraction, uniquely matches the causal candidate, assesses Meteor/Fireball severity, and appends immutable event and derivative evidence.",
        runner: "LogicHost / transient validator"
    },
    {
        id: "eventNotification",
        label: "Fireball notification",
        type: "Review-gated owner email",
        group: "LogicHost transient",
        x: 2430,
        y: 900,
        required: false,
        dependencies: ["centralValidation"],
        outputs: ["event-notification-receipt"],
        artifactCount: 0,
        description: "After an authorized review confirms or overrides the event to Meteor/Fireball, records a durable owner notification and fenced delivery result.",
        runner: "LogicHost / notification worker"
    }
];

const runDefinitions = [
    {
        id: "84220",
        title: "Capture #84220",
        subtitle: "Succeeded / related 31 August Fireball",
        time: "03:13",
        timestamp: "2026-09-01T03:13:55Z",
        trigger: "Hualapai Night Schedule at 03:13:55",
        outcome: "success",
        status: "Succeeded",
        duration: "8.03s",
        artifactCount: 22,
        delivery: "Acknowledged",
        noticeTone: "warning",
        noticeTitle: "31 August Fireball confirmed from a five-frame window",
        noticeText: "The image pipeline completed independently. LogicHost validated the causal candidate with N-2 through N+2 and retained reviewable event evidence.",
        noticeNode: "centralValidation",
        states: {
            ...successStates(),
            ...positiveTransientStates()
        }
    },
    {
        id: "84219",
        title: "Capture #84219",
        subtitle: "Night survey / VirtualSky ASI676MC",
        time: "03:13",
        timestamp: "2026-09-01T03:13:45Z",
        trigger: "Hualapai Night Schedule at 03:13:45",
        outcome: "warning",
        status: "Delivered with warning",
        duration: "9.60s",
        artifactCount: 16,
        delivery: "Acknowledged",
        noticeTitle: "Capture delivered; notification needs attention",
        noticeText: "All required image evidence is durable locally and acknowledged by LogicHost. The optional notification timed out.",
        noticeNode: "notify",
        states: {
            acquire: state("success", ".84s", "03:13:45.00", 1),
            calibration: state("success", "1.26s", "03:13:45.84", 1),
            enhance: state("success", "2.14s", "03:13:47.10", 1),
            cloud: state("success", ".71s", "03:13:47.10", 1),
            scene: state("success", ".19s", "03:13:47.10", 1),
            stars: state("success", ".18s", "03:13:47.29", 1),
            constellations: state("success", ".09s", "03:13:47.29", 1),
            cardinals: state("success", ".04s", "03:13:47.29", 1),
            imageCircle: state("success", ".03s", "03:13:47.29", 1),
            cloudOverlay: state("success", ".11s", "03:13:47.81", 1),
            environment: state("success", ".06s", "03:13:49.24", 1),
            overlayManifest: state("success", ".05s", "03:13:49.30", 1),
            materialize: state("success", ".48s", "03:13:49.35", 1),
            save: state("success", ".12s", "03:13:49.83", 1),
            relay: state("success", "3.21s", "03:13:49.95", 2, "Recovered after one endpoint timeout."),
            ingest: state("success", ".21s", "03:13:53.16", 1),
            notify: state("failure", ".45s", "03:13:53.37", 3, "notification.provider-timeout"),
            ...noCandidateTransientStates("03:13:45.84")
        }
    },
    {
        id: "84218",
        title: "Capture #84218",
        subtitle: "Night survey / VirtualSky ASI676MC",
        time: "03:13",
        timestamp: "2026-09-01T03:13:35Z",
        trigger: "Hualapai Night Schedule at 03:13:35",
        outcome: "success",
        status: "Succeeded",
        duration: "8.03s",
        artifactCount: 16,
        delivery: "Acknowledged",
        noticeTitle: "Capture and delivery completed",
        noticeText: "All required stages succeeded. LogicHost verified the complete artifact set and notification was accepted.",
        noticeNode: "ingest",
        states: successStates(-20)
    },
    {
        id: "84217",
        title: "Capture #84217",
        subtitle: "Night survey / VirtualSky ASI676MC",
        time: "03:13",
        timestamp: "2026-09-01T03:13:25Z",
        trigger: "Hualapai Night Schedule at 03:13:25",
        outcome: "failure",
        status: "Processing failed",
        duration: "2.18s",
        artifactCount: 2,
        delivery: "Not queued",
        noticeTitle: "Calibration failed; raw evidence retained",
        noticeText: "The raw frame and capture manifest are durable. Dependent processing was skipped because the calibration reference was unavailable.",
        noticeNode: "calibration",
        states: {
            acquire: state("success", ".81s", "03:13:25.00", 1),
            calibration: state("failure", "1.37s", "03:13:25.81", 3, "calibration.reference-unavailable"),
            enhance: state("skipped", "-", "-", 0, "Required dependency failed."),
            cloud: state("skipped", "-", "-", 0, "Required dependency failed."),
            scene: state("skipped", "-", "-", 0, "Required dependency failed."),
            stars: state("skipped", "-", "-", 0, "Scene projection was unavailable."),
            constellations: state("skipped", "-", "-", 0, "Scene projection was unavailable."),
            cardinals: state("skipped", "-", "-", 0, "Scene projection was unavailable."),
            imageCircle: state("skipped", "-", "-", 0, "Scene projection was unavailable."),
            cloudOverlay: state("skipped", "-", "-", 0, "Cloud assessment was unavailable."),
            environment: state("skipped", "-", "-", 0, "Image enhancement was unavailable."),
            overlayManifest: state("skipped", "-", "-", 0, "Required dependency failed."),
            materialize: state("skipped", "-", "-", 0, "Required dependency failed."),
            save: state("skipped", "-", "-", 0, "Required dependency failed."),
            relay: state("skipped", "-", "-", 0, "No published artifact set."),
            ingest: state("skipped", "-", "-", 0, "No upload receipt."),
            notify: state("skipped", "-", "-", 0, "No central capture."),
            ...noCandidateTransientStates("03:13:25.81")
        }
    },
    {
        id: "84216",
        title: "Capture #84216",
        subtitle: "Night survey / VirtualSky ASI676MC",
        time: "03:13",
        timestamp: "2026-09-01T03:13:15Z",
        trigger: "Hualapai Night Schedule at 03:13:15",
        outcome: "running",
        status: "Waiting for LogicHost",
        duration: "4m 31s",
        artifactCount: 16,
        delivery: "Retrying",
        noticeTitle: "Local capture complete; central delivery is retrying",
        noticeText: "All image evidence is durable on HVO Main Fisheye. The outbox is retaining work while LogicHost connectivity is unavailable.",
        noticeNode: "relay",
        states: {
            ...successStates(-40),
            relay: state("retrying", "4m 26s", "03:13:19.84", 4, "transport.connection-refused"),
            ingest: state("pending", "-", "-", 0, "Waiting for upload receipt."),
            notify: state("pending", "-", "-", 0, "Waiting for central ingest.")
        }
    },
    {
        id: "84215",
        title: "Capture #84215",
        subtitle: "Night survey / VirtualSky ASI676MC",
        time: "03:13",
        timestamp: "2026-09-01T03:13:05Z",
        trigger: "Hualapai Night Schedule at 03:13:05",
        outcome: "success",
        status: "Succeeded",
        duration: "8.26s",
        artifactCount: 16,
        delivery: "Acknowledged",
        noticeTitle: "Capture and delivery completed",
        noticeText: "All required stages succeeded and LogicHost acknowledged the immutable artifact set.",
        noticeNode: "ingest",
        states: successStates(-50)
    }
];

const artifactDefinitions = [
    ["Raw frame", "raw / source", "Acquire image", "24.1 MB", "7 days", "Acknowledged"],
    ["Capture manifest", "manifest / capture-v2", "Acquire image", "18 KB", "30 days", "Acknowledged"],
    ["Calibrated frame", "calibrated / pseudo-calibrated", "Calibration", "24.1 MB", "7 days", "Acknowledged"],
    ["Rolling stack", "combined / rolling-mean", "Image enhancement", "24.1 MB", "7 days", "Acknowledged"],
    ["Display preview", "preview / rolling-mean", "Image enhancement", "3.8 MB", "30 days", "Acknowledged"],
    ["Cloud assessment", "analysis / cloud-assessment-v1", "Cloud assessment", "42 KB", "30 days", "Acknowledged"],
    ["Cloud tile mask", "mask / cloud-assessment-v1", "Cloud assessment", "256 KB", "30 days", "Acknowledged"],
    ["Projected scene", "scene / projected-scene-v1", "Scene projection", "84 KB", "30 days", "Acknowledged"],
    ["Star label layer", "overlay / star-labels", "Stars and labels", "620 KB", "30 days", "Acknowledged"],
    ["Constellation layer", "overlay / constellation-lines", "Constellation lines", "410 KB", "30 days", "Acknowledged"],
    ["Cardinal corner layer", "overlay / cardinal-corners", "Cardinal corners", "96 KB", "30 days", "Acknowledged"],
    ["Image boundary layer", "overlay / image-boundary", "Image boundary", "112 KB", "30 days", "Acknowledged"],
    ["Cloud mask layer", "overlay / cloud-mask", "Cloud mask overlay", "390 KB", "30 days", "Acknowledged"],
    ["Environment facts layer", "overlay / environment-facts", "Environment facts", "128 KB", "30 days", "Acknowledged"],
    ["Overlay manifest", "manifest / presentation-layers-v1", "Overlay manifest", "12 KB", "30 days", "Acknowledged"],
    ["Annotated preview", "preview / layered-presentation", "Materialize image", "4.6 MB", "30 days", "Acknowledged"],
    ["Causal extraction receipt", "transient / causal-v1", "Causal candidate scan", "86 KB", "Event hold", "Acknowledged"],
    ["Centered extraction receipt", "transient / centered-v1", "Validate fireball event", "132 KB", "Event hold", "Acknowledged"],
    ["Event crop", "transient / crop", "Validate fireball event", "740 KB", "Event hold", "Acknowledged"],
    ["Event preview", "transient / preview", "Validate fireball event", "1.1 MB", "Event hold", "Acknowledged"],
    ["Event mask and overlay", "transient / mask-overlay", "Validate fireball event", "510 KB", "Event hold", "Acknowledged"],
    ["Event reconstruction", "transient / reconstruction", "Validate fireball event", "2.8 MB", "Event hold", "Acknowledged"]
];

const requestedRunId = new URLSearchParams(window.location.search).get("run");
const requestedRun = requestedRunId ? runDefinitions.find(run => run.id === requestedRunId) : runDefinitions[0];
const unknownRunId = requestedRunId && !requestedRun ? requestedRunId : null;
let selectedRun = requestedRun ?? runDefinitions[0];
let selectedNodeId = defaultNodeForRun(selectedRun);
let requiredOnly = false;
let zoom = 1;
let toastTimer;

const elements = Object.fromEntries([
    "runList", "noRuns", "runSearch", "outcomeFilter", "stageList", "requiredToggle", "runTitle", "runSubtitle",
    "titleStatus", "summaryTrigger", "summaryStatus", "summaryDuration", "summaryArtifacts", "summaryDelivery",
    "runNotice", "noticeTitle", "noticeText", "noticeAction", "graphViewport", "graphScale", "graphCanvas",
    "graphEdges", "graphNodes", "zoomValue", "fitGraph", "zoomOut", "zoomIn", "graphHint", "detailEyebrow",
    "detailStatus", "detailDescription", "detailStarted", "detailDuration", "detailAttempt", "detailRunner",
    "detailDependencies", "detailOutputs", "eventList", "artifactBadge", "artifactRows", "attemptsHeading",
    "attemptList", "reprocessButton", "reprocessDialog", "dialogRunId", "reprocessOptions", "actionExplanation",
    "confirmAction", "toast", "summaryPanel", "artifactsPanel", "attemptsPanel"
].map(id => [id, document.getElementById(id)]));

function state(status, duration, started, attempt, reason = null) {
    return { status, duration, started, attempt, reason };
}

function successStates(offsetSeconds = 0) {
    const at = value => shiftPrototypeTime(value, offsetSeconds);
    return {
        acquire: state("success", ".82s", at("03:13:55.00"), 1),
        calibration: state("success", "1.19s", at("03:13:55.82"), 1),
        enhance: state("success", "2.06s", at("03:13:57.01"), 1),
        cloud: state("success", ".68s", at("03:13:57.01"), 1),
        scene: state("success", ".20s", at("03:13:57.01"), 1),
        stars: state("success", ".17s", at("03:13:57.21"), 1),
        constellations: state("success", ".08s", at("03:13:57.21"), 1),
        cardinals: state("success", ".04s", at("03:13:57.21"), 1),
        imageCircle: state("success", ".03s", at("03:13:57.21"), 1),
        cloudOverlay: state("success", ".10s", at("03:13:57.69"), 1),
        environment: state("success", ".06s", at("03:13:59.07"), 1),
        overlayManifest: state("success", ".05s", at("03:13:59.13"), 1),
        materialize: state("success", ".43s", at("03:13:59.18"), 1),
        save: state("success", ".11s", at("03:13:59.61"), 1),
        relay: state("success", "2.73s", at("03:13:59.72"), 1),
        ingest: state("success", ".19s", at("03:14:02.45"), 1),
        notify: state("success", ".29s", at("03:14:02.64"), 1),
        ...noCandidateTransientStates(at("03:13:55.82"))
    };
}

function shiftPrototypeTime(value, offsetSeconds) {
    const [hours, minutes, seconds] = value.split(":");
    const decimals = seconds.includes(".") ? seconds.length - seconds.indexOf(".") - 1 : 0;
    const total = Number(hours) * 3600 + Number(minutes) * 60 + Number(seconds) + offsetSeconds;
    const shiftedHours = Math.floor(total / 3600) % 24;
    const shiftedMinutes = Math.floor(total % 3600 / 60);
    const shiftedSeconds = (total % 60).toFixed(decimals).padStart(decimals ? decimals + 3 : 2, "0");
    return `${String(shiftedHours).padStart(2, "0")}:${String(shiftedMinutes).padStart(2, "0")}:${shiftedSeconds}`;
}

function defaultNodeForRun(run) {
    return run.id === "84220" ? "causal" : run.noticeNode;
}

function noCandidateTransientStates(started = "03:13:55.82") {
    return {
        transientInput: state("success", ".21s", started, 1),
        frameHistory: state("success", ".05s", started, 1),
        causal: state("success", ".43s", started, 1, "transient.extraction.no-candidate"),
        candidate: state("skipped", "-", "-", 0, "No causal candidate was produced."),
        retainEvent: state("skipped", "-", "-", 0, "No candidate evidence requires a hold."),
        transientRelay: state("skipped", "-", "-", 0, "No candidate requires Hybrid delivery."),
        centeredWindow: state("skipped", "-", "-", 0, "No accepted candidate requires centered context."),
        centralValidation: state("skipped", "-", "-", 0, "No central validation job was created."),
        eventNotification: state("skipped", "-", "-", 0, "No reviewed meteor event is eligible.")
    };
}

function positiveTransientStates() {
    return {
        transientInput: state("success", ".24s", "03:13:55.82", 1),
        frameHistory: state("success", ".06s", "03:13:55.82", 1),
        causal: state("success", ".87s", "03:13:56.06", 1, "One bounded elongated candidate produced."),
        candidate: state("success", ".04s", "03:13:56.93", 1),
        retainEvent: state("success", ".03s", "03:13:56.97", 1),
        transientRelay: state("success", ".92s", "03:13:57.00", 1, "Central acceptance froze validation work; authoritative validation remained pending."),
        centeredWindow: state("success", "50.4s", "03:13:57.92", 1, "Resolved one exact compatible N-2 through N+2 window."),
        centralValidation: state("success", "2.36s", "03:14:48.32", 1, "Meteor / Fireball assessment appended with five derivative products."),
        eventNotification: state("success", ".44s", "03:14:50.68", 1, "Confirmed owner notification accepted.")
    };
}

function statusLabel(status) {
    return ({ success: "Succeeded", failure: "Failed", warning: "Attention", running: "Running", retrying: "Retrying", pending: "Pending", skipped: "Skipped" })[status] ?? status;
}

function iconStatus(status) {
    return status === "retrying" ? "warning" : status;
}

function renderRuns() {
    const query = elements.runSearch.value.trim().toLowerCase();
    const outcome = elements.outcomeFilter.value;
    const filtered = runDefinitions.filter(run =>
        (outcome === "all" || run.outcome === outcome) &&
        (!query || `${run.id} ${run.status} ${run.subtitle}`.toLowerCase().includes(query)));

    elements.runList.replaceChildren(...filtered.map(run => {
        const button = document.createElement("button");
        button.type = "button";
        button.className = `run-item${run.id === selectedRun.id ? " selected" : ""}`;
        button.dataset.runId = run.id;
        button.setAttribute("aria-pressed", String(run.id === selectedRun.id));
        button.innerHTML = `
            <span class="status-icon ${iconStatus(run.outcome)}" aria-hidden="true"></span>
            <span class="run-copy"><strong>Capture #${run.id}</strong><small>${escapeHtml(run.status)}</small></span>
            <time datetime="${run.timestamp}">${run.time}</time>`;
        button.addEventListener("click", () => selectRun(run.id));
        return button;
    }));
    elements.noRuns.hidden = filtered.length > 0;
}

function renderStages() {
    const visibleNodes = requiredOnly ? nodes.filter(node => node.required) : nodes;
    elements.stageList.replaceChildren(...visibleNodes.map(node => {
        const nodeState = selectedRun.states[node.id];
        const button = document.createElement("button");
        button.type = "button";
        button.className = `stage-item${node.id === selectedNodeId ? " selected" : ""}`;
        button.dataset.nodeId = node.id;
        button.setAttribute("aria-pressed", String(node.id === selectedNodeId));
        button.innerHTML = `<span class="status-icon ${iconStatus(nodeState.status)}" aria-hidden="true"></span><span>${escapeHtml(node.label)}</span><small>${escapeHtml(nodeState.duration)}</small>`;
        button.addEventListener("click", () => selectNode(node.id, true));
        return button;
    }));
}

function renderGraph() {
    elements.graphNodes.replaceChildren(...nodes.map(node => {
        const nodeState = selectedRun.states[node.id];
        const artifactCount = nodeState.status === "success" ? node.artifactCount : 0;
        const button = document.createElement("button");
        button.type = "button";
        const transientNode = node.group.toLowerCase().includes("transient");
        button.className = `graph-node${node.id === selectedNodeId ? " selected" : ""}${node.group.includes("LogicHost") ? " central" : ""}${transientNode ? " transient" : ""}`;
        button.style.left = `${node.x}px`;
        button.style.top = `${node.y}px`;
        button.dataset.nodeId = node.id;
        button.dataset.status = nodeState.status;
        button.setAttribute("aria-label", `${node.label}: ${statusLabel(nodeState.status)}, ${nodeState.duration}`);
        button.innerHTML = `
            <span class="node-heading">
                <span class="status-icon ${iconStatus(nodeState.status)}" aria-hidden="true"></span>
                <span class="node-title"><strong>${escapeHtml(node.label)}</strong><small>${escapeHtml(node.type)}</small></span>
                <span class="node-duration">${escapeHtml(nodeState.duration)}</span>
            </span>
            <span class="node-footer"><span>${artifactCount} ${artifactCount === 1 ? "artifact" : "artifacts"}</span><span class="requirement${node.required ? "" : " optional"}">${node.required ? "Required" : "Optional"}</span></span>`;
        button.addEventListener("click", () => selectNode(node.id));
        return button;
    }));

    const edgeFragment = document.createDocumentFragment();
    nodes.forEach(target => {
        target.dependencies.forEach(sourceId => {
            const source = nodes.find(node => node.id === sourceId);
            const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
            const startX = source.x + 204;
            const startY = source.y + 50;
            const endX = target.x;
            const endY = target.y + 50;
            const bend = Math.max(40, (endX - startX) * .46);
            path.setAttribute("d", `M ${startX} ${startY} C ${startX + bend} ${startY}, ${endX - bend} ${endY}, ${endX - 8} ${endY}`);
            const optional = target.optionalDependencies?.includes(sourceId) || !target.required;
            const connected = selectedNodeId === sourceId || selectedNodeId === target.id;
            const transientEdge = source.group.toLowerCase().includes("transient") || target.group.toLowerCase().includes("transient");
            path.setAttribute("class", `edge${optional ? " optional" : ""}${transientEdge ? " transient" : ""}${connected ? " highlight" : " muted"}`);
            edgeFragment.append(path);
        });
    });
    elements.graphEdges.querySelectorAll("path.edge").forEach(path => path.remove());
    elements.graphEdges.append(edgeFragment);
}

function renderRunSummary() {
    const artifactCount = artifactsForRun(selectedRun).length;
    elements.runTitle.textContent = selectedRun.title;
    elements.runSubtitle.textContent = selectedRun.subtitle;
    elements.titleStatus.className = `large-status ${iconStatus(selectedRun.outcome)}`;
    elements.summaryTrigger.textContent = selectedRun.trigger;
    elements.summaryStatus.textContent = selectedRun.status;
    elements.summaryDuration.textContent = selectedRun.duration;
    elements.summaryArtifacts.textContent = artifactCount;
    elements.summaryDelivery.textContent = selectedRun.delivery;
    elements.noticeTitle.textContent = selectedRun.noticeTitle;
    elements.noticeText.textContent = selectedRun.noticeText;
    const noticeTone = selectedRun.noticeTone ?? selectedRun.outcome;
    elements.runNotice.className = `run-notice ${noticeTone}`;
    elements.runNotice.querySelector(".notice-icon").className = `notice-icon ${iconStatus(noticeTone)}`;
    elements.artifactBadge.textContent = artifactCount;
    elements.dialogRunId.textContent = `capture #${selectedRun.id}`;
}

function renderDetail() {
    const node = nodes.find(item => item.id === selectedNodeId);
    const nodeState = selectedRun.states[selectedNodeId];
    const heading = document.getElementById("detail-heading");
    heading.textContent = node.label;
    elements.detailEyebrow.textContent = `${node.group} / ${node.required ? "Required" : "Optional"}`;
    elements.detailDescription.textContent = node.description;
    elements.detailStatus.className = `state-chip ${nodeState.status}`;
    elements.detailStatus.textContent = statusLabel(nodeState.status);
    elements.detailStarted.textContent = nodeState.started === "-" ? "Not started" : `${nodeState.started} UTC`;
    elements.detailDuration.textContent = nodeState.duration;
    elements.detailAttempt.textContent = nodeState.attempt ? `${nodeState.attempt} of 3` : "None";
    elements.detailRunner.textContent = node.runner;
    elements.detailDependencies.replaceChildren(...tokenElements(node.dependencies.length ? node.dependencies.map(dependency => nodes.find(item => item.id === dependency).label) : ["Scheduled admission"]));
    elements.detailOutputs.replaceChildren(...tokenElements(node.outputs));
    elements.eventList.replaceChildren(...eventElements(node, nodeState));
    elements.attemptsHeading.textContent = `${node.label} attempts`;
    elements.attemptList.replaceChildren(...attemptElements(node, nodeState));
    elements.graphHint.textContent = `${node.label}: ${statusLabel(nodeState.status)} in ${nodeState.duration}.`;
}

function renderArtifacts() {
    const rows = artifactsForRun(selectedRun);
    elements.artifactRows.replaceChildren(...rows.map((artifact, index) => {
        const row = document.createElement("tr");
        const delivery = selectedRun.outcome === "failure" || selectedRun.outcome === "running" || artifact[5] === "Local only"
            ? "Local only"
            : artifact[5];
        row.innerHTML = `
            <td><strong>${artifact[0]}</strong><small>sha256:${sampleHash(index)}</small></td>
            <td>${artifact[2]}<small>${artifact[1]}</small></td>
            <td>${artifact[3]}</td>
            <td>${artifact[4]}</td>
            <td><span class="table-state${delivery === "Local only" ? " local" : ""}">${delivery}</span></td>`;
        return row;
    }));
}

function artifactsForRun(run) {
    return artifactDefinitions.filter(artifact => {
        const producer = nodes.find(node => node.label === artifact[2]);
        return producer && run.states[producer.id]?.status === "success";
    });
}

function tokenElements(values) {
    return values.map(value => {
        const token = document.createElement("span");
        token.textContent = value;
        return token;
    });
}

function eventElements(node, nodeState) {
    const start = nodeState.started === "-" ? "--:--:--.--" : nodeState.started;
    const events = [];
    if (["pending", "skipped"].includes(nodeState.status)) {
        events.push([start, nodeState.status, nodeState.reason ?? "Stage has not started."]);
    } else {
        events.push([start, "running", `${node.label} attempt ${nodeState.attempt} started.`]);
        events.push([start, nodeState.status, nodeState.reason ?? `${node.outputs.join(", ")} committed with verified lineage.`]);
        if (nodeState.status === "success" && node.id === "relay" && nodeState.attempt > 1) {
            events.unshift([start, "warning", "Previous delivery attempt ended with a bounded transport timeout."]);
        }
    }
    return events.map(([time, level, message]) => {
        const item = document.createElement("li");
        const visualLevel = iconStatus(level);
        item.innerHTML = `<time>${escapeHtml(time)}</time><span class="event-level ${visualLevel}">${escapeHtml(statusLabel(level))}</span><p>${escapeHtml(message)}</p>`;
        return item;
    });
}

function attemptElements(node, nodeState) {
    const count = Math.max(1, nodeState.attempt);
    return Array.from({ length: count }, (_, offset) => count - offset).map(attempt => {
        const current = attempt === count;
        const status = current ? nodeState.status : "warning";
        const item = document.createElement("li");
        const message = current
            ? nodeState.reason ?? `${node.outputs.join(", ")} committed successfully.`
            : "Bounded retry scheduled after a transient dependency or transport failure.";
        item.innerHTML = `
            <span class="status-icon ${iconStatus(status)}" aria-hidden="true"></span>
            <span class="attempt-copy"><strong>Attempt ${attempt} - ${statusLabel(status)}</strong><span>${escapeHtml(message)}</span></span>
            <time>${nodeState.started === "-" ? "Not started" : `${nodeState.started} UTC`}</time>`;
        return item;
    });
}

function selectRun(runId) {
    selectedRun = runDefinitions.find(run => run.id === runId);
    selectedNodeId = defaultNodeForRun(selectedRun);
    const url = new URL(window.location.href);
    url.searchParams.set("run", runId);
    window.history.replaceState(null, "", url);
    renderAll();
}

function selectNode(nodeId, scrollToGraph = false) {
    selectedNodeId = nodeId;
    renderStages();
    renderGraph();
    renderDetail();
    if (scrollToGraph) {
        document.querySelector(".graph-card").scrollIntoView({ behavior: "smooth", block: "start" });
    }
}

function renderAll() {
    renderRuns();
    renderStages();
    renderRunSummary();
    renderGraph();
    renderDetail();
    renderArtifacts();
}

function setZoom(nextZoom) {
    zoom = Math.min(1.2, Math.max(.5, nextZoom));
    elements.graphCanvas.style.transform = `scale(${zoom})`;
    elements.graphScale.style.width = `${graphWidth * zoom}px`;
    elements.graphScale.style.height = `${graphHeight * zoom}px`;
    elements.zoomValue.value = `${Math.round(zoom * 100)}%`;
}

function fitGraph() {
    const available = elements.graphViewport.clientWidth - 24;
    const minimumReadableZoom = .5;
    setZoom(Math.max(minimumReadableZoom, Math.min(1, available / graphWidth)));
    elements.graphViewport.scrollTo({ left: 0, top: 0, behavior: "smooth" });
}

function switchTab(tabName) {
    document.querySelectorAll(".detail-tabs button").forEach(button => {
        const active = button.dataset.tab === tabName;
        button.classList.toggle("active", active);
        button.setAttribute("aria-selected", String(active));
        button.tabIndex = active ? 0 : -1;
    });
    elements.summaryPanel.hidden = tabName !== "summary";
    elements.artifactsPanel.hidden = tabName !== "artifacts";
    elements.attemptsPanel.hidden = tabName !== "attempts";
}

function updateActionChoice() {
    const selected = elements.reprocessOptions.querySelector("input:checked");
    document.querySelectorAll(".action-option").forEach(option => option.classList.toggle("selected", option.contains(selected)));
    const content = {
        delivery: ["Delivery retry is idempotent and does not rerun image processing.", "Review retry"],
        reprocess: ["A successor execution will retain links to the original source and profile.", "Review reprocess"],
        acquire: ["A new exposure will receive a new capture identity and acquisition time.", "Review acquisition"]
    }[selected.value];
    elements.actionExplanation.textContent = content[0];
    elements.confirmAction.textContent = content[1];
}

function showToast() {
    clearTimeout(toastTimer);
    elements.toast.hidden = false;
    toastTimer = setTimeout(() => { elements.toast.hidden = true; }, 4000);
}

function sampleHash(index) {
    const hashes = ["4da91c7f...ad81", "56c80a2b...f104", "783f9d11...9e25", "93b05a8c...f177", "a02df08e...4c11", "b18f2207...d906"];
    return hashes[index % hashes.length];
}

function escapeHtml(value) {
    return String(value).replace(/[&<>'"]/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[character]);
}

elements.runSearch.addEventListener("input", renderRuns);
elements.outcomeFilter.addEventListener("change", renderRuns);
elements.requiredToggle.addEventListener("click", () => {
    requiredOnly = !requiredOnly;
    elements.requiredToggle.setAttribute("aria-pressed", String(requiredOnly));
    renderStages();
});
elements.noticeAction.addEventListener("click", () => selectNode(selectedRun.noticeNode));
elements.fitGraph.addEventListener("click", fitGraph);
elements.zoomOut.addEventListener("click", () => setZoom(zoom - .1));
elements.zoomIn.addEventListener("click", () => setZoom(zoom + .1));
const detailTabs = [...document.querySelectorAll(".detail-tabs button")];
detailTabs.forEach((button, index) => {
    button.addEventListener("click", () => switchTab(button.dataset.tab));
    button.addEventListener("keydown", event => {
        if (!["ArrowLeft", "ArrowRight"].includes(event.key)) return;
        event.preventDefault();
        const direction = event.key === "ArrowRight" ? 1 : -1;
        const target = detailTabs[(index + direction + detailTabs.length) % detailTabs.length];
        switchTab(target.dataset.tab);
        target.focus();
    });
});
elements.reprocessButton.addEventListener("click", () => elements.reprocessDialog.showModal());
elements.reprocessOptions.addEventListener("change", updateActionChoice);
elements.reprocessDialog.addEventListener("close", () => {
    if (elements.reprocessDialog.returnValue === "confirm") showToast();
});
window.addEventListener("resize", () => {
    if (window.innerWidth < 780 && zoom === 1) fitGraph();
});

if (unknownRunId) {
    document.title = "Pipeline run not loaded | HVO SkyMonitor Prototype";
    const layout = document.querySelector(".app-layout");
    layout.classList.add("missing-run-layout");
    layout.innerHTML = `<main id="main-content"><section class="missing-run"><span class="status-icon neutral" aria-hidden="true"></span><div><p class="eyebrow">Operations / bounded prototype</p><h1>Pipeline run for capture #${escapeHtml(unknownRunId)} is not loaded</h1><p>The requested run is outside this prototype's bounded sample. No other run has been substituted.</p></div><a class="button secondary" href="index.html">Return to pipeline runs</a></section></main>`;
} else {
    renderAll();
    setZoom(1);
    requestAnimationFrame(() => {
        fitGraph();
    });
}
