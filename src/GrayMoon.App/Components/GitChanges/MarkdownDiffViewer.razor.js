// Rendered markdown preview host for Git Changes. Uses the same co-located .razor.js +
// IJSObjectReference pattern as GitDiffViewer so each instance can be set/cleared/disposed
// independently. Mermaid is lazy-loaded from /mermaid/mermaid.min.js on first diagram.

const viewers = new Map();
let mermaidReadyPromise = null;

function ensureMermaidLoaded() {
    const existingApi = resolveMermaidApi();
    if (existingApi) {
        return Promise.resolve(existingApi);
    }

    if (mermaidReadyPromise) {
        return mermaidReadyPromise;
    }

    mermaidReadyPromise = new Promise((resolve, reject) => {
        const finishOk = (api) => {
            try {
                api.initialize({
                    startOnLoad: false,
                    theme: 'dark',
                    securityLevel: 'strict',
                });
            } catch (err) {
                mermaidReadyPromise = null;
                reject(err);
                return;
            }
            window.mermaid = api;
            resolve(api);
        };

        const finishErr = (err) => {
            mermaidReadyPromise = null;
            reject(err instanceof Error ? err : new Error(String(err)));
        };

        const existing = document.querySelector('script[data-graymoon-mermaid]');
        if (existing) {
            const api = resolveMermaidApi();
            if (api) {
                finishOk(api);
                return;
            }
            // Prior tag present but API missing - remove and retry a fresh load.
            existing.remove();
        }

        // Monaco registers an AMD `define`. Some Mermaid bundles mis-detect it and never
        // assign window.mermaid. Hide AMD only while this classic script evaluates.
        const previousDefine = Object.getOwnPropertyDescriptor(window, 'define');
        const hideAmd = () => {
            try {
                Object.defineProperty(window, 'define', {
                    configurable: true,
                    writable: true,
                    value: undefined,
                });
            } catch {
                try { window.define = undefined; } catch { /* ignore */ }
            }
        };
        const restoreAmd = () => {
            try {
                if (previousDefine) {
                    Object.defineProperty(window, 'define', previousDefine);
                } else {
                    delete window.define;
                }
            } catch {
                /* ignore */
            }
        };

        hideAmd();

        const script = document.createElement('script');
        // Root-absolute so Blazor routes like /workspaces/1/changes do not resolve to
        // /workspaces/1/mermaid/... (base href="/" also helps; absolute is unambiguous).
        script.src = '/mermaid/mermaid.min.js';
        script.async = true;
        script.dataset.graymoonMermaid = '1';
        script.onload = () => {
            restoreAmd();
            const api = resolveMermaidApi();
            if (!api) {
                finishErr(new Error('mermaid.js loaded but window.mermaid is missing'));
                return;
            }
            finishOk(api);
        };
        script.onerror = () => {
            restoreAmd();
            finishErr(new Error('Failed to load mermaid.js'));
        };
        document.head.appendChild(script);
    });

    return mermaidReadyPromise;
}

function resolveMermaidApi() {
    if (window.mermaid && typeof window.mermaid.initialize === 'function' && typeof window.mermaid.render === 'function') {
        return window.mermaid;
    }

    const bundled = globalThis.__esbuild_esm_mermaid_nm?.mermaid;
    if (!bundled) {
        return null;
    }

    const api = bundled.default && typeof bundled.default.initialize === 'function'
        ? bundled.default
        : (typeof bundled.initialize === 'function' ? bundled : null);

    if (api) {
        window.mermaid = api;
    }

    return api;
}

function collectChangeNodes(root) {
    return Array.from(root.querySelectorAll('ins, del'));
}

function flashChange(node) {
    node.classList.add('markdown-diff-change-flash');
    window.setTimeout(() => node.classList.remove('markdown-diff-change-flash'), 900);
}

