const bindings = new WeakMap();

export function attach(root, receiver) {
    if (!root?.isConnected) return;
    detach(root);
    let pending = false;
    const refresh = async () => {
        if (pending || document.visibilityState === "hidden") return;
        pending = true;
        try { await receiver.invokeMethodAsync("RefreshAsync"); }
        catch { /* The reconnect UI owns disconnected-circuit reporting. */ }
        finally { pending = false; }
    };
    const reconnected = event => {
        if (event.detail?.state === "hide") refresh();
    };
    const remember = event => {
        const link = event.target.closest("a[id]");
        if (link && root.contains(link)) {
            history.replaceState({ ...history.state, hvoScopeFocus: link.id }, "");
        }
    };
    window.addEventListener("focus", refresh);
    window.addEventListener("online", refresh);
    document.addEventListener("visibilitychange", refresh);
    const reconnect = document.getElementById("components-reconnect-modal");
    reconnect?.addEventListener("components-reconnect-state-changed", reconnected);
    root.addEventListener("click", remember);
    root.dataset.scopeInteractive = "true";
    // Blazor can remove the element before .NET disposal can resolve its reference.
    // Keep the live node here so window listeners cannot retain a removed scope.
    const observer = new MutationObserver(() => {
        if (!root.isConnected) detach(root);
    });
    bindings.set(root, { refresh, reconnected, remember, reconnect, observer });
    observer.observe(document.body, { childList: true, subtree: true });
}

export function restoreFocus(root, requested) {
    const archiveFocus = /^archive-capture-[0-9a-f-]{36}$/i.test(requested || "") ? requested : null;
    const targets = [archiveFocus, history.state?.hvoScopeFocus,
        requested === "observatories" ? "scope-observatories"
            : requested === "cameras" ? "scope-cameras" : null,
        "scope-heading"];
    const element = targets.filter(target => typeof target === "string")
        .map(target => document.getElementById(target))
        .find(candidate => candidate && root.contains(candidate));
    if (!element) return;
    const picker = element.closest("details");
    if (picker) picker.open = true;
    const list = element.closest(".scope-list");
    if (list && element.matches("h2")) list.scrollTop = 0;
    element.focus({ preventScroll: true });
    element.scrollIntoView({ block: "nearest", inline: "nearest" });
}

export function detach(root) {
    const binding = bindings.get(root);
    if (!binding) return;
    binding.observer.disconnect();
    window.removeEventListener("focus", binding.refresh);
    window.removeEventListener("online", binding.refresh);
    document.removeEventListener("visibilitychange", binding.refresh);
    binding.reconnect?.removeEventListener("components-reconnect-state-changed", binding.reconnected);
    root.removeEventListener("click", binding.remember);
    delete root.dataset.scopeInteractive;
    bindings.delete(root);
}
