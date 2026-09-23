const bindings = new WeakMap();

export async function bindLayerToggles(root, width, height) {
    const image = root?.querySelector(".sky-layer-canvas > img");
    if (!image || !Number.isSafeInteger(width) || width <= 0 || !Number.isSafeInteger(height) || height <= 0) return "unavailable";
    if (!image.complete) {
        await new Promise(resolve => {
            image.addEventListener("load", resolve, { once: true });
            image.addEventListener("error", resolve, { once: true });
        });
    }
    if (root.querySelector(".sky-layer-canvas > img") !== image || !image.complete || !image.naturalWidth || !image.naturalHeight) return "unavailable";
    if (image.naturalWidth !== width || image.naturalHeight !== height) return "mismatch";
    bindings.get(root)?.abort();
    const controller = new AbortController();
    bindings.set(root, controller);
    for (const toggle of root.querySelectorAll("[data-layer-target]")) {
        const apply = () => {
            const group = root.querySelector(`#${CSS.escape(toggle.dataset.layerTarget)}`);
            if (group) {
                group.removeAttribute("display");
                group.style.display = toggle.checked ? "" : "none";
            }
        };
        toggle.addEventListener("change", apply, { signal: controller.signal });
        apply();
    }
    return "valid";
}
