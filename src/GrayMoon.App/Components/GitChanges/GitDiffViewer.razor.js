// Monaco diff editor wrapper for the Git Changes feature. This is the only component in the repo using
// the co-located .razor.js + IJSObjectReference module pattern - everywhere else in the app uses plain
// global window.* scripts. Monaco's per-instance lifecycle (model disposal, dynamic AMD require) benefits
// from real module scoping, which is the reason for the one-off exception; keep it isolated here rather
// than spreading the pattern.

const editors = new Map();
let monacoReadyPromise = null;

// Without this, Monaco spawns its language workers by wrapping vs/base/worker/workerMain.js in a Blob
// (for cross-origin safety) and then AMD-requiring the language submodule (e.g. vs/language/html/
// htmlWorker) *from inside that worker* using the same page-relative path string used on the main
// thread. A blob: URL has no real location to resolve a relative fetch() against, so that nested
// require fails with "Failed to parse URL from monaco/vs/language/html/htmlWorker.js". Declaring
// getWorkerUrl tells Monaco to spawn each language's worker directly from its real vendored file
// instead, bypassing the broken nested relative resolution entirely.
function ensureWorkerEnvironment() {
    if (window.MonacoEnvironment) {
        return;
    }

    const workerFileByLabel = {
        json: 'language/json/jsonWorker.js',
        css: 'language/css/cssWorker.js',
        scss: 'language/css/cssWorker.js',
        less: 'language/css/cssWorker.js',
        html: 'language/html/htmlWorker.js',
        handlebars: 'language/html/htmlWorker.js',
        razor: 'language/html/htmlWorker.js',
        typescript: 'language/typescript/tsWorker.js',
        javascript: 'language/typescript/tsWorker.js',
    };

    window.MonacoEnvironment = {
        getWorkerUrl(_moduleId, label) {
            const workerFile = workerFileByLabel[label] ?? 'base/worker/workerMain.js';
            return new URL(`monaco/vs/${workerFile}`, document.baseURI).href;
        },
    };
}

function ensureMonacoLoaded() {
    if (monacoReadyPromise) {
        return monacoReadyPromise;
    }

    monacoReadyPromise = new Promise((resolve, reject) => {
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
        this._subscription = diffEditor.onDidUpdateDiff(() => {
            this._changes = diffEditor.getLineChanges() || [];
            this._index = -1;
        });
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
    console.log('[GitDiffViewer] init', elementId, 'editors.size before =', editors.size, 'domDiffEditors =', document.querySelectorAll('.monaco-diff-editor').length, 'domEditors =', document.querySelectorAll('.monaco-editor').length);

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
    console.log('[GitDiffViewer] dispose called', elementId, 'hasEntry =', editors.has(elementId));

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

    console.log('[GitDiffViewer] dispose done', elementId, 'editors.size after =', editors.size, 'domDiffEditors =', document.querySelectorAll('.monaco-diff-editor').length);
}
