/**
 * Render a dependency graph with Cytoscape (dark scheme).
 * Node types use distinct shapes and soft fills so they stay readable without neon borders.
 * Dragged positions are stored in this browser only, keyed by workspace and feature context.
 * @param {string} containerId - Id of the div element to render into
 * @param {Array<{id: string, label: string, nodeType?: string}>} nodes - Nodes with id, label, and optional nodeType
 * @param {Array<{source: string, target: string}>} edges - Edges with source and target node ids
 * @param {string[]} [roots] - Optional node ids to use as roots (no incoming edges). Layout flows from these for a clear hierarchy.
 * @param {number|string} [workspaceId] - Workspace id. Positions are not stored when this is missing.
 * @param {number|string|null} [contextId] - Feature context id. Null or empty keys the layout by workspace only.
 * @param {Array<string|number>} [repositoryIds] - Every repository id in this graph. Saved positions for other ids are dropped on the next drag.
 * @param {{ invokeMethodAsync?: function }} [dotNetRef] - Notified after a drag is stored so the page can enable Reset layout.
 */
window.renderCytoscapeGraph = function (containerId, nodes, edges, roots, workspaceId, contextId, repositoryIds, dotNetRef) {
    var container = document.getElementById(containerId);
    if (!container || typeof cytoscape === 'undefined') return null;

    var persistLayout = workspaceId !== null && workspaceId !== undefined && workspaceId !== '';
    var stored = persistLayout ? readDependencyLayout(workspaceId, contextId) : {};
    var savedById = {};
    var nodeElements = (nodes || []).map(function (n) {
        var id = String(n.id);
        var element = { data: { id: id, label: n.label || id, nodeType: n.nodeType || 'other' } };
        var pos = savedPosition(stored, id);
        if (pos) {
            element.position = pos;
            savedById[id] = pos;
        }
        return element;
    });
    var edgeElements = (edges || []).map(function (e, i) {
        return { data: { id: 'e' + i, source: String(e.source), target: String(e.target) } };
    });

    container.style.backgroundColor = '#141416';

    if (typeof cytoscapeDagre !== 'undefined') cytoscape.use(cytoscapeDagre);

    var cy = cytoscape({
        container: container,
        elements: nodeElements.concat(edgeElements),
        layout: { name: 'preset', fit: false }, // keep saved coordinates; the automatic layout runs next when it is needed
        style: [
            {
                selector: 'node',
                style: {
                    'shape': 'round-rectangle',
                    'background-color': '#2a2a2e',
                    'background-opacity': 1,
                    'label': 'data(label)',
                    'color': '#e4e4e7',
                    'text-valign': 'center',
                    'text-halign': 'center',
                    'font-size': '11px',
                    'font-weight': 500,
                    'text-wrap': 'wrap',
                    'text-max-width': '118px',
                    'text-outline-color': '#18181b',
                    'text-outline-width': 1.5,
                    'text-outline-opacity': 0.55,
                    'padding': '6px',
                    'border-width': 1.5,
                    'border-color': '#52525b',
                    'border-opacity': 0.9,
                    'width': 140,
                    'height': 42
                }
            },
            {
                /* Apps / hosts - rounded card */
                selector: 'node[nodeType = "service"]',
                style: {
                    'shape': 'round-rectangle',
                    'background-color': '#3b2f1e',
                    'border-color': '#c9842f',
                    'color': '#fde68a'
                }
            },
            {
                /* NuGet packages - rounded card, same teal as before */
                selector: 'node[nodeType = "package"]',
                style: {
                    'shape': 'round-rectangle',
                    'background-color': '#16353f',
                    'border-color': '#38bdf8',
                    'color': '#bae6fd'
                }
            },
            {
                /* Shared libraries - clean rectangle */
                selector: 'node[nodeType = "library"]',
                style: {
                    'shape': 'rectangle',
                    'background-color': '#1e293b',
                    'border-color': '#64748b',
                    'color': '#cbd5e1'
                }
            },
            {
                /* Executables / tools - cut corners */
                selector: 'node[nodeType = "executable"]',
                style: {
                    'shape': 'cut-rectangle',
                    'background-color': '#1a2e24',
                    'border-color': '#4ade80',
                    'color': '#bbf7d0'
                }
            },
            {
                /* Tests - diamond stands apart from runtime nodes */
                selector: 'node[nodeType = "test"]',
                style: {
                    'shape': 'diamond',
                    'background-color': '#2e1f3d',
                    'border-color': '#c084fc',
                    'color': '#e9d5ff',
                    'width': 130,
                    'height': 56,
                    'text-max-width': '90px'
                }
            },
            {
                selector: 'edge',
                style: {
                    'width': 1.75,
                    'line-color': '#52525b',
                    'target-arrow-color': '#71717a',
                    'target-arrow-shape': 'triangle',
                    'curve-style': 'bezier',
                    'arrow-scale': 0.9,
                    'opacity': 0.85
                }
            }
        ],
        minZoom: 0.02,
        maxZoom: 3
    });

    var nodeCount = nodeElements.length;
    var savedCount = 0;
    var savedId;
    for (savedId in savedById) {
        if (Object.prototype.hasOwnProperty.call(savedById, savedId)) savedCount++;
    }
    var layout;
    if (nodeCount > 0 && savedCount === nodeCount) {
        layout = cy.layout({ name: 'preset', fit: false });
    } else if (savedCount === 0) {
        layout = createAutomaticLayout(cy.elements(), nodeCount, edgeElements.length, roots);
    } else {
        var freeNodes = cy.nodes().filter(function (node) { return !savedById[node.id()]; });
        var freeIds = {};
        freeNodes.forEach(function (node) { freeIds[node.id()] = true; });
        var freeEdges = cy.edges().filter(function (edge) {
            return freeIds[edge.source().id()] && freeIds[edge.target().id()];
        });
        var subsetRoots = [];
        if (roots && roots.length) {
            for (var r = 0; r < roots.length; r++) {
                var rootId = String(roots[r]);
                if (freeIds[rootId]) subsetRoots.push(rootId);
            }
        }
        layout = createAutomaticLayout(freeNodes.union(freeEdges), freeNodes.length, freeEdges.length, subsetRoots);
    }

    function restoreSavedPositions() {
        cy.nodes().forEach(function (node) {
            var pos = savedById[node.id()];
            if (pos) node.position(pos);
        });
    }
    function fitToContainer() {
        if (cy && !cy.destroyed()) {
            cy.resize();
            cy.fit(20);
        }
    }
    var layoutFinished = false;
    function finishLayout() {
        if (!cy || cy.destroyed()) return;
        restoreSavedPositions();
        if (layoutFinished) return;
        layoutFinished = true;
        requestAnimationFrame(fitToContainer);
    }
    // layoutstop is emitted on the layout, and these layouts finish inside run().
    layout.one('layoutstop', finishLayout);
    cy.once('layoutstop', finishLayout);
    layout.run();
    finishLayout();

    if (persistLayout) {
        var allow = allowRepositoryIds(repositoryIds, nodes);
        cy.on('dragfree', 'node', function (event) {
            var node = event.target;
            var id = node.id();
            if (!allow[id]) return;
            var map = readDependencyLayout(workspaceId, contextId);
            var pos = node.position();
            map[id] = { x: pos.x, y: pos.y };
            var pruned = {};
            Object.keys(map).forEach(function (key) {
                if (!allow[key]) return;
                var kept = savedPosition(map, key);
                if (kept) pruned[key] = kept;
            });
            writeDependencyLayout(workspaceId, contextId, pruned);
            if (dotNetRef && typeof dotNetRef.invokeMethodAsync === 'function')
                dotNetRef.invokeMethodAsync('OnDependencyLayoutSaved');
        });
    }

    var hoveredNodeId = null;
    var legendHideToken = 0;
    window['__cy_legend_' + containerId] = function () {
        legendHideToken++;
        hoveredNodeId = null;
        hideDependencyTypeLegend();
    };

    // The type label stays up while the pointer is on that node. A leave that is
    // immediately followed by another enter (redraw under the pointer) does not hide it.
    cy.on('mouseover', 'node', function (event) {
        var node = event.target;
        if (!node || typeof node.data !== 'function') return;
        legendHideToken++;
        hoveredNodeId = node.id();
        showDependencyTypeLegend(cy, node);
    });
    cy.on('mouseout', 'node', function (event) {
        var node = event.target;
        if (!node || node.id() !== hoveredNodeId) return;
        var pos = event.renderedPosition;
        var box = node.renderedBoundingBox({ includeLabels: true });
        if (pos && pos.x >= box.x1 && pos.x <= box.x2 && pos.y >= box.y1 && pos.y <= box.y2)
            return;
        var token = ++legendHideToken;
        var id = node.id();
        requestAnimationFrame(function () {
            if (token !== legendHideToken || hoveredNodeId !== id) return;
            hoveredNodeId = null;
            hideDependencyTypeLegend();
        });
    });
    cy.on('grab', 'node', function () {
        cy._alignDrag = null;
    });
    cy.on('free', 'node', function () {
        cy._alignDrag = null;
    });
    cy.on('drag', 'node', function (event) {
        snapDraggedNodeAlignment(cy, event.target);
    });

    var resizeHandler = function () { requestAnimationFrame(fitToContainer); };
    window.addEventListener('resize', resizeHandler);
    window['__cy_resize_' + containerId] = resizeHandler;

    window['__cy_' + containerId] = cy;
    return true;

    function createAutomaticLayout(elements, layoutNodeCount, layoutEdgeCount, layoutRoots) {
        var scale = dependencySpacingScale(layoutNodeCount, layoutEdgeCount);
        if (layoutEdgeCount === 0 && layoutNodeCount > 0) {
            /* No connections: distribute nodes in a grid (2+ columns when we have 2+ nodes) */
            var gridOpts = {
                name: 'grid',
                fit: false,
                padding: 20,
                condense: false,
                spacingFactor: scale
            };
            if (layoutNodeCount === 1) {
                gridOpts.rows = 1;
            } else {
                gridOpts.cols = Math.max(2, Math.ceil(Math.sqrt(layoutNodeCount)));
            }
            return elements.layout(gridOpts);
        }

        var dagreOpts = {
            name: 'dagre',
            rankDir: 'TB',
            nodeSep: Math.round(50 * scale),
            rankSep: Math.round(70 * scale),
            edgeSep: Math.round(20 * scale),
            padding: 10,
            ranker: 'network-simplex',
            fit: false
        };
        try {
            return elements.layout(dagreOpts);
        } catch (e) {
            var breadthOpts = {
                name: 'breadthfirst',
                directed: true,
                spacingFactor: 1.5 * scale,
                padding: 0,
                fit: false
            };
            if (layoutRoots && layoutRoots.length > 0) breadthOpts.roots = layoutRoots.map(String);
            return elements.layout(breadthOpts);
        }
    }
};

