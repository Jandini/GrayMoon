/**
 * Hides feature-selector open-in buttons that would paint over the header actions
 * beside the context bar (Branch, Update, Push, Sync, and the rest). The name,
 * chevron, and New Feature button stay. Buttons come back when the row is wide enough.
 */
(function () {
    const SELECTOR = '.workspace-feature-selector';
    const TOOL = '.workspace-feature-selector__tool';
    const COLLAPSED = 'is-collapsed';

    const resizeObserver = new ResizeObserver(schedule);
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

    function limitRight(selector) {
        const bar = selector.closest('.workspace-context-search');
        let limit = bar ? bar.getBoundingClientRect().right : null;
        if (bar) {
            const nextLeft = nextVisibleLeft(bar);
            if (nextLeft != null)
                limit = limit == null ? nextLeft : Math.min(limit, nextLeft);
        }
        if (limit == null && selector.parentElement)
            limit = selector.parentElement.getBoundingClientRect().right;
        return limit;
    }

    function nextVisibleLeft(bar) {
        let next = bar.nextElementSibling;
        while (next) {
            const rect = next.getBoundingClientRect();
            if (rect.width > 0 && rect.height > 0)
                return rect.left;
            next = next.nextElementSibling;
        }
        return null;
    }

    function anchorRight(selector, tools) {
        const previous = tools[0] && tools[0].previousElementSibling;
        if (previous)
            return previous.getBoundingClientRect().right;
        return selector.getBoundingClientRect().left;
    }

    function setCollapsed(tool, collapsed) {
        tool.classList.toggle(COLLAPSED, collapsed);
        // hidden wins over the button's display:flex even before scoped CSS is rebuilt.
        tool.hidden = collapsed;
    }

    function toolWidth(tool) {
        if (!tool.classList.contains(COLLAPSED)) {
            const width = tool.getBoundingClientRect().width;
            if (width > 0)
                tool.dataset.slotWidth = String(width);
            return width;
        }
        const cached = Number(tool.dataset.slotWidth);
        return cached > 0 ? cached : 0;
    }

    function fitSelector(selector) {
        const tools = Array.from(selector.querySelectorAll(':scope > ' + TOOL));
        if (tools.length === 0)
            return;

        const limit = limitRight(selector);
        if (limit == null || limit <= 0)
            return;

        let edge = anchorRight(selector, tools);
        for (let i = 0; i < tools.length; i++) {
            const tool = tools[i];
            const collapsed = tool.classList.contains(COLLAPSED);
            const width = toolWidth(tool);
            // Not laid out yet (width 0). Leave it visible so the next pass can measure it.
            if (!(width > 0)) {
                if (collapsed)
                    setCollapsed(tool, false);
                continue;
            }
            // A hidden button needs a couple of spare pixels before it returns, so a
            // subpixel resize does not show it and immediately hide it again.
            const fits = edge + width <= limit - (collapsed ? 2 : 0);
            setCollapsed(tool, !fits);
            if (fits)
                edge += width;
        }
    }

    function watch(selector) {
        if (watched.has(selector))
            return;
        watched.add(selector);
        resizeObserver.observe(selector);
        const bar = selector.closest('.workspace-context-search');
        if (bar)
            resizeObserver.observe(bar);
        const header = selector.closest('.workspace-repos-header-actions');
        if (header)
            resizeObserver.observe(header);
    }

    function fitAll() {
        if (fitting)
            return;
        fitting = true;
        try {
            document.querySelectorAll(SELECTOR).forEach(function (selector) {
                watch(selector);
                fitSelector(selector);
            });
        } finally {
            fitting = false;
        }
    }

    function mutationMatters(mutation) {
        const target = mutation.target;
        if (target.nodeType === 1 && target.closest &&
            target.closest(SELECTOR + ', .workspace-context-search, .workspace-repos-header-actions'))
            return true;

        for (let i = 0; i < mutation.addedNodes.length; i++) {
            const node = mutation.addedNodes[i];
            if (node.nodeType !== 1 || !node.matches)
                continue;
            if (node.matches(SELECTOR) || node.matches(TOOL) ||
                (node.querySelector && node.querySelector(SELECTOR)))
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
