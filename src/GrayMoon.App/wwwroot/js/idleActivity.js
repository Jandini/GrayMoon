(function () {
  // Single global tracker (one AppActivityTracker component per circuit) rather than a keyed Map like
  // matrixOverlay.js, since there is only ever one activity state per browser tab.
  let s = null;

  // Passive listeners only ever write a timestamp - no Blazor interop on every event. Interop only
  // happens from the periodic tier check below, and only when the computed tier actually changes.
  const ACTIVITY_EVENTS = ["mousemove", "mousedown", "keydown", "wheel", "touchstart", "scroll"];
  const TIER_CHECK_INTERVAL_MS = 2000;

  function computeTier() {
    if (document.hidden) return "Hidden";
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
  }

  function dispose() {
    if (!s) return;
    ACTIVITY_EVENTS.forEach((evt) => document.removeEventListener(evt, onActivity));
    document.removeEventListener("visibilitychange", onVisibilityChange);
    if (s.intervalId) window.clearInterval(s.intervalId);
    s = null;
  }

  window.grayMoonIdleActivity = { init, dispose };
})();
