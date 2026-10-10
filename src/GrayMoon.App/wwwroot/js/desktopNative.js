window.graymoonDesktop = window.graymoonDesktop || {};
window.graymoonDesktop.isAvailable = function () {
  return !!(window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === "function");
};
window.graymoonDesktop.post = function (command, payload) {
  if (!window.graymoonDesktop.isAvailable()) return false;
  window.chrome.webview.postMessage({
    command: command,
    version: 1,
    payload: payload || {}
  });
  return true;
};
window.graymoonDesktop.openFolder = function (path) {
  return window.graymoonDesktop.post("OpenLocalRepositoryFolder", { path: path });
};
// Open-in tools (Cursor, Claude CLI, Terminal, ...) post their own command through
// graymoonDesktop.post; see FeatureOpenInTools.All for the command names.
window.graymoonDesktop.openExternalUrl = function (url) {
  return window.graymoonDesktop.post("OpenExternalUrl", { url: url });
};

// GrayMoon.Desktop's MainWindow.xaml.cs detects which external tools (Cursor, VS Code, Visual
// Studio, Claude CLI, Codex CLI) are installed and pushes the result via postMessage after every navigation
// (see NotifyToolAvailability). Cached here so Blazor can read it synchronously via JS interop
// instead of round-tripping to the host on every dropdown open. null until the first message
// arrives (or forever, outside the desktop shell).
window.graymoonDesktop._toolAvailability = null;
window.graymoonDesktop._toolAvailabilitySubscribers = [];
window.graymoonDesktop.getToolAvailability = function () {
  return window.graymoonDesktop._toolAvailability;
};
window.graymoonDesktop.subscribeToolAvailability = function (dotNetRef) {
  if (!dotNetRef) return;
  window.graymoonDesktop._toolAvailabilitySubscribers.push(dotNetRef);
};
window.graymoonDesktop.unsubscribeToolAvailability = function (dotNetRef) {
  var subscribers = window.graymoonDesktop._toolAvailabilitySubscribers;
  var index = subscribers.indexOf(dotNetRef);
  if (index >= 0) subscribers.splice(index, 1);
};
window.graymoonDesktop._publishToolAvailability = function () {
  var subscribers = window.graymoonDesktop._toolAvailabilitySubscribers.slice();
  subscribers.forEach(function (dotNetRef) {
    try {
      dotNetRef.invokeMethodAsync("OnToolAvailabilityChanged");
    } catch (e) {
      // The page that subscribed may already have been disposed.
    }
  });
};
if (window.chrome && window.chrome.webview && typeof window.chrome.webview.addEventListener === "function") {
  window.chrome.webview.addEventListener("message", function (event) {
    var data = event.data;
    if (data && data.type === "toolAvailability") {
      // Every other boolean field is an Open-in tool id (cursor, claudeCli, codexCli, ...),
      // so a new tool needs no change here.
      var installed = {};
      Object.keys(data).forEach(function (key) {
        if (key !== "type" && typeof data[key] === "boolean") installed[key] = data[key];
      });
      window.graymoonDesktop._toolAvailability = installed;
      window.graymoonDesktop._publishToolAvailability();
    }
  });
}
