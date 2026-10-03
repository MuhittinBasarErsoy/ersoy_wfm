// WFM ortak JS: Leaflet harita, adres arama (Nominatim), imza alanı, konum.
const maps = new Map();

function dotIcon(color, label, pulse) {
    const html = `<div class="wfm-pin${pulse ? ' wfm-pin-pulse' : ''}" style="--pin:${color}">${label ?? ''}</div>`;
    return L.divIcon({ html, className: 'wfm-pin-wrap', iconSize: [28, 28], iconAnchor: [14, 14], popupAnchor: [0, -14] });
}

export function createMap(elementId, lat, lng, zoom, dotnetRef, clickable) {
    const el = document.getElementById(elementId);
    if (!el || typeof L === 'undefined') return false;
    if (maps.has(elementId)) disposeMap(elementId);
    const map = L.map(el, { zoomControl: true }).setView([lat, lng], zoom);
    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; OpenStreetMap'
    }).addTo(map);
    const state = { map, layers: {}, markers: {}, lines: {}, dotnetRef, pick: null };
    if (clickable) {
        map.on('click', e => dotnetRef.invokeMethodAsync('OnMapClick', e.latlng.lat, e.latlng.lng));
    }
    maps.set(elementId, state);
    // Konteyner boyutu sonradan oturursa (sekme, dialog) haritayı düzelt.
    new ResizeObserver(() => map.invalidateSize()).observe(el);
    return true;
}

function layer(state, name) {
    if (!state.layers[name]) {
        state.layers[name] = L.layerGroup().addTo(state.map);
        state.markers[name] = {};
    }
    return state.layers[name];
}

function buildMarker(state, layerName, m) {
    const marker = L.marker([m.lat, m.lng], { icon: dotIcon(m.color, m.label, m.pulse), title: m.title ?? '' });
    if (m.popup) marker.bindPopup(m.popup);
    marker.on('click', () => state.dotnetRef?.invokeMethodAsync('OnMarkerClick', layerName, m.id));
    return marker;
}

export function setMarkers(elementId, layerName, markers) {
    const state = maps.get(elementId);
    if (!state) return;
    const lg = layer(state, layerName);
    lg.clearLayers();
    state.markers[layerName] = {};
    for (const m of markers) {
        const mk = buildMarker(state, layerName, m).addTo(lg);
        state.markers[layerName][m.id] = mk;
    }
}

export function upsertMarker(elementId, layerName, m) {
    const state = maps.get(elementId);
    if (!state) return;
    const lg = layer(state, layerName);
    const existing = state.markers[layerName][m.id];
    if (existing) {
        existing.setLatLng([m.lat, m.lng]);
        existing.setIcon(dotIcon(m.color, m.label, m.pulse));
        if (m.popup) existing.setPopupContent(m.popup);
    } else {
        state.markers[layerName][m.id] = buildMarker(state, layerName, m).addTo(lg);
    }
}

export function setPolyline(elementId, name, points, color) {
    const state = maps.get(elementId);
    if (!state) return;
    if (state.lines[name]) { state.lines[name].remove(); delete state.lines[name]; }
    if (points && points.length > 1) {
        state.lines[name] = L.polyline(points.map(p => [p.lat, p.lng]), { color, weight: 4, opacity: .8 }).addTo(state.map);
        state.map.fitBounds(state.lines[name].getBounds(), { padding: [30, 30] });
    }
}

export function setPick(elementId, lat, lng) {
    const state = maps.get(elementId);
    if (!state) return;
    if (state.pick) state.pick.remove();
    state.pick = L.marker([lat, lng], { icon: dotIcon('#d32f2f', '●', true) }).addTo(state.map);
    state.map.setView([lat, lng], Math.max(state.map.getZoom(), 15));
}

export function setView(elementId, lat, lng, zoom) {
    maps.get(elementId)?.map.setView([lat, lng], zoom);
}

export function openPopup(elementId, layerName, id) {
    const state = maps.get(elementId);
    const mk = state?.markers[layerName]?.[id];
    if (mk) { state.map.setView(mk.getLatLng(), Math.max(state.map.getZoom(), 14)); mk.openPopup(); }
}

