window.loadingOverlayTerminal = {
  scrollToEnd: (el) => {
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  }
};
