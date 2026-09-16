/**
 * Shared dialog keyboard: Ctrl/Cmd+Enter invokes the dialog's default action by clicking the
 * same button the user would click (marked with data-default-action, else the last enabled
 * .btn-primary in .modal-footer). Capture-phase preventDefault stops a newline in textareas
 * before Blazor Server can see the key - a C# @onkeydown handler would be too late.
 *
 * Disabled / aria-disabled / busy primary buttons are not clicked. A loading overlay with
 * equal or higher z-index also blocks the shortcut (same as not being able to click).
 * Escape is left to each dialog's existing C# handler.
 */
(function () {
    function isEffectivelyEnabled(el) {
        if (!el || el.disabled)
            return false;
        if (el.getAttribute('aria-disabled') === 'true')
            return false;
        if (el.classList.contains('disabled'))
            return false;
        return true;
    }

    function getTopModal() {
        const modals = document.querySelectorAll('.modal.show');
        let top = null;
        let topZ = -Infinity;
        for (const modal of modals) {
            const z = parseFloat(window.getComputedStyle(modal).zIndex);
            const zVal = Number.isFinite(z) ? z : 0;
            if (!top || zVal >= topZ) {
                top = modal;
                topZ = zVal;
            }
        }
        return top;
    }

    function findDefaultActionButton(modal) {
        const marked = modal.querySelectorAll('[data-default-action]');
        if (marked.length > 0)
            return marked[marked.length - 1];

        const footer = modal.querySelector('.modal-footer');
        if (!footer)
            return null;
        const primaries = footer.querySelectorAll('button.btn-primary:not(.dropdown-toggle)');
        return primaries.length > 0 ? primaries[primaries.length - 1] : null;
    }

    /** True when a visible loading overlay sits on top of the dialog (clicks would not reach it). */
    function isCoveredByLoadingOverlay(modal) {
        const overlays = document.querySelectorAll('.loading-overlay.show');
        if (overlays.length === 0)
            return false;

        const modalZ = parseFloat(window.getComputedStyle(modal).zIndex);
        const modalZVal = Number.isFinite(modalZ) ? modalZ : 0;
        for (const overlay of overlays) {
            const z = parseFloat(window.getComputedStyle(overlay).zIndex);
            const zVal = Number.isFinite(z) ? z : 0;
            if (zVal >= modalZVal)
                return true;
        }
        return false;
    }

    document.addEventListener('keydown', function (e) {
        if (e.repeat)
            return;
        if (!((e.ctrlKey || e.metaKey) && (e.key === 'Enter' || e.key === 'NumpadEnter')))
            return;

        const modal = getTopModal();
        if (!modal)
            return;
        if (isCoveredByLoadingOverlay(modal))
            return;

        const button = findDefaultActionButton(modal);
        if (!button)
            return;

        e.preventDefault();
        e.stopPropagation();
        if (typeof e.stopImmediatePropagation === 'function')
            e.stopImmediatePropagation();

        if (isEffectivelyEnabled(button))
            button.click();
    }, true);
})();
