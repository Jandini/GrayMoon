/**
 * Positions the repository picker list with position:fixed so modal overflow
 * cannot clip it or grow the dialog. Flips above the input when the viewport
 * has more room there.
 */
(function () {
    const GAP = 2;
    const MARGIN = 8;
    const MAX_PX = 176;
    const MIN_PX = 80;
    const cleanups = new Map();

    function position(anchor, list) {
        const rect = anchor.getBoundingClientRect();
        const spaceBelow = window.innerHeight - rect.bottom - GAP - MARGIN;
        const spaceAbove = rect.top - GAP - MARGIN;
        const openUp = spaceBelow < MIN_PX && spaceAbove > spaceBelow;
        const available = Math.max(MIN_PX, openUp ? spaceAbove : spaceBelow);
        const maxHeight = Math.min(MAX_PX, available);

        list.style.position = 'fixed';
        list.style.left = rect.left + 'px';
        list.style.width = rect.width + 'px';
        list.style.right = 'auto';
        list.style.maxHeight = maxHeight + 'px';
        list.style.zIndex = '1080';
        if (openUp) {
            list.style.top = 'auto';
            list.style.bottom = (window.innerHeight - rect.top + GAP) + 'px';
        } else {
            list.style.bottom = 'auto';
            list.style.top = (rect.bottom + GAP) + 'px';
        }
        list.style.visibility = 'visible';
    }

    function detach(id) {
        const cleanup = cleanups.get(id);
        if (!cleanup)
            return;
        cleanup();
        cleanups.delete(id);
    }

    window.graymoonRepositoryPicker = {
        attach(id, anchor, list) {
            detach(id);
            if (!anchor || !list)
                return;
            const update = () => position(anchor, list);
            update();
            window.addEventListener('resize', update);
            window.addEventListener('scroll', update, true);
            cleanups.set(id, () => {
                window.removeEventListener('resize', update);
                window.removeEventListener('scroll', update, true);
            });
        },
        detach
    };
})();
