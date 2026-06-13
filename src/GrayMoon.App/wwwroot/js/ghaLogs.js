window.ghaLogs = {
    initAutoSelect: function (el) {
        if (!el) return;
        el.addEventListener('mouseup', async function () {
            const sel = window.getSelection();
            if (!sel || sel.toString().length === 0) return;
            try {
                await navigator.clipboard.writeText(sel.toString());
                window.graymoonShowToast('Copied!');
            } catch (_) { }
        });
    },

    downloadText: function (filename, content) {
        const blob = new Blob([content], { type: 'text/plain' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        setTimeout(function () {
            document.body.removeChild(a);
            URL.revokeObjectURL(url);
        }, 200);
    },

    scrollToJob: function (contentEl, anchorId) {
        if (!contentEl) return;
        // The anchor div is a direct child of the scroll container and has position: relative
        // as its offsetParent (because the container is position: relative).
        // offsetTop gives the natural position — not affected by sticky children.
        const anchor = document.getElementById(anchorId);
        if (anchor) contentEl.scrollTo({ top: anchor.offsetTop, behavior: 'smooth' });
    }
};
