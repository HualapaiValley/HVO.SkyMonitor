const frameParameters = new URLSearchParams(window.location.search);
const frameDate = "2026-07-21";
const requestedPeriod = frameParameters.get("period");
const requestedTime = frameParameters.get("at") ?? (requestedPeriod === "night" ? "23:48" : "14:10");
const requestedCivilDate = frameParameters.get("civil");
const requestedHour = Number.parseInt(requestedTime.slice(0, 2), 10);
const requestedMinute = Number.parseInt(requestedTime.slice(3, 5), 10);
const requestedMinutes = requestedHour * 60 + requestedMinute;
const dateValue = new Date(`${frameDate}T12:00:00Z`);
const displayDate = new Intl.DateTimeFormat("en", { weekday: "long", month: "long", day: "numeric", timeZone: "UTC" }).format(dateValue);
const requestedClock = requestedTime.length === 5 ? `${requestedTime}:00` : requestedTime;
const selectedCivilDay = requestedCivilDate === "2026-07-22" || (!requestedCivilDate && requestedMinutes < 354) ? "02" : "01";
const selectedInstant = new Date(`2000-01-${selectedCivilDay}T${requestedClock}Z`);
const skyDayStart = new Date("2000-01-01T05:54:00Z");
const sunset = new Date("2000-01-01T19:14:00Z");
const skyDayEnd = new Date("2000-01-02T05:55:00Z");
const periodForInstant = instant => instant >= sunset && instant < skyDayEnd ? "night" : "daylight";
const displayedPeriod = requestedPeriod ?? periodForInstant(selectedInstant);
const showSeconds = requestedTime.length > 5 && !requestedTime.endsWith(":00");
const timeFormatter = new Intl.DateTimeFormat("en", { hour: "numeric", minute: "2-digit", second: showSeconds ? "2-digit" : undefined, hour12: true, timeZone: "UTC" });
const displayTime = timeFormatter.format(selectedInstant);
const formatQueryTime = instant => `${String(instant.getUTCHours()).padStart(2, "0")}:${String(instant.getUTCMinutes()).padStart(2, "0")}:${String(instant.getUTCSeconds()).padStart(2, "0")}`;
const formatCivilDate = instant => `2026-07-${instant.getUTCDate() === 2 ? "22" : "21"}`;
const formatFrameUrl = instant => `frames.html?date=${frameDate}&civil=${formatCivilDate(instant)}&at=${formatQueryTime(instant)}`;
const clampToSkyDay = instant => new Date(Math.min(skyDayEnd.getTime(), Math.max(skyDayStart.getTime(), instant.getTime())));
const disableNavigationLink = link => {
    const placeholder = document.createElement("span");
    placeholder.className = "frame-adjacent-disabled";
    placeholder.setAttribute("aria-disabled", "true");
    placeholder.textContent = link.textContent;
    link.replaceWith(placeholder);
};

document.querySelector(".frame-heading .eyebrow").textContent = `Sky day / ${displayDate}`;
document.querySelector(".frame-breadcrumb a:nth-of-type(2)").textContent = displayDate.replace(",", "");
document.querySelector("#frame-time").value = requestedTime.length === 5 ? `${requestedTime}:00` : requestedTime;
document.querySelector("#selected-frame-heading").textContent = `${displayTime} HST`;
document.querySelector(".frame-detail__head .eyebrow").textContent = `Selected frame / ${displayedPeriod}`;

const selectedImage = document.querySelector(".frame-canvas img");
selectedImage.src = displayedPeriod === "night" ? "assets/all-sky-night.svg" : "assets/all-sky-day.svg";
selectedImage.alt = `Published ${displayedPeriod} all-sky frame near ${displayTime}`;
document.querySelector(".frame-detail__head div > span").textContent = "Nearest modeled published frame to the requested local time";

const adjacentLinks = document.querySelectorAll(".frame-detail__head nav a");
for (const [index, offset] of [-1, 1].entries()) {
    const instant = clampToSkyDay(new Date(selectedInstant.getTime() + offset * 60_000));
    if (instant.getTime() === selectedInstant.getTime()) {
        disableNavigationLink(adjacentLinks[index]);
    } else {
        adjacentLinks[index].href = formatFrameUrl(instant);
    }
}

