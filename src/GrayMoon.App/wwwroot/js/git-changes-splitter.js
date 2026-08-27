/**
 * Draggable vertical splitter for two-panel layouts (Git Changes). A container with class
 * "graymoon-splitter" holds two children ("graymoon-splitter__left" / "graymoon-splitter__right")
 * and a drag handle ("graymoon-splitter__handle") between them. Left panel width is persisted in
 * localStorage per container id (and, when present, per "data-workspace-id"), mirroring
 * resizable-columns.js's storage convention.
 *
 * The saved width is a percentage, so it naturally stays proportional as the window/container is
 * resized (the "keep responsive until the user picks their own position" requirement) while a
 * user-set width always wins over the CSS default once one has been saved.
 */
(function () {
    const STORAGE_PREFIX = 'graymoon-splitter-width-';
    const MIN_WIDTH_PERCENT = 20;
    const MAX_WIDTH_PERCENT = 70;

    function getStorageKey(container) {
        const workspaceId = container.dataset.workspaceId;
        const baseId = container.id || 'default';
        return STORAGE_PREFIX + baseId + (workspaceId ? '-workspace-' + workspaceId : '');
    }

    function readSavedPercent(key) {
        try {
            const raw = localStorage.getItem(key);
            if (!raw) return null;
            const parsed = parseFloat(raw);
            if (!Number.isFinite(parsed)) return null;
            return Math.max(MIN_WIDTH_PERCENT, Math.min(MAX_WIDTH_PERCENT, parsed));
        } catch (_) {
            return null;
        }
    }

    function applyWidth(left, percent) {
        left.style.setProperty('flex-basis', percent + '%', 'important');
        left.style.setProperty('width', percent + '%', 'important');
    }

    function applySavedOrDefaultWidth(container, left) {
        const key = getStorageKey(container);
        const savedPercent = readSavedPercent(key);
        if (savedPercent !== null) {
            applyWidth(left, savedPercent);
        } else {
            left.style.removeProperty('flex-basis');
            left.style.removeProperty('width');
        }
    }

    function initSplitter(container) {
        const left = container.querySelector('.graymoon-splitter__left');
        const handle = container.querySelector('.graymoon-splitter__handle');
        if (!left || !handle) return;

        // Re-apply whenever this is a brand-new container OR the same container was reused for a
        // different workspace (e.g. Blazor keeps the DOM node in place across SPA navigation
        // between workspaces since the markup is structurally identical). Skipping this check
        // whenever the workspace id is unchanged keeps drag-in-progress reflows from re-reading
        // localStorage on every mutation.
        const workspaceKey = container.dataset.workspaceId || '';
        if (container.dataset.splitterInit === '1' && container.dataset.splitterAppliedWorkspace === workspaceKey) {
            return;
        }
        container.dataset.splitterInit = '1';
        container.dataset.splitterAppliedWorkspace = workspaceKey;

        applySavedOrDefaultWidth(container, left);

        if (container.dataset.splitterDragBound === '1') return;
        container.dataset.splitterDragBound = '1';

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
                localStorage.setItem(getStorageKey(container), String(percent));
            } catch (_) { /* ignore */ }
        });
    }

    function clearCommitMessageInlineSizing() {
        document.querySelectorAll('.git-changes-workspace-commit__message').forEach((el) => {
            el.style.removeProperty('height');
            el.style.removeProperty('overflow-y');
        });
    }

    function initAll() {
        document.querySelectorAll('.graymoon-splitter').forEach(initSplitter);
        clearCommitMessageInlineSizing();
    }

    document.addEventListener('DOMContentLoaded', initAll);
    initAll();

    const observer = new MutationObserver(initAll);
    observer.observe(document.body, { childList: true, subtree: true });
})();
