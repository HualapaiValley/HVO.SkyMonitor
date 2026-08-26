const bindings = new WeakMap();

export function bindLayerToggles(root) {
    if (!root) {
        return;
    }
    bindings.get(root)?.abort();
    const controller = new AbortController();
    bindings.set(root, controller);
    for (const toggle of root.querySelectorAll("[data-layer-target]")) {
        const apply = () => {
            const group = root.querySelector(`#${CSS.escape(toggle.dataset.layerTarget)}`);
            if (group) {
                group.style.display = toggle.checked ? "" : "none";
            }
        };
        toggle.addEventListener("change", apply, { signal: controller.signal });
        apply();
    }
}

export function selectedLayerIdentities(root) {
    if (!root) {
        return [];
    }
    return Array.from(root.querySelectorAll("[data-layer-identity]:checked"), toggle => toggle.dataset.layerIdentity);
}