const metadata = document.querySelectorAll(".frame-metadata dd");
metadata[0].textContent = `${displayTime} HST`;
metadata[1].textContent = "7 sec later";
if (displayedPeriod === "night") {
    metadata[2].textContent = "250 ms";
    metadata[3].textContent = "8 frames";
    metadata[4].textContent = "Mostly clear";
    metadata[5].textContent = "14%";
}

const eventNote = document.querySelector(".frame-event-note");
eventNote.querySelector("strong").textContent = "3 candidates in this sky day";
eventNote.querySelector("p").textContent = "Use the event browser for candidate times and public-safe detail.";
eventNote.querySelector("a").href = "events.html";
eventNote.querySelector("a").textContent = "Browse events →";

const imageLink = document.querySelectorAll(".frame-open-image")[0];
imageLink.href = selectedImage.src;

const nearbyOffsets = [-90, -60, -30, 0, 30, 60, 90];
const nearbyInstants = [...new Set(nearbyOffsets.map(offset => clampToSkyDay(new Date(selectedInstant.getTime() + offset * 60_000)).getTime()))]
    .map(value => new Date(value));
while (nearbyInstants.length < 7) {
    const next = new Date(nearbyInstants.at(-1).getTime() + 30 * 60_000);
    if (next <= skyDayEnd) nearbyInstants.push(next);
    else nearbyInstants.unshift(new Date(nearbyInstants[0].getTime() - 30 * 60_000));
}
nearbyInstants.sort((left, right) => left - right);
const activeThumbnailIndex = nearbyInstants.findIndex(instant => instant.getTime() === selectedInstant.getTime());
for (const [index, thumbnail] of [...document.querySelectorAll(".frame-filmstrip a")].entries()) {
    const instant = nearbyInstants[index];
    const period = periodForInstant(instant);
    const queryTime = formatQueryTime(instant);
    const time = thumbnail.querySelector("time");
    const image = thumbnail.querySelector("img");
    thumbnail.href = formatFrameUrl(instant);
    thumbnail.classList.toggle("frame-thumb--active", index === activeThumbnailIndex);
    if (index === activeThumbnailIndex) thumbnail.setAttribute("aria-current", "true");
    else thumbnail.removeAttribute("aria-current");
    time.dateTime = `${formatCivilDate(instant)}T${queryTime}-10:00`;
    time.textContent = timeFormatter.format(instant);
    image.src = period === "night" ? "assets/all-sky-night.svg" : "assets/all-sky-day.svg";
    image.alt = "";
    thumbnail.querySelector("span:not(.frame-thumb__event)").textContent = index === activeThumbnailIndex ? "Selected" : "Published frame";
    thumbnail.querySelector(".frame-thumb__event")?.remove();
}

document.querySelector(".frame-nearby header div > span").textContent = `Seven representative results around ${displayTime}; move the bounded window without loading the full day.`;
const windowLinks = document.querySelectorAll(".frame-nearby header nav a");
for (const [index, offset] of [-120, 120].entries()) {
    const instant = clampToSkyDay(new Date(selectedInstant.getTime() + offset * 60_000));
    if (instant.getTime() === selectedInstant.getTime()) disableNavigationLink(windowLinks[index]);
    else windowLinks[index].href = formatFrameUrl(instant);
}

for (const link of document.querySelectorAll("a[href*='date=2026-07-21']")) {
    link.href = link.href.replace("date=2026-07-21", `date=${frameDate}`);
}

for (const periodLink of document.querySelectorAll(".frame-periods a")) {
    const linkPeriod = new URL(periodLink.href).searchParams.get("period") ?? "full";
    const isSelected = linkPeriod === (requestedPeriod ?? "full");
    periodLink.classList.toggle("frame-pill--active", isSelected);
    if (isSelected) periodLink.setAttribute("aria-current", "page");
    else periodLink.removeAttribute("aria-current");
}

const dewarpedLink = [...document.querySelectorAll("a")].find(link => link.textContent.includes("dewarped view"));
if (dewarpedLink) dewarpedLink.href = `dewarped.html?date=${frameDate}&civil=${formatCivilDate(selectedInstant)}&at=${requestedTime}`;
