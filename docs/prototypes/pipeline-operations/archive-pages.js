const generatedProducts = [
    { id: "aug31-timelapse", date: "2026-08-31", type: "timelapse", title: "31 August Night Timelapse", status: "running", image: "assets/w6-current.jpg", description: "The open hourly segment is accumulating processed captures; final playback waits for capture-window close.", sources: "37 frames", span: "6m", created: "Running", recipe: "Sky Motion Timelapse", automation: "Hourly Sky Motion Timelapse" },
    { id: "aug31-star-trail", date: "2026-08-31", type: "star-trail", title: "31 August Star Trail", status: "running", image: "assets/hualapai-night.jpg", description: "The working lighten composite contains quality-approved frames from the open night.", sources: "31 frames", span: "5m", created: "Running", recipe: "Night Star Trail", automation: "Nightly Star Trail" },
    { id: "aug31-keogram", date: "2026-08-31", type: "keogram", title: "31 August North-South Keogram", status: "running", image: "assets/siding-spring-night.jpg", description: "North-south slices are appended every minute while the observing window remains open.", sources: "6 samples", span: "6m", created: "Running", recipe: "North-South Keogram", automation: "Nightly Keogram" },
    { id: "aug31-summary", date: "2026-08-31", type: "summary", title: "31 August Observing Summary", status: "running", image: "assets/w6-current.jpg", description: "Live counters remain provisional until observing-day rollover commits the summary.", sources: "37 captures", span: "6m", created: "Pending rollover", recipe: "Observing Day Summary", automation: "Observing Day Summary" },
    { id: "aug30-timelapse", date: "2026-08-30", type: "timelapse", title: "30 August Night Timelapse", status: "partial", image: "assets/hualapai-night.jpg", description: "Playback retains a marked 22-minute camera-maintenance gap.", sources: "2,604 frames", span: "9h 31m", created: "05:19 MST", recipe: "Sky Motion Timelapse", automation: "Hourly Sky Motion Timelapse" },
    { id: "aug30-star-trail", date: "2026-08-30", type: "star-trail", title: "30 August Star Trail", status: "available", image: "assets/hualapai-night.jpg", description: "Quality-gated composite with the maintenance interval excluded.", sources: "2,103 frames", span: "7h 48m", created: "05:16 MST", recipe: "Night Star Trail", automation: "Nightly Star Trail" },
    { id: "aug29-keogram", date: "2026-08-29", type: "keogram", title: "29 August North-South Keogram", status: "available", image: "assets/siding-spring-night.jpg", description: "Night brightness and cloud movement along the local meridian.", sources: "584 samples", span: "9h 44m", created: "05:12 MST", recipe: "North-South Keogram", automation: "Nightly Keogram" }
];

const detectedEvents = [
    { id: "august-fireball", technicalId: "E-1042", date: "2026-08-31", time: "1 Sep 03:13:55 UTC", localTime: "31 Aug 20:13:55 MST", title: "31 August Fireball", classification: "fireball", classificationLabel: "Fireball", review: "confirmed", reviewLabel: "Owner confirmed", confidence: "0.94", duration: "2.14s", image: "assets/hualapai-night.jpg", summary: "A bright elongated path crossed the northeast sky. Centered five-frame evidence is complete and the classification was owner confirmed." },
    { id: "august-meteor", technicalId: "E-1031", date: "2026-08-24", time: "25 Aug 04:42:18 UTC", localTime: "24 Aug 21:42:18 MST", title: "24 August Meteor Candidate", classification: "meteor", classificationLabel: "Meteor candidate", review: "pending", reviewLabel: "Awaiting review", confidence: "0.81", duration: "0.82s", image: "assets/w6-current.jpg", summary: "A short linear track passed local quality gates. Centered evidence is ready for operator review." },
    { id: "august-satellite", technicalId: "E-1019", date: "2026-08-19", time: "20 Aug 02:11:06 UTC", localTime: "19 Aug 19:11:06 MST", title: "19 August Satellite Track", classification: "satellite", classificationLabel: "Satellite", review: "rejected", reviewLabel: "Rejected", confidence: "0.97", duration: "14.6s", image: "assets/siding-spring-night.jpg", summary: "The long constant-velocity track matched a satellite signature and was rejected as a meteor event." },
    { id: "august-aircraft", technicalId: "E-1008", date: "2026-08-11", time: "12 Aug 05:02:41 UTC", localTime: "11 Aug 22:02:41 MST", title: "11 August Aircraft Track", classification: "aircraft", classificationLabel: "Aircraft", review: "rejected", reviewLabel: "Rejected", confidence: "0.92", duration: "18.3s", image: "assets/w6-current.jpg", summary: "Repeated light pulses and path duration matched an aircraft signature." }
];

