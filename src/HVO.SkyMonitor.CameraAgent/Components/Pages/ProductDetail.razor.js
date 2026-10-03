export function bind(viewer, trigger, artifactId, reference) {
    let active = true;
    let wasFullscreen = false;
    const report = () => {
        if (!active) return;
        const fullscreen = document.fullscreenElement === viewer;
        if (fullscreen) {
            trigger.title = 'Exit fullscreen';
            trigger.removeAttribute('aria-description');
        }
        reference.invokeMethodAsync('FullscreenChanged', artifactId, fullscreen).catch(() => {});
        if (wasFullscreen && !fullscreen && trigger.isConnected) trigger.focus({ preventScroll: true });
        wasFullscreen = fullscreen;
    };
    const click = () => {
        // Request inside the native click/keyboard activation rather than after a server round trip.
        if (document.fullscreenElement === viewer) document.exitFullscreen().catch(showBlocked);
        else if (viewer.requestFullscreen) viewer.requestFullscreen().catch(showBlocked);
    };
    const showBlocked = () => {
        trigger.title = 'Fullscreen request was blocked; use fit or 100% viewing.';
        trigger.setAttribute('aria-description', trigger.title);
    };
    const showUnavailable = () => {
        trigger.title = 'Fullscreen is unavailable in this browser; use fit or 100% viewing.';
        trigger.setAttribute('aria-label', trigger.title);
    };
    const keydown = event => {
        if (event.key === 'Escape' && document.fullscreenElement === viewer) {
            event.preventDefault();
            document.exitFullscreen().catch(showBlocked);
        }
    };
    if (!viewer.requestFullscreen) {
        trigger.disabled = true;
        showUnavailable();
    } else trigger.addEventListener('click', click);
    document.addEventListener('fullscreenchange', report);
    viewer.addEventListener('keydown', keydown);
    viewer.dataset.interactive = 'true';
    // The returned handle owns listeners directly; cleanup never needs to re-resolve a detached ElementReference.
    return {
        dispose() {
            if (!active) return;
            active = false;
            trigger.removeEventListener('click', click);
            document.removeEventListener('fullscreenchange', report);
            viewer.removeEventListener('keydown', keydown);
            if (document.fullscreenElement === viewer) document.exitFullscreen().catch(() => {});
        }
    };
}
