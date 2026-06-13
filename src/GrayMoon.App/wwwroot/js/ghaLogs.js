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
        el.addEventListener('keydown', function (e) {
            const handler = ghaLogs._scrollHandlers(el)[e.key];
            if (!handler) return;
            e.preventDefault();
            e.stopPropagation();
            handler();
        });
        el.focus({ preventScroll: true });
    },

    scrollContent: function (el, key) {
        if (!el) return;
        const handler = ghaLogs._scrollHandlers(el)[key];
        if (handler) handler();
    },

    _scrollHandlers: function (el) {
        return {
            ArrowUp:   function () { el.scrollBy(0, -20); },
            ArrowDown: function () { el.scrollBy(0,  20); },
            PageUp:    function () { el.scrollBy(0, -el.clientHeight); },
            PageDown:  function () { el.scrollBy(0,  el.clientHeight); },
            Home:      function () { el.scrollTo(0, 0); },
            End:       function () { el.scrollTo(0, el.scrollHeight); },
        };
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
    }
};
