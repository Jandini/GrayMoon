/**
 * Keeps the Actions title row on one line. When the row is too narrow the refresh
 * status shortens from "Refreshing 1 of 3 repositories..." to "1 of 3" (spinner and
 * abort stay). If it still overflows, workflow count and status chips hide from the
 * right and come back when the row is wide enough. The Refresh button lives on the
 * toolbar row and is kept intact by CSS (nowrap, no shrink).
 */
(function () {
    const ROW = '.workspace-actions-header .workspace-repos-title-row';
    const COLLAPSED = 'is-collapsed';
    const SHORT = 'is-short';
    const ELLIPSIS = 'is-ellipsis';

    const resizeObserver = new ResizeObserver(schedule);
    const rowObserver = new MutationObserver(function () {
        if (!fitting)
            schedule();
    });
    const watched = new WeakSet();
    let scheduled = false;
    let fitting = false;

    function schedule() {
        if (scheduled) return;
        scheduled = true;
        requestAnimationFrame(function () {
            scheduled = false;
            fitAll();
        });
    }

    function setCollapsed(el, collapsed) {
        el.classList.toggle(COLLAPSED, collapsed);
        el.hidden = collapsed;
    }

    function overflows(row) {
        // Sum laid-out children instead of scrollWidth. A nowrap flex row with a
        // growing spacer reports scrollWidth === clientWidth even when a later
        // item has been pushed outside the row.
        const style = getComputedStyle(row);
        const gap = parseFloat(style.columnGap) || 0;
        const pad = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);
        let width = 0;
        let count = 0;
        for (let i = 0; i < row.children.length; i++) {
            const el = row.children[i];
            if (el.hidden)
                continue;
            width += el.getBoundingClientRect().width;
            count++;
        }
        if (count > 1)
            width += gap * (count - 1);
        return width > (row.clientWidth - pad) + 1;
    }

    function optionals(row) {
        const items = [];
        const count = row.querySelector('.workspace-actions__workflow-count');
        if (count)
            items.push(count);
        const sep = row.querySelector('.workspace-actions__filter-sep');
        if (sep)
            items.push(sep);
        const filters = row.querySelector('.workspace-actions__filters');
        if (filters) {
            for (let i = 0; i < filters.children.length; i++)
                items.push(filters.children[i]);
        }
        const ai = row.querySelector('.workspace-actions__ai-toggle');
        if (ai)
            items.push(ai);
        return items;
    }

    function anyVisible(nodes) {
        if (!nodes)
            return false;
        for (let i = 0; i < nodes.length; i++) {
            if (!nodes[i].hidden)
                return true;
        }
        return false;
    }

    function fitRow(row) {
        if (row.clientWidth <= 0)
            return;

        const status = row.querySelector('.workspace-actions__refresh-status');
        const subtitle = row.querySelector('.workspace-actions__subtitle');
        const filters = row.querySelector('.workspace-actions__filters');
        const extras = optionals(row);

        if (status)
            status.classList.remove(SHORT);
        if (subtitle)
            subtitle.classList.remove(ELLIPSIS);
        for (let i = 0; i < extras.length; i++)
            setCollapsed(extras[i], false);

        if (!overflows(row))
            return;

        if (status)
            status.classList.add(SHORT);
        if (!overflows(row))
            return;

        for (let i = extras.length - 1; i >= 0 && overflows(row); i--)
            setCollapsed(extras[i], true);

        for (let i = 0; i < extras.length; i++) {
            if (!extras[i].hidden)
                continue;
            setCollapsed(extras[i], false);
            if (overflows(row)) {
                setCollapsed(extras[i], true);
                break;
            }
        }

        const ai = row.querySelector('.workspace-actions__ai-toggle');
        const filtersShown = anyVisible(filters ? filters.children : null);
        const aiShown = !!(ai && !ai.hidden);
        if (!filtersShown && !aiShown) {
            const sep = row.querySelector('.workspace-actions__filter-sep');
            if (sep)
                setCollapsed(sep, true);
        }

        if (overflows(row) && subtitle)
            subtitle.classList.add(ELLIPSIS);
    }

    function watch(row) {
        if (watched.has(row))
            return;
        watched.add(row);
        resizeObserver.observe(row);
        rowObserver.observe(row, { childList: true, subtree: true, characterData: true });
    }

    function fitAll() {
        if (fitting)
            return;
        fitting = true;
        try {
            document.querySelectorAll(ROW).forEach(function (row) {
                watch(row);
                fitRow(row);
            });
        } finally {
            fitting = false;
        }
    }

    function mutationMatters(mutation) {
        for (let i = 0; i < mutation.addedNodes.length; i++) {
            const node = mutation.addedNodes[i];
            if (node.nodeType !== 1 || !node.querySelector)
                continue;
            if (node.matches('.workspace-actions-header') || node.querySelector('.workspace-actions-header'))
                return true;
        }
        return false;
    }

    const mutationObserver = new MutationObserver(function (mutations) {
        if (fitting)
            return;
        for (let i = 0; i < mutations.length; i++) {
            if (mutationMatters(mutations[i])) {
                schedule();
                return;
            }
        }
    });

    function start() {
        mutationObserver.observe(document.body, { childList: true, subtree: true });
        window.addEventListener('resize', schedule);
        fitAll();
    }

    if (document.body)
        start();
    else
        document.addEventListener('DOMContentLoaded', start);
})();
