/**
 * Positions dependency-badge tooltips with position:fixed so they escape the scrolling tbody
 * without toggling overflow (which would hide the grid scrollbar and shift header padding).
 *
 * MERGE NOTE (keep when merging into branches ahead of main):
 * Tooltip content is loaded lazily on first hover (EnsureTooltipDataForRepoAsync). Until that
 * data arrives the .dependency-badge-tooltip node is not in the DOM, so mouseenter/mouseover
 * cannot position it. After Blazor inserts the tip mid-hover, those events do not re-fire —
 * without the deferred watch below (and the C# repositionOpen call after load), the tip stays
 * at top/left -9999px. The metrics-block title="Dependencies" browser hint still appears, so
 * users see the hint but no popup until they leave and re-hover. Do not drop this path.
 */
(function () {
    const TOOLTIP_SELECTOR = '.dependency-badge-tooltip-wrap';
    const TIP_CLASS = 'dependency-badge-tooltip';
    const pendingObservers = new WeakMap();

    function positionTooltip(wrap) {
        const tip = wrap.querySelector('.' + TIP_CLASS);
        if (!tip) return false;

        const anchor = wrap.getBoundingClientRect();
        const tipWidth = tip.offsetWidth || 480;
        const margin = 8;
        let left = anchor.right - tipWidth;
        if (left < margin) left = margin;
        const maxLeft = window.innerWidth - tipWidth - margin;
        if (left > maxLeft) left = Math.max(margin, maxLeft);
        tip.style.top = anchor.bottom + 'px';
        tip.style.left = left + 'px';
        return true;
    }

    /**
     * When the tip is inserted after hover started (lazy Blazor load), position it as soon as
     * it appears. mouseenter will not fire again while the cursor stays on the badge.
     */
    function watchForTip(wrap) {
        if (pendingObservers.has(wrap)) return;

        const observer = new MutationObserver(function () {
            if (positionTooltip(wrap)) {
                observer.disconnect();
                pendingObservers.delete(wrap);
            }
        });
        pendingObservers.set(wrap, observer);
        observer.observe(wrap, { childList: true, subtree: true });

        // Bound the watch if the user leaves before data arrives or load fails.
        setTimeout(function () {
            if (pendingObservers.get(wrap) === observer) {
                observer.disconnect();
                pendingObservers.delete(wrap);
            }
        }, 15000);
    }

    function ensurePositioned(wrap) {
        if (!positionTooltip(wrap)) {
            watchForTip(wrap);
        }
    }

    function repositionOpenTooltips() {
        document.querySelectorAll(TOOLTIP_SELECTOR + ':hover, ' + TOOLTIP_SELECTOR + ':focus-within')
            .forEach(ensurePositioned);
    }

    function onWrapActivated(e) {
        const target = e.target;
        const el = target instanceof Element ? target : target?.parentElement;
        if (!el) return;
        const wrap = el.closest(TOOLTIP_SELECTOR);
        if (wrap) ensurePositioned(wrap);
    }

    document.addEventListener('mouseover', onWrapActivated, true);
    document.addEventListener('mouseenter', onWrapActivated, true);
    document.addEventListener('focusin', onWrapActivated, true);
    document.addEventListener('scroll', repositionOpenTooltips, true);
    window.addEventListener('resize', repositionOpenTooltips);

    // Called from Blazor after EnsureTooltipDataForRepoAsync renders tip content mid-hover.
    window.grayMoonDependencyBadgeTooltip = {
        repositionOpen: repositionOpenTooltips
    };
})();
