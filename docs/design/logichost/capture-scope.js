const captureParameters = new URLSearchParams(window.location.search);
const observatory = captureParameters.get("observatory");
const camera = captureParameters.get("camera");

if (observatory === "volcano" && camera === "zenith") {
    document.querySelector("#capture-context").textContent = "Volcano Station / Zenith All-Sky";
    document.querySelector("#capture-site").value = "volcano";
    document.querySelector("#capture-camera").value = "zenith";
    document.querySelector("#capture-count").textContent = "2 matching / Zenith All-Sky";
    document.querySelector("#capture-scope-all").removeAttribute("aria-current");
    document.querySelector("#capture-scope-volcano").setAttribute("aria-current", "location");
    document.querySelectorAll('[data-observatory]:not([data-observatory="volcano"])').forEach(card => card.hidden = true);
    document.querySelectorAll(".capture-detail-link").forEach(link => {
        link.href = "app-capture-detail.html?observatory=volcano&camera=zenith";
    });
}
