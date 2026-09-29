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

const watchedMaps = new WeakSet();

// Map tiles come from a third-party server the browser may not reach (offline site, blocked egress).
// One failed tile switches the whole map to the offline schematic rather than showing a partial mosaic.
export function watchTiles(container) {
  if (!container) {
    return;
  }
  delete container.dataset.tiles;
  if (!watchedMaps.has(container)) {
    watchedMaps.add(container);
    // An image error does not bubble but can be captured here, which also covers tiles rendered after this call.
    container.addEventListener("error", event => {
      if (event.target instanceof HTMLImageElement && event.target.closest(".site-map-tiles")) {
        container.dataset.tiles = "offline";
      }
    }, { capture: true });
  }
  // A tile that failed before the listener existed is already broken.
  if ([...container.querySelectorAll(".site-map-tiles img")].some(image => image.complete && image.naturalWidth === 0)) {
    container.dataset.tiles = "offline";
  }
}
