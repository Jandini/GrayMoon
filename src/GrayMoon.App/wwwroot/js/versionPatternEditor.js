window.versionPatternEditor = {
    // Returns { atPos, query } where atPos is the index of the triggering '@' and query is text typed after it.
    // Returns null if the caret is not inside an active @... token.
    getAtContext: function (el) {
        const pos = el.selectionStart;
        const text = el.value.substring(0, pos);
        // Find the last '@' on the current line (don't cross newlines)
        const lineStart = text.lastIndexOf('\n') + 1;
        const lineText = text.substring(lineStart);
        const atIdx = lineText.lastIndexOf('@');
        if (atIdx === -1) return null;
        // Make sure there's no closing '}' between '@' and caret
        const afterAt = lineText.substring(atIdx + 1);
        if (afterAt.includes('}')) return null;
        return { atPos: lineStart + atIdx, query: afterAt };
    },

    // Replaces the '@...' fragment at atPos with '{repoName}' and returns { value, caret }.
    insertRepo: function (el, atPos, repoName) {
        const val = el.value;
        const caretPos = el.selectionStart;
        const newToken = '{' + repoName + '}';
        const newVal = val.substring(0, atPos) + newToken + val.substring(caretPos);
        const newCaret = atPos + newToken.length;
        return { value: newVal, caret: newCaret };
    },

    // Sets caret position in a textarea (call after Blazor re-render).
    setCaret: function (el, pos) {
        el.focus();
        el.setSelectionRange(pos, pos);
    },

    // Grows the textarea to fit its content (call on input and on open).
    autoResize: function (el) {
        el.style.height = 'auto';
        el.style.height = el.scrollHeight + 'px';
    },

    // Returns the viewport pixel position { x, y } where y is the bottom of the line
    // containing atPos, so the dropdown appears directly beneath the '@' character.
    getAtPixelCoords: function (el, atPos) {
        const cs = window.getComputedStyle(el);
        const pLeft  = parseFloat(cs.paddingLeft)      || 0;
        const pTop   = parseFloat(cs.paddingTop)       || 0;
        const pRight = parseFloat(cs.paddingRight)     || 0;
        const bLeft  = parseFloat(cs.borderLeftWidth)  || 0;
        const bTop   = parseFloat(cs.borderTopWidth)   || 0;
        const bRight = parseFloat(cs.borderRightWidth) || 0;

        const elRect = el.getBoundingClientRect();
        const contentW = elRect.width - pLeft - pRight - bLeft - bRight;

        // Mirror div replicates text rendering without padding/border so
        // marker coords are pure text offsets from the content origin.
        const mirror = document.createElement('div');
        ['fontFamily', 'fontSize', 'fontWeight', 'fontStyle', 'fontVariant',
         'letterSpacing', 'wordSpacing', 'textIndent', 'textTransform',
         'lineHeight', 'tabSize'].forEach(p => {
            try { mirror.style[p] = cs[p]; } catch (_) {}
        });
        mirror.style.cssText += ';position:fixed;visibility:hidden;top:0;left:0;' +
            'white-space:pre-wrap;word-wrap:break-word;overflow:hidden;' +
            'padding:0;border:none;margin:0;width:' + contentW + 'px;';

        document.body.appendChild(mirror);
        mirror.appendChild(document.createTextNode(el.value.substring(0, atPos)));
        const marker = document.createElement('span');
        marker.textContent = '\u200b'; // zero-width space – just marks the position
        mirror.appendChild(marker);

        const mRect = marker.getBoundingClientRect();
        document.body.removeChild(mirror);

        return {
            x: elRect.left + bLeft + pLeft + mRect.left   - el.scrollLeft,
            y: elRect.top  + bTop  + pTop  + mRect.bottom - el.scrollTop
        };
    }
};
