document.querySelectorAll(".source-options input[type=radio], .mode-selector input[type=radio]").forEach((input) => {
    input.addEventListener("change", () => {
        const group = input.closest(".source-options, .mode-selector");
        const activeClass = group.classList.contains("source-options") ? "source-option--active" : "mode-selector--active";
        group.querySelectorAll("label").forEach((label) => label.classList.remove(activeClass));
        input.closest("label").classList.add(activeClass);
        if (group.classList.contains("source-options")) {
            const values = [...document.querySelectorAll(".source-selector .settings-facts dd")];
            const source = input.closest("label").querySelector("strong").textContent;
            if (source === "Physical camera") {
                values[0].textContent = "Unavailable / no adapter installed";
                values[1].textContent = "Required / not selected";
                values[2].textContent = "No matching physical device";
            } else if (source === "Test generator") {
                values[0].textContent = "random-image@1 / developer only";
                values[1].textContent = "Not applicable";
                values[2].textContent = "Synthetic fixture";
            } else {
                values[0].textContent = "virtual-sky@2";
                values[1].textContent = "Not applicable to VirtualSky";
                values[2].textContent = "No physical adapter installed";
            }
        }
    });
});

document.querySelectorAll(".policy-switch input[type=checkbox]").forEach((input) => {
    const update = () => {
        if (input.disabled) return;
        input.closest(".policy-switch").querySelector("span").textContent = input.checked ? "Enabled" : "Disabled";
    };
    input.addEventListener("change", update);
    update();
});

const overlayInputs = document.querySelectorAll(".overlay-layer-list input[type=checkbox]");
if (overlayInputs.length > 0) {
    const updateOverlayCount = () => {
        const count = [...overlayInputs].filter((input) => input.checked).length;
        const draftCount = document.querySelector(".draft-bar dl div:first-child dd");
        const footerCount = document.querySelector(".window-footer__inner span:nth-child(2)");
        if (draftCount) draftCount.textContent = `${count} enabled`;
        if (footerCount) footerCount.textContent = `${count} enabled layers`;
    };
    overlayInputs.forEach((input) => input.addEventListener("change", updateOverlayCount));
    updateOverlayCount();
}

document.querySelectorAll(".confirmation-check input[type=checkbox]").forEach((input) => {
    const action = input.closest(".settings-card").querySelector(".setup-button--danger");
    const update = () => action.toggleAttribute("disabled", !input.checked);
    input.addEventListener("change", update);
    update();
});

document.querySelectorAll(".profile-list-item").forEach((button) => {
    if (button instanceof HTMLButtonElement) {
        button.disabled = true;
        button.setAttribute("aria-pressed", button.classList.contains("profile-list-item--active") ? "true" : "false");
    }
});

document.querySelectorAll(".control-table, .style-table").forEach((table) => table.setAttribute("tabindex", "0"));
