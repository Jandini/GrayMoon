/**
 * Render a dependency graph with Cytoscape (dark scheme).
 * Node types use distinct shapes and soft fills so they stay readable without neon borders.
 * @param {string} containerId - Id of the div element to render into
 * @param {Array<{id: string, label: string, nodeType?: string}>} nodes - Nodes with id, label, and optional nodeType
 * @param {Array<{source: string, target: string}>} edges - Edges with source and target node ids
 * @param {string[]} [roots] - Optional node ids to use as roots (no incoming edges). Layout flows from these for a clear hierarchy.
 */
window.renderCytoscapeGraph = function (containerId, nodes, edges, roots) {
    var container = document.getElementById(containerId);
    if (!container || typeof cytoscape === 'undefined') return null;

    var nodeElements = (nodes || []).map(function (n) {
        return { data: { id: String(n.id), label: n.label || String(n.id), nodeType: n.nodeType || 'other' } };
    });
    var edgeElements = (edges || []).map(function (e, i) {
        return { data: { id: 'e' + i, source: String(e.source), target: String(e.target) } };
    });

    container.style.backgroundColor = '#141416';

    if (typeof cytoscapeDagre !== 'undefined') cytoscape.use(cytoscapeDagre);

    var cy = cytoscape({
        container: container,
        elements: nodeElements.concat(edgeElements),
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
                /* NuGet packages - squat hexagon (almost square, mild side chamfers) */
                selector: 'node[nodeType = "package"]',
                style: {
                    'shape': 'polygon',
                    'shape-polygon-points': '-0.92 -1  0.92 -1  1 0  0.92 1  -0.92 1  -1 0',
                    'background-color': '#16353f',
                    'border-color': '#38bdf8',
                    'color': '#bae6fd',
                    'width': 148,
                    'height': 44
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
        minZoom: 0.2,
        maxZoom: 3
    });

    var nodeCount = (nodes || []).length;
    var edgeCount = (edges || []).length;
    var layout;
    if (edgeCount === 0 && nodeCount > 0) {
        /* No connections: distribute nodes in a grid (2+ columns when we have 2+ nodes) */
        var gridOpts = { name: 'grid', fit: false, padding: 20, condense: false };
        if (nodeCount === 1) {
            gridOpts.rows = 1;
        } else {
            gridOpts.cols = Math.max(2, Math.ceil(Math.sqrt(nodeCount)));
        }
        layout = cy.layout(gridOpts);
    } else {
        var layoutOpts = { name: 'dagre', rankDir: 'TB', nodeSep: 50, rankSep: 70, edgeSep: 20, padding: 10, ranker: 'network-simplex' };
        try {
            layout = cy.layout(layoutOpts);
        } catch (e) {
            layoutOpts = { name: 'breadthfirst', directed: true, spacingFactor: 1.5, padding: 0 };
            if (roots && roots.length > 0) layoutOpts.roots = roots.map(String);
            layout = cy.layout(layoutOpts);
        }
    }
    layout.run();
    function fitToContainer() {
        if (cy && !cy.destroyed()) {
            cy.resize();
            cy.fit(20);
        }
    }
    cy.once('layoutstop', function () { requestAnimationFrame(fitToContainer); });

    var resizeHandler = function () { requestAnimationFrame(fitToContainer); };
    window.addEventListener('resize', resizeHandler);
    window['__cy_resize_' + containerId] = resizeHandler;

    window['__cy_' + containerId] = cy;
    return true;
};

/**
 * Destroy a Cytoscape instance and free resources.
 * @param {string} containerId - Id used when calling renderCytoscapeGraph
 */
window.destroyCytoscapeGraph = function (containerId) {
    window.removeEventListener('resize', window['__cy_resize_' + containerId]);
    window['__cy_resize_' + containerId] = null;
    var key = '__cy_' + containerId;
    var cy = window[key];
    if (cy) {
        cy.destroy();
        window[key] = null;
    }
};
