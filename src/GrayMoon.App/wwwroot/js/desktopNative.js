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
window.graymoonDesktop.openInVisualStudio = function (path) {
  return window.graymoonDesktop.post("OpenInVisualStudio", { path: path });
};
window.graymoonDesktop.openInClaudeCli = function (path) {
  return window.graymoonDesktop.post("OpenInClaudeCli", { path: path });
};
window.graymoonDesktop.openInTerminal = function (path) {
  return window.graymoonDesktop.post("OpenInTerminal", { path: path });
};
window.graymoonDesktop.openExternalUrl = function (url) {
  return window.graymoonDesktop.post("OpenExternalUrl", { url: url });
};

// GrayMoon.Desktop's MainWindow.xaml.cs detects which external tools (Cursor, VS Code, Visual
// Studio, Claude CLI) are installed and pushes the result via postMessage after every navigation
// (see NotifyToolAvailability). Cached here so Blazor can read it synchronously via JS interop
// instead of round-tripping to the host on every dropdown open. null until the first message
// arrives (or forever, outside the desktop shell).
window.graymoonDesktop._toolAvailability = null;
window.graymoonDesktop.getToolAvailability = function () {
  return window.graymoonDesktop._toolAvailability;
};
if (window.chrome && window.chrome.webview && typeof window.chrome.webview.addEventListener === "function") {
  window.chrome.webview.addEventListener("message", function (event) {
    var data = event.data;
    if (data && data.type === "toolAvailability") {
      window.graymoonDesktop._toolAvailability = {
        cursor: !!data.cursor,
        vsCode: !!data.vsCode,
        visualStudio: !!data.visualStudio,
        claudeCli: !!data.claudeCli
      };
    }
  });
}
