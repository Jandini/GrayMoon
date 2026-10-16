// Closes an open AI provider menu when the user presses outside its control. A full-screen backdrop cannot do this
// on the Changes page: the commit area is a CSS query container, which contains fixed-position elements, so a
// "fixed" backdrop would only cover that area.
// Also keeps the open menu inside the window: its height is capped at the space left below its top edge, and the
// menu scrolls (ai-generation.css) when its entries need more.
window.gmAiMenu = {
    // Gap kept between the menu and the bottom of the window, in pixels.
    bottomMargin: 8,

    watch(menu, dotNetRef) {
        if (!menu) return;
        window.gmAiMenu.unwatch(menu);
        const control = menu.closest('.gm-split-control') || menu.parentElement;
        const onPointerDown = (e) => {
            if (!menu.isConnected) {
                window.gmAiMenu.unwatch(menu);
                return;
            }
            if (control && control.contains(e.target)) return;
            window.gmAiMenu.unwatch(menu);
            dotNetRef.invokeMethodAsync('CloseMenuFromOutside');
        };
        const onViewportChange = () => {
            if (!menu.isConnected) {
                window.gmAiMenu.unwatch(menu);
                return;
            }
            window.gmAiMenu.fit(menu);
        };
        menu._gmAiOutside = onPointerDown;
        menu._gmAiViewport = onViewportChange;
        document.addEventListener('pointerdown', onPointerDown, true);
        window.addEventListener('resize', onViewportChange);
        // Capture: a scrolling ancestor (the file-tree pane) moves the menu too, not just the window.
        document.addEventListener('scroll', onViewportChange, true);
        window.gmAiMenu.fit(menu);
    },

    fit(menu) {
        const top = menu.getBoundingClientRect().top;
        const available = Math.floor(window.innerHeight - top - window.gmAiMenu.bottomMargin);
        menu.style.setProperty('--gm-ai-menu-max-height', `${Math.max(available, 0)}px`);
    },

    unwatch(menu) {
        if (!menu) return;
        if (menu._gmAiOutside) {
            document.removeEventListener('pointerdown', menu._gmAiOutside, true);
            menu._gmAiOutside = null;
        }
        if (menu._gmAiViewport) {
            window.removeEventListener('resize', menu._gmAiViewport);
            document.removeEventListener('scroll', menu._gmAiViewport, true);
            menu._gmAiViewport = null;
        }
    }
};
