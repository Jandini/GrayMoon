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
window.graymoonDesktop.showInExplorer = function (path) {
  return window.graymoonDesktop.post("ShowInExplorer", { path: path });
};
window.graymoonDesktop.openInCursor = function (path) {
  return window.graymoonDesktop.post("OpenInCursor", { path: path });
};
window.graymoonDesktop.openInVsCode = function (path) {
  return window.graymoonDesktop.post("OpenInVsCode", { path: path });
};
window.graymoonDesktop.openInTerminal = function (path) {
  return window.graymoonDesktop.post("OpenInTerminal", { path: path });
};
window.graymoonDesktop.openExternalUrl = function (url) {
  return window.graymoonDesktop.post("OpenExternalUrl", { url: url });
};