function initializeArchiveCalendar() {
    const grid = document.getElementById("calendarGrid");
    if (!grid) return;
    let year = 2026;
    let month = 7;
    const monthLabel = document.getElementById("calendarMonth");

    function renderMonth() {
        const firstWeekday = new Date(Date.UTC(year, month, 1)).getUTCDay();
        const daysInMonth = new Date(Date.UTC(year, month + 1, 0)).getUTCDate();
        const previousMonthDays = new Date(Date.UTC(year, month, 0)).getUTCDate();
        const cells = [];
        for (let index = firstWeekday - 1; index >= 0; index -= 1) cells.push(createCalendarDay(previousMonthDays - index, true, -1));
        for (let day = 1; day <= daysInMonth; day += 1) cells.push(createCalendarDay(day, false, 0));
        let nextDay = 1;
        while (cells.length % 7 !== 0) cells.push(createCalendarDay(nextDay++, true, 1));
        grid.replaceChildren(...cells);
        monthLabel.textContent = new Intl.DateTimeFormat("en-US", { month: "long", year: "numeric", timeZone: "UTC" }).format(new Date(Date.UTC(year, month, 1)));
    }

    function createCalendarDay(day, outside, monthOffset) {
        const cellDate = new Date(Date.UTC(year, month + monthOffset, day));
        const date = cellDate.toISOString().slice(0, 10);
        const hasArchiveData = !outside && date.startsWith("2026-08-") && day >= 5;
        const isPartial = hasArchiveData && ["2026-08-12", "2026-08-22", "2026-08-30"].includes(date);
        const products = generatedProducts.filter(product => product.date === date && product.type !== "summary");
        const hasEvent = detectedEvents.some(event => event.date === date);
        const element = document.createElement(hasArchiveData ? "a" : "div");
        element.className = `calendar-day${outside ? " outside" : ""}${hasArchiveData ? "" : " empty-day"}${date === "2026-08-31" ? " today" : ""}`;
        if (hasArchiveData) element.href = `day.html?date=${date}`;
        element.setAttribute("aria-label", `${friendlyDate(date)}${hasArchiveData ? `, ${isPartial ? "partial" : "complete"} archive, ${2800 + day} captures` : ", no archived session"}`);
        const image = day % 2 ? "assets/w6-current.jpg" : "assets/hualapai-night.jpg";
        const dayLabel = outside ? `${new Intl.DateTimeFormat("en-US", { month: "short", timeZone: "UTC" }).format(cellDate)} ${day}` : day;
        element.innerHTML = `<header><strong>${dayLabel}</strong>${hasArchiveData ? `<i class="day-quality${isPartial ? " partial" : ""}" title="${isPartial ? "Partial coverage" : "Complete coverage"}"></i>` : ""}</header>${hasArchiveData ? `<img class="calendar-thumb" src="${image}" alt="Representative image for ${date}"><small>${isPartial ? "Partial" : "Complete"} / ${2800 + day} captures</small><span class="calendar-products">${products.map(product => `<i title="${productTypeName(product.type)}">${product.type === "timelapse" ? "T" : product.type === "star-trail" ? "S" : "K"}</i>`).join("")}${hasEvent ? `<i class="event" title="Detected event">!</i>` : ""}</span>` : "<small>No archived session</small>"}`;
        return element;
    }

    document.getElementById("previousMonth").addEventListener("click", () => { month -= 1; if (month < 0) { month = 11; year -= 1; } renderMonth(); });
    document.getElementById("nextMonth").addEventListener("click", () => { month += 1; if (month > 11) { month = 0; year += 1; } renderMonth(); });
    document.getElementById("todayMonth").addEventListener("click", () => { year = 2026; month = 7; renderMonth(); });
    renderMonth();
}

function initializeObservingDay() {
    const grid = document.getElementById("dayProducts");
    if (!grid) return;
    const requestedDate = validDate(new URLSearchParams(window.location.search).get("date")) ?? "2026-08-31";
    if (requestedDate !== "2026-08-31") {
        renderGenericObservingDay(requestedDate);
        return;
    }
    grid.replaceChildren(...generatedProducts.filter(product => product.date === requestedDate && product.type !== "summary").map(createDailyProductCard));
}

