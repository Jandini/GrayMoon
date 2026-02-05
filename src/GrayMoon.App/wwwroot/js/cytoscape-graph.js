/**
 * Render a dependency graph with Cytoscape (dark scheme).
 * @param {string} containerId - Id of the div element to render into
 * @param {Array<{id: string, label: string}>} nodes - Nodes with id and label
 * @param {Array<{source: string, target: string}>} edges - Edges with source and target node ids
 * @param {string[]} [roots] - Optional node ids to use as roots (no incoming edges). Layout flows from these for a clear hierarchy.
 */
window.renderCytoscapeGraph = function (containerId, nodes, edges, roots) {
    var container = document.getElementById(containerId);
    if (!container || typeof cytoscape === 'undefined') return null;

    var nodeElements = (nodes || []).map(function (n) {
        return { data: { id: String(n.id), label: n.label || String(n.id) } };
    });
    var edgeElements = (edges || []).map(function (e, i) {
        return { data: { id: 'e' + i, source: String(e.source), target: String(e.target) } };
    });

    container.style.backgroundColor = '#1a1a1a';

    var layoutOpts = {
        name: 'breadthfirst',
        directed: true,
        spacingFactor: 1.5,
        padding: { top: 28, right: 28, bottom: 52, left: 28 }
    };
    if (roots && roots.length > 0) {
        layoutOpts.roots = roots.map(String);
    }

    var cy = cytoscape({
        container: container,
        elements: nodeElements.concat(edgeElements),
        style: [
            {
                selector: 'node',
                style: {
                    'shape': 'rectangle',
                    'background-color': '#27272a',
                    'label': 'data(label)',
                    'color': '#fafafa',
                    'text-valign': 'center',
                    'text-halign': 'center',
                    'font-size': '11px',
                    'text-wrap': 'wrap',
                    'text-max-width': '140px',
                    'border-width': 1,
                    'border-color': '#3f3f46',
                    'width': 140,
                    'height': 40,
                    'padding': '6px'
                }
            },
            {
                selector: 'edge',
                style: {
                    'width': 1.5,
                    'line-color': '#71717a',
                    'target-arrow-color': '#71717a',
                    'target-arrow-shape': 'triangle',
                    'curve-style': 'bezier',
                    'arrow-scale': 0.85
                }
            }
        ],
        layout: layoutOpts,
        minZoom: 0.2,
        maxZoom: 3,
        wheelSensitivity: 0.3
    });

    window['__cy_' + containerId] = cy;
    return true;
};

/**
 * Destroy a Cytoscape instance and free resources.
 * @param {string} containerId - Id used when calling renderCytoscapeGraph
 */
window.destroyCytoscapeGraph = function (containerId) {
    var key = '__cy_' + containerId;
    var cy = window[key];
    if (cy) {
        cy.destroy();
        window[key] = null;
    }
};