function attachPanZoom(container, content, options = {}) {
    let scale = 1;
    let translateX = 0;
    let translateY = 0;
    let isPanning = false;
    let startX = 0;
    let startY = 0;
    const minScale = 0.25;
    const maxScale = 8;
    // directInteract: lightbox mode - wheel zooms and drag pans without Alt.
    const {
        onExpand = null,
        onClose = null,
        showExpand = true,
        host = null,
        directInteract = false,
    } = options;
    const diagramHost = host || content.querySelector('.mermaid') || content;

    const apply = () => {
        content.style.transform = `translate(${translateX}px, ${translateY}px) scale(${scale})`;
    };

    const clearTextSelection = () => {
        const sel = window.getSelection?.();
        if (sel && sel.rangeCount > 0) {
            sel.removeAllRanges();
        }
    };

    const fit = () => {
        fitSvgToContainer(container, diagramHost);
        scale = 1;
        translateX = 0;
        translateY = 0;
        apply();
    };

    const zoomBy = (factor, clientX, clientY) => {
        const rect = container.getBoundingClientRect();
        const cx = (clientX ?? (rect.left + rect.width / 2)) - rect.left;
        const cy = (clientY ?? (rect.top + rect.height / 2)) - rect.top;
        const prev = scale;
        scale = Math.min(maxScale, Math.max(minScale, scale * factor));
        translateX = cx - (cx - translateX) * (scale / prev);
        translateY = cy - (cy - translateY) * (scale / prev);
        apply();
    };

    const controls = document.createElement('div');
    controls.className = 'mermaid-panzoom-controls';
    const expandBtn = showExpand
        ? '<button type="button" class="mermaid-panzoom-btn" data-act="expand" title="Expand diagram" aria-label="Expand diagram"><i class="bi bi-arrows-fullscreen" aria-hidden="true"></i></button>'
        : '';
    controls.innerHTML =
        '<button type="button" class="mermaid-panzoom-btn" data-act="in" title="Zoom in" aria-label="Zoom in"><i class="bi bi-zoom-in" aria-hidden="true"></i></button>' +
        '<button type="button" class="mermaid-panzoom-btn" data-act="out" title="Zoom out" aria-label="Zoom out"><i class="bi bi-zoom-out" aria-hidden="true"></i></button>' +
        '<button type="button" class="mermaid-panzoom-btn" data-act="reset" title="Reset view" aria-label="Reset view"><i class="bi bi-arrow-counterclockwise" aria-hidden="true"></i></button>' +
        expandBtn;

    controls.addEventListener('click', (e) => {
        const btn = e.target.closest('[data-act]');
        if (!btn) {
            return;
        }
        e.preventDefault();
        e.stopPropagation();
        const act = btn.getAttribute('data-act');
        if (act === 'in') {
            zoomBy(1.2);
        } else if (act === 'out') {
            zoomBy(1 / 1.2);
        } else if (act === 'reset') {
            fit();
        } else if (act === 'expand' && typeof onExpand === 'function') {
            onExpand();
        }
    });

    container.appendChild(controls);
    container.classList.add('mermaid-panzoom--ready');
    if (directInteract) {
        container.classList.add('mermaid-panzoom--direct');
    }
    content.style.transformOrigin = '0 0';
    // Prevent accidental text selection while dragging / panning the diagram.
    container.style.userSelect = 'none';
    container.style.webkitUserSelect = 'none';
    container.addEventListener('selectstart', (e) => e.preventDefault());

    requestAnimationFrame(() => {
        fit();
        // Second pass after layout settles (lightbox flex height especially).
        requestAnimationFrame(fit);
    });

    container.addEventListener(
        'wheel',
        (e) => {
            if (!directInteract && !e.altKey) {
                return;
            }
            e.preventDefault();
            clearTextSelection();
            const factor = e.deltaY < 0 ? 1.1 : 1 / 1.1;
            zoomBy(factor, e.clientX, e.clientY);
        },
        { passive: false },
    );

    container.addEventListener('pointerdown', (e) => {
        if (e.target.closest('.mermaid-panzoom-controls')) {
            return;
        }
        const canPan = directInteract || e.altKey;
        if (!canPan || e.button !== 0) {
            return;
        }
        e.preventDefault();
        clearTextSelection();
        isPanning = true;
        startX = e.clientX - translateX;
        startY = e.clientY - translateY;
        container.setPointerCapture(e.pointerId);
        container.classList.add('mermaid-panzoom--dragging');
    });

    container.addEventListener('pointermove', (e) => {
        if (!isPanning) {
            return;
        }
        e.preventDefault();
        clearTextSelection();
        translateX = e.clientX - startX;
        translateY = e.clientY - startY;
        apply();
    });

    const endPan = (e) => {
        if (!isPanning) {
            return;
        }
        isPanning = false;
        container.classList.remove('mermaid-panzoom--dragging');
        clearTextSelection();
        try {
            container.releasePointerCapture(e.pointerId);
        } catch {
            // ignore
        }
    };

    container.addEventListener('pointerup', endPan);
    container.addEventListener('pointercancel', endPan);

    container.addEventListener('dblclick', (e) => {
        if (e.target.closest('.mermaid-panzoom-controls')) {
            return;
        }
        if (typeof onClose === 'function') {
            e.preventDefault();
            onClose();
            return;
        }
        if (typeof onExpand === 'function') {
            e.preventDefault();
            onExpand();
        }
    });

    return { fit };
}

