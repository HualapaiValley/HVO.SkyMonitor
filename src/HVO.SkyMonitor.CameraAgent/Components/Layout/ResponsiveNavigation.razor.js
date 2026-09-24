const panels = new WeakMap();

export function initialize(panel, toggle, breakpoint, receiver) {
    const media = matchMedia(`(max-width: ${breakpoint}px)`);
    const notify = () => receiver.invokeMethodAsync('SetExpanded', panel.matches(':modal')).catch(() => {});
    let returnFocus = false;
    const resize = () => {
        returnFocus = panel.matches(':modal') && media.matches;
        if (panel.matches(':modal')) panel.close();
        panel.toggleAttribute('open', !media.matches);
        notify();
    };
    const closed = () => {
        // The native close event is queued. Restore desktop visibility after it, too.
        if (!media.matches) panel.setAttribute('open', '');
        if (returnFocus && media.matches && toggle.isConnected) toggle.focus({ preventScroll: true });
        returnFocus = false;
        notify();
    };
    const keydown = event => {
        if (event.key !== 'Tab' || !panel.matches(':modal')) return;
        const items = [...panel.querySelectorAll('a[href], button:not(:disabled), summary, input:not(:disabled), [tabindex="0"]')]
            .filter(element => element.getClientRects().length && !element.closest('details:not([open]) > :not(summary)'));
        const first = items[0];
        const last = items.at(-1);
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
    };
    const backdrop = event => { if (event.target === panel && panel.matches(':modal')) close(panel); };
    // A layout can disappear before .NET disposal reaches JS. Release media listeners locally.
    const observer = new MutationObserver(() => { if (!panel.isConnected) dispose(panel); });
    observer.observe(document.body, { childList: true, subtree: true });
    panel.addEventListener('close', closed);
    panel.addEventListener('keydown', keydown);
    panel.addEventListener('click', backdrop);
    media.addEventListener('change', resize);
    panels.set(panel, { media, resize, closed, keydown, backdrop, notify, observer, opened: () => { returnFocus = true; } });
    resize();
}

export function open(panel) {
    const state = panels.get(panel);
    if (!state?.media.matches || panel.matches(':modal')) return;
    if (panel.open) panel.close();
    // Native modality makes every background subtree inert, including the site header.
    panel.showModal();
    state.opened();
    panel.querySelector('button')?.focus();
    state.notify();
}

export function close(panel) {
    if (panel?.matches(':modal')) panel.close();
}

export function dispose(panel) {
    const state = panels.get(panel);
    if (!state) return;
    state.observer.disconnect();
    panel.removeEventListener('close', state.closed);
    panel.removeEventListener('keydown', state.keydown);
    panel.removeEventListener('click', state.backdrop);
    state.media.removeEventListener('change', state.resize);
    if (panel.open) panel.close();
    panels.delete(panel);
}
