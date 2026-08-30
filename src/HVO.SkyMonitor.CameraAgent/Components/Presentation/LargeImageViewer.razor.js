const handlers = new WeakMap();

export function connect(dialog, dotNetReference) {
    disconnect(dialog);
    const cancel = event => {
        event.preventDefault();
        dotNetReference.invokeMethodAsync("CloseFromJavaScriptAsync");
    };
    const keydown = event => {
        if (event.key !== "Tab" || !dialog.open) {
            return;
        }
        const controls = Array.from(dialog.querySelectorAll(
            "button:not(:disabled), a[href], input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex='-1'])"));
        if (controls.length === 0) {
            event.preventDefault();
            dialog.focus();
            return;
        }
        const first = controls[0];
        const last = controls[controls.length - 1];
        if (!dialog.contains(document.activeElement)) {
            event.preventDefault();
            (event.shiftKey ? last : first).focus();
        } else if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
        }
    };
    handlers.set(dialog, { cancel, keydown });
    dialog.addEventListener("cancel", cancel);
    document.addEventListener("keydown", keydown);
}

export function show(dialog, closeButton) {
    if (dialog && !dialog.open) {
        dialog.showModal();
    }
    closeButton?.focus({ preventScroll: true });
}

export function close(dialog, triggerId) {
    if (dialog?.open) {
        dialog.close();
    }
    if (triggerId) {
        const focusTarget = document.getElementById(triggerId) ?? document.querySelector("main h1");
        focusTarget?.focus({ preventScroll: false });
    }
}

export function disconnect(dialog) {
    const binding = handlers.get(dialog);
    if (binding) {
        dialog.removeEventListener("cancel", binding.cancel);
        document.removeEventListener("keydown", binding.keydown);
        handlers.delete(dialog);
    }
}
