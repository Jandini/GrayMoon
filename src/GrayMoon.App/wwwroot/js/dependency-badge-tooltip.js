/**

 * Positions dependency-badge tooltips with position:fixed so they escape the scrolling tbody

 * without toggling overflow (which would hide the grid scrollbar and shift header padding).

 */

(function () {

    const TOOLTIP_SELECTOR = '.dependency-badge-tooltip-wrap';

    const TIP_CLASS = 'dependency-badge-tooltip';



    function positionTooltip(wrap) {

        const tip = wrap.querySelector('.' + TIP_CLASS);

        if (!tip) return;

        const anchor = wrap.getBoundingClientRect();

        const tipWidth = tip.offsetWidth || 480;

        const margin = 8;

        let left = anchor.right - tipWidth;

        if (left < margin) left = margin;

        const maxLeft = window.innerWidth - tipWidth - margin;

        if (left > maxLeft) left = Math.max(margin, maxLeft);

        tip.style.top = anchor.bottom + 'px';

        tip.style.left = left + 'px';

    }



    function repositionOpenTooltips() {

        document.querySelectorAll(TOOLTIP_SELECTOR + ':hover, ' + TOOLTIP_SELECTOR + ':focus-within')

            .forEach(positionTooltip);

    }



    function onWrapActivated(e) {

        const wrap = e.target.closest(TOOLTIP_SELECTOR);

        if (wrap) positionTooltip(wrap);

    }



    document.addEventListener('mouseover', onWrapActivated, true);

    document.addEventListener('mouseenter', onWrapActivated, true);

    document.addEventListener('focusin', onWrapActivated, true);

    document.addEventListener('scroll', repositionOpenTooltips, true);

    window.addEventListener('resize', repositionOpenTooltips);

})();

