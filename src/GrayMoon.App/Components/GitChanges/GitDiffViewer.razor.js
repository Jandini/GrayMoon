// Monaco diff editor wrapper for the Git Changes feature. This is the only component in the repo using
// the co-located .razor.js + IJSObjectReference module pattern - everywhere else in the app uses plain
// global window.* scripts. Monaco's per-instance lifecycle (model disposal, dynamic AMD require) benefits
// from real module scoping, which is the reason for the one-off exception; keep it isolated here rather
// than spreading the pattern.

const editors = new Map();
let monacoReadyPromise = null;

function ensureMonacoLoaded() {
    if (monacoReadyPromise) {
        return monacoReadyPromise;
    }

    monacoReadyPromise = new Promise((resolve, reject) => {
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

export async function init(elementId, options) {
    const container = document.getElementById(elementId);
    if (!container) {
        return false;
    }

    if (editors.has(elementId)) {
        return true;
    }

    const monaco = await ensureMonacoLoaded();
    monaco.editor.setTheme('vs-dark');

    const editor = monaco.editor.createDiffEditor(container, {
        automaticLayout: true,
        readOnly: true,
        originalEditable: false,
        renderSideBySide: options?.renderSideBySide ?? true,
        ignoreTrimWhitespace: options?.ignoreWhitespace ?? false,
        wordWrap: options?.wordWrap ? 'on' : 'off',
        minimap: { enabled: false },
        renderOverviewRuler: true,
    });

    const navigator = new SimpleDiffNavigator(editor);

    editors.set(elementId, { editor, navigator, originalModel: null, modifiedModel: null });
    return true;
}

export async function setDiff(elementId, originalContent, modifiedContent, languageId) {
    const entry = editors.get(elementId);
    if (!entry) {
        return;
    }

    const monaco = await ensureMonacoLoaded();
    disposeModels(entry);

    const resolvedLanguage = languageId || 'plaintext';
    entry.originalModel = monaco.editor.createModel(originalContent ?? '', resolvedLanguage);
    entry.modifiedModel = monaco.editor.createModel(modifiedContent ?? '', resolvedLanguage);

    entry.editor.setModel({ original: entry.originalModel, modified: entry.modifiedModel });
}

export function setViewMode(elementId, mode) {
    const entry = editors.get(elementId);
    if (!entry) {
        return;
    }

    entry.editor.updateOptions({ renderSideBySide: mode === 'side-by-side' });
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

    disposeModels(entry);
    entry.editor.setModel(null);
}

export function dispose(elementId) {
    const entry = editors.get(elementId);
    if (!entry) {
        return;
    }

    disposeModels(entry);
    entry.navigator.dispose();
    entry.editor.dispose();
    editors.delete(elementId);
}
