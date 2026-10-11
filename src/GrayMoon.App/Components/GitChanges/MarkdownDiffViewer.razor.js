// Rendered markdown preview host for Git Changes. Uses the same co-located .razor.js +
// IJSObjectReference pattern as GitDiffViewer so each instance can be set/cleared/disposed
// independently. Mermaid is lazy-loaded from /mermaid/mermaid.min.js on first diagram.

const viewers = new Map();
let mermaidReadyPromise = null;

function configureMermaid(api) {
    api.initialize({
        startOnLoad: false,
        theme: 'dark',
        securityLevel: 'strict',
        // Otherwise parse failures inject Mermaid's bomb SVG into document.body
        // and leave it there after we catch and show our own caption.
        suppressErrorRendering: true,
    });
}

function ensureMermaidLoaded() {
    const existingApi = resolveMermaidApi();
    if (existingApi) {
        try {
            configureMermaid(existingApi);
        } catch {
            /* ignore - keep using whatever config is already active */
        }
        return Promise.resolve(existingApi);
    }

    if (mermaidReadyPromise) {
        return mermaidReadyPromise;
    }

    mermaidReadyPromise = new Promise((resolve, reject) => {
        const finishOk = (api) => {
            try {
                configureMermaid(api);
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

/** Mermaid.render creates temporary #id / #d{id} nodes; remove leftovers after render returns.
 * Must run before assigning the returned SVG into the page - that SVG reuses the same id. */
function cleanupMermaidScratch(id) {
    if (!id) {
        return;
    }
    try {
        document.getElementById(id)?.remove();
        document.getElementById(`d${id}`)?.remove();
    } catch {
        /* ignore */
    }
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
        let svg;
        try {
            ({ svg } = await mermaid.render(id, source));
        } finally {
            // Strip Mermaid's temporary nodes before we insert the returned SVG
            // (the SVG string reuses the same id - cleaning after insert removes the diagram).
            cleanupMermaidScratch(id);
        }
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

    // Each fence is independent: a parse/render failure replaces only that frame with
    // an error caption + source fallback; later diagrams still render.
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
            let svg;
            try {
                ({ svg } = await mermaid.render(id, source));
            } finally {
                // Strip Mermaid's temporary nodes before we insert the returned SVG
                // (the SVG string reuses the same id - cleaning after insert removes the diagram).
                cleanupMermaidScratch(id);
            }
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

const FIND_MARK = 'markdown-find-match';
const FIND_MARK_CURRENT = 'markdown-find-match--current';
const FIND_SKIP_SELECTOR = 'script, style, textarea, input, title, desc, .mermaid-panzoom-controls, .markdown-find-widget';
const FIND_TYPE_DELAY_MS = 400;
const SVG_NS = 'http://www.w3.org/2000/svg';

let findKeyListener = null;

function escapeRegExp(value) {
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function buildFindRegex(query, matchCase, wholeWord, useRegex) {
    if (!query) {
        return { regex: null, error: false };
    }

    let source = query;
    if (useRegex) {
        try {
            // Validate before wrapping so a bad pattern surfaces as Invalid, not a throw.
            new RegExp(query);
        } catch {
            return { regex: null, error: true };
        }
    } else {
        source = escapeRegExp(query);
    }

    if (wholeWord) {
        source = `\\b(?:${source})\\b`;
    }

    try {
        return { regex: new RegExp(source, matchCase ? 'g' : 'gi'), error: false };
    } catch {
        return { regex: null, error: true };
    }
}

function isSvgTextContext(node) {
    const parent = node.parentElement;
    return !!parent && parent.namespaceURI === SVG_NS;
}

function createFindHighlight(text, inSvg) {
    if (inSvg) {
        const tspan = document.createElementNS(SVG_NS, 'tspan');
        tspan.setAttribute('class', FIND_MARK);
        tspan.textContent = text;
        return tspan;
    }

    const mark = document.createElement('mark');
    mark.className = FIND_MARK;
    mark.textContent = text;
    return mark;
}

function unwrapFindMarks(root) {
    const marks = root.querySelectorAll(`mark.${FIND_MARK}, tspan.${FIND_MARK}`);
    for (const mark of marks) {
        const parent = mark.parentNode;
        if (!parent) {
            continue;
        }

        while (mark.firstChild) {
            parent.insertBefore(mark.firstChild, mark);
        }

        parent.removeChild(mark);
        parent.normalize();
    }
}

function collectFindTextNodes(root) {
    const nodes = [];
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
        acceptNode(node) {
            if (!node.nodeValue) {
                return NodeFilter.FILTER_REJECT;
            }

            const parent = node.parentElement;
            if (!parent || parent.closest(FIND_SKIP_SELECTOR)) {
                return NodeFilter.FILTER_REJECT;
            }

            return NodeFilter.FILTER_ACCEPT;
        },
    });

    let node;
    while ((node = walker.nextNode())) {
        nodes.push(node);
    }

    return nodes;
}

function highlightFindMatches(root, regex) {
    unwrapFindMarks(root);
    const marks = [];
    const textNodes = collectFindTextNodes(root);

    for (const node of textNodes) {
        const text = node.nodeValue;
        regex.lastIndex = 0;
        const matches = [];
        let match;
        while ((match = regex.exec(text)) !== null) {
            if (!match[0]) {
                if (regex.lastIndex === match.index) {
                    regex.lastIndex += 1;
                }
                continue;
            }

            matches.push({ start: match.index, end: match.index + match[0].length });
            if (regex.lastIndex === match.index) {
                regex.lastIndex += 1;
            }
        }

        if (matches.length === 0) {
            continue;
        }

        const inSvg = isSvgTextContext(node);
        const frag = document.createDocumentFragment();
        let last = 0;
        for (const range of matches) {
            if (range.start > last) {
                frag.appendChild(document.createTextNode(text.slice(last, range.start)));
            }

            const mark = createFindHighlight(text.slice(range.start, range.end), inSvg);
            frag.appendChild(mark);
            marks.push(mark);
            last = range.end;
        }

        if (last < text.length) {
            frag.appendChild(document.createTextNode(text.slice(last)));
        }

        node.parentNode.replaceChild(frag, node);
    }

    return marks;
}

function selectedPreviewText(entry) {
    const selection = window.getSelection();
    if (!selection || selection.isCollapsed || !selection.rangeCount) {
        return '';
    }

    const anchor = selection.anchorNode;
    if (!anchor || !entry.el.contains(anchor)) {
        return '';
    }

    return (selection.toString() || '').replace(/\s+/g, ' ').trim().slice(0, 200);
}

function isPreviewSurfaceVisible(entry) {
    if (!entry?.el?.isConnected) {
        return false;
    }

    const container = entry.el.closest('.git-diff-markdown-container');
    if (container) {
        if (container.style.display === 'none') {
            return false;
        }

        const style = window.getComputedStyle(container);
        if (style.display === 'none' || style.visibility === 'hidden') {
            return false;
        }
    }

    const host = entry.wrap || entry.el;
    return host.getClientRects().length > 0;
}

function visibleFindEntry() {
    for (const entry of viewers.values()) {
        if (isPreviewSurfaceVisible(entry)) {
            return entry;
        }
    }

    return null;
}

function updateFindUi(entry) {
    if (!entry.widget) {
        return;
    }

    const matches = entry.findMarks || [];
    const label = entry.widget.querySelector('.markdown-find-widget__matches');
    if (label) {
        if (entry.findError) {
            label.textContent = 'Invalid';
            label.classList.add('is-error');
        } else if (!entry.findQuery) {
            label.textContent = '';
            label.classList.remove('is-error');
        } else if (matches.length === 0) {
            label.textContent = 'No results';
            label.classList.add('is-error');
        } else {
            label.textContent = `${entry.findIndex + 1} of ${matches.length}`;
            label.classList.remove('is-error');
        }
    }

    if (entry.findInput) {
        entry.findInput.classList.toggle('is-error', !!entry.findError);
        if (entry.findInput.value !== entry.findQuery) {
            entry.findInput.value = entry.findQuery;
        }
    }

    entry.widget.classList.toggle('is-no-results', !!entry.findQuery && !entry.findError && matches.length === 0);
    for (const btn of entry.widget.querySelectorAll('[data-opt]')) {
        const on = btn.dataset.opt === 'case' ? entry.findMatchCase
            : btn.dataset.opt === 'word' ? entry.findWholeWord
                : entry.findRegex;
        btn.classList.toggle('is-active', on);
        btn.setAttribute('aria-pressed', on ? 'true' : 'false');
    }
}

function revealFindMatch(entry, index, { smooth = true } = {}) {
    const marks = entry.findMarks || [];
    for (let i = 0; i < marks.length; i++) {
        marks[i].classList.toggle(FIND_MARK_CURRENT, i === index);
    }

    const current = marks[index];
    if (!current) {
        return;
    }

    try {
        current.scrollIntoView({ block: 'center', behavior: smooth ? 'smooth' : 'auto' });
    } catch {
        /* ignore - preview must stay usable even if a match node is odd */
    }
}

function cancelFindDebounce(entry) {
    if (entry.findDebounce) {
        clearTimeout(entry.findDebounce);
        entry.findDebounce = null;
    }
}

function scheduleFind(entry) {
    cancelFindDebounce(entry);
    entry.findDebounce = window.setTimeout(() => {
        entry.findDebounce = null;
        applyFind(entry, { reveal: true, smooth: false });
    }, FIND_TYPE_DELAY_MS);
}

function applyFind(entry, { reveal = true, smooth = true } = {}) {
    cancelFindDebounce(entry);
    if (!entry.findOpen) {
        unwrapFindMarks(entry.el);
        entry.findMarks = [];
        entry.findIndex = -1;
        updateFindUi(entry);
        return;
    }

    const { regex, error } = buildFindRegex(
        entry.findQuery,
        entry.findMatchCase,
        entry.findWholeWord,
        entry.findRegex);
    entry.findError = error;
    entry.findMarks = !error && regex ? highlightFindMatches(entry.el, regex) : [];
    entry.findIndex = entry.findMarks.length > 0 ? 0 : -1;
    updateFindUi(entry);
    if (reveal && entry.findIndex >= 0) {
        revealFindMatch(entry, entry.findIndex, { smooth });
    }
}

function findStep(entry, delta) {
    if (entry.findDebounce) {
        applyFind(entry, { reveal: false });
    }

    const marks = entry.findMarks || [];
    if (marks.length === 0) {
        return;
    }

    const next = (entry.findIndex + delta + marks.length) % marks.length;
    entry.findIndex = next;
    updateFindUi(entry);
    revealFindMatch(entry, next);
}

function findNext(entry) {
    findStep(entry, 1);
}

function findPrevious(entry) {
    findStep(entry, -1);
}

function closeFind(entry) {
    cancelFindDebounce(entry);
    entry.findOpen = false;
    unwrapFindMarks(entry.el);
    entry.findMarks = [];
    entry.findIndex = -1;
    if (entry.widget) {
        entry.widget.hidden = true;
    }

    updateFindUi(entry);
    try {
        entry.el.focus({ preventScroll: true });
    } catch {
        /* ignore */
    }
}

function openFind(entry, { seedFromSelection = true } = {}) {
    ensureFindWidget(entry);
    if (seedFromSelection) {
        const seeded = selectedPreviewText(entry);
        if (seeded) {
            entry.findQuery = seeded;
        }
    }

    entry.findOpen = true;
    entry.widget.hidden = false;
    applyFind(entry, { reveal: true });
    requestAnimationFrame(() => {
        entry.findInput?.focus();
        entry.findInput?.select();
    });
}

function bindFindWidget(entry, widget) {
    widget.addEventListener('input', (e) => {
        if (e.target === entry.findInput) {
            entry.findQuery = entry.findInput.value;
            scheduleFind(entry);
        }
    });

    widget.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
            e.preventDefault();
            const pending = !!entry.findDebounce;
            if (pending) {
                applyFind(entry, { reveal: true, smooth: false });
                return;
            }

            if (e.shiftKey) {
                findPrevious(entry);
            } else {
                findNext(entry);
            }
            return;
        }

        if (e.altKey && !e.ctrlKey && !e.metaKey) {
            const key = e.key.toLowerCase();
            if (key === 'c') {
                e.preventDefault();
                entry.findMatchCase = !entry.findMatchCase;
                applyFind(entry, { reveal: true });
            } else if (key === 'w') {
                e.preventDefault();
                entry.findWholeWord = !entry.findWholeWord;
                applyFind(entry, { reveal: true });
            } else if (key === 'r') {
                e.preventDefault();
                entry.findRegex = !entry.findRegex;
                applyFind(entry, { reveal: true });
            }
        }
    });

    widget.addEventListener('click', (e) => {
        const btn = e.target.closest('button');
        if (!btn || !widget.contains(btn)) {
            return;
        }

        if (btn.dataset.nav === 'next') {
            findNext(entry);
            return;
        }

        if (btn.dataset.nav === 'prev') {
            findPrevious(entry);
            return;
        }

        if (btn.dataset.act === 'close') {
            closeFind(entry);
            return;
        }

        if (btn.dataset.opt === 'case') {
            entry.findMatchCase = !entry.findMatchCase;
            applyFind(entry, { reveal: true });
        } else if (btn.dataset.opt === 'word') {
            entry.findWholeWord = !entry.findWholeWord;
            applyFind(entry, { reveal: true });
        } else if (btn.dataset.opt === 'regex') {
            entry.findRegex = !entry.findRegex;
            applyFind(entry, { reveal: true });
        }
    });
}

function ensureFindWidget(entry) {
    if (entry.widget?.isConnected) {
        return entry.widget;
    }

    const wrap = entry.el.closest('.markdown-diff-viewer-wrap') || entry.el.parentElement;
    entry.wrap = wrap;
    const widget = document.createElement('div');
    widget.className = 'markdown-find-widget';
    widget.hidden = !entry.findOpen;
    widget.setAttribute('role', 'search');
    widget.setAttribute('aria-label', 'Find');
    widget.innerHTML = `
        <div class="markdown-find-widget__row">
            <div class="markdown-find-widget__input-wrap">
                <input type="text" class="markdown-find-widget__input" placeholder="Find" aria-label="Find" autocomplete="off" autocorrect="off" autocapitalize="off" spellcheck="false" />
                <span class="markdown-find-widget__matches" aria-live="polite"></span>
            </div>
            <button type="button" class="markdown-find-widget__opt" data-opt="case" title="Match Case (Alt+C)" aria-label="Match Case" aria-pressed="false">Aa</button>
            <button type="button" class="markdown-find-widget__opt" data-opt="word" title="Match Whole Word (Alt+W)" aria-label="Match Whole Word" aria-pressed="false">ab</button>
            <button type="button" class="markdown-find-widget__opt" data-opt="regex" title="Use Regular Expression (Alt+R)" aria-label="Use Regular Expression" aria-pressed="false">.*</button>
            <button type="button" class="markdown-find-widget__nav" data-nav="prev" title="Previous Match (Shift+Enter)" aria-label="Previous Match"><i class="bi bi-chevron-up" aria-hidden="true"></i></button>
            <button type="button" class="markdown-find-widget__nav" data-nav="next" title="Next Match (Enter)" aria-label="Next Match"><i class="bi bi-chevron-down" aria-hidden="true"></i></button>
            <button type="button" class="markdown-find-widget__close" data-act="close" title="Close (Escape)" aria-label="Close"><i class="bi bi-x" aria-hidden="true"></i></button>
        </div>`;
    wrap.appendChild(widget);
    entry.widget = widget;
    entry.findInput = widget.querySelector('.markdown-find-widget__input');
    bindFindWidget(entry, widget);
    updateFindUi(entry);
    return widget;
}

function refreshFindAfterContent(entry) {
    if (!entry.findOpen) {
        entry.findMarks = [];
        entry.findIndex = -1;
        return;
    }

    ensureFindWidget(entry);
    applyFind(entry, { reveal: true });
}

function isFindTargetInPreview(target, entry) {
    if (!target || typeof target.closest !== 'function') {
        return false;
    }

    if (target.closest('.markdown-diff-viewer-wrap') || target.closest('.git-diff-markdown-container')) {
        return true;
    }

    return !!(entry && target.closest('.git-diff-chrome') && isPreviewSurfaceVisible(entry));
}

function onFindDocumentKeydown(e) {
    const entry = visibleFindEntry();
    if (!entry) {
        return;
    }

    if (document.getElementById('gm-mermaid-lightbox') || document.querySelector('.modal.show')) {
        return;
    }

    const target = e.target;
    const inFind = !!(target && typeof target.closest === 'function' && target.closest('.markdown-find-widget'));
    const inPreview = inFind || isFindTargetInPreview(target, entry);
    const inOtherField = !!(target && typeof target.closest === 'function'
        && target.closest('textarea, input, select, [contenteditable="true"]')
        && !inFind);

    if (e.key === 'Escape' && entry.findOpen) {
        e.preventDefault();
        e.stopPropagation();
        closeFind(entry);
        return;
    }

    const modifier = e.ctrlKey || e.metaKey;
    if (modifier && !e.altKey && !e.shiftKey && (e.key === 'f' || e.key === 'F')) {
        if (inOtherField || (!inPreview && !entry.findOpen)) {
            return;
        }

        e.preventDefault();
        e.stopPropagation();
        openFind(entry, { seedFromSelection: true });
        return;
    }

    if (e.key === 'F3') {
        if (inOtherField || (!inPreview && !entry.findOpen)) {
            return;
        }

        if (!entry.findOpen && !entry.findQuery) {
            return;
        }

        e.preventDefault();
        if (!entry.findOpen) {
            openFind(entry, { seedFromSelection: false });
        }

        if (e.shiftKey) {
            findPrevious(entry);
        } else {
            findNext(entry);
        }
    }
}

function ensureFindKeyListener() {
    if (findKeyListener) {
        return;
    }

    findKeyListener = onFindDocumentKeydown;
    document.addEventListener('keydown', findKeyListener, true);
}

function releaseFindKeyListener() {
    if (!findKeyListener) {
        return;
    }

    document.removeEventListener('keydown', findKeyListener, true);
    findKeyListener = null;
}

export async function init(elementId) {
    const el = document.getElementById(elementId);
    if (!el) {
        return false;
    }

    if (viewers.has(elementId)) {
        return true;
    }

    const entry = {
        el,
        changeIndex: -1,
        findOpen: false,
        findQuery: '',
        findMatchCase: false,
        findWholeWord: false,
        findRegex: false,
        findError: false,
        findIndex: -1,
        findMarks: [],
        findDebounce: null,
        widget: null,
        findInput: null,
        wrap: null,
    };
    viewers.set(elementId, entry);
    ensureFindWidget(entry);
    ensureFindKeyListener();
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
        refreshFindAfterContent(entry);
        return;
    }

    await renderMermaidIn(entry.el);
    refreshFindAfterContent(entry);
}

export function clear(elementId) {
    const entry = viewers.get(elementId);
    if (!entry) {
        return;
    }

    closeMermaidLightbox();
    cancelFindDebounce(entry);
    unwrapFindMarks(entry.el);
    entry.el.innerHTML = '';
    entry.changeIndex = -1;
    entry.findMarks = [];
    entry.findIndex = -1;
    updateFindUi(entry);
}

export function goToNextChange(elementId) {
    const entry = viewers.get(elementId);
    if (!entry) {
        return;
    }

    const nodes = collectChangeNodes(entry.el).filter((n) => n.isConnected);
    if (nodes.length === 0) {
        return;
    }

    entry.changeIndex = (entry.changeIndex + 1) % nodes.length;
    const node = nodes[entry.changeIndex];
    try {
        node.scrollIntoView({ block: 'center', behavior: 'smooth' });
        flashChange(node);
    } catch {
        /* ignore - preview must stay usable even if a change node is odd */
    }
}

export function goToPreviousChange(elementId) {
    const entry = viewers.get(elementId);
    if (!entry) {
        return;
    }

    const nodes = collectChangeNodes(entry.el).filter((n) => n.isConnected);
    if (nodes.length === 0) {
        return;
    }

    entry.changeIndex = entry.changeIndex <= 0 ? nodes.length - 1 : entry.changeIndex - 1;
    const node = nodes[entry.changeIndex];
    try {
        node.scrollIntoView({ block: 'center', behavior: 'smooth' });
        flashChange(node);
    } catch {
        /* ignore - preview must stay usable even if a change node is odd */
    }
}

export function dispose(elementId) {
    const entry = viewers.get(elementId);
    if (!entry) {
        return;
    }

    closeMermaidLightbox();
    cancelFindDebounce(entry);
    unwrapFindMarks(entry.el);
    entry.widget?.remove();
    entry.el.innerHTML = '';
    viewers.delete(elementId);
    if (viewers.size === 0) {
        releaseFindKeyListener();
    }
}
