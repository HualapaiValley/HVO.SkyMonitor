const dashboardStages = {
    processed: {
        title: "Processed presentation",
        subtitle: "Unregistered causal mean with the selected presentation layers.",
        badge: "Current baseline",
        badgeClass: "current",
        figureClass: "",
        showLayers: true
    },
    live: {
        title: "Unregistered live mean",
        subtitle: "Five calibrated sources ending at capture #84220, combined without geometric registration.",
        badge: "Current baseline",
        badgeClass: "current",
        figureClass: "stage-live",
        showLayers: false
    },
    calibrated: {
        title: "Calibrated reference frame",
        subtitle: "Capture #84220 after calibration, before temporal combination or presentation layers.",
        badge: "Single frame N",
        badgeClass: "source",
        figureClass: "stage-calibrated",
        showLayers: false
    },
    raw: {
        title: "Immutable raw source",
        subtitle: "Capture #84220 as acquired. Display conversion does not modify retained sensor evidence.",
        badge: "Primary evidence",
        badgeClass: "source",
        figureClass: "stage-raw",
        showLayers: false
    }
};

const galleryCaptures = [
    {
        id: "84220",
        image: "assets/w6-current.jpg",
        captured: "03:13:55",
        timestamp: "2026-09-01T03:13:55Z",
        outcome: "success",
        status: "Succeeded",
        product: "causal",
        productLabel: "Unregistered live mean",
        stage: "Processed",
        source: "5 frames / N-4..N / endpoint #84220",
        integration: "25.0s",
        artifacts: 22,
        summary: "Display processing completed independently while the 31 August Fireball followed edge detection with central confirmation.",
        event: "31 August Fireball",
        eventId: "E-1042"
    },
    {
        id: "84219",
        image: "assets/hualapai-night.jpg",
        captured: "03:13:45",
        timestamp: "2026-09-01T03:13:45Z",
        outcome: "warning",
        status: "Delivered with warning",
        product: "causal",
        productLabel: "Unregistered live mean",
        stage: "Processed",
        source: "5 frames / N-4..N / endpoint #84219",
        integration: "25.0s",
        artifacts: 17,
        summary: "Required image evidence is durable; the optional capture notification timed out."
    },
    {
        id: "84218",
        image: "assets/w6-current.jpg",
        captured: "03:13:35",
        timestamp: "2026-09-01T03:13:35Z",
        outcome: "success",
        status: "Succeeded",
        product: "single",
        productLabel: "Calibrated single frame",
        stage: "Calibrated",
        source: "Reference #84218 only",
        integration: "5.0s",
        artifacts: 17,
        summary: "The thumbnail selects the calibrated reference artifact; the run also retains its causal rolling-mean derivative."
    },
    {
        id: "84217",
        image: "assets/siding-spring-night.jpg",
        captured: "03:13:25",
        timestamp: "2026-09-01T03:13:25Z",
        outcome: "failure",
        status: "Processing failed",
        product: "raw",
        productLabel: "Raw evidence only",
        stage: "Raw",
        source: "Reference #84217 only",
        integration: "5.0s",
        artifacts: 3,
        summary: "Calibration failed, but immutable raw evidence and the independent no-candidate transient result remain available."
    },
    {
        id: "84216",
        image: "assets/hualapai-night.jpg",
        captured: "03:13:15",
        timestamp: "2026-09-01T03:13:15Z",
        outcome: "running",
        status: "Waiting for LogicHost",
        product: "registered",
        productLabel: "Registered stack / design target",
        stage: "Processed",
        source: "5 frames / N-4..N / ref #84216",
        integration: "25.0s",
        artifacts: 17,
        summary: "Local image processing and retention are complete; central delivery continues from the durable outbox."
    },
    {
        id: "84215",
        image: "assets/w6-current.jpg",
        captured: "03:13:05",
        timestamp: "2026-09-01T03:13:05Z",
        outcome: "success",
        status: "Succeeded",
        product: "causal",
        productLabel: "Unregistered live mean",
        stage: "Combined",
        source: "5 frames / N-4..N / endpoint #84215",
        integration: "25.0s",
        artifacts: 17,
        summary: "The causal arithmetic mean is retained and labeled separately from a future registered product."
    }
];

