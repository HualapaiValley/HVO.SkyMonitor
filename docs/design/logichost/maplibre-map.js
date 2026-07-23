const publicObservatoryProjections = [
    { name: "Volcano Station", region: "Island of Hawaii", coordinates: [-155.47, 19.55], state: "Capturing", cameras: 2 },
    { name: "Sonoran Sky", region: "Central Arizona", coordinates: [-112.07, 33.45], state: "Capturing", cameras: 2 },
    { name: "Hokkaido Horizon", region: "Northern Japan", coordinates: [141.35, 43.06], state: "Cloudy", cameras: 2 },
    { name: "Southern Cross", region: "Southeastern Australia", coordinates: [149.13, -35.28], state: "Twilight", cameras: 1 }
];

const standardStyle = "https://tiles.openfreemap.org/styles/liberty";
const satelliteStyle = {
    version: 8,
    sources: {
        satellite: {
            type: "raster",
            tiles: ["https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"],
            tileSize: 256,
            attribution: "Tiles &copy; Esri and contributors"
        }
    },
    layers: [{ id: "satellite", type: "raster", source: "satellite" }]
};

function createPopupContent(observatory) {
    const content = document.createElement("div");
    content.className = "map-popup";
    const name = document.createElement("strong");
    name.textContent = observatory.name;
    const region = document.createElement("span");
    region.textContent = observatory.region;
    const details = document.createElement("span");
    details.textContent = `${observatory.cameras} public camera${observatory.cameras === 1 ? "" : "s"} / ${observatory.state}`;
    content.append(name, region, details);
    return content;
}

if (!window.maplibregl) {
    document.querySelector("#observatory-map").textContent = "MapLibre could not load. Check network access to the map library and tile provider.";
} else {
    const mapContainer = document.querySelector("#observatory-map");
    const minimumWorldWidth = 512;
    const unscaledHeight = mapContainer.clientHeight;
    let compactScale = 1;
    let map;

    function applyWorldScale() {
        const visibleWidth = mapContainer.parentElement.clientWidth;
        compactScale = Math.min(1, visibleWidth / minimumWorldWidth);
        mapContainer.classList.toggle("map-canvas--scaled", compactScale < 1);
        mapContainer.style.setProperty("--map-inverse-scale", String(1 / compactScale));
        if (compactScale < 1) {
            mapContainer.style.width = `${minimumWorldWidth}px`;
            mapContainer.style.transform = `scale(${compactScale})`;
            mapContainer.style.transformOrigin = "top left";
            mapContainer.style.marginBottom = `${-unscaledHeight * (1 - compactScale)}px`;
        } else {
            mapContainer.style.width = "100%";
            mapContainer.style.transform = "none";
            mapContainer.style.marginBottom = "0";
        }

        if (map) {
            map.resize();
            map.fitBounds([[-178, -58], [178, 78]], { padding: compactScale < 1 ? 0 : 18, duration: 0 });
        }
    }

    applyWorldScale();
    map = new maplibregl.Map({
        container: mapContainer,
        style: standardStyle,
        center: [0, 12],
        zoom: 0,
        minZoom: 0,
        renderWorldCopies: false,
        attributionControl: true
    });

    map.addControl(new maplibregl.NavigationControl(), "top-right");
    if (compactScale === 1) {
        map.addControl(new maplibregl.FullscreenControl(), "top-right");
    }
    map.once("load", () => {
        map.fitBounds([[-178, -58], [178, 78]], { padding: compactScale < 1 ? 0 : 18, duration: 0 });
    });
    new ResizeObserver(applyWorldScale).observe(mapContainer.parentElement);

    for (const observatory of publicObservatoryProjections) {
        const markerElement = document.createElement("button");
        markerElement.className = "map-marker";
        markerElement.type = "button";
        markerElement.title = observatory.name;
        markerElement.setAttribute("aria-label", `${observatory.name}, ${observatory.region}`);
        const popup = new maplibregl.Popup({ offset: 18 }).setDOMContent(createPopupContent(observatory));
        new maplibregl.Marker({ element: markerElement })
            .setLngLat(observatory.coordinates)
            .setPopup(popup)
            .addTo(map);
    }

    document.querySelectorAll("[data-map-style]").forEach(button => {
        button.addEventListener("click", () => {
            const useSatellite = button.dataset.mapStyle === "satellite";
            map.setStyle(useSatellite ? satelliteStyle : standardStyle);
            document.querySelectorAll("[data-map-style]").forEach(candidate => {
                candidate.setAttribute("aria-pressed", String(candidate === button));
            });
        });
    });
}
