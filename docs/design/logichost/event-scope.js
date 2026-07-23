const eventParameters = new URLSearchParams(window.location.search);

if (eventParameters.get("observatory") === "volcano") {
    document.querySelector("#event-context").textContent = "Volcano Station";
    document.querySelector("#event-count").textContent = "1 current Volcano assessment / Owner actions enabled";
    document.querySelectorAll('[data-observatory]:not([data-observatory="volcano"])').forEach(row => row.hidden = true);
}