function renderGenericObservingDay(date) {
    const main = document.getElementById("main-content");
    [...main.children].slice(1).forEach(element => element.remove());
    const products = generatedProducts.filter(product => product.date === date && product.type !== "summary");
    const events = detectedEvents.filter(event => event.date === date);
    const day = Number(date.slice(-2));
    const inProgress = products.some(product => product.status === "running");
    const hasCaptureArchive = (date.startsWith("2026-08-") && day >= 5) || products.length > 0;
    const partial = inProgress || ["2026-08-12", "2026-08-22", "2026-08-30"].includes(date);
    const previousDate = shiftDate(date, -1);
    const nextDate = shiftDate(date, 1);
    const captureCount = inProgress ? 346 : hasCaptureArchive ? 2800 + day : 0;
    const archiveState = inProgress ? "Observing day in progress" : partial ? "Archived with a marked gap" : "Night processing complete";
    const coverageState = inProgress ? "In progress" : partial ? "Partial" : "Complete";
    document.title = `${friendlyDate(date)} Observing Day | HVO SkyMonitor Prototype`;
    main.insertAdjacentHTML("beforeend", `
        <header class="day-heading"><div class="day-nav"><a class="icon-button" href="day.html?date=${previousDate}" aria-label="Previous observing day"><svg viewBox="0 0 20 20"><path d="m12 4-6 6 6 6"></path></svg></a><div><p class="eyebrow">Observing day / HVO Main Fisheye</p><h1>${friendlyDate(date)}</h1><p>Local noon ${friendlyDate(date)} through local noon ${friendlyDate(nextDate)}.</p></div><a class="icon-button" href="day.html?date=${nextDate}" aria-label="Next observing day"><svg viewBox="0 0 20 20"><path d="m8 4 6 6-6 6"></path></svg></a></div><div class="archive-heading-actions">${hasCaptureArchive ? `<a class="button secondary" href="gallery.html?from=${date}T19:00&amp;to=${nextDate}T19:00">View ${captureCount.toLocaleString()} captures</a>` : ""}<a class="button secondary" href="events.html?date=${date}">View events</a></div></header>
        ${hasCaptureArchive ? `<section class="day-hero"><div class="day-cover"><img src="${day % 2 ? "assets/w6-current.jpg" : "assets/hualapai-night.jpg"}" alt="Representative image for ${friendlyDate(date)} observing day"><span><small>Representative retained capture</small><strong>${inProgress ? "Open observing-day window" : partial ? "Partial observing-day coverage" : "Complete observing-day archive"}</strong></span></div><div class="day-health"><div><span class="status-icon ${partial ? "warning" : "success"}"></span><span><strong>${archiveState}</strong><small>${products.length ? `${products.length} generated product record${products.length === 1 ? "" : "s"}` : "No generated product records in this bounded sample"}</small></span></div><dl><div><dt>Observing boundary</dt><dd>12:00 - 12:00 MST</dd></div><div><dt>Capture coverage</dt><dd>${coverageState}</dd></div><div><dt>Retained captures</dt><dd>${captureCount.toLocaleString()}</dd></div><div><dt>Generated products</dt><dd>${products.length}</dd></div><div><dt>Detected events</dt><dd>${events.length}</dd></div><div><dt>Authority</dt><dd>CameraAgent local</dd></div></dl></div></section>` : `<section class="archive-empty-day"><span class="status-icon neutral"></span><div><h2>No archived observing session</h2><p>This bounded prototype has no capture record for ${friendlyDate(date)}.</p></div><a class="button secondary" href="calendar.html">Return to calendar</a></section>`}
        <section class="section-heading"><div><p class="eyebrow">Daily derivatives</p><h2>Generated products</h2></div><a href="products.html?date=${date}">View product archive</a></section>
        ${products.length ? `<div class="daily-product-grid">${products.map(product => `<article class="daily-product-card">${productMediaLink(product, "daily-product-media")}<div class="daily-product-body"><p>${product.description}</p></div></article>`).join("")}</div>` : `<section class="archive-empty-day compact"><div><h2>No generated products in this sample</h2><p>Capture evidence remains independently available when a daily derivative is absent.</p></div></section>`}
        ${events.map(event => `<section class="archive-panel generic-day-event"><header><div><h2>${event.title}</h2><p>${event.time} / ${event.reviewLabel}</p></div><span class="event-classification">${event.classificationLabel}</span></header><p>${event.summary}</p><a class="text-button" href="event.html?event=${event.id}">Open event evidence</a></section>`).join("")}`);
}

function createDailyProductCard(product) {
    const article = document.createElement("article");
    article.className = "daily-product-card";
    article.innerHTML = `${productMediaLink(product, "daily-product-media")}<div class="daily-product-body"><p>${product.description}</p><div class="product-fact-row"><span>Sources<strong>${product.sources}</strong></span><span>Span<strong>${product.span}</strong></span><span>Created<strong>${product.created}</strong></span></div></div>`;
    return article;
}

function productMediaLink(product, className) {
    const typeName = productTypeName(product.type);
    const mediaClass = `${className} ${product.type}`;
    return `<a class="${mediaClass}" href="product.html?product=${product.id}"><img src="${product.image}" alt="${typeName} preview for ${friendlyDate(product.date)}"><span><span><strong>${product.title}</strong><small>${typeName} / ${product.status}</small></span><small>Open product</small></span></a>`;
}

function initializeProductLibrary() {
    const grid = document.getElementById("productGrid");
    if (!grid) return;
    const type = document.getElementById("productTypeFilter");
    const status = document.getElementById("productStatusFilter");
    const date = document.getElementById("productDateFilter");
    const queryDate = new URLSearchParams(window.location.search).get("date");
    if (queryDate) date.value = queryDate;

    function render() {
        const products = generatedProducts.filter(product => (type.value === "all" || product.type === type.value) && (status.value === "all" || product.status === status.value) && (!date.value || product.date === date.value));
        grid.replaceChildren(...products.map(createProductCard));
        document.getElementById("productEmpty").hidden = products.length > 0;
    }

    type.addEventListener("change", render);
    status.addEventListener("change", render);
    date.addEventListener("change", render);
    document.getElementById("clearProductFilters").addEventListener("click", () => { date.value = ""; render(); });
    render();
}

function createProductCard(product) {
    const article = document.createElement("article");
    article.className = "product-card";
    article.innerHTML = `${productMediaLink(product, "product-card-media")}<div class="product-card-body"><p>${product.description}</p><div class="product-fact-row"><span>Sources<strong>${product.sources}</strong></span><span>Span<strong>${product.span}</strong></span><span>Status<strong>${capitalize(product.status)}</strong></span></div><div class="product-card-meta"><a href="day.html?date=${product.date}">${friendlyDate(product.date)}</a><span>${product.automation}</span></div></div>`;
    return article;
}

