/**
 * Positions ".gm-menu-panel" popups with position:fixed so they escape the scrolling repositories
 * table without toggling overflow (mirrors dependency-badge-tooltip.js). Visibility itself is pure
 * CSS (":hover"/":focus-within" on the owning ".gm-menu-trigger"); this script only computes
 * top/left/right so each panel appears next to its trigger instead of at its static (off-screen)
 * position. Works for any nesting depth: the level-actions menu is a ".gm-menu-trigger" whose
 * panel contains further ".gm-menu-trigger" rows (the "Share..." / "Open in GitHub"
 * flyouts), each with its own direct-child ".gm-menu-panel" positioned independently.
 */
(function () {
    const TRIGGER_CLASS = 'gm-menu-trigger';
    const TRIGGER_SEL = '.' + TRIGGER_CLASS;
    const PANEL_CLASS = 'gm-menu-panel';
    const SUBMENU_CLASS = 'gm-menu-panel--submenu';
    const FORCE_CLOSED_CLASS = 'gm-menu-force-closed';
    const MARGIN = 8;

    function ownPanel(trigger) {
        for (const child of trigger.children) {
            if (child.classList.contains(PANEL_CLASS)) return child;
        }
        return null;
    }

    function positionPanel(trigger, panel) {
        const anchor = trigger.getBoundingClientRect();

        if (panel.classList.contains(SUBMENU_CLASS)) {
            // Flyout beside its row, like a native context menu: prefer the left side (triggers sit
            // near the right edge of the table), fall back to the right if there isn't room.
            const panelWidth = panel.offsetWidth || 220;
            let left = anchor.left - panelWidth - 2;
            if (left < MARGIN) left = anchor.right + 2;

            const panelHeight = panel.offsetHeight || 0;
            let top = anchor.top;
            if (top + panelHeight > window.innerHeight - MARGIN) {
                top = Math.max(MARGIN, window.innerHeight - panelHeight - MARGIN);
            }

            panel.style.left = left + 'px';
            panel.style.right = 'auto';
            panel.style.top = top + 'px';
            return;
        }

        // Top-level panel: directly below the trigger (no gap). Contiguous with the trigger, rather
        // than leaving a screen-space gap, so a slow mouse move down into the panel never crosses dead
        // space. Flip above the trigger (still contiguous) when there isn't room below to show it in
        // full; if it doesn't fit either way, clamp so it's at least fully on-screen and scrollable.
        const panelHeight = panel.offsetHeight || 0;
        let top = anchor.bottom;
        if (top + panelHeight > window.innerHeight - MARGIN) {
            const above = anchor.top - panelHeight;
            top = above >= MARGIN ? above : Math.max(MARGIN, window.innerHeight - panelHeight - MARGIN);
        }

        // Horizontal anchor: the repo-name trigger's width tracks its (variable-length) text, so
        // right-aligning it would make the panel's left edge drift with the repository name's length -
        // and run off the left edge of the screen entirely for short names, since that column sits at
        // the left of the table. Left-align to the trigger instead, falling back to right-aligned only
        // if there isn't room on the right. The level-actions count trigger sits at the right edge of
        // the row, so it keeps the original right-alignment.
        const panelWidth = panel.offsetWidth || 0;
        let left;
        if (trigger.classList.contains('repo-name-cell')) {
            left = anchor.left;
            if (left + panelWidth > window.innerWidth - MARGIN) {
                left = Math.max(MARGIN, window.innerWidth - panelWidth - MARGIN);
            }
        } else {
            left = Math.max(MARGIN, anchor.right - panelWidth);
        }

        panel.style.top = top + 'px';
        panel.style.left = left + 'px';
        panel.style.right = 'auto';
    }

    function repositionOpenPanels() {
        document.querySelectorAll(TRIGGER_SEL + ':hover, ' + TRIGGER_SEL + ':focus-within').forEach(trigger => {
            const panel = ownPanel(trigger);
            if (panel) positionPanel(trigger, panel);
        });
    }

    function onTriggerActivated(e) {
        const target = e.target;
        const el = target instanceof Element ? target : target?.parentElement;
        if (!el) return;
        const trigger = el.closest(TRIGGER_SEL);
        if (!trigger) return;
        const panel = ownPanel(trigger);
        if (panel) positionPanel(trigger, panel);
    }

    // Outermost ".gm-menu-trigger" ancestor of el (walks past nested flyout triggers up to the
    // top-level one, e.g. the level-actions "N repositories" trigger or the repo-name trigger).
    function outermostTrigger(el) {
        let current = el.closest(TRIGGER_SEL);
        let top = current;
        while (current) {
            const next = current.parentElement ? current.parentElement.closest(TRIGGER_SEL) : null;
            if (!next) break;
            current = next;
            top = current;
        }
        return top;
    }

    // Clicking an actual menu action (not a flyout row that only opens a submenu) should close the
    // whole menu immediately, rather than leaving it visible until the mouse physically moves away.
    function onMenuItemClick(e) {
        const target = e.target;
        const el = target instanceof Element ? target : target?.parentElement;
        if (!el) return;
        const item = el.closest('.gm-menu-item');
        if (!item || item.classList.contains(TRIGGER_CLASS)) return;
        const trigger = outermostTrigger(item);
        if (!trigger) return;
        trigger.classList.add(FORCE_CLOSED_CLASS);
        if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    }

    // Re-arm the trigger once the cursor actually leaves it (and its panel), so the next hover opens
    // the menu normally again.
    function onTriggerMouseLeave(e) {
        const target = e.target;
        if (!(target instanceof Element)) return;
        const trigger = target.closest(TRIGGER_SEL + '.' + FORCE_CLOSED_CLASS);
        if (trigger) trigger.classList.remove(FORCE_CLOSED_CLASS);
    }

    document.addEventListener('mouseover', onTriggerActivated, true);
    document.addEventListener('mouseenter', onTriggerActivated, true);
    document.addEventListener('focusin', onTriggerActivated, true);
    document.addEventListener('click', onMenuItemClick, true);
    document.addEventListener('mouseleave', onTriggerMouseLeave, true);
    document.addEventListener('scroll', repositionOpenPanels, true);
    window.addEventListener('resize', repositionOpenPanels);
})();
