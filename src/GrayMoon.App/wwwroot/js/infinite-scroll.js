window.grayMoonInfiniteScroll = (function () {
    const observers = new WeakMap();

    function getScrollRoot(sentinel) {
        return sentinel.closest('tbody')
            || sentinel.closest('.repository-list')
            || null;
    }

    return {
        observe: function (sentinel, dotNetRef) {
            if (!sentinel) {
                return;
            }
            grayMoonInfiniteScroll.disconnect(sentinel);
            var scrollRoot = getScrollRoot(sentinel);
            var observer = new IntersectionObserver(function (entries) {
                entries.forEach(function (entry) {
                    if (entry.isIntersecting) {
                        dotNetRef.invokeMethodAsync('OnIntersect').catch(function () { });
                    }
                });
            }, { root: scrollRoot, rootMargin: '80px', threshold: 0 });
            observer.observe(sentinel);
            observers.set(sentinel, observer);
        },
        disconnect: function (sentinel) {
            if (!sentinel) {
                return;
            }
            var observer = observers.get(sentinel);
            if (observer) {
                observer.disconnect();
                observers.delete(sentinel);
            }
        }
    };
})();
