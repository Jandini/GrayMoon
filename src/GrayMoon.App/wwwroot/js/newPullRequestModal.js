window.newPullRequestModal = {
    // Focuses the title input and selects its current value so the user can
    // overwrite the default with Ctrl+V without selecting first.
    selectTitle: function (el) {
        if (!el) return;
        requestAnimationFrame(function () {
            el.focus();
            el.select();
        });
    },

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
    },

    // Pins the open target menu to the target cell. The menu is a top-layer popover, so it
    // does not extend the dialog and stays on the cell after the repository list is scrolled.
    placeOpenTargetMenu: function (dotNetRef) {
        if (placingTargetMenu) return;
        placingTargetMenu = true;
        try {
            if (dotNetRef) targetMenuDotNet = dotNetRef;
            ensureTargetMenuListeners();

            const menu = document.querySelector('.new-pr-modal .new-pr-branch-menu');
            const anchor = document.querySelector('.new-pr-modal .new-pr-branch-target--open');
            if (!menu || !anchor) return;

            const panel = anchor.closest('.new-pr-branch-panel');
            if (panel && !rectsOverlap(anchor.getBoundingClientRect(), panel.getBoundingClientRect())) {
                menu.classList.remove('new-pr-branch-menu--placed');
                if (typeof menu.hidePopover === 'function' && menu.matches(':popover-open')) {
                    try { menu.hidePopover(); } catch (e) { }
                }
                if (targetMenuDotNet) targetMenuDotNet.invokeMethodAsync('CloseOpenTargetMenu');
                return;
            }

            const rect = anchor.getBoundingClientRect();
            const gap = 2;
            const edge = 8;
            const rootPx = parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
            const preferredMax = 14 * rootPx;

            menu.style.position = 'fixed';
            menu.style.margin = '0';
            menu.style.inset = 'auto';
            menu.style.right = 'auto';
            menu.style.bottom = 'auto';
            menu.style.left = rect.left + 'px';
            menu.style.width = rect.width + 'px';
            menu.style.maxHeight = preferredMax + 'px';
            menu.style.top = (rect.bottom + gap) + 'px';

            openTargetMenuLayer(menu);

            const natural = menu.scrollHeight;
            const spaceBelow = window.innerHeight - rect.bottom - gap - edge;
            const spaceAbove = rect.top - gap - edge;
            const openUp = natural > spaceBelow && spaceAbove > spaceBelow;
            const available = Math.max(48, openUp ? spaceAbove : spaceBelow);
            const height = Math.min(natural > 0 ? natural : preferredMax, preferredMax, available);

            menu.style.maxHeight = height + 'px';
            menu.style.top = (openUp ? Math.max(edge, rect.top - gap - height) : rect.bottom + gap) + 'px';
            menu.classList.add('new-pr-branch-menu--placed');
        } finally {
            placingTargetMenu = false;
        }
    }
};

let targetMenuDotNet = null;
let targetMenuListenersBound = false;
let placingTargetMenu = false;

function ensureTargetMenuListeners() {
    if (targetMenuListenersBound) return;
    targetMenuListenersBound = true;

    window.addEventListener('resize', function () {
        if (document.querySelector('.new-pr-modal .new-pr-branch-menu'))
            window.newPullRequestModal.placeOpenTargetMenu(null);
    });

    document.addEventListener('scroll', function (e) {
        const menu = document.querySelector('.new-pr-modal .new-pr-branch-menu');
        if (!menu || (e.target && menu.contains(e.target))) return;
        window.newPullRequestModal.placeOpenTargetMenu(null);
    }, true);

    // A click outside the open menu closes it and does not activate whatever is underneath,
    // same as the old full-screen catcher, without putting an element inside the scrolling list.
    document.addEventListener('pointerdown', function (e) {
        const menu = document.querySelector('.new-pr-modal .new-pr-branch-menu');
        if (!menu) return;
        const anchor = document.querySelector('.new-pr-modal .new-pr-branch-target--open');
        if (menu.contains(e.target) || (anchor && anchor.contains(e.target))) return;
        e.preventDefault();
        e.stopPropagation();
        if (targetMenuDotNet) targetMenuDotNet.invokeMethodAsync('CloseOpenTargetMenu');
    }, true);
}

function openTargetMenuLayer(menu) {
    if (typeof menu.showPopover !== 'function') {
        menu.removeAttribute('popover');
        menu.style.display = 'block';
        return;
    }

    if (menu.matches(':popover-open')) return;

    try {
        menu.showPopover();
    } catch (e) {
        menu.removeAttribute('popover');
        menu.style.display = 'block';
    }
}

function rectsOverlap(a, b) {
    return a.bottom > b.top + 1 && a.top < b.bottom - 1 && a.right > b.left + 1 && a.left < b.right - 1;
}
