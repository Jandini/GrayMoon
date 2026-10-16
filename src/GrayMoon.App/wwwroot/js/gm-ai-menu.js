// Closes an open AI provider menu when the user presses outside its control. A full-screen backdrop cannot do this
// on the Changes page: the commit area is a CSS query container, which contains fixed-position elements, so a
// "fixed" backdrop would only cover that area.
window.gmAiMenu = {
    watch(menu, dotNetRef) {
        if (!menu) return;
        window.gmAiMenu.unwatch(menu);
        const control = menu.closest('.gm-split-control') || menu.parentElement;
        const onPointerDown = (e) => {
            if (!menu.isConnected) {
                document.removeEventListener('pointerdown', onPointerDown, true);
                return;
            }
            if (control && control.contains(e.target)) return;
            document.removeEventListener('pointerdown', onPointerDown, true);
            dotNetRef.invokeMethodAsync('CloseMenuFromOutside');
        };
        menu._gmAiOutside = onPointerDown;
        document.addEventListener('pointerdown', onPointerDown, true);
    },

    unwatch(menu) {
        if (menu && menu._gmAiOutside) {
            document.removeEventListener('pointerdown', menu._gmAiOutside, true);
            menu._gmAiOutside = null;
        }
    }
};