function getSvgNaturalSize(svg) {
    try {
        const viewBox = svg.viewBox?.baseVal;
        if (viewBox && viewBox.width > 0 && viewBox.height > 0) {
            return { width: viewBox.width, height: viewBox.height };
        }
    } catch {
        // ignore
    }

    try {
        const box = svg.getBBox();
        if (box.width > 0 && box.height > 0) {
            return { width: box.width, height: box.height };
        }
    } catch {
        // ignore
    }

    const attrW = parseFloat(svg.getAttribute('width') || '');
    const attrH = parseFloat(svg.getAttribute('height') || '');
    if (Number.isFinite(attrW) && Number.isFinite(attrH) && attrW > 0 && attrH > 0) {
        return { width: attrW, height: attrH };
    }

    return null;
}

/** Scale the SVG to fill the container while preserving aspect ratio (CSS object-fit: contain). */
function fitSvgToContainer(container, host) {
    const svg = host?.querySelector?.('svg') || (host?.tagName === 'svg' ? host : null);
    if (!svg || !container) {
        return;
    }

    const natural = getSvgNaturalSize(svg);
    if (!natural) {
        return;
    }

    const style = window.getComputedStyle(container);
    const padX = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);
    const padY = (parseFloat(style.paddingTop) || 0) + (parseFloat(style.paddingBottom) || 0);
    const availW = Math.max(40, container.clientWidth - padX - 16);
    const availH = Math.max(40, container.clientHeight - padY - 16);
    if (availW <= 0 || availH <= 0) {
        return;
    }

    const fitScale = Math.min(availW / natural.width, availH / natural.height);
    const displayW = Math.max(1, Math.floor(natural.width * fitScale));
    const displayH = Math.max(1, Math.floor(natural.height * fitScale));

    svg.setAttribute('width', String(displayW));
    svg.setAttribute('height', String(displayH));
    svg.style.cssText = `width:${displayW}px;height:${displayH}px;max-width:none;max-height:none;display:block;`;
}

function sizeMermaidFrame(wrap) {
    const vh = window.innerHeight || 800;
    // Tall default frame; diagram is then fit-to-fill this box.
    const preferred = Math.max(340, Math.min(580, Math.round(vh * 0.48)));
    wrap.style.minHeight = `${preferred}px`;
    wrap.style.height = `${preferred}px`;
}

