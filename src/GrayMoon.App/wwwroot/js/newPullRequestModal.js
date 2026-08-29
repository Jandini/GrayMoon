window.newPullRequestModal = {
    // Attaches a paste listener on the title input (idempotent). When the pasted text
    // contains multiple lines, the default paste is suppressed and the raw text is
    // forwarded to .NET so the first line can go to the title and the rest to the description.
    initTitlePasteHandler: function (el, dotNetRef) {
        if (!el || el.dataset.pasteHandlerInit) return;
        el.dataset.pasteHandlerInit = '1';
        el.addEventListener('paste', function (e) {
            const clipboard = e.clipboardData || window.clipboardData;
            if (!clipboard) return;
            const text = clipboard.getData('text');
            if (text && text.indexOf('\n') !== -1) {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnTitlePasteMultiline', text);
            }
        });
    }
};