function initializeProductDetail() {
    const container = document.getElementById("productDetail");
    if (!container) return;
    const requested = new URLSearchParams(window.location.search).get("product");
    const product = requested ? generatedProducts.find(item => item.id === requested) : generatedProducts[0];
    if (!product) {
        document.title = "Product not loaded | HVO SkyMonitor Prototype";
        container.innerHTML = `<section class="archive-empty-day"><span class="status-icon neutral"></span><div><h1>Generated product not loaded</h1><p>The requested product is outside this bounded prototype. No other product has been substituted.</p></div><a class="button secondary" href="products.html">Return to products</a></section>`;
        return;
    }
    const typeName = productTypeName(product.type);
    document.title = `${product.title} | HVO SkyMonitor Prototype`;
    if (product.status === "running") {
        renderRunningProductDetail(container, product, typeName);
        return;
    }
    container.innerHTML = `
        <header class="product-detail-heading"><div><p class="eyebrow">Archive / ${typeName} / HVO Main Fisheye</p><h1>${product.title}</h1><p>${friendlyDate(product.date)} observing day / ${capitalize(product.status)} / generated ${product.created}</p></div><div class="detail-heading-actions"><a class="button secondary" href="day.html?date=${product.date}">Observing day</a><button class="button secondary" type="button" data-archive-action="A successor generation run would be created; the current product remains immutable.">Regenerate</button></div></header>
        <section class="product-viewer-layout"><div class="generated-media ${product.type}">${product.type === "keogram" ? "" : `<img src="${product.image}" alt="${typeName} for ${friendlyDate(product.date)}">`}<span class="media-label">${product.type === "keogram" ? "North to south / time increases left to right" : `${product.span} source window`}</span>${product.type === "timelapse" ? `<div class="media-controls"><button id="productPlay" class="icon-button" type="button" aria-label="Play timelapse"><svg viewBox="0 0 20 20"><path d="m7 4 9 6-9 6V4Z"></path></svg></button><span class="media-progress"><i></i></span><time>03:12 / 05:58</time></div>` : ""}</div><aside class="product-inspector"><header><h2>Product evidence</h2><p>${product.description}</p></header><dl class="product-provenance"><div><dt>Source frames</dt><dd>${product.sources}</dd></div><div><dt>Observation span</dt><dd>${product.span}</dd></div><div><dt>Recipe</dt><dd>${product.recipe}</dd></div><div><dt>Executor</dt><dd>HVO Main Fisheye</dd></div><div><dt>Automation</dt><dd>${product.automation}</dd></div><div><dt>Completeness</dt><dd>${product.status === "partial" ? "Partial / gaps marked" : product.status === "running" ? "In progress" : "Complete"}</dd></div><div class="full"><dt>Source window</dt><dd>${friendlyDate(product.date)} 19:08 through ${friendlyDate(shiftDate(product.date, 1))} 05:08 MST</dd></div><div class="full"><dt>Technical product ID</dt><dd><code>product_${product.id.replaceAll("-", "_")}_v2</code></dd></div></dl><a class="text-button" href="operations.html?section=automations&amp;view=runs">Open generation run</a></aside></section>
        <div class="day-detail-grid"><section class="product-detail-panel archive-panel"><header><div><h2>Generation history</h2><p>Regeneration creates a successor and preserves prior evidence.</p></div><span class="state-chip success">Current v2</span></header><ol class="product-versions"><li><span class="status-icon success"></span><span><strong>Version 2 / current</strong><small>Quality exclusions and final session boundary applied</small></span><time>05:21</time></li><li><span class="status-icon success"></span><span><strong>Version 1 / superseded</strong><small>Initial generation before day summary closed</small></span><time>05:10</time></li></ol></section><section class="product-detail-panel archive-panel"><header><div><h2>Integrity &amp; lineage</h2><p>Exact source and recipe identities remain available.</p></div><span>Verified</span></header><dl class="product-provenance"><div><dt>Output checksum</dt><dd><code>9af2...8c31</code></dd></div><div><dt>Manifest</dt><dd>daily-product-v2</dd></div><div><dt>Capture profile</dt><dd>Night Fisheye Capture</dd></div><div><dt>Pipeline</dt><dd>Layered All-Sky Processing</dd></div><div class="full"><dt>Retention</dt><dd>90 days / observing-day product</dd></div></dl></section></div>`;
    document.getElementById("productPlay")?.addEventListener("click", event => {
        const playing = event.currentTarget.getAttribute("aria-label") === "Pause timelapse";
        event.currentTarget.setAttribute("aria-label", playing ? "Play timelapse" : "Pause timelapse");
        event.currentTarget.innerHTML = playing ? `<svg viewBox="0 0 20 20"><path d="m7 4 9 6-9 6V4Z"></path></svg>` : `<svg viewBox="0 0 20 20"><path d="M7 5v10m6-10v10"></path></svg>`;
    });
}

