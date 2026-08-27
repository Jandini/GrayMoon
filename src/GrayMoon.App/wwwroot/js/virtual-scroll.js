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

    function persistScrollTop(state, scrollTop) {
        if (!state.storageKey) {
            return;
        }
        try {
            sessionStorage.setItem(state.storageKey, String(scrollTop));
        } catch (e) {
            /* storage unavailable (e.g. private mode quota) - scroll restore is best-effort */
        }
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
            persistScrollTop(state, scrollTop);
            if (state.inflight) {
                state.pending = { scrollTop: scrollTop, clientHeight: clientHeight };
                return;
            }
            invokeScroll(el, state, scrollTop, clientHeight);
        });
    }

    return {
        attach: function (tbody, dotNetRef, totalHeight, initialScrollTop, storageKey) {
            if (!tbody) {
                return;
            }
            grayMoonVirtualScroll.detach(tbody);
            var state = {
                dotNetRef: dotNetRef,
                raf: 0,
                totalHeight: totalHeight || 0,
                inflight: false,
                pending: null,
                storageKey: storageKey || null
            };
            stateByEl.set(tbody, state);
            tbody.addEventListener('scroll', onScroll, { passive: true });
            var restoreTop = (typeof initialScrollTop === 'number' && isFinite(initialScrollTop) && initialScrollTop > 0)
                ? initialScrollTop
                : 0;
            tbody.scrollTop = restoreTop;
            var actualTop = tbody.scrollTop;
            invokeScroll(tbody, state, actualTop, tbody.clientHeight);
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