function initializeDashboard() {
    const figure = document.querySelector(".sky-figure");
    if (!figure) return;

    const requestedCaptureId = new URLSearchParams(window.location.search).get("capture");
    const selectedCapture = requestedCaptureId ? galleryCaptures.find(capture => capture.id === requestedCaptureId) : galleryCaptures[0];
    if (!selectedCapture) {
        document.title = "Capture not loaded | HVO SkyMonitor Prototype";
        document.getElementById("main-content").innerHTML = `<section class="missing-capture"><span class="status-icon neutral" aria-hidden="true"></span><div><p class="eyebrow">Archive / bounded prototype</p><h1>Capture #${escapePageHtml(requestedCaptureId)} is not loaded</h1><p>The route identifies evidence outside this prototype's bounded capture sample. No other capture has been substituted.</p></div><a class="button secondary" href="gallery.html">Return to captures</a></section>`;
        return;
    }
    const stageButtons = [...document.querySelectorAll("[data-stage]")];
    const layerInputs = [...document.querySelectorAll("[data-target]")];
    const title = document.getElementById("viewTitle");
    const subtitle = document.getElementById("viewSubtitle");
    const badge = document.getElementById("productBadge");
    const layerCount = document.getElementById("layerCount");

    function selectStage(stageName) {
        const stage = dashboardStages[stageName];
        const stageButton = stageButtons.find(button => button.dataset.stage === stageName);
        if (!stage || stageButton?.disabled) return;
        stageButtons.forEach(button => {
            const active = button.dataset.stage === stageName;
            button.classList.toggle("active", active);
            button.setAttribute("aria-pressed", String(active));
        });
        figure.classList.remove("stage-live", "stage-calibrated", "stage-raw", "layers-suppressed");
        if (stage.figureClass) figure.classList.add(stage.figureClass);
        figure.classList.toggle("layers-suppressed", !stage.showLayers);
        title.textContent = stage.title;
        subtitle.textContent = stage.subtitle;
        badge.textContent = stage.badge;
        badge.className = `product-badge ${stage.badgeClass}`;
        document.getElementById("skyImage").alt = `${stage.title} for capture ${selectedCapture.id}`;
        updateLayerCount(stage.showLayers);
    }

    function updateLayerCount(layersVisible = !figure.classList.contains("layers-suppressed")) {
        const selected = layerInputs.filter(input => input.checked).length;
        layerCount.textContent = layersVisible ? `${selected} selected` : `${selected} retained / hidden at this stage`;
    }

    function updateLayer(input) {
        const target = document.getElementById(input.dataset.target);
        target?.classList.toggle("layer-hidden", !input.checked);
        updateLayerCount();
    }

    stageButtons.forEach(button => button.addEventListener("click", () => selectStage(button.dataset.stage)));
    layerInputs.forEach(input => input.addEventListener("change", () => updateLayer(input)));
    document.querySelectorAll("[data-select-stage]").forEach(link => link.addEventListener("click", event => {
        event.preventDefault();
        selectStage(link.dataset.selectStage);
        figure.scrollIntoView({ behavior: "smooth", block: "center" });
    }));
    document.getElementById("resetLayers")?.addEventListener("click", () => {
        layerInputs.forEach(input => { input.checked = true; updateLayer(input); });
    });
    document.getElementById("fullScreenImage")?.addEventListener("click", () => figure.requestFullscreen?.());

    const initialStage = presentDashboardCapture(selectedCapture, stageButtons);
    selectStage(initialStage);
}