function cleanHeadingTitle(text) {
    let t = (text || '').replace(/\s+/g, ' ').trim();
    // Strip leading/trailing punctuation and markdown leftovers (# : - | ·).
    t = t.replace(/^[\s#.:\-\u2013\u2014|·•*]+/u, '').replace(/[\s#.:\-\u2013\u2014|·•*]+$/u, '').trim();
    return t || 'Diagram';
}

/** Walk a few previous siblings for the nearest heading to title the lightbox. */
function findPrecedingHeadingTitle(fromEl) {
    let el = fromEl?.previousElementSibling || null;
    for (let i = 0; el && i < 10; i++, el = el.previousElementSibling) {
        if (/^H[1-6]$/i.test(el.tagName)) {
            return cleanHeadingTitle(el.textContent || '');
        }
    }

    const parent = fromEl?.parentElement;
    if (parent) {
        el = parent.previousElementSibling;
        for (let i = 0; el && i < 6; i++, el = el.previousElementSibling) {
            if (/^H[1-6]$/i.test(el.tagName)) {
                return cleanHeadingTitle(el.textContent || '');
            }
        }
    }

    return 'Diagram';
}

function closeMermaidLightbox() {
    const existing = document.getElementById('gm-mermaid-lightbox');
    if (!existing) {
        return;
    }
    existing.remove();
    document.body.classList.remove('gm-mermaid-lightbox-open');
    document.removeEventListener('keydown', onMermaidLightboxKeydown, true);
}

function onMermaidLightboxKeydown(e) {
    if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        closeMermaidLightbox();
    }
}

async function openMermaidLightbox(source, title) {
    closeMermaidLightbox();

    let mermaid;
    try {
        mermaid = await ensureMermaidLoaded();
    } catch (err) {
        return;
    }

    const label = cleanHeadingTitle(title) || 'Diagram';
    const overlay = document.createElement('div');
    overlay.id = 'gm-mermaid-lightbox';
    overlay.className = 'mermaid-lightbox';
    overlay.setAttribute('role', 'dialog');
    overlay.setAttribute('aria-modal', 'true');
    overlay.setAttribute('aria-label', label);

    const titleEl = document.createElement('div');
    titleEl.className = 'mermaid-lightbox__title';
    titleEl.textContent = label;

    const closeBtn = document.createElement('button');
    closeBtn.type = 'button';
    closeBtn.className = 'mermaid-lightbox__close';
    closeBtn.title = 'Close';
    closeBtn.setAttribute('aria-label', 'Close');
    closeBtn.innerHTML = '<i class="bi bi-x-lg" aria-hidden="true"></i>';

    const stage = document.createElement('div');
    stage.className = 'mermaid-panzoom mermaid-panzoom--lightbox';
    const content = document.createElement('div');
    content.className = 'mermaid-panzoom__content';
    const host = document.createElement('div');
    host.className = 'mermaid';
    content.appendChild(host);
    stage.appendChild(content);

    overlay.appendChild(titleEl);
    overlay.appendChild(closeBtn);
    overlay.appendChild(stage);
    document.body.appendChild(overlay);
    document.body.classList.add('gm-mermaid-lightbox-open');
    document.addEventListener('keydown', onMermaidLightboxKeydown, true);

    closeBtn.addEventListener('click', (e) => {
        e.preventDefault();
        closeMermaidLightbox();
    });

    try {
        const id = `gm-mermaid-lb-${Math.random().toString(36).slice(2)}`;
        const { svg } = await mermaid.render(id, source);
        host.innerHTML = svg;
        attachPanZoom(stage, content, {
            showExpand: false,
            host,
            directInteract: true,
            onClose: closeMermaidLightbox,
        });
        closeBtn.focus();
    } catch (err) {
        const pre = document.createElement('pre');
        pre.className = 'mermaid-fallback';
        pre.textContent = source;
        const caption = document.createElement('div');
        caption.className = 'mermaid-error';
        caption.textContent = err?.message ? `Mermaid: ${err.message}` : 'Mermaid diagram failed to render.';
        stage.replaceWith(caption, pre);
    }
}

/** Map HtmlDiff wrappers onto Mermaid frames so Old/New keep red/green after pre→panzoom swap. */
function resolveMermaidDiffKind(node) {
    if (node.closest('ins.diffins, ins.diffmod, .markdown-diff-all-ins')) {
        return 'added';
    }
    if (node.closest('del.diffdel, del.diffmod, .markdown-diff-all-del')) {
        return 'removed';
    }
    return null;
}

async function renderMermaidIn(root) {
    const nodes = Array.from(root.querySelectorAll('pre.mermaid'));
    if (nodes.length === 0) {
        return;
    }

    let mermaid;
    try {
        mermaid = await ensureMermaidLoaded();
    } catch (err) {
        for (const node of nodes) {
            const caption = document.createElement('div');
            caption.className = 'mermaid-error';
            caption.textContent = err?.message
                ? `Mermaid library failed to load: ${err.message}`
                : 'Mermaid library failed to load.';
            node.insertAdjacentElement('afterend', caption);
        }
        return;
    }

    for (const node of nodes) {
        const source = node.textContent ?? '';
        const title = findPrecedingHeadingTitle(node);
        const wrap = document.createElement('div');
        wrap.className = 'mermaid-panzoom';
        const diffKind = resolveMermaidDiffKind(node);
        if (diffKind) {
            wrap.classList.add(`mermaid-panzoom--${diffKind}`);
            wrap.dataset.mermaidDiff = diffKind;
        }
        wrap.dataset.mermaidSource = source;
        wrap.dataset.mermaidTitle = title;
        const content = document.createElement('div');
        content.className = 'mermaid-panzoom__content';
        const host = document.createElement('div');
        host.className = 'mermaid';
        content.appendChild(host);
        wrap.appendChild(content);
        node.replaceWith(wrap);

        try {
            const id = `gm-mermaid-${Math.random().toString(36).slice(2)}`;
            const { svg } = await mermaid.render(id, source);
            host.innerHTML = svg;
            sizeMermaidFrame(wrap);
            attachPanZoom(wrap, content, {
                showExpand: true,
                host,
                onExpand: () => openMermaidLightbox(source, title),
            });
        } catch (err) {
            const pre = document.createElement('pre');
            pre.className = 'mermaid-fallback';
            pre.textContent = source;
            const caption = document.createElement('div');
            caption.className = 'mermaid-error';
            caption.textContent = err?.message ? `Mermaid: ${err.message}` : 'Mermaid diagram failed to render.';
            wrap.replaceWith(caption, pre);
        }
    }
}

function openSafeLinks(root) {
    for (const a of root.querySelectorAll('a[href]')) {
        a.setAttribute('target', '_blank');
        a.setAttribute('rel', 'noopener noreferrer');
    }
}

function attachBrokenImageFallbacks(root) {
    for (const img of root.querySelectorAll('img[src]')) {
        if (img.dataset.gmImgFallback === '1') {
            continue;
        }
        img.dataset.gmImgFallback = '1';
        img.addEventListener('error', () => {
            if (!img.isConnected) {
                return;
            }
            const label = (img.getAttribute('alt') || 'image').trim() || 'image';
            const chip = document.createElement('span');
            chip.className = 'markdown-img-fallback';
            chip.textContent = label;
            if (img.getAttribute('src')) {
                chip.title = img.getAttribute('src');
            }
            img.replaceWith(chip);
        });
    }
}

export async function init(elementId) {
    const el = document.getElementById(elementId);
    if (!el) {
        return false;
    }

    if (viewers.has(elementId)) {
        return true;
    }

    viewers.set(elementId, {
        el,
        changeIndex: -1,
    });
    return true;
}

export async function setHtml(elementId, html) {
    const entry = viewers.get(elementId);
    if (!entry) {
        return;
    }

    closeMermaidLightbox();
    entry.el.innerHTML = html || '';
    entry.changeIndex = -1;
    openSafeLinks(entry.el);
    attachBrokenImageFallbacks(entry.el);

    if (!html || !html.trim()) {
        entry.el.innerHTML = '<p class="markdown-diff-empty text-muted">Nothing to preview.</p>';
        return;
    }

    await renderMermaidIn(entry.el);
}

export function clear(elementId) {
    const entry = viewers.get(elementId);
    if (!entry) {
        return;
    }

    closeMermaidLightbox();
    entry.el.innerHTML = '';
    entry.changeIndex = -1;
}

export function goToNextChange(elementId) {
    const entry = viewers.get(elementId);
    if (!entry) {
        return;
    }

    const nodes = collectChangeNodes(entry.el);
    if (nodes.length === 0) {
        return;
    }

    entry.changeIndex = (entry.changeIndex + 1) % nodes.length;
    const node = nodes[entry.changeIndex];
    node.scrollIntoView({ block: 'center', behavior: 'smooth' });
    flashChange(node);
}

export function goToPreviousChange(elementId) {
    const entry = viewers.get(elementId);
    if (!entry) {
        return;
    }

    const nodes = collectChangeNodes(entry.el);
    if (nodes.length === 0) {
        return;
    }

    entry.changeIndex = entry.changeIndex <= 0 ? nodes.length - 1 : entry.changeIndex - 1;
    const node = nodes[entry.changeIndex];
    node.scrollIntoView({ block: 'center', behavior: 'smooth' });
    flashChange(node);
}

export function dispose(elementId) {
    const entry = viewers.get(elementId);
    if (!entry) {
        return;
    }

    closeMermaidLightbox();
    entry.el.innerHTML = '';
    viewers.delete(elementId);
}
