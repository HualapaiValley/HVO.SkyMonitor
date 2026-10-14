export function hasFailed(image) {
    return image instanceof HTMLImageElement && image.complete && image.naturalWidth === 0;
}
