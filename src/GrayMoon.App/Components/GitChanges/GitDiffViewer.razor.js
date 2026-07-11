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

    const navigator = monaco.editor.createDiffNavigator(editor, {
        followsCaret: true,
        ignoreCharChanges: true,
    });

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
    entry.editor.dispose();
    editors.delete(elementId);
}
