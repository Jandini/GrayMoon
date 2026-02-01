window.graymoonWorkspaceSync = (function () {
    var eventSources = {};
    return {
        subscribe: function (workspaceId, baseUri, dotNetRef) {
            var url = baseUri.replace(/\/$/, '') + '/api/workspaces/' + workspaceId + '/sync-events';
            if (eventSources[workspaceId]) {
                eventSources[workspaceId].close();
            }
            var es = new EventSource(url);
            es.onmessage = function () {
                dotNetRef.invokeMethodAsync('OnSyncEvent');
            };
            es.onerror = function () {
                es.close();
                delete eventSources[workspaceId];
            };
            eventSources[workspaceId] = es;
        },
        unsubscribe: function (workspaceId) {
            if (eventSources[workspaceId]) {
                eventSources[workspaceId].close();
                delete eventSources[workspaceId];
            }
        }
    };
})();
