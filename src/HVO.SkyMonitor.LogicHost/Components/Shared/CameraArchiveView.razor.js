import { attach as attachRefresh, detach as detachRefresh } from "./OperationsScopeShell.razor.js";
export { authorizeRawDownload } from "../Pages/Operations/OperationsCaptureDetail.razor.js";
const handlers = new WeakMap();
export function attach(root, receiver) {
    if (!root?.isConnected) return;
    detach(root);
    attachRefresh(root, receiver);
    const remember = event => {
        const link = event.target.closest("a[data-capture]");
        if (!link || !root.contains(link)) return;
        const url = new URL(location.href);
        url.searchParams.set("focus", link.dataset.capture);
        history.replaceState(history.state, "", url);
    };
    root.addEventListener("click", remember);
    handlers.set(root, remember);
}
export function restoreArchiveFocus(root, requested) {
    const focus = requested || new URL(location.href).searchParams.get("focus");
    if (!/^[0-9a-f-]{36}$/i.test(focus || "")) return;
    // Run after the scope shell's heading restoration.
    requestAnimationFrame(() => requestAnimationFrame(() => {
        if (!root?.isConnected) return;
        const target = root.querySelector(`#archive-capture-${CSS.escape(focus)}`) || root.querySelector("#archive-heading");
        target?.focus({ preventScroll: true });
        target?.scrollIntoView({ block: "nearest", inline: "nearest" });
    }));
}
export function detach(root) {
    if (!root) return;
    detachRefresh(root);
    const remember = handlers.get(root);
    if (remember) root.removeEventListener("click", remember);
    handlers.delete(root);
}
