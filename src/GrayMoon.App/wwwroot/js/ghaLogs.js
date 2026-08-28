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

    scrollToNext: function (el, kind) {
        if (!el) return;
        const cls = kind === 'warning' ? 'gha-log-line--warning' : 'gha-log-line--error';
        const lines = el.querySelectorAll('.gha-log-line.' + cls);
        if (lines.length === 0) return;

        const prev = el.querySelector('.gha-log-line--current');
        let idx = -1;
        if (prev) {
            for (let i = 0; i < lines.length; i++) {
                if (lines[i] === prev) { idx = i; break; }
            }
            prev.classList.remove('gha-log-line--current');
        }

        idx = (idx + 1) % lines.length;
        const target = lines[idx];
        target.classList.add('gha-log-line--current');

        let node = target.parentElement;
        while (node && node !== el) {
            if (node.tagName === 'DETAILS') node.open = true;
            node = node.parentElement;
        }

        requestAnimationFrame(function () {
            const group = target.closest('.gha-log-group');
            const sticky = group ? group.querySelector('.gha-log-group__header') : null;
            const stickyH = sticky ? sticky.getBoundingClientRect().height : 0;
            const cRect = el.getBoundingClientRect();
            const tRect = target.getBoundingClientRect();
            const visibleMid = cRect.top + stickyH + (cRect.height - stickyH) / 2;
            const lineMid = tRect.top + tRect.height / 2;
            el.scrollTop += lineMid - visibleMid;
        });
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