/**
 * True when this workspace (and feature context, when set) has any saved positions.
 */
window.cytoscapeDependencyLayoutSaved = function (workspaceId, contextId) {
    var map = readDependencyLayout(workspaceId, contextId);
    var key;
    for (key in map) {
        if (Object.prototype.hasOwnProperty.call(map, key)) return true;
    }
    return false;
};

/**
 * Clears saved positions for this workspace and feature context only.
 */
window.clearCytoscapeDependencyLayout = function (workspaceId, contextId) {
    writeDependencyLayout(workspaceId, contextId, {});
};

/**
 * Today's dagre gaps (nodeSep 50, rankSep 70, edgeSep 20) through 8 nodes.
 * Larger diagrams spread with the square root of the node count, and a little
 * more when there are many edges per node. Capped so the diagram can still fit.
 */
function dependencySpacingScale(nodeCount, edgeCount) {
    var count = nodeCount || 0;
    if (count <= 8) return 1;
    var scale = Math.sqrt(count / 8);
    var density = (edgeCount || 0) / count;
    if (density > 2) scale *= Math.min(1.25, 1 + (density - 2) * 0.08);
    if (scale > 4) return 4;
    return scale;
}

/** Workspace only when there is no feature context, so the key does not depend on the workspace row id. */
function dependencyLayoutStorageKey(workspaceId, contextId) {
    var workspace = String(workspaceId);
    if (contextId === null || contextId === undefined || contextId === '' || contextId === 0)
        return 'graymoon-deps-layout-' + workspace;
    return 'graymoon-deps-layout-' + workspace + '-' + String(contextId);
}

