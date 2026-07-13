// Monaco diff editor wrapper for the Git Changes feature. This is the only component in the repo using
// the co-located .razor.js + IJSObjectReference module pattern - everywhere else in the app uses plain
// global window.* scripts. Monaco's per-instance lifecycle (model disposal, dynamic AMD require) benefits
// from real module scoping, which is the reason for the one-off exception; keep it isolated here rather
// than spreading the pattern.

const editors = new Map();
let monacoReadyPromise = null;
let headObserver = null;
const injectedHeadNodes = new Set();

// Blazor's enhanced navigation reconciles <head> against the server-rendered markup on every SPA
// navigation - including every <link>/<style> Monaco injects at runtime (its base editor.main.css,
// theme colors, and per-language syntax token colors are all added to <head> dynamically by Monaco's
// own AMD loader, never part of any server response). Losing that CSS is what breaks Monaco on the
// first navigation away from and back to a page hosting the editor: syntax highlighting disappears,
// scrollbar/layout rules break ("scrolling goes crazy"), and Monaco's hidden input-capture
// <textarea class="inputarea"> - normally invisible via `resize:none;color:transparent;z-index:-10` -
// shows up as a tiny raw resizable box because that CSS is gone.
//
// `data-permanent` alone does NOT fix this. Read directly out of the shipped blazor.web.js: the head
// reconciliation is a Myers-diff-style edit script over OLD vs NEW children, and `data-permanent` is
// only consulted as a similarity check between a specific old node and a specific *candidate* new node
// (it forces "infinite distance" - i.e. "these two are not the same element" - when their
// data-permanent values differ, which stops the algorithm from silently reusing/rewriting an unrelated
// matched pair). It says nothing about an old node that has literally no counterpart anywhere in the
// new page's head, which is exactly Monaco's case - its CSS was never server-rendered on any page, so
// there is nothing for it to match against and it is simply deleted as unneeded.
//
// The actual fix: track every <style>/<link> Monaco adds to <head>, and right before the editor is
// used on each mount, re-attach any of them that enhanced navigation disconnected. Monaco itself won't
// redo this - once window.monaco exists, ensureMonacoLoaded() never re-runs the AMD `require` that
// injected this CSS the first time - so nothing else will put it back.
//
// The observer is filtered to Monaco's own signature rather than every STYLE/LINK ever added to head -
// other libraries do this too (e.g. wwwroot/js/cytoscape.min.js, used on the Dependencies page, injects
// its own <style id="__________cytoscape_stylesheet">), and an unfiltered observer would wrongly track
// and could later wrongly restore those unrelated elements. Confirmed from
// wwwroot/monaco/vs/editor/editor.main.js: Monaco's one <link> (editor.main.css) always gets
// rel="stylesheet" plus a data-name attribute set to the AMD module id; its dynamically injected
// <style> tags (theme colors, per-language token colors) always get type="text/css" and
// media="screen", and never an id.
function isMonacoHeadNode(node) {
    if (node.nodeType !== Node.ELEMENT_NODE) {
        return false;
    }

    if (node.tagName === 'LINK') {
        return node.rel === 'stylesheet' && node.hasAttribute('data-name');
    }

    if (node.tagName === 'STYLE') {
        return node.type === 'text/css' && node.media === 'screen' && !node.id;
    }

    return false;
}

function trackHeadInjections() {
    if (headObserver) {
        return;
    }

    headObserver = new MutationObserver((mutations) => {
        for (const mutation of mutations) {
            mutation.addedNodes.forEach((node) => {
                if (isMonacoHeadNode(node)) {
                    injectedHeadNodes.add(node);
                }
            });
        }
    });
    headObserver.observe(document.head, { childList: true });
}

function restoreHeadInjectionsIfMissing() {
    for (const node of injectedHeadNodes) {
        if (!node.isConnected) {
            document.head.appendChild(node);
        }
    }
}

// Without this, Monaco spawns its worker by wrapping vs/base/worker/workerMain.js in a Blob (for
// cross-origin safety), which loses its real script location. workerMain.js is the AMD bootstrap - it
// defines `define`/`require` inside the worker and then internally requires the actual language
// submodule (e.g. vs/language/html/htmlWorker, itself just an AMD module, NOT a standalone worker
// script) using that same page-relative path config used on the main thread. A blob: URL has no real
// location to resolve that nested relative fetch() against, so it fails with "Failed to parse URL from
// monaco/vs/language/html/htmlWorker.js". Declaring getWorkerUrl to point at the real, non-blob
// workerMain.js file (for every label - it is the one shared bootstrap, not a per-language file) fixes
// the nested resolution without bypassing the AMD bootstrap the submodules depend on.
function ensureWorkerEnvironment() {
    if (window.MonacoEnvironment) {
        return;
    }

    window.MonacoEnvironment = {
        getWorkerUrl() {
            return new URL('monaco/vs/base/worker/workerMain.js', document.baseURI).href;
        },
    };
}

