// Blazor circuit reconnect modal (see Components/Layout/ReconnectModal.razor).
function getReconnectModal() {
    return document.getElementById("components-reconnect-modal");
}

function bindReconnectModal() {
    const reconnectModal = getReconnectModal();
    if (!reconnectModal || reconnectModal.dataset.reconnectBound === "true") {
        return;
    }

    reconnectModal.dataset.reconnectBound = "true";
    reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

    const retryButton = document.getElementById("components-reconnect-button");
    if (retryButton) {
        retryButton.addEventListener("click", retry);
    }

    const resumeButton = document.getElementById("components-resume-button");
    if (resumeButton) {
        resumeButton.addEventListener("click", resume);
    }
}

if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", bindReconnectModal);
} else {
    bindReconnectModal();
}

function handleReconnectStateChanged(event) {
    const reconnectModal = event.currentTarget;
    if (!(reconnectModal instanceof HTMLDialogElement)) {
        return;
    }

    if (event.detail.state === "show") {
        reconnectModal.showModal();
    } else if (event.detail.state === "hide") {
        if (reconnectModal.open) {
            reconnectModal.close();
        }
    } else if (event.detail.state === "failed") {
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (event.detail.state === "rejected" || event.detail.state === "resume-failed") {
        location.reload();
    }
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        const successful = await Blazor.reconnect();
        if (!successful) {
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                location.reload();
            } else {
                getReconnectModal()?.close();
            }
        }
    } catch {
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        }
    } catch {
        const reconnectModal = getReconnectModal();
        if (reconnectModal) {
            reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
        }
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}
