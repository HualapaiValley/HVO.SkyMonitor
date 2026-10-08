export function canPlay(mediaType) {
    return document.createElement("video").canPlayType(mediaType) !== "";
}
