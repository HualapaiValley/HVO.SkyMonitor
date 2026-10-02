const bound = new WeakSet();

// Report a click on the whole-frame preview as fractions of the rendered image. The image keeps its exact aspect,
// so the fractions map to source pixels; catalog-star buttons handle their own clicks.
export function bindPicker(element, reference) {
  if (!element || bound.has(element)) {
    return;
  }
  bound.add(element);
  element.addEventListener("click", event => {
    const image = element.querySelector("img");
    if (!image || event.target !== image) {
      return;
    }
    const rect = image.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) {
      return;
    }
    const x = (event.clientX - rect.left) / rect.width;
    const y = (event.clientY - rect.top) / rect.height;
    if (x < 0 || x > 1 || y < 0 || y > 1) {
      return;
    }
    reference.invokeMethodAsync("PickAsync", x, y).catch(() => { });
  });
}
