// Filter box (FilterSearchInput.razor): everything the user sees while typing happens here, in the
// browser, in the same task as the keystroke. The server is told the value only after the debounce
// (or at once for Escape, Enter and Clear), so typing and Backspace never wait on a round trip.
//
// The grammar in scan() mirrors GrayMoon.Common.Search.FilterSearchExpression (the parser that does
// the filtering). FilterSearchInputScriptTests runs this file against that parser; change both together.
window.grayMoonFilterSearch = (function () {
    'use strict';

    var PLAIN = 0;
    var AND = 1;
    var OR = 2;
    var PAREN = 3;
    var FIELD = 4;

    // Class per run kind. A plain run (words and the spaces between them) has no class.
    var RUN_CLASS = [
        '',
        'filter-search-token-and',
        'filter-search-token-or',
        'filter-search-token-paren',
        'filter-search-token-field'
    ];

    // FilterSearchExpression.MaxNestingDepth.
    var MAX_NESTING_DEPTH = 64;

    // System.Char.IsWhiteSpace, so a token ends at the same characters here and in the parser.
    function isSpace(c) {
        if (c === 32) {
            return true;
        }
        if (c < 128) {
            return c >= 9 && c <= 13;
        }
        return c === 0x85 || c === 0xA0 || c === 0x1680
            || (c >= 0x2000 && c <= 0x200A)
            || c === 0x2028 || c === 0x2029 || c === 0x202F || c === 0x205F || c === 0x3000;
    }

    // One pass over the text: checks the syntax and writes the colored runs into out.kinds / out.ends
    // (out.ends[i] is the exclusive end of run i; neighbours of the same kind are one run).
    // Returns false as soon as the text cannot be a valid query; the runs are then not complete.
    function scan(text, out) {
        var kinds = out.kinds;
        var ends = out.ends;
        var length = text.length;
        var count = 0;
        var last = -1;
        var depth = 0;
        var expectOperand = true;
        var hasToken = false;
        var i = 0;

        while (i < length) {
            var c = text.charCodeAt(i);
            var kind;

            if (isSpace(c)) {
                do {
                    i++;
                } while (i < length && isSpace(text.charCodeAt(i)));
                kind = PLAIN;
            } else if (c === 40) {
                if (++depth > MAX_NESTING_DEPTH) {
                    return false;
                }
                i++;
                hasToken = true;
                expectOperand = true;
                kind = PAREN;
            } else if (c === 41) {
                if (expectOperand || depth === 0) {
                    return false;
                }
                depth--;
                i++;
                kind = PAREN;
            } else {
                var start = i;
                var colon = -1;
                do {
                    if (c === 58 && colon < 0) {
                        colon = i;
                    }
                    i++;
                } while (i < length && (c = text.charCodeAt(i)) !== 40 && c !== 41 && !isSpace(c));

                hasToken = true;
                var size = i - start;
                if (size === 3
                    && (text.charCodeAt(start) | 32) === 97
                    && (text.charCodeAt(start + 1) | 32) === 110
                    && (text.charCodeAt(start + 2) | 32) === 100) {
                    kind = AND;
                } else if (size === 2
                    && (text.charCodeAt(start) | 32) === 111
                    && (text.charCodeAt(start + 1) | 32) === 114) {
                    kind = OR;
                } else {
                    // field:value needs a name before the colon and a value after it.
                    kind = colon > start && colon < i - 1 ? FIELD : PLAIN;
                }

                if (kind === AND || kind === OR) {
                    if (expectOperand) {
                        return false;
                    }
                    expectOperand = true;
                } else {
                    expectOperand = false;
                }
            }

            if (kind === last) {
                ends[count - 1] = i;
            } else {
                kinds[count] = kind;
                ends[count] = i;
                count++;
                last = kind;
            }
        }

        out.count = count;
        return !hasToken || (!expectOperand && depth === 0);
    }

    function FilterSearch(input, backdrop, clearButton, dotNet, serverValue, debounceMs) {
        this.input = input;
        this.wrap = input.parentElement;
        this.inner = backdrop.querySelector('.filter-search-backdrop-inner');
        this.clearButton = clearButton;
        this.dotNet = dotNet;
        this.debounceMs = debounceMs;

        this.runs = { kinds: [], ends: [], count: 0 };
        // Span pool: spans[i] / texts[i] keep the kind and text they last showed, so a keystroke
        // touches only the runs that changed (typing at the end changes one text node).
        this.spans = [];
        this.texts = [];
        this.spanKinds = [];
        this.spanTexts = [];
        this.spanCount = 0;

        this.value = null;
        this.state = null;
        this.offset = 0;
        this.committed = serverValue;
        this.timer = 0;
        this.disposed = false;

        var self = this;
        this.onInput = function () {
            self.render();
            self.scheduleCommit();
        };
        this.onScroll = function () {
            self.syncScroll();
        };
        this.onKeyDown = function (e) {
            self.keyDown(e);
        };
        this.onClearClick = function (e) {
            // The button is inside rows and bars that react to a click themselves.
            e.stopPropagation();
            self.clear();
        };

        input.addEventListener('input', this.onInput);
        input.addEventListener('scroll', this.onScroll, { passive: true });
        input.addEventListener('keydown', this.onKeyDown);
        clearButton.addEventListener('click', this.onClearClick);

        this.render();
        if (input.value !== serverValue) {
            // Typed before the circuit was ready.
            this.scheduleCommit();
        }
    }

    FilterSearch.prototype.render = function () {
        var value = this.input.value;
        if (value === this.value) {
            return;
        }
        this.value = value;

        var runs = this.runs;
        var state;
        if (value.length === 0) {
            state = 'empty';
            runs.count = 0;
        } else if (scan(value, runs)) {
            state = 'valid';
        } else {
            // Invalid text is shown by the input itself, in the error color.
            state = 'invalid';
            runs.count = 0;
        }

        this.patch(value, runs);

        if (state !== this.state) {
            this.state = state;
            this.wrap.setAttribute('data-filter-state', state);
        }

        this.syncScroll();
    };

    FilterSearch.prototype.patch = function (value, runs) {
        var count = runs.count;
        var kinds = runs.kinds;
        var ends = runs.ends;
        var spans = this.spans;
        var texts = this.texts;
        var spanKinds = this.spanKinds;
        var spanTexts = this.spanTexts;
        var shown = this.spanCount;
        var start = 0;
        var i;

        for (i = 0; i < count; i++) {
            var end = ends[i];
            var kind = kinds[i];
            var text = value.substring(start, end);
            start = end;

            if (i === spans.length) {
                var span = document.createElement('span');
                var node = document.createTextNode(text);
                span.className = RUN_CLASS[kind];
                span.appendChild(node);
                spans.push(span);
                texts.push(node);
                spanKinds.push(kind);
                spanTexts.push(text);
            } else {
                if (spanKinds[i] !== kind) {
                    spanKinds[i] = kind;
                    spans[i].className = RUN_CLASS[kind];
                }
                if (spanTexts[i] !== text) {
                    spanTexts[i] = text;
                    texts[i].data = text;
                }
            }

            if (i >= shown) {
                this.inner.appendChild(spans[i]);
            }
        }

        for (i = shown - 1; i >= count; i--) {
            spans[i].remove();
        }

        this.spanCount = count;
    };

    // The input scrolls its own text; the colored copy follows with a transform (no layout).
    FilterSearch.prototype.syncScroll = function () {
        var offset = this.state === 'valid' ? this.input.scrollLeft : 0;
        if (offset !== this.offset) {
            this.offset = offset;
            this.inner.style.transform = offset === 0 ? '' : 'translateX(' + -offset + 'px)';
        }
    };

    FilterSearch.prototype.scheduleCommit = function () {
        if (this.timer !== 0) {
            clearTimeout(this.timer);
            this.timer = 0;
        }

        if (this.debounceMs <= 0) {
            this.commit();
            return;
        }

        var self = this;
        this.timer = setTimeout(function () {
            self.timer = 0;
            self.commit();
        }, this.debounceMs);
    };

    FilterSearch.prototype.cancelCommit = function () {
        if (this.timer !== 0) {
            clearTimeout(this.timer);
            this.timer = 0;
        }
    };

    FilterSearch.prototype.commit = function () {
        var value = this.input.value;
        if (value === this.committed) {
            return;
        }
        this.committed = value;
        this.invoke('CommitFromClient', value);
    };

    FilterSearch.prototype.keyDown = function (e) {
        if (e.isComposing || (e.key !== 'Escape' && e.key !== 'Enter')) {
            return;
        }

        // The value travels with the key, so the server handles the key against what is on screen
        // and not against the last debounced value.
        this.cancelCommit();
        var value = this.input.value;
        this.committed = value;
        this.invoke('CommandKeyFromClient', value, e.key, e.code, e.ctrlKey, e.shiftKey, e.altKey, e.metaKey, e.repeat);
    };

    FilterSearch.prototype.clear = function () {
        this.cancelCommit();
        this.input.value = '';
        this.committed = '';
        this.render();
        this.input.focus();
        this.invoke('ClearFromClient');
    };

    // The server replaced the value (Escape handler on the page, reset by the parent).
    FilterSearch.prototype.setValue = function (value) {
        if (this.disposed) {
            return;
        }
        this.cancelCommit();
        this.committed = value;
        if (this.input.value !== value) {
            this.input.value = value;
        }
        this.render();
    };

    FilterSearch.prototype.invoke = function (method) {
        if (this.disposed) {
            return;
        }
        var self = this;
        var args = Array.prototype.slice.call(arguments);
        this.dotNet.invokeMethodAsync.apply(this.dotNet, args).catch(function (error) {
            // After dispose the component is gone and the value has no receiver; that is expected.
            if (!self.disposed) {
                console.error('Filter box: ' + method + ' failed.', error);
            }
        });
    };

    FilterSearch.prototype.dispose = function () {
        if (this.disposed) {
            return;
        }
        this.disposed = true;
        this.cancelCommit();
        this.input.removeEventListener('input', this.onInput);
        this.input.removeEventListener('scroll', this.onScroll);
        this.input.removeEventListener('keydown', this.onKeyDown);
        this.clearButton.removeEventListener('click', this.onClearClick);
    };

    return {
        scan: scan,
        attach: function (input, backdrop, clearButton, dotNet, serverValue, debounceMs) {
            return new FilterSearch(input, backdrop, clearButton, dotNet, serverValue, debounceMs);
        }
    };
})();
