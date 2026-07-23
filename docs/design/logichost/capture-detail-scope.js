const detailParameters = new URLSearchParams(window.location.search);

if (detailParameters.get("observatory") === "volcano" && detailParameters.get("camera") === "zenith") {
    document.querySelectorAll(".capture-return").forEach(link => {
        link.href = "app-captures.html?observatory=volcano&camera=zenith";
    });
}
