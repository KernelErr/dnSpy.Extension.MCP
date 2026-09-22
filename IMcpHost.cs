using System;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;

namespace dnSpy.Extension.MCP
{
    /// <summary>
    /// Everything <see cref="McpTools"/> needs from the process it runs in, so one set of tools serves
    /// both hosts: inside dnSpy (<see cref="DnSpyMcpHost"/>: dnSpy's document service, the decompiler
    /// selected in its UI, and its tree/tab UI) and headless (dnSpy.Extension.MCP.Headless: its own
    /// document list, dnSpy's C# decompiler loaded the way dnSpy.Console.exe loads it, and no UI).
    /// </summary>
    interface IMcpHost
    {
        /// <summary>Snapshot of the loaded top-level documents. Must be safe to call from any thread.</summary>
        IDsDocument[] GetDocuments();

        /// <summary>
        /// Loads the file at <paramref name="path"/> (a full path), or returns its document when it is
        /// already loaded; null when it isn't a .NET module. Only UI-thread tools (open_files) call it.
        /// </summary>
        IDsDocument? OpenDocument(string path);

        /// <summary>The decompiler behind the decompile_* tools.</summary>
        IDecompiler Decompiler { get; }

        /// <summary>
        /// Runs a tool that mutates metadata or touches UI state: synchronously on dnSpy's UI thread, or
        /// inline when there is no UI.
        /// </summary>
        T InvokeOnUiThread<T>(Func<T> action);

        /// <summary>
        /// Renames <paramref name="definition"/> by running <paramref name="rename"/>, keeping any UI that
        /// shows it in sync (dnSpy re-sorts and redraws its tree node, then rebuilds open decompiled views
        /// of <paramref name="module"/>). If <paramref name="rename"/> throws, <paramref name="rollback"/>
        /// runs before the exception propagates. UI failures after the rename are logged, never thrown.
        /// </summary>
        void ApplyRename(IMemberDef definition, ModuleDef module, Action rename, Action rollback, string operation);

        /// <summary>Redraws <paramref name="definition"/>'s tree node, if the host shows one. Failures are logged, never thrown.</summary>
        void RefreshTreeNode(IMemberDef definition, string operation);

        /// <summary>Rebuilds the open decompiled views of <paramref name="module"/>, if the host has any.</summary>
        void RefreshDecompiledViews(ModuleDef module, string operation);
    }
}
