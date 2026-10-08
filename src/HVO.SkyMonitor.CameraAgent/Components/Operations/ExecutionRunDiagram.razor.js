const instances = new WeakMap();

export function observe(root, viewport, trigger, reference) {
    dispose(root);
    let active = true;
    let wasFullscreen = false;
    let last = '';
    const report = () => {
        if (!active || !root.isConnected) return;
        const fullscreen = document.fullscreenElement === root;
        const key = `${viewport.clientWidth}:${viewport.clientHeight}:${fullscreen}`;
        if (key !== last) {
            last = key;
            reference.invokeMethodAsync('ViewportChanged', viewport.clientWidth, viewport.clientHeight, fullscreen).catch(() => {});
        }
        if (wasFullscreen && !fullscreen && trigger.isConnected) trigger.focus({ preventScroll: true });
        wasFullscreen = fullscreen;
    };
    const observer = new ResizeObserver(report);
    const keydown = event => {
        if (event.key === 'Escape' && document.fullscreenElement === root) {
            event.preventDefault();
            document.exitFullscreen().catch(() => {});
        }
    };
    observer.observe(viewport);
    document.addEventListener('fullscreenchange', report);
    root.addEventListener('keydown', keydown);
    instances.set(root, () => {
        active = false;
        observer.disconnect();
        document.removeEventListener('fullscreenchange', report);
        root.removeEventListener('keydown', keydown);
    });
    root.dataset.interactive = 'true';
    report();
}

export async function toggleFullscreen(root) {
    if (document.fullscreenElement === root) await document.exitFullscreen();
    else if (root.requestFullscreen) await root.requestFullscreen();
}

export function resetScroll(viewport) {
    viewport.scrollTo({ left: 0, top: 0, behavior: 'instant' });
}

export function dispose(root) {
    instances.get(root)?.();
    instances.delete(root);
    if (document.fullscreenElement === root) document.exitFullscreen().catch(() => {});
}
