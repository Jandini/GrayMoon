/**
 * Keeps the Changes title summary on one line. When the row is too narrow the branch
 * chip is hidden first, then the trailing stat chips from the right, so "M 6 +256 -53"
 * stays while it still fits. Chips come back when the row is wide enough.
 */
(function () {
    const SUMMARY = '.git-changes-header__summary';
    const BRANCH = '.git-changes-header__branch';
    const COLLAPSED = 'is-collapsed';

    const resizeObserver = new ResizeObserver(schedule);
    const summaryObserver = new MutationObserver(function () {
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

    function overflows(summary) {
        return summary.scrollWidth > summary.clientWidth + 1;
    }

    function afterBranch(summary, branch) {
        const items = [];
        let seen = !branch;
        for (let i = 0; i < summary.children.length; i++) {
            const el = summary.children[i];
            if (el === branch) {
                seen = true;
                continue;
            }
            if (seen && el.classList.contains('git-changes-header__summary-item'))
                items.push(el);
        }
        return items;
    }

    function fitSummary(summary) {
        if (summary.clientWidth <= 0)
            return;

        const branch = summary.querySelector(':scope > ' + BRANCH);
        const trailing = afterBranch(summary, branch);

        if (branch && !branch.hidden && overflows(summary))
            setCollapsed(branch, true);

        for (let i = trailing.length - 1; i >= 0 && overflows(summary); i--) {
            if (!trailing[i].hidden)
                setCollapsed(trailing[i], true);
        }

        // Put stat chips back from the left, and the branch only once those fit.
        for (let i = 0; i < trailing.length; i++) {
            if (!trailing[i].hidden)
                continue;
            setCollapsed(trailing[i], false);
            if (overflows(summary)) {
                setCollapsed(trailing[i], true);
                return;
            }
        }

        if (branch && branch.hidden) {
            setCollapsed(branch, false);
            if (overflows(summary))
                setCollapsed(branch, true);
        }
    }

    function watch(summary) {
        if (watched.has(summary))
            return;
        watched.add(summary);
        resizeObserver.observe(summary);
        summaryObserver.observe(summary, { childList: true, subtree: true, characterData: true });
        const row = summary.closest('.workspace-repos-title-row');
        if (row)
            resizeObserver.observe(row);
    }

    function fitAll() {
        if (fitting)
            return;
        fitting = true;
        try {
            document.querySelectorAll(SUMMARY).forEach(function (summary) {
                watch(summary);
                fitSummary(summary);
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
            if (node.matches(SUMMARY) || node.querySelector(SUMMARY))
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
