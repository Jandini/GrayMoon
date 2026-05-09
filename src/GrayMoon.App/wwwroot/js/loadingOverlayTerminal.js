window.loadingOverlayTerminal = {
  scrollToEnd: (el) => {
    if (!el) return;
    // Defer until after Blazor layout/paint so scrollHeight is final (avoids jump from wrong height).
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        el.scrollTop = el.scrollHeight;
      });
    });
  }
};