function presentDashboardCapture(capture, stageButtons) {
    const isCurrent = capture.id === galleryCaptures[0].id;
    const hasStack = capture.product === "registered" || capture.product === "causal";
    const setText = (id, value) => { document.getElementById(id).textContent = value; };

    document.title = `${isCurrent ? "Current Sky" : `Capture ${capture.id}`} | HVO SkyMonitor Prototype`;
    setText("captureEyebrow", `Capture #${capture.id} / sequence ${capture.id}`);
    setText("captureIdentity", `#${capture.id}`);
    setText("captureOutcomeTitle", capture.event ?? capture.status);
    setText("captureOutcomeSubtitle", capture.event ? `Event ${capture.eventId} / validated independently from the display stack` : capture.summary);
    document.getElementById("captureOutcomeIcon").className = `status-icon ${capture.outcome}`;
    document.getElementById("pipelineRunLink").href = `index.html?run=${capture.id}`;
    document.getElementById("skyImage").src = capture.image;
    setText("environmentTimestamp", `2026-09-01 ${capture.captured} UTC`);
    setText("exposureMidpoint", `${midpointFor(capture.captured)} UTC`);
    dashboardStages.calibrated.subtitle = `Capture #${capture.id} after calibration, before temporal combination or presentation layers.`;
    dashboardStages.raw.subtitle = `Capture #${capture.id} as acquired. Display conversion does not modify retained sensor evidence.`;

    if (!isCurrent) {
        setText("dashboardContext", "Archive capture / retained local evidence");
        setText("dashboardHeading", `Capture #${capture.id}`);
        setText("dashboardIntroduction", "A retained CameraAgent result with its display product, source lineage, and pipeline outcome kept together.");
        setText("captureCadence", `${capture.captured} UTC / 1 Sep 2026 / 31 Aug observing day`);
        document.getElementById("captureCadence").classList.add("history");
        setText("dashboardRefresh", "Return to current sky");
        setText("dashboardStatusTitle", capture.outcome === "failure" ? "Raw evidence retained" : "Capture evidence retained");
        setText("dashboardStatusSubtitle", capture.status);
        setText("captureIdentityLabel", "Selected capture");
        setText("captureAgeLabel", "Captured UTC");
        setText("captureAge", capture.captured);
        setText("captureDelivery", capture.outcome === "failure" ? "Not queued" : capture.outcome === "running" ? "Retrying" : "Acknowledged");
        document.getElementById("dashboardStatusIcon").className = `status-icon ${capture.outcome}`;

        dashboardStages.processed.title = capture.productLabel;
        dashboardStages.processed.subtitle = capture.summary;
        dashboardStages.processed.badge = capture.stage;
        dashboardStages.processed.badgeClass = capture.outcome === "failure" ? "source" : "current";
        dashboardStages.live.title = capture.product === "causal" ? "Unregistered live mean" : "Registered live stack";
        dashboardStages.live.subtitle = `${capture.source}; ${capture.integration} total integration.`;
        dashboardStages.live.badge = capture.product === "causal" ? "Current baseline" : "5-frame product";
    }

    stageButtons.forEach(button => {
        button.disabled = capture.product === "raw"
            ? button.dataset.stage !== "raw"
            : capture.product === "single" && button.dataset.stage === "live";
    });
    const liveStageHint = stageButtons.find(button => button.dataset.stage === "live")?.querySelector("small");
    if (liveStageHint) liveStageHint.textContent = capture.product === "causal" ? "5-frame causal mean" : "5-frame design target";

    const lineage = document.getElementById("stackLineage");
    lineage.hidden = !hasStack;
    if (hasStack) {
        const isRegistered = capture.product === "registered";
        setText("stack-heading", `${isRegistered ? "Registered live stack" : "Unregistered live mean"} ending at capture #${capture.id}`);
        setText("registrationQuality", isRegistered ? "0.31 px RMS" : "No registration");
        setText("referenceGeometryLabel", isRegistered ? "Reference geometry" : "Window endpoint");
        setText("referenceGeometry", `Capture #${capture.id}`);
        document.getElementById("designDisclosure").innerHTML = isRegistered
            ? "<strong>Design target:</strong> registration is shown to make the intended product semantics testable. The current baseline rolling mean is not yet registered."
            : "<strong>Current baseline:</strong> this is a causal arithmetic mean with no geometric registration. It is not labeled as a registered stack.";
        renderSourceStrip(capture);
    }

    updateOperationsSummary(capture);
    return capture.product === "raw" ? "raw" : capture.product === "single" ? "calibrated" : "processed";
}