function renderRunningProductDetail(container, product, typeName) {
    container.innerHTML = `
        <header class="product-detail-heading"><div><p class="eyebrow">Archive / ${typeName} / HVO Main Fisheye</p><h1>${product.title}</h1><p>${friendlyDate(product.date)} observing day / generation in progress</p></div><div class="detail-heading-actions"><a class="button secondary" href="day.html?date=${product.date}">Observing day</a></div></header>
        <section class="product-viewer-layout"><div class="generated-media ${product.type}"><img src="${product.image}" alt="Current ${typeName.toLowerCase()} preview for ${friendlyDate(product.date)}"><span class="media-label">Open source window / ${product.span} collected</span></div><aside class="product-inspector"><header><h2>In-progress product evidence</h2><p>${product.description}</p></header><dl class="product-provenance"><div><dt>Sources so far</dt><dd>${product.sources}</dd></div><div><dt>Current span</dt><dd>${product.span}</dd></div><div><dt>Recipe</dt><dd>${product.recipe}</dd></div><div><dt>Executor</dt><dd>HVO Main Fisheye</dd></div><div><dt>Automation</dt><dd>${product.automation}</dd></div><div><dt>Completeness</dt><dd>In progress</dd></div><div class="full"><dt>Source window</dt><dd>Opened ${friendlyDate(product.date)} at 19:08 MST / closes at schedule boundary</dd></div><div class="full"><dt>Technical run ID</dt><dd><code>generation_${product.id.replaceAll("-", "_")}_active</code></dd></div></dl><a class="text-button" href="operations.html?section=automations&amp;view=runs">Open active generation run</a></aside></section>
        <section class="product-detail-panel archive-panel running-product-state"><header><div><h2>Generation state</h2><p>The durable run may append segments until the source window closes.</p></div><span class="state-chip running">Running</span></header><dl class="product-provenance"><div><dt>Current version</dt><dd>Working version 1</dd></div><div><dt>Final checksum</dt><dd>Pending completion</dd></div><div><dt>Prior version</dt><dd>None</dd></div><div><dt>Retention state</dt><dd>Inputs pinned</dd></div></dl></section>`;
}

function initializeEvents() {
    const results = document.getElementById("eventResults");
    if (!results) return;
    const classification = document.getElementById("eventClassFilter");
    const review = document.getElementById("eventReviewFilter");
    const date = document.getElementById("eventDateFilter");
    const listButton = document.getElementById("eventListView");
    const calendarButton = document.getElementById("eventCalendarView");
    const calendar = document.getElementById("eventCalendarPanel");
    const resultStatus = document.createElement("p");
    resultStatus.className = "event-result-count";
    resultStatus.setAttribute("role", "status");
    results.before(resultStatus);
    const queryDate = validDate(new URLSearchParams(window.location.search).get("date"));
    date.value = queryDate ?? "";
    date.closest("label").querySelector("span").textContent = "Observing date";
    classification.querySelector('option[value="rejected"]')?.remove();
    if (!classification.querySelector('option[value="satellite"]')) classification.insertAdjacentHTML("beforeend", `<option value="satellite">Satellite</option><option value="aircraft">Aircraft</option>`);
    const latestEventDate = document.querySelector(".event-summary article:last-child small");
    if (latestEventDate) latestEventDate.textContent = "1 September UTC / 31 August local";

    function filteredEvents() {
        return detectedEvents.filter(event => (classification.value === "all" || event.classification === classification.value) && (review.value === "all" || event.review === review.value) && (!date.value || event.date === date.value));
    }

    function renderList() {
        const events = filteredEvents();
        resultStatus.textContent = `${events.length} matching event${events.length === 1 ? "" : "s"}`;
        results.replaceChildren(...events.map(createEventCard));
        if (!events.length) results.innerHTML = `<section class="archive-empty-day compact" role="status"><div><h2>No events match these filters</h2><p>Change the classification, review state, or observing date.</p></div></section>`;
    }

    function renderCurrentView() {
        const events = filteredEvents();
        resultStatus.textContent = `${events.length} matching event${events.length === 1 ? "" : "s"}`;
        if (calendar.hidden) renderList(); else renderEventCalendar(calendar, events);
    }

    function setView(calendarVisible) {
        const events = filteredEvents();
        resultStatus.textContent = `${events.length} matching event${events.length === 1 ? "" : "s"}`;
        results.hidden = calendarVisible;
        calendar.hidden = !calendarVisible;
        listButton.classList.toggle("active", !calendarVisible);
        calendarButton.classList.toggle("active", calendarVisible);
        listButton.setAttribute("aria-pressed", String(!calendarVisible));
        calendarButton.setAttribute("aria-pressed", String(calendarVisible));
        if (calendarVisible) renderEventCalendar(calendar, events); else renderList();
    }

    classification.addEventListener("change", renderCurrentView);
    review.addEventListener("change", renderCurrentView);
    date.addEventListener("change", renderCurrentView);
    listButton.addEventListener("click", () => setView(false));
    calendarButton.addEventListener("click", () => setView(true));
    renderList();
}

function createEventCard(event) {
    const article = document.createElement("article");
    article.className = "event-card";
    article.innerHTML = `<a class="event-card-media" href="event.html?event=${event.id}"><img src="${event.image}" alt="Evidence preview for ${event.title}"><span>${event.time}</span></a><div class="event-card-main"><header><div><p class="eyebrow">${friendlyDate(event.date)} / HVO Main Fisheye</p><h2>${event.title}</h2></div><span class="classification-chip ${event.classification}">${event.classificationLabel}</span></header><p>${event.summary}</p><a href="event.html?event=${event.id}">Open event evidence</a></div><dl class="event-card-facts"><div><dt>Review</dt><dd>${event.reviewLabel}</dd></div><div><dt>Confidence</dt><dd>${event.confidence}</dd></div><div><dt>Duration</dt><dd>${event.duration}</dd></div></dl>`;
    return article;
}

