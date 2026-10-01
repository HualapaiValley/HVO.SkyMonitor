const bindings = new WeakMap();
const fullScreenBindings = new WeakMap();

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
    (evidence.querySelector('.evidence-tabs [aria-selected="true"]') ?? evidence).focus({ preventScroll: true });
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
export async function requestFullScreen(figure, reference) {
    if (!figure?.isConnected || !figure.requestFullscreen) return false;
    if (document.fullscreenElement === figure) return true;
    const trigger = figure.querySelector('.image-tool');
    const restoreFocus = () => {
        if (document.fullscreenElement === figure) return;
        document.removeEventListener('fullscreenchange', restoreFocus);
        fullScreenBindings.delete(figure);
        reference.invokeMethodAsync('ViewerClosedAsync').then(() => {
            if (trigger?.isConnected) trigger.focus({ preventScroll: true });
        }).catch(() => {});
    };
    try {
        document.addEventListener('fullscreenchange', restoreFocus);
        fullScreenBindings.set(figure, restoreFocus);
        await figure.requestFullscreen();
        return true;
    } catch {
        document.removeEventListener('fullscreenchange', restoreFocus);
        fullScreenBindings.delete(figure);
        return false;
    }
}

export async function exitFullScreen(figure) {
    if (document.fullscreenElement === figure) await document.exitFullscreen();
}

export async function disconnect(root, figure) {
    bindings.get(root)?.abort();
    bindings.delete(root);
    const restore = fullScreenBindings.get(figure);
    if (restore) document.removeEventListener('fullscreenchange', restore);
    fullScreenBindings.delete(figure);
    await exitFullScreen(figure);
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
        const retainedGroup = root.querySelector(`#${CSS.escape(toggle.dataset.layerTarget)}`);
        const swatch = toggle.closest("label")?.querySelector(".layer-swatch");
        const labelPath = retainedGroup?.querySelector('path[fill]:not([fill="none"]):not([fill="#000000"])');
        const glyph = labelPath ?? retainedGroup?.querySelector('[stroke]:not([stroke="#000000"]), [fill]:not([fill="none"]):not([fill="#000000"])');
        const color = labelPath?.getAttribute("fill") ?? glyph?.getAttribute("stroke") ?? glyph?.getAttribute("fill");
        if (swatch && /^#[0-9a-f]{6}$/i.test(color ?? "")) {
            swatch.style.backgroundColor = color;
            swatch.style.borderColor = color;
        }
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
