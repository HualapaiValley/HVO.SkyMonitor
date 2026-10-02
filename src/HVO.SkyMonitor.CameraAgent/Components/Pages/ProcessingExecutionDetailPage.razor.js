export function focusTab(panel, id) {
    panel.closest('.detail-card')?.querySelector(`#${id}`)?.focus();
}
