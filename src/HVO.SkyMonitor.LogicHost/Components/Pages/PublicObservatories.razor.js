let map;
let markers = [];

const fixtureGrid = {
    type: "FeatureCollection",
    features: [
        ...[-120, -60, 0, 60, 120].map(longitude => ({
            type: "Feature",
            geometry: { type: "LineString", coordinates: [[longitude, -85], [longitude, 85]] }
        })),
        ...[-60, -30, 0, 30, 60].map(latitude => ({
            type: "Feature",
            geometry: { type: "LineString", coordinates: [[-180, latitude], [180, latitude]] }
        }))
    ]
};

function fixtureStyle(kind) {
    const satellite = kind === "satellite";
    return {
        version: 8,
        sources: { grid: { type: "geojson", data: fixtureGrid } },
        layers: [
            { id: "background", type: "background", paint: { "background-color": satellite ? "#07111f" : "#0b1d32" } },
            { id: "grid", type: "line", source: "grid", paint: { "line-color": satellite ? "#40526d" : "#1e7498", "line-opacity": 0.46, "line-width": 1 } }
        ]
    };
}

export function initialize(items) {
    const container = document.getElementById("observatory-map");
    if (!container || !window.maplibregl) {
        if (container) container.textContent = "Interactive map unavailable; the accessible directory remains below.";
        return;
    }
    map = new window.maplibregl.Map({
        container,
        style: fixtureStyle("standard"),
        bounds: [[-180, -85], [180, 85]],
        fitBoundsOptions: { padding: 12, duration: 0 },
        minZoom: 0,
        renderWorldCopies: false,
        attributionControl: false
    });
    map.addControl(new window.maplibregl.NavigationControl({ showCompass: false }), "top-right");
    map.addControl(new window.maplibregl.AttributionControl({
        compact: true,
        customAttribution: "HVO local fixture; production provider deferred to #151"
    }));
    updateMarkers(items);
}

export function updateMarkers(items) {
    if (!map) return;
    for (const marker of markers) marker.remove();
    markers = items.map(item => {
        const link = document.createElement("a");
        link.className = "directory-map-marker";
        link.href = `/observatories/${encodeURIComponent(item.slug)}`;
        link.title = item.displayName;
        link.setAttribute("aria-label", `${item.displayName}, ${item.region}`);
        const popup = new window.maplibregl.Popup({ offset: 16 }).setText(`${item.displayName} - ${item.region}`);
        return new window.maplibregl.Marker({ element: link })
            .setLngLat([item.longitude, item.latitude])
            .setPopup(popup)
            .addTo(map);
    });
}

export function setStyle(kind) {
    if (map) map.setStyle(fixtureStyle(kind));
}

export function dispose() {
    for (const marker of markers) marker.remove();
    markers = [];
    map?.remove();
    map = undefined;
}
