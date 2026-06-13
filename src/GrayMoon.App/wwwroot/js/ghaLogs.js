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
    }
};
