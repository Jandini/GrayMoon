window.grayMoonFilterSearch = {
    syncScroll: function (input, backdrop) {
        if (!input || !backdrop) {
            return;
        }
        backdrop.scrollLeft = input.scrollLeft;
    }
};