function readDependencyLayout(workspaceId, contextId) {
    if (typeof window.graymoonStorageGet !== 'function') return {};
    var raw;
    try {
        raw = window.graymoonStorageGet(dependencyLayoutStorageKey(workspaceId, contextId));
    } catch (e) {
        return {};
    }
    if (!raw) return {};
    try {
        var parsed = JSON.parse(raw);
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {};
        return parsed;
    } catch (e) {
        return {};
    }
}

function writeDependencyLayout(workspaceId, contextId, map) {
    if (typeof window.graymoonStorageSet !== 'function') return;
    try {
        window.graymoonStorageSet(dependencyLayoutStorageKey(workspaceId, contextId), JSON.stringify(map || {}));
    } catch (e) { /* storage unavailable - best-effort */ }
}

function savedPosition(map, id) {
    var entry = map && map[id];
    if (!entry || typeof entry !== 'object') return null;
    var x = Number(entry.x);
    var y = Number(entry.y);
    if (!isFinite(x) || !isFinite(y)) return null;
    return { x: x, y: y };
}

function allowRepositoryIds(repositoryIds, nodes) {
    var allow = {};
    var source = repositoryIds && repositoryIds.length
        ? repositoryIds
        : (nodes || []).map(function (n) { return n.id; });
    for (var i = 0; i < source.length; i++) {
        if (source[i] !== null && source[i] !== undefined && source[i] !== '')
            allow[String(source[i])] = true;
    }
    return allow;
}

