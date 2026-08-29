(function () {
  // Single global tracker (one AppActivityTracker component per circuit) rather than a keyed Map like
  // matrixOverlay.js, since there is only ever one activity state per browser tab.
  let s = null;

  // Passive listeners only ever write a timestamp - no Blazor interop on every event. Interop only
  // happens from the periodic tier check below, and only when the computed tier actually changes.
  const ACTIVITY_EVENTS = ["mousemove", "mousedown", "keydown", "wheel", "touchstart", "scroll"];
  const TIER_CHECK_INTERVAL_MS = 2000;

  // GrayMoon Desktop's WebView2 shell (window.chrome.webview) is a hosted control, not a top-level
  // Chrome window - minimizing it is not guaranteed to flip document.hidden the way a real browser
  // window does. GrayMoon.Desktop's MainWindow.xaml.cs forwards the real window state explicitly via
  // postMessage ({ type: "windowVisibility", hidden }) so the "Hidden" tier is deterministic there too.
  // undefined (not false) until a message arrives, so a delayed/missing message never forces "hidden".
  let nativeWindowHidden;

  function computeTier() {
    if (document.hidden || nativeWindowHidden === true) return "Hidden";
    return (Date.now() - s.lastActivityTs) >= s.idleTimeoutMs ? "Idle" : "Active";
  }

  function checkTier() {
    if (!s) return;
    const tier = computeTier();
    if (tier === s.currentTier) return;
    s.currentTier = tier;
    s.dotNetRef.invokeMethodAsync("OnActivityStateChanged", tier).catch(() => { /* circuit gone - ignore */ });
  }

  function onActivity() {
    if (s) s.lastActivityTs = Date.now();
  }

  function onVisibilityChange() {
    checkTier();
  }

  function onNativeMessage(event) {
    const data = event.data;
    if (data && data.type === "windowVisibility" && typeof data.hidden === "boolean") {
      nativeWindowHidden = data.hidden;
      checkTier();
    }
  }

  function init(dotNetRef, idleTimeoutMs) {
    dispose(); // restart cleanly if already initialized

    s = {
      dotNetRef,
      idleTimeoutMs: Math.max(1000, idleTimeoutMs | 0),
      lastActivityTs: Date.now(),
      currentTier: "Active",
      intervalId: null
    };

    ACTIVITY_EVENTS.forEach((evt) => document.addEventListener(evt, onActivity, { passive: true }));
    document.addEventListener("visibilitychange", onVisibilityChange);
    s.intervalId = window.setInterval(checkTier, TIER_CHECK_INTERVAL_MS);

    // Feature-detected: window.chrome.webview only exists inside GrayMoon Desktop's WebView2 host,
    // so this is a complete no-op (and GrayMoon behaves exactly as before) in a normal browser tab.
    if (window.chrome && window.chrome.webview && typeof window.chrome.webview.addEventListener === "function") {
      window.chrome.webview.addEventListener("message", onNativeMessage);
    }
  }

  function dispose() {
    if (!s) return;
    ACTIVITY_EVENTS.forEach((evt) => document.removeEventListener(evt, onActivity));
    document.removeEventListener("visibilitychange", onVisibilityChange);
    if (window.chrome && window.chrome.webview && typeof window.chrome.webview.removeEventListener === "function") {
      window.chrome.webview.removeEventListener("message", onNativeMessage);
    }
    if (s.intervalId) window.clearInterval(s.intervalId);
    nativeWindowHidden = undefined;
    s = null;
  }

  window.grayMoonIdleActivity = { init, dispose };
})();
