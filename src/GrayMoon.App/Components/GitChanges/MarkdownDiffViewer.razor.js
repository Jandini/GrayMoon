// Rendered markdown preview host for Git Changes. Uses the same co-located .razor.js +
// IJSObjectReference pattern as GitDiffViewer so each instance can be set/cleared/disposed
// independently. Mermaid is lazy-loaded from /mermaid/mermaid.min.js on first diagram.

const viewers = new Map();
let mermaidReadyPromise = null;

function ensureMermaidLoaded() {
    if (window.mermaid) {
        return Promise.resolve(window.mermaid);
    }

    if (mermaidReadyPromise) {
        return mermaidReadyPromise;
    }

    mermaidReadyPromise = new Promise((resolve, reject) => {
        const existing = document.querySelector('script[data-graymoon-mermaid]');
        if (existing) {
            existing.addEventListener('load', () => resolve(window.mermaid));
            existing.addEventListener('error', () => reject(new Error('Failed to load mermaid.js')));
            return;
        }

        const script = document.createElement('script');
        script.src = 'mermaid/mermaid.min.js';
        script.async = true;
        script.dataset.graymoonMermaid = '1';
        script.onload = () => {
            if (!window.mermaid) {
                reject(new Error('mermaid.js loaded but window.mermaid is missing'));
                return;
            }
            window.mermaid.initialize({
                startOnLoad: false,
                theme: 'dark',
                securityLevel: 'strict',
            });
            resolve(window.mermaid);
        };
        script.onerror = () => reject(new Error('Failed to load mermaid.js'));
        document.head.appendChild(script);
    });

    return mermaidReadyPromise;
}

function collectChangeNodes(root) {
    return Array.from(root.querySelectorAll('ins, del'));
}

function flashChange(node) {
    node.classList.add('markdown-diff-change-flash');
    window.setTimeout(() => node.classList.remove('markdown-diff-change-flash'), 900);
}

function attachPanZoom(container, content) {
    let scale = 1;
    let translateX = 0;
    let translateY = 0;
    let isPanning = false;
    let panMode = false;
    let startX = 0;
    let startY = 0;
    const minScale = 0.25;
    const maxScale = 5;

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
    controls.innerHTML =
        '<button type="button" class="mermaid-panzoom-btn" data-act="in" title="Zoom in">+</button>' +
        '<button type="button" class="mermaid-panzoom-btn" data-act="out" title="Zoom out">-</button>' +
        '<button type="button" class="mermaid-panzoom-btn" data-act="reset" title="Reset view">reset</button>' +
        '<button type="button" class="mermaid-panzoom-btn" data-act="pan" title="Toggle pan mode">pan</button>';

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
            caption.textContent = 'Mermaid library failed to load.';
            node.insertAdjacentElement('afterend', caption);
        }
        return;
    }

    for (const node of nodes) {
        const source = node.textContent ?? '';
        const wrap = document.createElement('div');
        wrap.className = 'mermaid-panzoom';
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
            attachPanZoom(wrap, content);
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

    entry.el.innerHTML = html || '';
    entry.changeIndex = -1;
    openSafeLinks(entry.el);

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

    entry.el.innerHTML = '';
    viewers.delete(elementId);
}