/** Screen pixels. Divided by zoom so a zoomed-out diagram still snaps when centers are about this close on screen. */
var dependencyAlignSnapRenderedPx = 10;

/**
 * While a node is dragged, line its center up with the closest other node on each axis
 * when that axis is within a few screen pixels. The pointer position is tracked separately
 * because each drag step is applied on top of the snapped position. Leaving the range
 * follows the pointer again.
 */
function snapDraggedNodeAlignment(cy, node) {
    if (!cy || cy.destroyed() || !node || typeof node.position !== 'function') return;
    var zoom = cy.zoom();
    if (!(zoom > 0) || !isFinite(zoom)) return;
    var threshold = dependencyAlignSnapRenderedPx / zoom;
    var pos = node.position();
    if (!pos || !isFinite(pos.x) || !isFinite(pos.y)) return;
    var state = cy._alignDrag;
    var cursorX = pos.x;
    var cursorY = pos.y;
    if (state && state.id === node.id()
        && isFinite(state.cursorX) && isFinite(state.cursorY)
        && isFinite(state.snappedX) && isFinite(state.snappedY)) {
        cursorX = state.cursorX + (pos.x - state.snappedX);
        cursorY = state.cursorY + (pos.y - state.snappedY);
    }
    var bestDx = Infinity;
    var bestDy = Infinity;
    var snapX = null;
    var snapY = null;
    var draggedId = node.id();
    cy.nodes().forEach(function (other) {
        if (!other || other === node || other.id() === draggedId) return;
        var otherPos = other.position();
        if (!otherPos || !isFinite(otherPos.x) || !isFinite(otherPos.y)) return;
        var dx = Math.abs(cursorX - otherPos.x);
        var dy = Math.abs(cursorY - otherPos.y);
        if (dx <= threshold && dx < bestDx) {
            bestDx = dx;
            snapX = otherPos.x;
        }
        if (dy <= threshold && dy < bestDy) {
            bestDy = dy;
            snapY = otherPos.y;
        }
    });
    var nextX = snapX !== null ? snapX : cursorX;
    var nextY = snapY !== null ? snapY : cursorY;
    cy._alignDrag = {
        id: draggedId,
        cursorX: cursorX,
        cursorY: cursorY,
        snappedX: nextX,
        snappedY: nextY
    };
    if (nextX === pos.x && nextY === pos.y) return;
    node.position({ x: nextX, y: nextY });
}

var dependencyTypeLabels = {
    service: 'Service',
    package: 'Package',
    library: 'Library',
    executable: 'Executable',
    test: 'Test'
};

function dependencyTypeLegendElement() {
    return document.getElementById('deps-graph-hover-legend');
}

function hideDependencyTypeLegend() {
    var legend = dependencyTypeLegendElement();
    if (!legend) return;
    legend.hidden = true;
}

function showDependencyTypeLegend(cy, node) {
    var legend = dependencyTypeLegendElement();
    if (!legend || !cy || cy.destroyed()) return;
    var type = node.data('nodeType');
    var label = dependencyTypeLabels[type];
    if (!label) {
        hideDependencyTypeLegend();
        return;
    }
    var swatch = legend.querySelector('.deps-graph-legend__swatch');
    var text = legend.querySelector('.deps-graph-legend__label');
    if (swatch)
        swatch.className = 'deps-graph-legend__swatch deps-graph-legend__swatch--' + type;
    if (text) text.textContent = label;
    legend.hidden = false;
}

/**
 * Destroy a Cytoscape instance and free resources.
 * @param {string} containerId - Id used when calling renderCytoscapeGraph
 */
window.destroyCytoscapeGraph = function (containerId) {
    var cancelLegend = window['__cy_legend_' + containerId];
    window['__cy_legend_' + containerId] = null;
    if (typeof cancelLegend === 'function') cancelLegend();
    else hideDependencyTypeLegend();
    window.removeEventListener('resize', window['__cy_resize_' + containerId]);
    window['__cy_resize_' + containerId] = null;
    var key = '__cy_' + containerId;
    var cy = window[key];
    if (cy) {
        cy.destroy();
        window[key] = null;
    }
};
