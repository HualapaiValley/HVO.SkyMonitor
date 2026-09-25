const bindings = new WeakMap();

export async function openTechnicalEvidence(root) {
    const evidence = root?.closest('.detail-page')?.querySelector('#technical-evidence');
    if (!evidence) return;
    if (document.fullscreenElement && root.contains(document.fullscreenElement)) {
        await document.exitFullscreen();
        // Let the fullscreen-exit listener restore its trigger before focusing the evidence tabs.
        await new Promise(resolve => requestAnimationFrame(resolve));
    }
    if (!root.isConnected || !evidence.isConnected) return;
    evidence.scrollIntoView({ block: 'start' });
    (evidence.querySelector('.evidence-tabs [aria-pressed="true"]') ?? evidence).focus({ preventScroll: true });
}

// Starts a same-origin attachment download without leaving the page.
export function downloadUrl(url) {
    const link = document.createElement('a');
    link.href = url;
    link.download = '';
    link.hidden = true;
    document.body.append(link);
    link.click();
    link.remove();
}

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
