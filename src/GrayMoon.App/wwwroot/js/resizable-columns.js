/**
 * Makes table columns resizable. Add class "resizable-columns" to any table.
 * Column widths are persisted in localStorage per table (key: graymoon-col-widths-{tableClass}).
 */
(function () {
    const STORAGE_PREFIX = 'graymoon-col-widths-';
    const RESIZE_HANDLE_WIDTH = 6;
    const MIN_COL_WIDTH = 40;

    function getTableStorageKey(table) {
        const classes = Array.from(table.classList).filter(c =>
            c && c !== 'table' && c !== 'table-striped' && c !== 'table-hover' && c !== 'mb-0' && c !== 'resizable-columns');
        return STORAGE_PREFIX + (classes[0] || 'table');
    }

    function getColumnWidths(table) {
        const key = getTableStorageKey(table);
        try {
            const raw = localStorage.getItem(key);
            if (raw) {
                const parsed = JSON.parse(raw);
                if (Array.isArray(parsed)) return parsed;
            }
        } catch (_) { /* ignore */ }
        return null;
    }

    function saveColumnWidths(table, widths) {
        const key = getTableStorageKey(table);
        try {
            localStorage.setItem(key, JSON.stringify(widths));
        } catch (_) { /* ignore */ }
    }

    function initTable(table) {
        if (table.dataset.resizableColumnsInit === '1') return;
        const thead = table.querySelector('thead');
        const headerRow = thead && thead.querySelector('tr');
        const ths = headerRow ? Array.from(headerRow.querySelectorAll('th')) : [];
        if (ths.length === 0) return;

        table.dataset.resizableColumnsInit = '1';
        table.style.tableLayout = 'fixed';
        table.style.width = '100%';

        const savedWidths = getColumnWidths(table);
        if (savedWidths && savedWidths.length === ths.length) {
            ths.forEach((th, i) => {
                th.style.width = savedWidths[i] + '%';
                th.style.minWidth = MIN_COL_WIDTH + 'px';
            });
        } else {
            // On first load (no saved widths): capture the current header widths
            // and freeze them as explicit percentages so thead and tbody stay aligned.
            const tableRect = table.getBoundingClientRect();
            const tableWidth = tableRect.width;
            if (tableWidth > 0) {
                ths.forEach(th => {
                    const cellWidth = th.getBoundingClientRect().width;
                    const pct = (cellWidth / tableWidth) * 100;
                    th.style.width = pct + '%';
                    th.style.minWidth = MIN_COL_WIDTH + 'px';
                });
            } else {
                ths.forEach(th => {
                    th.style.minWidth = MIN_COL_WIDTH + 'px';
                });
            }
        }

        ths.forEach((th, index) => {
            th.style.position = 'relative';
            th.style.overflow = 'hidden';
            const handle = document.createElement('div');
            handle.className = 'resize-handle';
            handle.setAttribute('aria-hidden', 'true');
            handle.addEventListener('mousedown', (e) => startResize(e, table, ths, index));
            th.appendChild(handle);
        });

        syncBodyColumnWidths(table, ths);
    }

    function syncBodyColumnWidths(table, ths) {
        const tbody = table.querySelector('tbody');
        if (!tbody) return;
        const numCols = ths.length;
        const rows = tbody.querySelectorAll('tr');
        rows.forEach(tr => {
            const tds = tr.querySelectorAll('td');
            if (tds.length !== numCols) return; /* skip colspan placeholder rows */
            ths.forEach((th, i) => {
                tds[i].style.width = th.style.width || '';
                tds[i].style.minWidth = th.style.minWidth || '';
            });
        });
    }

    let activeResize = null;

    function startResize(e, table, ths, columnIndex) {
        e.preventDefault();
        if (columnIndex >= ths.length - 1) return;
        const th = ths[columnIndex];
        const startX = e.clientX;
        const tableRect = table.getBoundingClientRect();
        const tableWidth = tableRect.width;
        const startWidths = ths.map(t => (t.getBoundingClientRect().width / tableWidth) * 100);

        function onMove(moveEvent) {
            const dx = moveEvent.clientX - startX;
            const deltaPct = (dx / tableWidth) * 100;
            let newLeft = startWidths[columnIndex] + deltaPct;
            let newRight = startWidths[columnIndex + 1] - deltaPct;
            const minPct = (MIN_COL_WIDTH / tableWidth) * 100;
            if (newLeft < minPct) {
                newRight += (newLeft - minPct);
                newLeft = minPct;
            }
            if (newRight < minPct) {
                newLeft += (newRight - minPct);
                newRight = minPct;
            }
            th.style.width = newLeft + '%';
            ths[columnIndex + 1].style.width = newRight + '%';
            syncBodyColumnWidths(table, ths);
        }

        function onUp() {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            const tableWidthPx = table.getBoundingClientRect().width;
            const widths = ths.map(t => ((t.getBoundingClientRect().width / tableWidthPx) * 100));
            saveColumnWidths(table, widths);
            syncBodyColumnWidths(table, ths);
            activeResize = null;
        }

        document.body.style.cursor = 'col-resize';
        document.body.style.userSelect = 'none';
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
        activeResize = { onUp };
    }

    function runInit() {
        document.querySelectorAll('table.resizable-columns').forEach(initTable);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', runInit);
    } else {
        runInit();
    }

    const observer = new MutationObserver((mutations) => {
        // Initialize any brand-new resizable tables that were added,
        // but avoid re-initializing existing ones so column widths are not reset.
        for (const m of mutations) {
            if (!m.addedNodes.length) continue;
            for (const node of m.addedNodes) {
                if (node.nodeType !== 1) continue;
                const el = /** @type {HTMLElement} */ (node);

                // Case 1: the added node itself is a resizable table
                if (el.matches && el.matches('table.resizable-columns')) {
                    if (el.dataset.resizableColumnsInit !== '1') {
                        initTable(el);
                    }
                    continue;
                }

                // Case 2: the added node contains one or more resizable tables (e.g. modal content)
                if (el.querySelector) {
                    const tables = el.querySelectorAll('table.resizable-columns');
                    tables.forEach(tbl => {
                        if (tbl.dataset.resizableColumnsInit !== '1') {
                            initTable(tbl);
                        }
                    });
                }
            }
        }

        // Always resync body column widths for already-initialized tables so that
        // when a grid's rows are reloaded (e.g. statuses updated), the new rows
        // inherit the existing header widths instead of drifting.
        document.querySelectorAll('table.resizable-columns').forEach(tbl => {
            if (tbl.dataset.resizableColumnsInit === '1') {
                const thead = tbl.querySelector('thead');
                const headerRow = thead && thead.querySelector('tr');
                const ths = headerRow ? Array.from(headerRow.querySelectorAll('th')) : [];
                if (ths.length) {
                    syncBodyColumnWidths(tbl, ths);
                }
            }
        });
    });

    observer.observe(document.body, { childList: true, subtree: true });

    window.graymoonResizableColumns = { init: runInit };
})();