export function fitAll(elementId) {
    const state = maps.get(elementId);
    if (!state) return;
    const pts = [];
    for (const name in state.markers) for (const id in state.markers[name]) pts.push(state.markers[name][id].getLatLng());
    if (pts.length === 1) state.map.setView(pts[0], 15);
    else if (pts.length > 1) state.map.fitBounds(L.latLngBounds(pts), { padding: [40, 40], maxZoom: 16 });
}

export function disposeMap(elementId) {
    const state = maps.get(elementId);
    if (state) { state.map.remove(); maps.delete(elementId); }
}

// ---------- Adres arama (OpenStreetMap Nominatim) ----------
export async function searchAddress(query) {
    const url = `https://nominatim.openstreetmap.org/search?format=json&limit=6&accept-language=tr&q=${encodeURIComponent(query)}`;
    const res = await fetch(url, { headers: { 'Accept': 'application/json' } });
    if (!res.ok) return [];
    return (await res.json()).map(r => ({ address: r.display_name, latitude: parseFloat(r.lat), longitude: parseFloat(r.lon) }));
}

export async function reverseGeocode(lat, lng) {
    const url = `https://nominatim.openstreetmap.org/reverse?format=json&accept-language=tr&lat=${lat}&lon=${lng}`;
    const res = await fetch(url, { headers: { 'Accept': 'application/json' } });
    if (!res.ok) return null;
    return (await res.json()).display_name ?? null;
}

// ---------- Tarayıcı konumu ----------
export function getPosition() {
    return new Promise(resolve => {
        if (!navigator.geolocation) return resolve(null);
        navigator.geolocation.getCurrentPosition(
            p => resolve({ latitude: p.coords.latitude, longitude: p.coords.longitude, accuracy: p.coords.accuracy }),
            () => resolve(null),
            { enableHighAccuracy: true, timeout: 10000, maximumAge: 30000 });
    });
}

// ---------- İmza alanı ----------
const pads = new Map();

export function initSignature(canvasId) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;
    const ratio = window.devicePixelRatio || 1;
    const rect = canvas.getBoundingClientRect();
    canvas.width = rect.width * ratio;
    canvas.height = rect.height * ratio;
    const ctx = canvas.getContext('2d');
    ctx.scale(ratio, ratio);
    ctx.lineWidth = 2.2;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.strokeStyle = '#111';
    const state = { drawing: false, empty: true };
    const pos = e => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
    canvas.addEventListener('pointerdown', e => {
        state.drawing = true; state.empty = false;
        canvas.setPointerCapture(e.pointerId);
        ctx.beginPath(); ctx.moveTo(...pos(e));
        e.preventDefault();
    });
    canvas.addEventListener('pointermove', e => {
        if (!state.drawing) return;
        ctx.lineTo(...pos(e)); ctx.stroke();
        e.preventDefault();
    });
    const end = () => { state.drawing = false; };
    canvas.addEventListener('pointerup', end);
    canvas.addEventListener('pointercancel', end);
    pads.set(canvasId, state);
}

export function clearSignature(canvasId) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;
    canvas.getContext('2d').clearRect(0, 0, canvas.width, canvas.height);
    const s = pads.get(canvasId);
    if (s) s.empty = true;
}

/** İmzayı beyaz arka planlı PNG (base64, önek olmadan) olarak döner; boşsa null. */
export function getSignature(canvasId) {
    const canvas = document.getElementById(canvasId);
    const s = pads.get(canvasId);
    if (!canvas || !s || s.empty) return null;
    const out = document.createElement('canvas');
    out.width = canvas.width; out.height = canvas.height;
    const octx = out.getContext('2d');
    octx.fillStyle = '#fff'; octx.fillRect(0, 0, out.width, out.height);
    octx.drawImage(canvas, 0, 0);
    return out.toDataURL('image/png').split(',')[1];
}

export function openUrl(url) {
    window.open(url, '_blank');
}

// ---------- Tarayıcı konum takibi (web'den çalışan saha personeli için) ----------
export function startWatch(dotnetRef) {
    if (!navigator.geolocation) return -1;
    return navigator.geolocation.watchPosition(
        p => dotnetRef.invokeMethodAsync('OnPosition', p.coords.latitude, p.coords.longitude, p.coords.accuracy,
            p.coords.speed, p.coords.heading),
        () => { },
        { enableHighAccuracy: true, maximumAge: 15000, timeout: 20000 });
}

export function stopWatch(id) {
    if (id >= 0 && navigator.geolocation) navigator.geolocation.clearWatch(id);
}
