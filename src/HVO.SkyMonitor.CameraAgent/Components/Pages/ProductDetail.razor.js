const bindings = new WeakMap();
export function bind(viewer, trigger, artifactId, reference) {
    dispose(viewer);
    let active = true;
    let wasFullscreen = false;
    const change = () => {
        if (!active) return;
        const fullscreen = document.fullscreenElement === viewer;
        reference.invokeMethodAsync('FullscreenChanged', artifactId, fullscreen).catch(() => {});
        if (wasFullscreen && !fullscreen && trigger.isConnected) trigger.focus({ preventScroll: true });
        wasFullscreen = fullscreen;
    };
    const keydown = event => {
        if (event.key === 'Escape' && document.fullscreenElement === viewer) {
            event.preventDefault();
            document.exitFullscreen().catch(() => {});
        }
    };
    document.addEventListener('fullscreenchange', change);
    viewer.addEventListener('keydown', keydown);
    bindings.set(viewer, () => { active = false; document.removeEventListener('fullscreenchange', change); viewer.removeEventListener('keydown', keydown); });
    viewer.dataset.interactive = 'true';
}
export async function toggle(viewer) {
    if (document.fullscreenElement === viewer) await document.exitFullscreen();
    else if (viewer.requestFullscreen) await viewer.requestFullscreen();
}
export function dispose(viewer) {
    bindings.get(viewer)?.();
    bindings.delete(viewer);
    if (document.fullscreenElement === viewer) document.exitFullscreen().catch(() => {});
}
