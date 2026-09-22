using System;
using System.ComponentModel.Composition;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;

namespace dnSpy.Extension.MCP
{
    /// <summary>
    /// <see cref="IMcpHost"/> inside dnSpy. Documents come from dnSpy's document service (the list the
    /// assembly tree shows), decompilation uses the decompiler selected in dnSpy's UI, and renames keep
    /// the tree and open tabs in sync the way dnSpy's own editor does.
    /// </summary>
    [Export(typeof(IMcpHost))]
    sealed class DnSpyMcpHost : IMcpHost
    {
        readonly IDocumentTreeView documentTreeView;
        readonly IDocumentTabService documentTabService;
        readonly IDecompilerService decompilerService;
        readonly McpSettings settings;

        [ImportingConstructor]
        DnSpyMcpHost(
            IDocumentTreeView documentTreeView,
            IDocumentTabService documentTabService,
            IDecompilerService decompilerService,
            McpSettings settings)
        {
            this.documentTreeView = documentTreeView;
            this.documentTabService = documentTabService;
            this.decompilerService = decompilerService;
            this.settings = settings;
        }

        // GetDocuments() snapshots under the service's lock, so this is safe on the HTTP worker threads
        // the read-only tools run on — unlike tree nodes, which are UI-thread-only DispatcherObjects.
        public IDsDocument[] GetDocuments() => documentTreeView.DocumentService.GetDocuments();

        // The programmatic File → Open: the tree materializes the node from the service's
        // CollectionChanged event, which is why open_files runs on the UI thread.
        public IDsDocument? OpenDocument(string path) =>
            documentTreeView.DocumentService.TryGetOrCreate(DsDocumentInfo.CreateDocument(path));

        public IDecompiler Decompiler => decompilerService.Decompiler;

        public T InvokeOnUiThread<T>(Func<T> action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.CheckAccess())
                return action();
            return dispatcher.Invoke(action);
        }

        public void ApplyRename(IMemberDef definition, ModuleDef module, Action rename, Action rollback, string operation)
        {
            var node = FindNode(definition);
            var parentNode = node?.TreeNode.Parent;
            var originalIndex = parentNode == null || node == null
                ? -1
                : parentNode.Children.IndexOf(node.TreeNode);
            var wasSelected = node != null && node.TreeNode.TreeView.SelectedItem == node;

            try
            {
                // Reinsert a visible node so its parent's sort order is recalculated.
                if (parentNode != null && node != null && originalIndex >= 0)
                    parentNode.Children.RemoveAt(originalIndex);

                rename();

                if (parentNode != null && node != null && originalIndex >= 0)
                    parentNode.AddChild(node.TreeNode);
            }
            catch
            {
                rollback();
                if (parentNode != null && node != null && originalIndex >= 0)
                {
                    var currentIndex = parentNode.Children.IndexOf(node.TreeNode);
                    if (currentIndex >= 0)
                        parentNode.Children.RemoveAt(currentIndex);
                    parentNode.Children.Insert(Math.Min(originalIndex, parentNode.Children.Count), node.TreeNode);
                }
                throw;
            }

            // UI refresh failures must not report the metadata operation as failed after it has
            // already committed. Log the warning; the renamed metadata can still be saved.
            try
            {
                if (wasSelected && node != null)
                    node.TreeNode.TreeView.SelectItems(new[] { node });
                node?.TreeNode.RefreshUI();
            }
            catch (Exception ex)
            {
                settings.Log($"{operation} UI refresh warning: {ex.Message}");
            }
            RefreshDecompiledViews(module, operation);
        }

        public void RefreshTreeNode(IMemberDef definition, string operation)
        {
            try { FindNode(definition)?.TreeNode.RefreshUI(); }
            catch (Exception ex) { settings.Log($"{operation} UI refresh warning: {ex.Message}"); }
        }

        public void RefreshDecompiledViews(ModuleDef module, string operation)
        {
            var moduleNode = documentTreeView.FindNode(module);
            if (moduleNode?.Document == null)
            {
                settings.Log($"{operation}: module document node not found; open decompiler tabs were not refreshed");
                return;
            }

            // RefreshUI() only redraws the assembly-tree label. This public API is the
            // invalidation path dnSpy.AsmEditor ultimately uses to rebuild decompiled tabs after
            // its undo commands report modified document-tree objects.
            documentTabService.RefreshModifiedDocument(moduleNode.Document);
        }

        DocumentTreeNodeData? FindNode(IMemberDef definition) => definition switch
        {
            MethodDef method => documentTreeView.FindNode(method),
            TypeDef type => documentTreeView.FindNode(type),
            FieldDef field => documentTreeView.FindNode(field),
            PropertyDef property => documentTreeView.FindNode(property),
            EventDef eventDef => documentTreeView.FindNode(eventDef),
            _ => documentTreeView.FindNode((object)definition),
        };
    }
}
