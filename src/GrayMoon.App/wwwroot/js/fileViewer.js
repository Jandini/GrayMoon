window.fileViewer = {
    hasSelection: function (el) {
        if (!el) return false;
        return el.selectionStart !== el.selectionEnd;
    },

    getSelection: function (el) {
        if (!el) return null;
        var start = el.selectionStart;
        var end = el.selectionEnd;
        if (start === end) return null;
        return el.value.substring(start, end);
    },

    copySelection: async function (el) {
        if (!el) return false;
        var text = window.fileViewer.getSelection(el);
        if (!text) return false;
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    }
};
