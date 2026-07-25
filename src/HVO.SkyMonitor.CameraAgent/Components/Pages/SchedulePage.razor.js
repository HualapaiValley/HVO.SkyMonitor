export function focusById(id, fallbackId) {
  const target = document.getElementById(id) ?? document.getElementById(fallbackId);
  target?.focus();
}
