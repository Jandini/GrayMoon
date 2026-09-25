// GrayMoon Desktop WebView2 bridge for correlated native commands (e.g. InstallHostPrerequisites).
// Feature-detected: window.chrome.webview only exists inside the real GrayMoon Desktop host.
(function () {
  'use strict';

  var pending = Object.create(null);
  var listening = false;

  function isAvailable() {
    return !!(window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function');
  }

  function ensureListener() {
    if (listening || !isAvailable()) return;
    listening = true;
    window.chrome.webview.addEventListener('message', onNativeMessage);
  }

  function onNativeMessage(event) {
    var data = event && event.data;
    if (!data || data.type !== 'nativeCommandResult' || !data.id) return;

    var entry = pending[data.id];
    if (!entry) return;
    delete pending[data.id];

    try {
      entry.dotNetRef.invokeMethodAsync(
        'OnNativeCommandResult',
        data.id,
        !!data.success,
        !!data.cancelled,
        data.message || null,
        data.failedPrerequisiteIds || []);
    } catch (e) {
      // Component may have been disposed during navigation.
    }
  }

  function postCommand(command, payload, id) {
    if (!isAvailable()) return false;
    ensureListener();
    window.chrome.webview.postMessage({
      command: command,
      version: 1,
      id: id || null,
      payload: payload || {}
    });
    return true;
  }

  function registerPending(id, dotNetRef) {
    if (!id || !dotNetRef) return;
    ensureListener();
    pending[id] = { dotNetRef: dotNetRef };
  }

  function unregisterPending(id) {
    if (!id) return;
    delete pending[id];
  }

  function disposeAll(dotNetRef) {
    if (!dotNetRef) return;
    Object.keys(pending).forEach(function (id) {
      if (pending[id] && pending[id].dotNetRef === dotNetRef)
        delete pending[id];
    });
  }

  window.graymoonDesktopBridge = {
    isAvailable: isAvailable,
    postCommand: postCommand,
    registerPending: registerPending,
    unregisterPending: unregisterPending,
    disposeAll: disposeAll
  };
})();
