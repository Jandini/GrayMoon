/**
 * Positions WorkspaceRepositories header action menus with position:fixed so they
 * do not expand article.content scrollable overflow (overflow-x:hidden forces
 * overflow-y:auto on that scrollport). Scoped to .workspace-repository-actions-menu only.
 */
(function () {
    const HEADER_SEL = '.workspace-repos-header';
    const MENU_SEL = '.workspace-repository-actions-menu';
    const GAP = 2;
    const MARGIN = 8;

    function positionMenu(menu) {
        const group = menu.closest('.btn-group');
        if (!group) return;

        const anchor = group.getBoundingClientRect();
        const menuHeight = menu.offsetHeight || 0;
        const minWidth = Math.max(menu.offsetWidth || 0, anchor.width);
        menu.style.minWidth = minWidth + 'px';

        let top = anchor.bottom + GAP;
        if (top + menuHeight > window.innerHeight - MARGIN && anchor.top - menuHeight - GAP >= MARGIN) {
            top = anchor.top - menuHeight - GAP;
        }
        menu.style.top = top + 'px';

        if (menu.classList.contains('branch-menu-list-end')) {
            menu.style.left = 'auto';
            menu.style.right = (window.innerWidth - anchor.right) + 'px';
            return;
        }

        menu.style.right = 'auto';
        let left = anchor.left;
        const maxLeft = window.innerWidth - minWidth - MARGIN;
        if (left > maxLeft) left = Math.max(MARGIN, maxLeft);
        if (left < MARGIN) left = MARGIN;
        menu.style.left = left + 'px';
    }

    function positionOpenMenus() {
        document.querySelectorAll(HEADER_SEL + ' ' + MENU_SEL).forEach(positionMenu);
    }

    let observedHeader = null;
    const observer = new MutationObserver(function () {
        requestAnimationFrame(positionOpenMenus);
    });

    function ensureObserver() {
        const header = document.querySelector(HEADER_SEL);
        if (!header) return;
        if (observedHeader === header) return;
        if (observedHeader) observer.disconnect();
        observedHeader = header;
        observer.observe(header, { childList: true, subtree: true });
    }

    document.addEventListener('click', function () {
        ensureObserver();
        requestAnimationFrame(positionOpenMenus);
    }, true);

    window.addEventListener('resize', positionOpenMenus);
})();
