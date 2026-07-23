export function showModal(dialog) {
    if (dialog && !dialog.open) {
        dialog.showModal();
    }
}

export function close(dialog, triggerId, fallbackId) {
    if (dialog?.open) {
        dialog.close();
    }

    const target = (triggerId && document.getElementById(triggerId)) || document.getElementById(fallbackId);
    target?.focus({ preventScroll: false });
}