function ensureMonacoLoaded() {
    if (monacoReadyPromise) {
        return monacoReadyPromise;
    }

    monacoReadyPromise = new Promise((resolve, reject) => {
        trackHeadInjections();
        ensureWorkerEnvironment();

        if (window.monaco) {
            resolve(window.monaco);
            return;
        }

        if (!window.require || !window.require.config) {
            reject(new Error('Monaco AMD loader (window.require) is not available - was monaco/vs/loader.js loaded?'));
            return;
        }

        window.require.config({ paths: { vs: 'monaco/vs' } });
        window.require(['vs/editor/editor.main'], () => resolve(window.monaco), reject);
    });

    return monacoReadyPromise;
}

function disposeModels(entry) {
    entry.originalModel?.dispose();
    entry.modifiedModel?.dispose();
    entry.originalModel = null;
    entry.modifiedModel = null;
}

// Monaco removed editor.createDiffNavigator() from the standalone API; this reimplements just enough
// of it (next/previous change, centered on the modified side) using the still-supported
// onDidUpdateDiff/getLineChanges surface.
class SimpleDiffNavigator {
    constructor(diffEditor) {
        this._diffEditor = diffEditor;
        this._changes = diffEditor.getLineChanges() || [];
        this._index = -1;
        this._pendingAutoReveal = false;
        this._subscription = diffEditor.onDidUpdateDiff(() => {
            this._changes = diffEditor.getLineChanges() || [];
            this._index = -1;

            if (this._pendingAutoReveal) {
                this._pendingAutoReveal = false;
                this.next();
            }
        });
    }

    // Diff computation is async (worker-backed), so the line changes for models just set via setDiff()
    // aren't available yet - this arms a one-shot flag that the onDidUpdateDiff handler above consumes
    // the moment Monaco finishes computing the new diff, landing on the first change without the caller
    // needing to poll or click "next change" itself.
    revealFirstChangeOnNextUpdate() {
        this._pendingAutoReveal = true;
    }

    _revealChange(change) {
        const modifiedEditor = this._diffEditor.getModifiedEditor();
        const lineNumber = Math.max(change.modifiedStartLineNumber || change.originalStartLineNumber || 1, 1);
        modifiedEditor.revealLineInCenter(lineNumber);
        modifiedEditor.setPosition({ lineNumber, column: 1 });
    }

    next() {
        if (!this._changes.length) {
            return;
        }

        this._index = (this._index + 1) % this._changes.length;
        this._revealChange(this._changes[this._index]);
    }

    previous() {
        if (!this._changes.length) {
            return;
        }

        this._index = this._index <= 0 ? this._changes.length - 1 : this._index - 1;
        this._revealChange(this._changes[this._index]);
    }

    dispose() {
        this._subscription?.dispose();
    }
}

function bindPaneHeaderResize(diffEditor, originalHeaderEl, headersRowEl) {
    if (!originalHeaderEl || !headersRowEl) {
        return null;
    }

    const sync = () => {
        const layout = diffEditor.getOriginalEditor().getLayoutInfo();
        originalHeaderEl.style.width = `${layout.width}px`;
    };

    sync();
    return diffEditor.getOriginalEditor().onDidLayoutChange(sync);
}

function updatePaneHeadersVisibility(headersRowEl, sideBySide) {
    if (!headersRowEl) {
        return;
    }

    headersRowEl.style.display = sideBySide ? 'flex' : 'none';
}

