window.grayMoonVirtualScroll = (function () {
    const stateByEl = new WeakMap();

    function invokeScroll(el, state, scrollTop, clientHeight) {
        state.inflight = true;
        state.dotNetRef.invokeMethodAsync('OnVirtualScroll', scrollTop, clientHeight)
            .catch(function () { })
            .finally(function () {
                state.inflight = false;
                if (state.pending) {
                    var pending = state.pending;
                    state.pending = null;
                    invokeScroll(el, state, pending.scrollTop, pending.clientHeight);
                }
            });
    }

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
            var scrollTop = el.scrollTop;
            var clientHeight = el.clientHeight;
            if (state.inflight) {
                state.pending = { scrollTop: scrollTop, clientHeight: clientHeight };
                return;
            }
            invokeScroll(el, state, scrollTop, clientHeight);
        });
    }

    return {
        attach: function (tbody, dotNetRef, totalHeight) {
            if (!tbody) {
                return;
            }
            grayMoonVirtualScroll.detach(tbody);
            var state = {
                dotNetRef: dotNetRef,
                raf: 0,
                totalHeight: totalHeight || 0,
                inflight: false,
                pending: null
            };
            stateByEl.set(tbody, state);
            tbody.addEventListener('scroll', onScroll, { passive: true });
            tbody.scrollTop = 0;
            invokeScroll(tbody, state, 0, tbody.clientHeight);
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
            state.pending = null;
            stateByEl.delete(tbody);
        }
    };
})();
