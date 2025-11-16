// Monaco Editor Custom Context Menu Actions
window.monacoContextMenu = {
    addCustomActions: function (editorId) {
        // Find the editor instance by its container ID
        const editors = monaco.editor.getEditors();
        const editor = editors.find(e => {
            const container = e.getContainerDomNode();
            return container && (container.id === editorId || container.closest(`#${editorId}`));
        });

        if (!editor) {
            console.warn(`Could not find Monaco editor with ID: ${editorId}`);
            return;
        }

        // Add context menu actions
        editor.addAction({
            id: 'insert-new-guid',
            label: 'Insert New GUID',
            contextMenuGroupId: 'quick-values',
            contextMenuOrder: 1,
            run: function (ed) {
                const guid = crypto.randomUUID();
                const selection = ed.getSelection();
                ed.executeEdits('', [{
                    range: selection,
                    text: guid
                }]);
            }
        });

        editor.addAction({
            id: 'insert-guid-placeholder',
            label: 'Insert GUID Placeholder',
            contextMenuGroupId: 'quick-values',
            contextMenuOrder: 2,
            run: function (ed) {
                const selection = ed.getSelection();
                ed.executeEdits('', [{
                    range: selection,
                    text: '~newGuid~'
                }]);
            }
        });

        editor.addAction({
            id: 'insert-now',
            label: 'Insert Now (ISO)',
            contextMenuGroupId: 'quick-values',
            contextMenuOrder: 3,
            run: function (ed) {
                const now = new Date().toISOString();
                const selection = ed.getSelection();
                ed.executeEdits('', [{
                    range: selection,
                    text: now
                }]);
            }
        });

        editor.addAction({
            id: 'insert-now-placeholder',
            label: 'Insert Now Placeholder',
            contextMenuGroupId: 'quick-values',
            contextMenuOrder: 4,
            run: function (ed) {
                const selection = ed.getSelection();
                ed.executeEdits('', [{
                    range: selection,
                    text: '~now~'
                }]);
            }
        });

        editor.addAction({
            id: 'insert-now-plus-1m',
            label: 'Insert Now+1m (ISO)',
            contextMenuGroupId: 'quick-values',
            contextMenuOrder: 5,
            run: function (ed) {
                const date = new Date();
                date.setMinutes(date.getMinutes() + 1);
                const selection = ed.getSelection();
                ed.executeEdits('', [{
                    range: selection,
                    text: date.toISOString()
                }]);
            }
        });

        editor.addAction({
            id: 'insert-now-plus-1m-placeholder',
            label: 'Insert Now+1m Placeholder',
            contextMenuGroupId: 'quick-values',
            contextMenuOrder: 6,
            run: function (ed) {
                const selection = ed.getSelection();
                ed.executeEdits('', [{
                    range: selection,
                    text: '~now+1m~'
                }]);
            }
        });

        editor.addAction({
            id: 'insert-now-plus-5m',
            label: 'Insert Now+5m (ISO)',
            contextMenuGroupId: 'quick-values',
            contextMenuOrder: 7,
            run: function (ed) {
                const date = new Date();
                date.setMinutes(date.getMinutes() + 5);
                const selection = ed.getSelection();
                ed.executeEdits('', [{
                    range: selection,
                    text: date.toISOString()
                }]);
            }
        });

        editor.addAction({
            id: 'insert-now-plus-5m-placeholder',
            label: 'Insert Now+5m Placeholder',
            contextMenuGroupId: 'quick-values',
            contextMenuOrder: 8,
            run: function (ed) {
                const selection = ed.getSelection();
                ed.executeEdits('', [{
                    range: selection,
                    text: '~now+5m~'
                }]);
            }
        });

        editor.addAction({
            id: 'insert-now-plus-1h',
            label: 'Insert Now+1h (ISO)',
            contextMenuGroupId: 'quick-values',
            contextMenuOrder: 9,
            run: function (ed) {
                const date = new Date();
                date.setHours(date.getHours() + 1);
                const selection = ed.getSelection();
                ed.executeEdits('', [{
                    range: selection,
                    text: date.toISOString()
                }]);
            }
        });

        editor.addAction({
            id: 'insert-now-plus-1h-placeholder',
            label: 'Insert Now+1h Placeholder',
            contextMenuGroupId: 'quick-values',
            contextMenuOrder: 10,
            run: function (ed) {
                const selection = ed.getSelection();
                ed.executeEdits('', [{
                    range: selection,
                    text: '~now+1h~'
                }]);
            }
        });

        editor.addAction({
            id: 'insert-now-plus-1d',
            label: 'Insert Now+1d (ISO)',
            contextMenuGroupId: 'quick-values',
            contextMenuOrder: 11,
            run: function (ed) {
                const date = new Date();
                date.setDate(date.getDate() + 1);
                const selection = ed.getSelection();
                ed.executeEdits('', [{
                    range: selection,
                    text: date.toISOString()
                }]);
            }
        });

        editor.addAction({
            id: 'insert-now-plus-1d-placeholder',
            label: 'Insert Now+1d Placeholder',
            contextMenuGroupId: 'quick-values',
            contextMenuOrder: 12,
            run: function (ed) {
                const selection = ed.getSelection();
                ed.executeEdits('', [{
                    range: selection,
                    text: '~now+1d~'
                }]);
            }
        });
    }
};
