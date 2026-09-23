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
    let panMode = false;
    let startX = 0;
    let startY = 0;
    const minScale = 0.25;
    const maxScale = 5;
    const { onExpand = null, showExpand = true } = options;

    const apply = () => {
        content.style.transform = `translate(${translateX}px, ${translateY}px) scale(${scale})`;
    };

    const reset = () => {
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
        '<button type="button" class="mermaid-panzoom-btn" data-act="pan" title="Toggle pan mode" aria-label="Toggle pan mode"><i class="bi bi-arrows-move" aria-hidden="true"></i></button>' +
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
            reset();
        } else if (act === 'pan') {
            panMode = !panMode;
            btn.classList.toggle('is-active', panMode);
            container.classList.toggle('mermaid-panzoom--pan-mode', panMode);
        } else if (act === 'expand' && typeof onExpand === 'function') {
            onExpand();
        }
    });

    container.appendChild(controls);
    content.style.transformOrigin = '0 0';
    apply();

    container.addEventListener(
        'wheel',
        (e) => {
            if (!e.altKey) {
                return;
            }
            e.preventDefault();
            const factor = e.deltaY < 0 ? 1.1 : 1 / 1.1;
            zoomBy(factor, e.clientX, e.clientY);
        },
        { passive: false },
    );

    container.addEventListener('pointerdown', (e) => {
        if (!(panMode || e.altKey) || e.button !== 0) {
            return;
        }
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
        if (typeof onExpand === 'function') {
            e.preventDefault();
            onExpand();
        }
    });
}

function sizeMermaidFrame(wrap, host) {
    const svg = host.querySelector('svg');
    if (!svg) {
        wrap.style.height = '360px';
        return;
    }

    let naturalHeight = 0;
    try {
        const viewBox = svg.viewBox?.baseVal;
        if (viewBox && Number.isFinite(viewBox.height) && viewBox.height > 0) {
            naturalHeight = viewBox.height;
        }
    } catch {
        // ignore
    }

    if (naturalHeight <= 0) {
        try {
            const box = svg.getBBox();
            if (Number.isFinite(box.height) && box.height > 0) {
                naturalHeight = box.height;
            }
        } catch {
            // ignore
        }
    }

    if (naturalHeight <= 0) {
        const attrH = parseFloat(svg.getAttribute('height') || '');
        if (Number.isFinite(attrH) && attrH > 0) {
            naturalHeight = attrH;
        }
    }

    // Prefer a comfortable frame; never collapse (SVG max-height:100% + height:auto can paint as 0).
    const preferred = naturalHeight > 0
        ? Math.max(260, Math.min(640, Math.round(naturalHeight + 56)))
        : 360;
    wrap.style.minHeight = `${preferred}px`;
    wrap.style.height = `${preferred}px`;

    svg.style.maxWidth = '100%';
    svg.style.width = '100%';
    svg.style.height = 'auto';
    svg.style.maxHeight = 'none';
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

async function openMermaidLightbox(source) {
    closeMermaidLightbox();

    let mermaid;
    try {
        mermaid = await ensureMermaidLoaded();
    } catch (err) {
        return;
    }

    const overlay = document.createElement('div');
    overlay.id = 'gm-mermaid-lightbox';
    overlay.className = 'mermaid-lightbox';
    overlay.setAttribute('role', 'dialog');
    overlay.setAttribute('aria-modal', 'true');
    overlay.setAttribute('aria-label', 'Mermaid diagram');

    const panel = document.createElement('div');
    panel.className = 'mermaid-lightbox__panel';

    const header = document.createElement('div');
    header.className = 'mermaid-lightbox__header';
    header.innerHTML =
        '<span class="mermaid-lightbox__title">Diagram</span>' +
        '<span class="mermaid-lightbox__hint">Alt+wheel zoom · Esc close</span>' +
        '<button type="button" class="mermaid-lightbox__close" title="Close" aria-label="Close"><i class="bi bi-x-lg" aria-hidden="true"></i></button>';

    const stage = document.createElement('div');
    stage.className = 'mermaid-panzoom mermaid-panzoom--lightbox';
    const content = document.createElement('div');
    content.className = 'mermaid-panzoom__content';
    const host = document.createElement('div');
    host.className = 'mermaid';
    content.appendChild(host);
    stage.appendChild(content);

    panel.appendChild(header);
    panel.appendChild(stage);
    overlay.appendChild(panel);
    document.body.appendChild(overlay);
    document.body.classList.add('gm-mermaid-lightbox-open');
    document.addEventListener('keydown', onMermaidLightboxKeydown, true);

    header.querySelector('.mermaid-lightbox__close')?.addEventListener('click', (e) => {
        e.preventDefault();
        closeMermaidLightbox();
    });
    overlay.addEventListener('click', (e) => {
        if (e.target === overlay) {
            closeMermaidLightbox();
        }
    });

    try {
        const id = `gm-mermaid-lb-${Math.random().toString(36).slice(2)}`;
        const { svg } = await mermaid.render(id, source);
        host.innerHTML = svg;
        attachPanZoom(stage, content, { showExpand: false });
        header.querySelector('.mermaid-lightbox__close')?.focus();
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
        const wrap = document.createElement('div');
        wrap.className = 'mermaid-panzoom';
        wrap.dataset.mermaidSource = source;
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
            sizeMermaidFrame(wrap, host);
            attachPanZoom(wrap, content, {
                showExpand: true,
                onExpand: () => openMermaidLightbox(source),
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
