// Bağlantı kutusu: kullanıcıdan düğmeye basmasını beklemeden kendiliğinden toparlanır.
// - Sunucuya ulaşılamıyorsa (ör. güncelleme sırasında) birkaç saniyede bir yeniden dener.
// - Sunucu oturumu artık tanımıyorsa sayfayı yeniler.
const RetryIntervalMs = 5000;

const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

document.getElementById("components-reconnect-button").addEventListener("click", retry);
document.getElementById("components-resume-button").addEventListener("click", () => location.reload());

let retryTimer = null;

function handleReconnectStateChanged(event) {
    const state = event.detail.state;
    if (state === "show") {
        if (!reconnectModal.open) reconnectModal.showModal();
    } else if (state === "hide") {
        stopRetrying();
        reconnectModal.close();
    } else if (state === "failed") {
        startRetrying();
    } else if (state === "rejected") {
        location.reload();
    } else if (state === "paused") {
        // Sunucu oturumu duraklattı (ör. uzun süre arka planda): beklemeden sürdür.
        if (!reconnectModal.open) reconnectModal.showModal();
        resume();
    }
}

function startRetrying() {
    if (retryTimer) return;
    retryTimer = setInterval(retry, RetryIntervalMs);
    document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
}

function stopRetrying() {
    if (retryTimer) clearInterval(retryTimer);
    retryTimer = null;
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
}

async function retry() {
    try {
        // true: bağlandı; false: sunucuya ulaşıldı ama oturum yok; hata: sunucuya ulaşılamadı.
        const successful = await Blazor.reconnect();
        if (successful) {
            stopRetrying();
            return;
        }
        stopRetrying();
        await resume();
    } catch {
        // Sunucu hâlâ yok; zamanlayıcı yeniden deneyecek.
    }
}

async function resume() {
    try {
        if (await Blazor.resumeCircuit()) {
            reconnectModal.close();
        } else {
            location.reload();
        }
    } catch {
        reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
        location.reload();
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") await retry();
}
