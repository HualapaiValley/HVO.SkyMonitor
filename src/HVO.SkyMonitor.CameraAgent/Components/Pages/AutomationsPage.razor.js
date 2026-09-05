const guardedDialogs = new WeakSet();

export function showModal(dialog) {
  if (dialog && !dialog.open) {
    if (!guardedDialogs.has(dialog)) {
      dialog.addEventListener("cancel", event => event.preventDefault(), { capture: true });
      guardedDialogs.add(dialog);
      dialog.dataset.hvoCancelGuarded = "true";
    }
    dialog.showModal();
  }
}

export function focusById(id, fallbackId) {
  const target = document.getElementById(id) ?? document.getElementById(fallbackId);
  target?.focus();
}
