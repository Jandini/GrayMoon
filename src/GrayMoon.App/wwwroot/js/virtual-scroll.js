window.grayMoonVirtualScroll = (function () {
    const stateByEl = new WeakMap();
    function onScroll(ev) {
        var el = ev.currentTarget;
        var state = stateByEl.get(el);
        if (!state || !state.dotNetRef) {
            return;
        }
        if (state.raf) {
            cancelAnimationFrame(state.raf);
        }
        state.raf = requestAnimationFrame(function () {
            state.raf = 0;
            state.dotNetRef.invokeMethodAsync('OnVirtualScroll', el.scrollTop, el.clientHeight).catch(function () { });
        });
    }
    return {
        attach: function (tbody, dotNetRef, totalHeight) {
            if (!tbody) {
                return;
            }
            grayMoonVirtualScroll.detach(tbody);
            var state = { dotNetRef: dotNetRef, raf: 0, totalHeight: totalHeight || 0 };
            stateByEl.set(tbody, state);
            tbody.addEventListener('scroll', onScroll, { passive: true });
            tbody.scrollTop = 0;
            dotNetRef.invokeMethodAsync('OnVirtualScroll', 0, tbody.clientHeight).catch(function () { });
        },
        setTotalHeight: function (tbody, totalHeight) {
            var state = tbody ? stateByEl.get(tbody) : null;
            if (state) {
                state.totalHeight = totalHeight || 0;
            }
        },
        detach: function (tbody) {
            if (!tbody) {
                return;
            }
            var state = stateByEl.get(tbody);
            if (!state) {
                return;
            }
            tbody.removeEventListener('scroll', onScroll);
            if (state.raf) {
                cancelAnimationFrame(state.raf);
            }
            stateByEl.delete(tbody);
        }
    };
})();