function midpointFor(captured) {
    const [hours, minutes, seconds] = captured.split(":").map(Number);
    const totalSeconds = hours * 3600 + minutes * 60 + seconds + 2.5;
    const resultHours = Math.floor(totalSeconds / 3600) % 24;
    const resultMinutes = Math.floor(totalSeconds % 3600 / 60);
    const resultSeconds = (totalSeconds % 60).toFixed(3).padStart(6, "0");
    return `${String(resultHours).padStart(2, "0")}:${String(resultMinutes).padStart(2, "0")}:${resultSeconds}`;
}

function renderSourceStrip(capture) {
    const referenceId = Number(capture.id);
    const isRegistered = capture.product === "registered";
    const strip = document.getElementById("sourceStrip");
    const sources = Array.from({ length: 5 }, (_, index) => String(referenceId - 4 + index));
    strip.replaceChildren(...sources.map((sourceId, index) => {
        const knownCapture = galleryCaptures.find(item => item.id === sourceId);
        const link = document.createElement(knownCapture ? "a" : "div");
        if (knownCapture) link.href = `dashboard.html?capture=${sourceId}`;
        link.className = `${index === sources.length - 1 ? "reference " : ""}${knownCapture ? "" : "source-entry unavailable"}`.trim();
        if (knownCapture) {
            const image = document.createElement("img");
            image.src = knownCapture.image;
            image.alt = `${index === sources.length - 1 ? isRegistered ? "Reference " : "Window endpoint " : ""}capture ${sourceId}`;
            link.append(image);
        } else {
            const unavailable = document.createElement("b");
            unavailable.textContent = "Outside bounded page";
            link.append(unavailable);
        }
        const position = document.createElement("span");
        position.textContent = index === sources.length - 1 ? isRegistered ? "Reference N" : "Endpoint N" : `N-${sources.length - 1 - index}`;
        const identity = document.createElement("small");
        identity.textContent = `#${sourceId}`;
        link.append(position, identity);
        return link;
    }));
}

function updateOperationsSummary(capture) {
    const cards = [...document.querySelectorAll(".operations-strip article")];
    if (cards.length !== 4) return;
    const setCard = (card, title, detail) => {
        card.querySelector("strong").textContent = title;
        card.querySelector("div > span").textContent = detail;
    };
    setCard(cards[0], "Ready", "Immutable source retained");
    setCard(cards[1], capture.outcome === "failure" ? "Failed" : "17 stages", capture.outcome === "failure" ? "Calibration reference unavailable" : `Last run ${capture.id}`);
    setCard(cards[2], capture.event ? "Hybrid" : "No candidate", capture.event ? "1 confirmed event" : "Detector lane completed");
    setCard(cards[3], capture.outcome === "failure" ? "Not queued" : capture.outcome === "running" ? "Retrying" : "Acknowledged", capture.outcome === "running" ? "Durable outbox retains work" : capture.outcome === "failure" ? "No published artifact set" : "0 pending artifacts");
}