function renderEventCalendar(container, events = detectedEvents) {
    const blanks = Array.from({ length: 6 }, () => `<div class="event-month-day outside" aria-hidden="true"></div>`).join("");
    container.innerHTML = `<header class="section-heading"><div><p class="eyebrow">August 2026</p><h2>Event calendar</h2></div><span>${events.length} matching event${events.length === 1 ? "" : "s"}</span></header><div class="event-month-weekdays" aria-hidden="true"><span>Sun</span><span>Mon</span><span>Tue</span><span>Wed</span><span>Thu</span><span>Fri</span><span>Sat</span></div><div class="event-month-grid">${blanks}${Array.from({ length: 31 }, (_, index) => { const day = index + 1; const event = events.find(item => Number(item.date.slice(-2)) === day); return `<div class="event-month-day${event ? " has-event" : ""}"><strong>${day}</strong>${event ? `<a href="event.html?event=${event.id}">${event.classificationLabel}</a>` : ""}</div>`; }).join("")}</div>${events.length ? "" : `<p class="event-calendar-empty" role="status">No events match the current filters.</p>`}`;
}

function initializeEventDetail() {
    const container = document.getElementById("eventDetail");
    if (!container) return;
    const requested = new URLSearchParams(window.location.search).get("event");
    const event = requested ? detectedEvents.find(item => item.id === requested) : detectedEvents[0];
    if (!event) {
        document.title = "Event not loaded | HVO SkyMonitor Prototype";
        container.innerHTML = `<section class="archive-empty-day"><span class="status-icon neutral"></span><div><h1>Event record not loaded</h1><p>The requested event is outside this bounded prototype. No other event has been substituted.</p></div><a class="button secondary" href="events.html">Return to events</a></section>`;
        return;
    }
    document.title = `${event.title} | HVO SkyMonitor Prototype`;
    if (event.id !== "august-fireball") {
        renderGenericEventDetail(container, event);
        return;
    }
    container.innerHTML = `
        <header class="event-detail-heading"><div><p class="eyebrow">Events / confirmed / HVO Main Fisheye</p><h1>31 August Fireball</h1><p>31 Aug 20:13:55 MST / 1 Sep 03:13:55 UTC / centered evidence complete</p></div><div class="event-detail-status"><span class="status-icon warning"></span><span>Meteor / fireball</span></div></header>
        <div class="event-media-tabs" role="tablist" aria-label="Event media"><button class="active" type="button" role="tab" aria-selected="true" data-event-media="animation">Centered animation</button><button type="button" role="tab" aria-selected="false" data-event-media="stack">Path composite</button><button type="button" role="tab" aria-selected="false" data-event-media="mask">Detection mask</button></div>
        <section class="event-media-layout"><div id="eventMediaView" class="event-media-view animation"><img src="assets/hualapai-night.jpg" alt="Centered animation preview for 31 August Fireball"><i class="event-track"></i><span class="event-track-label">2.14s / northeast path</span></div><aside class="event-context"><h2>Centered context</h2><p>Five exact sequence-compatible frames used by central validation.</p><div class="context-frame-list">${["N-2", "N-1", "N", "N+1", "N+2"].map(renderFireballContextFrame).join("")}</div><a class="text-button" href="index.html?run=84220">Open detection pipeline run</a></aside></section>
        <section class="science-grid"><article class="science-panel"><header><h2>Observed measurements</h2><span>Direct camera evidence</span></header><dl class="science-facts"><div><dt>Duration <i class="science-value-kind">Measured</i></dt><dd>2.14 +/- 0.10s</dd></div><div><dt>Angular path <i class="science-value-kind">Measured</i></dt><dd>23.7 +/- 0.4 deg</dd></div><div><dt>Start direction <i class="science-value-kind">Measured</i></dt><dd>Az 47.2 / Alt 61.8</dd></div><div><dt>End direction <i class="science-value-kind">Measured</i></dt><dd>Az 69.1 / Alt 52.4</dd></div><div><dt>Angular velocity <i class="science-value-kind">Measured</i></dt><dd>11.1 deg/s</dd></div><div><dt>Peak signal <i class="science-value-kind">Measured</i></dt><dd>52,840 ADU</dd></div></dl></article><article class="science-panel"><header><h2>Derived estimates</h2><span>Method and uncertainty explicit</span></header><dl class="science-facts"><div><dt>Apparent magnitude <i class="science-value-kind derived">Derived</i></dt><dd>-4.1 +/- 0.7</dd></div><div><dt>Classification <i class="science-value-kind derived">Derived</i></dt><dd>Meteor / fireball</dd></div><div><dt>Confidence <i class="science-value-kind derived">Derived</i></dt><dd>0.94</dd></div><div><dt>Photometric method</dt><dd>Catalog-relative estimate</dd></div><div class="full"><dt>Limit</dt><dd>Estimate uses the local camera response and is not an absolute calibrated light curve.</dd></div></dl></article><article class="science-panel"><header><h2>Physical trajectory</h2><span>Single-camera limitation</span></header><div class="trajectory-visual"><i></i><span>Angular path only / no triangulation baseline</span></div><dl class="science-facts"><div><dt>Physical speed <i class="science-value-kind unresolved">Not resolved</i></dt><dd>Requires correlation</dd></div><div><dt>Event altitude <i class="science-value-kind unresolved">Not resolved</i></dt><dd>Requires correlation</dd></div><div><dt>Ground track <i class="science-value-kind unresolved">Not resolved</i></dt><dd>Requires multiple sites</dd></div><div><dt>Impact location <i class="science-value-kind unresolved">Not resolved</i></dt><dd>No physical trajectory</dd></div></dl></article></section>
        <section class="event-review-grid"><article class="event-detail-panel archive-panel"><header><div><h2>Review &amp; classification</h2><p>LogicHost owns the authoritative assessment and review history.</p></div><span class="state-chip success">Owner confirmed</span></header><div class="ops-note-banner"><span class="status-icon success"></span><span><strong>Confirmed as Meteor / Fireball.</strong> The local candidate and centered evidence remain immutable; review appended the authoritative classification.</span></div><div class="event-review-actions"><button class="button secondary" type="button" data-archive-action="A review revision editor would open; existing review history remains immutable.">Revise classification</button><a class="button secondary" href="day.html?date=2026-08-31">Observing day</a><a class="button secondary" href="gallery.html?from=2026-09-01T03:13&amp;to=2026-09-01T03:15">Nearby captures</a></div></article><article class="event-detail-panel archive-panel"><header><div><h2>Evidence &amp; provenance</h2><p>Technical identifiers remain secondary.</p></div><span>Verified</span></header><ul class="evidence-list"><li><strong>Event ID</strong><span>E-1042</span></li><li><strong>Camera</strong><span>HVO Main Fisheye</span></li><li><strong>Detector</strong><span>Meteor and Fireball Detection 1.4</span></li><li><strong>Capture profile</strong><span>Night Fisheye Capture / rev 17</span></li><li><strong>Rig geometry</strong><span>Hualapai roof survey / rev 4</span></li><li><strong>Evidence checksum</strong><span>4da9...ad81</span></li></ul></article></section>`;
    const mediaView = document.getElementById("eventMediaView");
    mediaView.id = "eventMediaPanel";
    mediaView.setAttribute("role", "tabpanel");
    const mediaTabs = [...document.querySelectorAll("[data-event-media]")];
    mediaTabs.forEach((button, index) => {
        button.id = `eventMedia${capitalize(button.dataset.eventMedia)}Tab`;
        button.setAttribute("aria-controls", "eventMediaPanel");
        button.tabIndex = index === 0 ? 0 : -1;
        button.addEventListener("click", () => selectEventMedia(button.dataset.eventMedia));
        button.addEventListener("keydown", event => {
            if (!["ArrowLeft", "ArrowRight"].includes(event.key)) return;
            event.preventDefault();
            const direction = event.key === "ArrowRight" ? 1 : -1;
            const target = mediaTabs[(index + direction + mediaTabs.length) % mediaTabs.length];
            selectEventMedia(target.dataset.eventMedia);
            target.focus();
        });
    });
    mediaView.setAttribute("aria-labelledby", mediaTabs[0].id);
}

