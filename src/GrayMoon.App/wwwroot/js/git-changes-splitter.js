/**
 * Draggable vertical splitter for two-panel layouts (Git Changes). A container with class
 * "graymoon-splitter" holds two children ("graymoon-splitter__left" / "graymoon-splitter__right")
 * and a drag handle ("graymoon-splitter__handle") between them. Left panel width is persisted in
 * localStorage per container id, mirroring resizable-columns.js's storage convention.
 */
(function () {
    const STORAGE_PREFIX = 'graymoon-splitter-width-';
    const MIN_WIDTH_PERCENT = 20;
    const MAX_WIDTH_PERCENT = 70;

    function getStorageKey(container) {
        return STORAGE_PREFIX + (container.id || 'default');
    }

    function applyWidth(left, percent) {
        left.style.flexBasis = percent + '%';
        left.style.width = percent + '%';
    }

    function initSplitter(container) {
        if (container.dataset.splitterInit === '1') return;
        const left = container.querySelector('.graymoon-splitter__left');
        const handle = container.querySelector('.graymoon-splitter__handle');
        if (!left || !handle) return;

        container.dataset.splitterInit = '1';

        const key = getStorageKey(container);
        let savedPercent = null;
        try {
            const raw = localStorage.getItem(key);
            if (raw) savedPercent = parseFloat(raw);
        } catch (_) { /* ignore */ }

        if (savedPercent && savedPercent >= MIN_WIDTH_PERCENT && savedPercent <= MAX_WIDTH_PERCENT) {
            applyWidth(left, savedPercent);
        }

        let dragging = false;

        handle.addEventListener('mousedown', function (e) {
            dragging = true;
            document.body.style.userSelect = 'none';
            e.preventDefault();
        });

        document.addEventListener('mousemove', function (e) {
            if (!dragging) return;
            const rect = container.getBoundingClientRect();
            let percent = ((e.clientX - rect.left) / rect.width) * 100;
            percent = Math.max(MIN_WIDTH_PERCENT, Math.min(MAX_WIDTH_PERCENT, percent));
            applyWidth(left, percent);
        });

        document.addEventListener('mouseup', function () {
            if (!dragging) return;
            dragging = false;
            document.body.style.userSelect = '';
            try {
                const rect = container.getBoundingClientRect();
                const percent = (left.getBoundingClientRect().width / rect.width) * 100;
                localStorage.setItem(key, String(percent));
            } catch (_) { /* ignore */ }
        });
    }

    function initAll() {
        document.querySelectorAll('.graymoon-splitter').forEach(initSplitter);
    }

    document.addEventListener('DOMContentLoaded', initAll);
    initAll();

    const observer = new MutationObserver(initAll);
    observer.observe(document.body, { childList: true, subtree: true });
})();
