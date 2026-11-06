import { attach as attachRefresh, detach as detachRefresh } from "./OperationsScopeShell.razor.js";
const handlers = new WeakMap();
export async function hasSession(root, expectedUser) {
    const statusPath = "/api/v1.0/status/detailed";
    const options = { credentials: "same-origin", cache: "no-store", redirect: "error", headers: { "Accept": "application/json" } };
    try {
        const status = await fetch(statusPath, options);
        if (!status.ok) { await status.body?.cancel(); return false; }
        const identity = await status.json();
        if (expectedUser && (identity.user ?? identity.User) !== expectedUser) return false;
        const path = root?.dataset.sessionCheck;
        if (!path || path === statusPath) return true;
        const response = await fetch(path, options);
        await response.body?.cancel();
        return response.ok;
    } catch { return false; }
}
function clearProtectedMetadata(root) {
    const content = root?.querySelector(".central-protected");
    if (!content) return;
    // Preserve DOM node identities for Blazor's next diff, while removing protected facts even if the
    // circuit cannot receive SuspendAsync yet. No evidence is cached by this module.
    const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) walker.currentNode.data = "";
    content.querySelectorAll("[href]").forEach(element => element.removeAttribute("href"));
    content.querySelectorAll("[id]").forEach(element => element.removeAttribute("id"));
    content.querySelectorAll("[data-evidence-focus]").forEach(element => element.removeAttribute("data-evidence-focus"));
    content.querySelectorAll("input").forEach(element => { element.value = ""; element.removeAttribute("value"); });
    content.querySelectorAll("select").forEach(element => { element.selectedIndex = -1; });
    content.querySelectorAll("button,input,select").forEach(element => { element.disabled = true; });
}
export function attach(root, receiver) {
    if (!root?.isConnected) return;
    detach(root);
    const suspend = async () => {
        clearProtectedMetadata(root);
        try { await receiver.invokeMethodAsync("SuspendAsync"); } catch { /* Reconnect revalidates before restoring evidence. */ }
    };
    attachRefresh(root, receiver);
    const reconnect = document.getElementById("components-reconnect-modal");
    const disconnected = event => { if (event.detail?.state !== "hide") void suspend(); };
    const restored = event => { if (event.persisted) void receiver.invokeMethodAsync("RefreshAsync").catch(() => {}); };
    window.addEventListener("offline", suspend);
    window.addEventListener("pagehide", suspend);
    window.addEventListener("pageshow", restored);
    reconnect?.addEventListener("components-reconnect-state-changed", disconnected);
    const remember = event => {
        const link = event.target.closest("a[data-evidence-focus]");
        if (!link || !root.contains(link)) return;
        const url = new URL(location.href);
        url.searchParams.set("focus", link.dataset.evidenceFocus);
        history.replaceState(history.state, "", url);
    };
    root.addEventListener("click", remember);
    const observer = new MutationObserver(() => { if (!root.isConnected) detach(root); });
    handlers.set(root, { remember, suspend, disconnected, reconnect, restored, observer });
    observer.observe(document.body, { childList: true, subtree: true });
}
export function restoreFocus(root, requested) {
    const focus = requested || new URL(location.href).searchParams.get("focus") || "central-heading";
    if (!/^central-[a-z0-9-]{1,80}$/.test(focus)) return;
    requestAnimationFrame(() => requestAnimationFrame(() => {
        if (!root?.isConnected) return;
        const target = root.querySelector(`#${CSS.escape(focus)}`) || root.querySelector("#central-heading");
        target?.focus({ preventScroll: true });
        target?.scrollIntoView({ block: "nearest", inline: "nearest" });
    }));
}
export function detach(root) {
    if (!root) return;
    detachRefresh(root);
    const handler = handlers.get(root);
    if (handler) {
        handler.observer.disconnect();
        root.removeEventListener("click", handler.remember);
        window.removeEventListener("offline", handler.suspend);
        window.removeEventListener("pagehide", handler.suspend);
        window.removeEventListener("pageshow", handler.restored);
        handler.reconnect?.removeEventListener("components-reconnect-state-changed", handler.disconnected);
    }
    handlers.delete(root);
}