function renderFireballContextFrame(position, index) {
    const captureId = 84218 + index;
    const seconds = index * 10 - 20;
    const offset = `${seconds >= 0 ? "+" : ""}${seconds.toFixed(1)}s`;
    if (captureId > 84220) {
        return `<div class="context-frame unavailable" aria-label="${position}, capture ${captureId}, outside bounded Archive"><span class="context-frame-placeholder">Not loaded</span><span><strong>Context ${position}</strong><small>Capture ${captureId} / retained evidence</small></span><span>${offset}</span></div>`;
    }
    return `<a class="context-frame${position === "N" ? " reference" : ""}" href="dashboard.html?capture=${captureId}"><img src="${index % 2 ? "assets/w6-current.jpg" : "assets/hualapai-night.jpg"}" alt="Context frame ${position}"><span><strong>${position === "N" ? "Candidate endpoint" : `Context ${position}`}</strong><small>Capture ${captureId}</small></span><span>${offset}</span></a>`;
}

function renderGenericEventDetail(container, event) {
    const stateClass = event.review === "pending" ? "warning" : "success";
    container.innerHTML = `
        <header class="event-detail-heading"><div><p class="eyebrow">Events / ${event.reviewLabel} / HVO Main Fisheye</p><h1>${event.title}</h1><p>${event.localTime} / ${event.time} / centered evidence retained</p></div><div class="event-detail-status"><span class="status-icon ${stateClass}"></span><span>${event.classificationLabel}</span></div></header>
        <section class="event-media-layout"><div class="event-media-view"><img src="${event.image}" alt="Centered evidence preview for ${event.title}"><i class="event-track"></i><span class="event-track-label">${event.duration} / local angular path</span></div><aside class="event-context"><h2>Candidate evidence</h2><p>${event.summary}</p><dl class="product-provenance"><div><dt>Review</dt><dd>${event.reviewLabel}</dd></div><div><dt>Confidence</dt><dd>${event.confidence}</dd></div><div><dt>Duration</dt><dd>${event.duration}</dd></div><div><dt>Classification</dt><dd>${event.classificationLabel}</dd></div><div class="full"><dt>Technical event ID</dt><dd>${event.technicalId}</dd></div></dl><a class="text-button" href="gallery.html?from=${event.date}T19:00&amp;to=${shiftDate(event.date, 1)}T19:00">Captures from this observing day</a></aside></section>
        <section class="science-grid"><article class="science-panel"><header><h2>Observed measurements</h2><span>Direct camera evidence</span></header><dl class="science-facts"><div><dt>Duration <i class="science-value-kind">Measured</i></dt><dd>${event.duration}</dd></div><div><dt>Angular path <i class="science-value-kind unresolved">Not included</i></dt><dd>Bounded prototype record</dd></div></dl></article><article class="science-panel"><header><h2>Classification</h2><span>LogicHost review state</span></header><dl class="science-facts"><div><dt>Result <i class="science-value-kind derived">Derived</i></dt><dd>${event.classificationLabel}</dd></div><div><dt>Confidence <i class="science-value-kind derived">Derived</i></dt><dd>${event.confidence}</dd></div></dl></article><article class="science-panel"><header><h2>Physical trajectory</h2><span>Single-camera limitation</span></header><dl class="science-facts"><div><dt>Physical speed <i class="science-value-kind unresolved">Not resolved</i></dt><dd>Requires correlation</dd></div><div><dt>Event altitude <i class="science-value-kind unresolved">Not resolved</i></dt><dd>Requires correlation</dd></div><div><dt>Ground track <i class="science-value-kind unresolved">Not resolved</i></dt><dd>Requires correlation</dd></div></dl></article></section>
        <section class="event-review-grid"><article class="event-detail-panel archive-panel"><header><div><h2>Review &amp; classification</h2><p>LogicHost owns the authoritative assessment and immutable review history.</p></div><span class="state-chip ${stateClass}">${event.reviewLabel}</span></header><div class="event-review-actions">${event.review === "pending" ? `<button class="button secondary" type="button" data-archive-action="A review revision would be appended; local evidence remains immutable.">Review candidate</button>` : `<button class="button secondary" type="button" data-archive-action="A review revision editor would open; existing history remains immutable.">Revise classification</button>`}<a class="button secondary" href="day.html?date=${event.date}">Observing day</a></div></article><article class="event-detail-panel archive-panel"><header><div><h2>Evidence provenance</h2><p>Technical identity remains secondary.</p></div><span>Verified</span></header><ul class="evidence-list"><li><strong>Event ID</strong><span>${event.technicalId}</span></li><li><strong>Camera</strong><span>HVO Main Fisheye</span></li><li><strong>Detector</strong><span>Meteor and Fireball Detection 1.4</span></li><li><strong>Review authority</strong><span>LogicHost</span></li></ul></article></section>`;
}