function initializeGallery() {
    const grid = document.getElementById("galleryGrid");
    if (!grid) return;

    const search = document.getElementById("gallerySearch");
    const outcome = document.getElementById("galleryOutcome");
    const product = document.getElementById("galleryProduct");
    const from = document.getElementById("galleryFrom");
    const to = document.getElementById("galleryTo");
    const count = document.getElementById("galleryResultCount");
    const empty = document.getElementById("galleryEmpty");
    const gridView = document.getElementById("gridView");
    const compactView = document.getElementById("compactView");

    function renderGallery() {
        const query = search.value.trim().toLowerCase();
        const selectedOutcome = outcome.value;
        const selectedProduct = product.value;
        const fromTimestamp = from.value ? new Date(`${from.value}Z`).getTime() : Number.NEGATIVE_INFINITY;
        const toTimestamp = to.value ? new Date(`${to.value}Z`).getTime() : Number.POSITIVE_INFINITY;
        const captures = galleryCaptures.filter(capture =>
            (selectedOutcome === "all" || capture.outcome === selectedOutcome) &&
            (selectedProduct === "all" || capture.product === selectedProduct) &&
            new Date(capture.timestamp).getTime() >= fromTimestamp &&
            new Date(capture.timestamp).getTime() <= toTimestamp &&
            (!query || `${capture.id} ${capture.status} ${capture.productLabel} ${capture.event ?? ""}`.toLowerCase().includes(query)));

        grid.replaceChildren(...captures.map(createCaptureCard));
        count.textContent = captures.length;
        empty.hidden = captures.length > 0;
    }

    function setLayout(compact) {
        grid.classList.toggle("compact", compact);
        gridView.classList.toggle("active", !compact);
        compactView.classList.toggle("active", compact);
        gridView.setAttribute("aria-pressed", String(!compact));
        compactView.setAttribute("aria-pressed", String(compact));
    }

    search.addEventListener("input", renderGallery);
    outcome.addEventListener("change", renderGallery);
    product.addEventListener("change", renderGallery);
    from.addEventListener("change", renderGallery);
    to.addEventListener("change", renderGallery);
    gridView.addEventListener("click", () => setLayout(false));
    compactView.addEventListener("click", () => setLayout(true));
    const galleryParameters = new URLSearchParams(window.location.search);
    if (galleryParameters.get("from")) from.value = galleryParameters.get("from");
    if (galleryParameters.get("to")) to.value = galleryParameters.get("to");
    renderGallery();
}

function createCaptureCard(capture) {
    const article = document.createElement("article");
    article.className = "capture-card";
    const eventBadge = capture.event ? `<span class="event-badge">${escapePageHtml(capture.event)}</span>` : "";
    article.innerHTML = `
        <a class="capture-image product-${capture.product}" href="dashboard.html?capture=${capture.id}" aria-label="Open capture ${capture.id}">
            <img src="${capture.image}" alt="All-sky preview for capture ${capture.id}" loading="lazy" decoding="async">
            <span class="capture-badges"><span>${escapePageHtml(capture.productLabel)}</span>${eventBadge}</span>
            <span class="capture-image-overlay"><span><strong>Capture #${capture.id}</strong><small>${escapePageHtml(capture.stage)} / ${capture.artifacts} artifacts</small></span><time datetime="${capture.timestamp}">${capture.captured} UTC</time></span>
        </a>
        <div class="capture-card-body">
            <p>${escapePageHtml(capture.summary)}</p>
            <dl class="capture-card-facts">
                <div><dt>Source</dt><dd title="${escapePageHtml(capture.source)}">${escapePageHtml(capture.source)}</dd></div>
                <div><dt>Integration</dt><dd>${capture.integration}</dd></div>
                <div><dt>Pipeline</dt><dd><span class="pipeline-state"><span class="status-icon ${capture.outcome}" aria-hidden="true"></span>${escapePageHtml(capture.status)}</span></dd></div>
            </dl>
            <div class="capture-card-actions"><a href="dashboard.html?capture=${capture.id}">Open image</a><a class="run-link" href="index.html?run=${capture.id}">View pipeline run</a></div>
        </div>`;
    return article;
}

function escapePageHtml(value) {
    return String(value).replace(/[&<>'"]/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[character]);
}

function initializeSharedNavigation() {
    document.querySelectorAll(".agent-menu").forEach(button => button.addEventListener("click", () => {
        const header = button.closest(".app-header");
        const expanded = !header.classList.contains("menu-open");
        header.classList.toggle("menu-open", expanded);
        button.setAttribute("aria-expanded", String(expanded));
    }));
}

initializeSharedNavigation();
initializeDashboard();
initializeGallery();
