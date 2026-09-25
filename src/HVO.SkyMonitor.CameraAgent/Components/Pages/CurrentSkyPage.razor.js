const bindings = new WeakMap();

// Mirrors the prototype's figure.requestFullscreen(): the same <figure> (base image, SVG layers, caption) is
// promoted, so the enlarged view is exactly the inline selection with no second fetch or stretch.
export async function requestFullScreen(figure) {
    if (!figure?.isConnected || !figure.requestFullscreen) return false;
    if (document.fullscreenElement === figure) return true;
    const trigger = figure.querySelector('.image-tool');
    const restoreFocus = () => {
        if (document.fullscreenElement === figure) return;
        document.removeEventListener('fullscreenchange', restoreFocus);
        if (trigger?.isConnected) trigger.focus({ preventScroll: true });
    };
    try {
        document.addEventListener('fullscreenchange', restoreFocus);
        await figure.requestFullscreen();
        return true;
    } catch {
        document.removeEventListener('fullscreenchange', restoreFocus);
        return false;
    }
}

export async function bindLayerToggles(root, width, height) {
    const image = root?.querySelector(".sky-layer-canvas > img");
    if (!image || !Number.isSafeInteger(width) || width <= 0 || !Number.isSafeInteger(height) || height <= 0) return "unavailable";
    if (!image.complete) {
        await new Promise(resolve => {
            const waiting = new AbortController();
            const finish = () => { clearTimeout(timeout); waiting.abort(); resolve(); };
            const timeout = setTimeout(finish, 5000);
            image.addEventListener("load", finish, { once: true, signal: waiting.signal });
            image.addEventListener("error", finish, { once: true, signal: waiting.signal });
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