export async function init(elementId, options) {
    // Put back whatever enhanced navigation stripped from <head> before Monaco (new or already-loaded)
    // gets used again on this mount - see trackHeadInjections()/restoreHeadInjectionsIfMissing() above.
    restoreHeadInjectionsIfMissing();

    const container = document.getElementById(elementId);
    if (!container) {
        return false;
    }

    if (editors.has(elementId)) {
        return true;
    }

    const monaco = await ensureMonacoLoaded();
    monaco.editor.setTheme('vs-dark');

    const renderSideBySide = options?.renderSideBySide ?? true;
    const originalHeaderEl = options?.originalHeaderId
        ? document.getElementById(options.originalHeaderId)
        : null;
    const headersRowEl = options?.headersRowId
        ? document.getElementById(options.headersRowId)
        : null;

    const editor = monaco.editor.createDiffEditor(container, {
        automaticLayout: true,
        readOnly: true,
        originalEditable: false,
        renderSideBySide,
        ignoreTrimWhitespace: options?.ignoreWhitespace ?? false,
        wordWrap: options?.wordWrap ? 'on' : 'off',
        minimap: { enabled: false },
        renderOverviewRuler: true,
    });

    const navigator = new SimpleDiffNavigator(editor);
    const layoutSub = bindPaneHeaderResize(editor, originalHeaderEl, headersRowEl);
    updatePaneHeadersVisibility(headersRowEl, renderSideBySide);

    // automaticLayout's own ResizeObserver doesn't reliably repaint after the container goes from
    // display:none (0x0, detached from layout) to visible - e.g. right after SetDiffAsync sets models
    // while the Blazor-controlled wrapper is still hidden. Force a real layout() whenever the container
    // is observed with non-zero size so the diff always renders correctly on the first paint.
    const resizeObserver = new ResizeObserver((entries) => {
        for (const entry of entries) {
            const { width, height } = entry.contentRect;
            if (width > 0 && height > 0) {
                editor.layout();
                break;
            }
        }
    });
    resizeObserver.observe(container);

    editors.set(elementId, {
        editor,
        navigator,
        originalModel: null,
        modifiedModel: null,
        layoutSub,
        resizeObserver,
        headersRowEl,
    });
    return true;
}

export async function setDiff(elementId, originalContent, modifiedContent, languageId) {
    const entry = editors.get(elementId);
    if (!entry) {
        return;
    }

    const monaco = await ensureMonacoLoaded();

    const resolvedLanguage = languageId || 'plaintext';
    const newOriginalModel = monaco.editor.createModel(originalContent ?? '', resolvedLanguage);
    const newModifiedModel = monaco.editor.createModel(modifiedContent ?? '', resolvedLanguage);
    const oldOriginalModel = entry.originalModel;
    const oldModifiedModel = entry.modifiedModel;

    // The diff editor must be pointed at the new models before the old ones are disposed - disposing a
    // model while it's still assigned to the widget throws "TextModel got disposed before
    // DiffEditorWidget model got reset", which (being uncaught) aborts whatever cleanup runs after it.
    entry.editor.setModel({ original: newOriginalModel, modified: newModifiedModel });
    entry.originalModel = newOriginalModel;
    entry.modifiedModel = newModifiedModel;
    entry.navigator.revealFirstChangeOnNextUpdate();

    oldOriginalModel?.dispose();
    oldModifiedModel?.dispose();
}

export function setViewMode(elementId, mode) {
    const entry = editors.get(elementId);
    if (!entry) {
        return;
    }

    const sideBySide = mode === 'side-by-side';
    entry.editor.updateOptions({ renderSideBySide: sideBySide });
    updatePaneHeadersVisibility(entry.headersRowEl, sideBySide);
}

export function setOptions(elementId, options) {
    const entry = editors.get(elementId);
    if (!entry) {
        return;
    }

    const update = {};
    if (options?.wordWrap !== undefined) {
        update.wordWrap = options.wordWrap ? 'on' : 'off';
    }
    if (options?.ignoreWhitespace !== undefined) {
        update.ignoreTrimWhitespace = options.ignoreWhitespace;
    }
    if (options?.renderSideBySide !== undefined) {
        update.renderSideBySide = options.renderSideBySide;
        updatePaneHeadersVisibility(entry.headersRowEl, options.renderSideBySide);
    }

    entry.editor.updateOptions(update);
}

export function goToNextChange(elementId) {
    editors.get(elementId)?.navigator.next();
}

export function goToPreviousChange(elementId) {
    editors.get(elementId)?.navigator.previous();
}

export function clear(elementId) {
    const entry = editors.get(elementId);
    if (!entry) {
        return;
    }

    entry.editor.setModel(null);
    disposeModels(entry);
}

export function dispose(elementId) {
    const entry = editors.get(elementId);
    if (!entry) {
        return;
    }

    entry.layoutSub?.dispose();
    entry.resizeObserver?.disconnect();
    entry.navigator.dispose();
    entry.editor.setModel(null);
    entry.editor.dispose();
    disposeModels(entry);
    editors.delete(elementId);
}
