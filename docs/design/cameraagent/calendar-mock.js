for (const link of document.querySelectorAll(".archive-day a")) {
    const date = link.querySelector("time")?.dateTime;
    if (!date || date === "2026-07-21") continue;

    const placeholder = document.createElement("span");
    placeholder.className = "archive-day__static";
    placeholder.setAttribute("aria-label", `${link.getAttribute("aria-label")}; date selection is not simulated in this static study`);
    placeholder.innerHTML = link.innerHTML;
    link.replaceWith(placeholder);
}