function selectEventMedia(media) {
    document.querySelectorAll("[data-event-media]").forEach(button => { const active = button.dataset.eventMedia === media; button.classList.toggle("active", active); button.setAttribute("aria-selected", String(active)); button.tabIndex = active ? 0 : -1; if (active) document.getElementById("eventMediaPanel").setAttribute("aria-labelledby", button.id); });
    const view = document.getElementById("eventMediaPanel");
    view.className = `event-media-view ${media}`;
    const image = view.querySelector("img");
    image.alt = `${media === "animation" ? "Centered animation" : media === "stack" ? "Maximum-intensity path composite" : "Detection mask"} for 31 August Fireball`;
    image.style.filter = media === "mask" ? "grayscale(1) contrast(2.2) brightness(.55)" : media === "stack" ? "contrast(1.55) brightness(1.1)" : "contrast(1.15)";
    view.querySelector(".event-track-label").textContent = media === "animation" ? "2.14s / northeast path" : media === "stack" ? "Maximum-intensity composite / 5 frames" : "Detector support mask / confidence 0.94";
}

let archiveToastTimer;
function showArchiveToast(message) {
    const toast = document.getElementById("archiveToast");
    if (!toast) return;
    document.getElementById("archiveToastText").textContent = message;
    toast.hidden = false;
    clearTimeout(archiveToastTimer);
    archiveToastTimer = setTimeout(() => { toast.hidden = true; }, 4000);
}

document.addEventListener("click", event => {
    const action = event.target.closest("[data-archive-action]");
    if (action) showArchiveToast(action.dataset.archiveAction);
});

function productTypeName(type) {
    return ({ timelapse: "Timelapse", "star-trail": "Star trail", keogram: "Keogram", summary: "Daily summary" })[type] ?? type;
}

function friendlyDate(value) {
    return new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "long", year: "numeric", timeZone: "UTC" }).format(new Date(`${value}T00:00:00Z`));
}

function validDate(value) {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(value ?? "")) return null;
    const parsed = new Date(`${value}T00:00:00Z`);
    return Number.isNaN(parsed.valueOf()) || parsed.toISOString().slice(0, 10) !== value ? null : value;
}

function shiftDate(value, days) {
    const date = new Date(`${value}T00:00:00Z`);
    date.setUTCDate(date.getUTCDate() + days);
    return date.toISOString().slice(0, 10);
}

function capitalize(value) { return value.charAt(0).toUpperCase() + value.slice(1); }

initializeArchiveCalendar();
initializeObservingDay();
initializeProductLibrary();
initializeProductDetail();
initializeEvents();
initializeEventDetail();
