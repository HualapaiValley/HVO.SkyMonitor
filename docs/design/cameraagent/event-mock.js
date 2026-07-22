const eventParameters = new URLSearchParams(window.location.search);
const selectedEventId = eventParameters.get("id") ?? "evt_7Q4M2K";
const eventData = {
    evt_7Q4M2K: { classification: "Meteor-like", confidence: "87%", period: "Night", observed: "Observed 10:17:08-10:17:10 PM HST", duration: "1.8 sec", length: "738 px", at: "22:17:08", preview: "assets/all-sky-night.svg" },
    evt_9P8R5T: { classification: "Aircraft-like", confidence: "78%", period: "Night", observed: "Observed 8:52:41-8:52:44 PM HST", duration: "3.2 sec", length: "680 px", at: "20:52:41", preview: "assets/all-sky-night.svg" },
    evt_3N6V1C: { classification: "Aircraft-like", confidence: "71%", period: "Daylight", observed: "Observed 3:14:12-3:14:16 PM HST", duration: "4.1 sec", length: "812 px", at: "15:14:12", preview: "assets/all-sky-day.svg" }
};

for (const link of document.querySelectorAll(".event-list-item")) {
    const isSelected = link.href.includes(`id=${selectedEventId}`);
    link.classList.toggle("event-list-item--active", isSelected);
    if (isSelected) link.setAttribute("aria-current", "true");
    else link.removeAttribute("aria-current");
}

const selectedEvent = eventData[selectedEventId];
if (selectedEvent) {
    document.querySelector("#event-detail-heading").textContent = `${selectedEvent.classification} track`;
    document.querySelector(".event-detail > header .eyebrow").textContent = `Local provisional / ${selectedEvent.period}`;
    document.querySelector(".event-detail > header div > span").textContent = selectedEvent.observed;
    const values = document.querySelectorAll(".event-facts dd");
    values[0].textContent = selectedEvent.classification;
    values[1].textContent = selectedEvent.confidence;
    values[2].textContent = selectedEvent.duration;
    values[3].textContent = selectedEvent.length;
    values[4].textContent = selectedEvent.period;
    const overlay = document.querySelector(".event-hero img");
    overlay.alt = `Display overlay showing a measured diagonal ${selectedEvent.classification.toLowerCase()} track with start and end markers`;
    document.querySelector(".event-detail > header > a").href = `frames.html?date=2026-07-21&at=${selectedEvent.at}`;
    document.querySelector(".event-products a:nth-child(3)").href = selectedEvent.preview;
}

if (window.location.hash === "#event-detail" && eventParameters.has("id")) {
    document.querySelector("#event-detail")?.focus();
}
